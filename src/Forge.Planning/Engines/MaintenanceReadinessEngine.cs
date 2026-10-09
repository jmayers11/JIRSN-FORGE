using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Engines;
using Forge.Maintenance.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Planning.Engines;

/// <summary>Everything one work order's readiness is decided from, read at one instant.</summary>
/// <param name="Available">Available (released) stock on hand by part. Not reserved: other work may take it first.</param>
/// <param name="From">When the work would start: its scheduled start, or now.</param>
/// <param name="Until">When the work would end: its scheduled end, or the start plus its duration.</param>
public sealed record MaintenanceReadinessRequest(
    WorkOrderInfo Order,
    AssetInfo? Asset,
    IReadOnlyList<WorkOrderPartRequirement> Parts,
    IReadOnlyList<WorkOrderPart> Issued,
    IReadOnlyDictionary<string, decimal> Available,
    IReadOnlyList<AlternateInfo> Alternates,
    DocumentInfo? Procedure,
    IReadOnlyList<PersonSnapshot> People,
    IReadOnlyList<UnavailabilityWindow> Unavailability,
    DateTimeOffset From,
    DateTimeOffset Until);

/// <summary>One required part: what is still needed, and whether stock (or an approved alternate) covers it.</summary>
public sealed record PartReadiness(
    string PartNumber,
    decimal Required,
    decimal Issued,
    decimal StillNeeded,
    decimal OnHand,
    bool Covered,
    string? Alternate,
    long? AlternateId,
    decimal AlternateQuantity,
    string Detail);

/// <param name="Status">Ready, Blocked, or Closed for work that is finished or cancelled.</param>
/// <param name="Blocking">The first dimension that fails (Asset, Material, Procedure, Technician).</param>
public sealed record MaintenanceReadiness(
    long WorkOrderId,
    string Target,
    string Status,
    string? Blocking,
    IReadOnlyList<ReadinessDimension> Dimensions,
    IReadOnlyList<PartReadiness> Parts,
    IReadOnlyList<string> Technicians,
    string Summary);

/// <summary>
/// Whether maintenance work can happen (V2.9), dimension by dimension, each failing on its own: the asset is
/// in a state to be worked on; every required part is issued, on hand, or covered by an approved alternate
/// usable for this asset (maintenance-only ones included, proposed ones never); the procedure revision is
/// released and in effect; someone available holds every required qualification for the whole window.
/// There is no score. Work that is not ready is reported as blocked, not planned as capacity loss.
/// </summary>
public static class MaintenanceReadinessEngine
{
    public static MaintenanceReadiness Decide(MaintenanceReadinessRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var order = r.Order;
        if (order.Status is WorkOrderStatus.Closed or WorkOrderStatus.Cancelled or WorkOrderStatus.Completed)
        {
            return new MaintenanceReadiness(order.WorkOrderId, order.Target, "Closed", null, [], [], [], $"Work order {order.WorkOrderId} is {order.Status}.");
        }

        var parts = r.Parts.Select(p => Part(r, p)).ToList();
        var (technician, technicians) = Technician(r);
        var dimensions = new List<ReadinessDimension> { Asset(r), Material(parts), Procedure(r), technician };
        var blocking = dimensions.FirstOrDefault(d => d.Outcome != EvidenceOutcome.Pass)?.Name;
        var summary = blocking is null
            ? $"Work order {order.WorkOrderId} on {order.Target} is ready."
            : $"Work order {order.WorkOrderId} on {order.Target} is blocked: " + string.Join("; ", dimensions.Where(d => d.Outcome != EvidenceOutcome.Pass).Select(d => $"{d.Name} — {d.Detail}"));
        return new MaintenanceReadiness(order.WorkOrderId, order.Target, blocking is null ? "Ready" : "Blocked", blocking, dimensions, parts, technicians, summary);
    }

    private static ReadinessDimension Asset(MaintenanceReadinessRequest r)
    {
        if (r.Asset is not { } asset)
        {
            return r.Order.AssetCode is { } code
                ? new ReadinessDimension("Asset", EvidenceOutcome.Fail, $"Asset {code} was not found.")
                : new ReadinessDimension("Asset", EvidenceOutcome.Pass, $"Machine {r.Order.MachineCode} has no asset record; nothing about it blocks the work.");
        }

        return asset.Status switch
        {
            AssetStatus.Decommissioned => new ReadinessDimension("Asset", EvidenceOutcome.Fail, $"Asset {asset.AssetCode} is decommissioned."),
            AssetStatus.Planned => new ReadinessDimension("Asset", EvidenceOutcome.Pass, $"Asset {asset.AssetCode} is not commissioned yet."),
            _ => new ReadinessDimension("Asset", EvidenceOutcome.Pass, $"Asset {asset.AssetCode} is {asset.Status}."),
        };
    }

    private static PartReadiness Part(MaintenanceReadinessRequest r, WorkOrderPartRequirement line)
    {
        // What has gone to this work order already, in the required part's terms (alternates by their primary quantity).
        var issued = r.Issued.Where(i => i.PartNumber == line.PartNumber && i.SubstitutesFor is null).Sum(i => i.NetIssued)
            + r.Issued.Where(i => i.SubstitutesFor == line.PartNumber).Sum(i => i.PrimaryNetIssued ?? 0);
        var still = Math.Max(0, line.Quantity - issued);
        var onHand = r.Available.TryGetValue(line.PartNumber, out var q) ? q : 0;
        if (still == 0)
        {
            return new PartReadiness(line.PartNumber, line.Quantity, issued, 0, onHand, true, null, null, 0, $"{line.PartNumber}: {Q(issued)} issued to the work order.");
        }

        if (onHand >= still)
        {
            return new PartReadiness(line.PartNumber, line.Quantity, issued, still, onHand, true, null, null, 0, $"{line.PartNumber}: {Q(still)} needed, {Q(onHand)} on hand.");
        }

        var context = new AlternateContext(DateOnly.FromDateTime(r.From.UtcDateTime), AssetCode: r.Order.AssetCode, MachineCode: r.Order.MachineCode, Maintenance: true);
        var candidates = Alternates.Candidates(r.Alternates, line.PartNumber, context);
        var shortPrimary = still - onHand;
        foreach (var c in candidates.Approved.OrderBy(c => c.Relationship.AlternateId))
        {
            var altOnHand = r.Available.TryGetValue(c.OfferedPart, out var a) ? a : 0;
            var altNeeded = decimal.Round(shortPrimary * c.Factor, 6, MidpointRounding.AwayFromZero);
            if (altOnHand >= altNeeded)
            {
                var how = onHand > 0 ? $"{Q(onHand)} on hand and {Q(altNeeded)} of approved alternate {c.OfferedPart}" : $"approved alternate {c.OfferedPart} ({Q(altOnHand)} on hand, {Q(altNeeded)} needed)";
                return new PartReadiness(line.PartNumber, line.Quantity, issued, still, onHand, true, c.OfferedPart, c.Relationship.AlternateId, altNeeded,
                    $"{line.PartNumber}: {Q(still)} needed, {Q(onHand)} on hand; covered by {how} — {Alternates.Label(c.Relationship.Kind).ToLowerInvariant()} relationship {c.Relationship.AlternateId}{Applies(c.Relationship)}.");
            }
        }

        var notUsable = candidates.RequiresApproval.Select(c => $"{c.OfferedPart} ({string.Join(" ", c.Reasons)})").ToList();
        var tooFew = candidates.Approved.Select(c => $"{c.OfferedPart} has {Q(r.Available.TryGetValue(c.OfferedPart, out var x) ? x : 0)}").ToList();
        var detail = $"{line.PartNumber}: {Q(still)} needed, {Q(onHand)} on hand"
            + (candidates.Approved.Count == 0 ? "; no approved alternate" : $"; approved alternates too short: {string.Join(", ", tooFew)}")
            + (notUsable.Count == 0 ? "." : $"; not usable without approval: {string.Join("; ", notUsable)}.");
        return new PartReadiness(line.PartNumber, line.Quantity, issued, still, onHand, false, null, null, 0, detail);
    }

    private static string Applies(AlternateInfo a)
    {
        var where = new[] { a.AssetCode is { } asset ? $"asset {asset}" : null, a.MachineCode is { } m ? $"machine {m}" : null }.Where(x => x is not null).ToList();
        return where.Count == 0 ? string.Empty : $" for {string.Join(", ", where)}";
    }

    private static ReadinessDimension Material(IReadOnlyList<PartReadiness> parts)
    {
        if (parts.Count == 0)
        {
            return new ReadinessDimension("Material", EvidenceOutcome.Pass, "No parts are listed for this work.");
        }

        var missing = parts.Where(p => !p.Covered).ToList();
        if (missing.Count > 0)
        {
            return new ReadinessDimension("Material", EvidenceOutcome.Fail, string.Join(" ", missing.Select(p => p.Detail)));
        }

        var viaAlternate = parts.Where(p => p.Alternate is not null).ToList();
        return new ReadinessDimension("Material", EvidenceOutcome.Pass,
            viaAlternate.Count == 0 ? $"All {parts.Count} part line(s) issued or on hand." : string.Join(" ", parts.Select(p => p.Detail)));
    }

    private static ReadinessDimension Procedure(MaintenanceReadinessRequest r)
    {
        var order = r.Order;
        if (order.ProcedureDocument is not { } number)
        {
            return new ReadinessDimension("Procedure", EvidenceOutcome.Pass, "No procedure is named for this work.");
        }

        if (r.Procedure is not { } document)
        {
            return new ReadinessDimension("Procedure", EvidenceOutcome.Fail, $"Procedure {number} was not found.");
        }

        var revision = document.Revisions.FirstOrDefault(x => x.Revision == order.ProcedureRevision);
        if (revision is null)
        {
            return new ReadinessDimension("Procedure", EvidenceOutcome.Fail, $"{number} has no revision {order.ProcedureRevision}.");
        }

        var on = DateOnly.FromDateTime(r.From.UtcDateTime);
        return revision.Status switch
        {
            DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable when revision.EffectiveFrom is { } from && from > on =>
                new ReadinessDimension("Procedure", EvidenceOutcome.Fail, $"{number} rev {revision.Revision} takes effect {from:yyyy-MM-dd}, after the work would start."),
            DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable when revision.EffectiveTo is { } to && to < on =>
                new ReadinessDimension("Procedure", EvidenceOutcome.Fail, $"{number} rev {revision.Revision} stopped applying {to:yyyy-MM-dd}."),
            DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable =>
                new ReadinessDimension("Procedure", EvidenceOutcome.Pass, $"{number} rev {revision.Revision} ({document.Title}) is {revision.Status.ToString().ToLowerInvariant()}."),
            DocumentRevisionStatus.Superseded or DocumentRevisionStatus.Obsolete =>
                new ReadinessDimension("Procedure", EvidenceOutcome.Fail, $"{number} rev {revision.Revision} is {revision.Status.ToString().ToLowerInvariant()}{(document.Current is { } current ? $"; rev {current.Revision} is in force" : string.Empty)}."),
            _ => new ReadinessDimension("Procedure", EvidenceOutcome.Fail, $"{number} rev {revision.Revision} is {revision.Status} and not released."),
        };
    }

    private static (ReadinessDimension Dimension, IReadOnlyList<string> Names) Technician(MaintenanceReadinessRequest r)
    {
        var required = r.Order.Qualifications;
        if (required.Count == 0)
        {
            return (new ReadinessDimension("Technician", EvidenceOutcome.Pass, "No qualification is required for this work."), []);
        }

        var unavailable = r.Unavailability.Where(u => u.StartsAt < r.Until && u.EndsAt > r.From).Select(u => u.PersonId).ToHashSet();
        var holders = r.People.Where(p => p.Active && required.All(q => p.HoldsAt(q, r.From) && p.HoldsAt(q, r.Until))).ToList();
        var able = holders.Where(p => !unavailable.Contains(p.PersonId)).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var list = string.Join(", ", required);
        if (able.Count > 0)
        {
            var names = able.Select(p => p.Name).ToList();
            return (new ReadinessDimension("Technician", EvidenceOutcome.Pass, $"{names.Count} available person(s) hold {list}: {string.Join(", ", names.Take(5))}{(names.Count > 5 ? ", …" : string.Empty)}."), names);
        }

        var detail = holders.Count > 0
            ? $"Everyone who holds {list} is unavailable during the work ({string.Join(", ", holders.Select(p => p.Name).Order(StringComparer.Ordinal))})."
            : $"Nobody active holds {list} for the whole window" + Missing(r, required) + ".";
        return (new ReadinessDimension("Technician", EvidenceOutcome.Fail, detail), []);
    }

    private static string Missing(MaintenanceReadinessRequest r, IReadOnlyList<string> required)
    {
        var none = required.Where(q => !r.People.Any(p => p.Active && p.HoldsAt(q, r.From))).ToList();
        return none.Count == 0 ? string.Empty : $"; nobody holds {string.Join(", ", none)}";
    }

    private static string Q(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
