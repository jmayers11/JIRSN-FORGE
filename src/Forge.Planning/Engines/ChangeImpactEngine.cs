using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Procurement.Contracts;
using Forge.Quality.Contracts;

namespace Forge.Planning.Engines;

/// <summary>A part the change reaches, and how: its own revision changes, or it is built to a changed document.</summary>
public sealed record ImpactedPart(string PartNumber, string? FromRevision, string? ToRevision, string Reason);

/// <summary>Everything the impact engine looks at, read once from one snapshot.</summary>
public sealed record ChangeImpactInput(
    ChangeInfo Change,
    IReadOnlyList<ImpactedPart> Parts,
    IReadOnlyList<OrderInfo> Orders,
    IReadOnlyList<PurchaseOrderInfo> PurchaseOrders,
    IReadOnlyList<StockBalance> Balances,
    IReadOnlyList<LotInfo> Lots,
    IReadOnlyList<RevisionStructure> Structures,
    IReadOnlyList<InspectionPlanInfo> Plans,
    IReadOnlyList<DocumentRequirementInfo> Requirements,
    Func<Guid?, bool> Visible);

public enum ImpactCategory
{
    /// <summary>Planned or firm planned orders: not released, they will be planned against what is in force.</summary>
    FutureOrder,

    /// <summary>Released orders with no operation started: candidates to move to the new revision.</summary>
    ReleasedNotStarted,

    /// <summary>Released orders with work started: a person decides each one (disposition).</summary>
    WorkInProgress,

    /// <summary>Completed orders: never changed; they keep their frozen copy.</summary>
    Completed,

    /// <summary>Open purchase order lines: the supplier may need the new revision.</summary>
    OpenPurchase,

    /// <summary>Stock on hand, by the revision it was received at.</summary>
    Stock,

    /// <summary>Released assemblies that use the part.</summary>
    WhereUsed,

    /// <summary>Inspection plans that apply to the part.</summary>
    InspectionPlan,

    /// <summary>Documents the part's current revision is built to (work instructions, specifications).</summary>
    Document,
}

public sealed record ImpactRecord(ImpactCategory Category, string PartNumber, string Reference, string Detail, decimal? Quantity, string? Trace);

public sealed record ChangeImpact(string ChangeNumber, IReadOnlyList<ImpactedPart> Parts, IReadOnlyList<ImpactRecord> Records, IReadOnlyDictionary<ImpactCategory, int> Counts, int HiddenOrders, string Summary);

/// <summary>
/// What a change would touch before it is released (review scenario 1): orders by state, open purchases,
/// stock by revision, where-used, inspection plans and documents of each part it reaches. Pure: reports,
/// decides nothing. Orders in programs the reader may not see are counted but never listed.
/// </summary>
public static class ChangeImpactEngine
{
    public const string NotRecorded = "revision not recorded";

    public static ChangeImpact Evaluate(ChangeImpactInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var records = new List<ImpactRecord>();
        var hidden = 0;
        var lots = input.Lots.ToDictionary(l => (l.PartNumber, l.LotNumber));
        foreach (var part in input.Parts)
        {
            foreach (var order in input.Orders.Where(o => o.PartNumber == part.PartNumber && o.Status != OrderStatus.Cancelled).OrderBy(o => o.OrderNumber))
            {
                if (!input.Visible(order.ProgramId))
                {
                    hidden++;
                    continue;
                }

                var started = order.Operations.Any(op => op.Status != OperationStatus.Pending);
                var category = order.Status switch
                {
                    OrderStatus.Planned or OrderStatus.FirmPlanned => ImpactCategory.FutureOrder,
                    OrderStatus.Completed => ImpactCategory.Completed,
                    _ when started => ImpactCategory.WorkInProgress,
                    _ => ImpactCategory.ReleasedNotStarted,
                };
                var detail = category switch
                {
                    ImpactCategory.FutureOrder => $"{order.Status}, rev {order.Revision}, due {order.DueDate:yyyy-MM-dd}: will be planned against the revision in force.",
                    ImpactCategory.ReleasedNotStarted => $"Released at rev {order.Revision}, no operation started: can move to the new revision.",
                    ImpactCategory.WorkInProgress => $"Rev {order.Revision}, {order.Operations.Count(op => op.Status == OperationStatus.Complete)} of {order.Operations.Count} operations done: needs a disposition.",
                    _ => $"Completed at rev {order.Revision}: keeps its frozen copy.",
                };
                records.Add(new ImpactRecord(category, part.PartNumber, $"Order {order.OrderNumber}", detail, order.Quantity, "order:" + order.OrderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            foreach (var po in input.PurchaseOrders.Where(p => p.Status is PurchaseOrderStatus.Draft or PurchaseOrderStatus.Approved))
            {
                foreach (var line in po.Lines.Where(l => l.PartNumber == part.PartNumber && l.Status == PurchaseLineStatus.Open))
                {
                    records.Add(new ImpactRecord(ImpactCategory.OpenPurchase, part.PartNumber, $"PO {po.PoNumber} line {line.LineNumber}", $"{po.Supplier}, {po.Status}, promised {line.PromisedDate:yyyy-MM-dd}: the supplier may need rev {part.ToRevision ?? "the new revision"}.", line.Quantity, null));
                }
            }

            foreach (var group in input.Balances.Where(b => b.PartNumber == part.PartNumber && b.Quantity != 0)
                .GroupBy(b => b.LotNumber is { } lot && lots.TryGetValue((b.PartNumber, lot), out var info) ? info.PartRevision ?? NotRecorded : NotRecorded)
                .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var label = group.Key == NotRecorded ? "Stock with no recorded revision" : $"Stock at rev {group.Key}";
                var note = group.Key == NotRecorded ? "a person dispositions it" : group.Key == part.FromRevision ? "the revision being replaced" : group.Key == part.ToRevision ? "already the new revision" : "another revision";
                records.Add(new ImpactRecord(ImpactCategory.Stock, part.PartNumber, label, $"{group.Sum(b => b.Quantity):0.###} on hand in {string.Join(", ", group.Select(b => b.LocationCode).Distinct())}: {note}.", group.Sum(b => b.Quantity), "item:" + part.PartNumber));
            }

            if (part.ToRevision is not null)
            {
                foreach (var parent in input.Structures.Where(s => s.Revision.EffectiveTo is null && s.Bom.Any(b => b.ComponentPartNumber == part.PartNumber)).OrderBy(s => s.Revision.PartNumber))
                {
                    records.Add(new ImpactRecord(ImpactCategory.WhereUsed, part.PartNumber, $"{parent.Revision.PartNumber} rev {parent.Revision.Revision}", $"Uses {part.PartNumber} in its released bill of materials.", null, "item:" + parent.Revision.PartNumber));
                }
            }

            foreach (var plan in input.Plans.Where(p => p.PartNumber == part.PartNumber && (p.Revision is null || p.Revision == part.FromRevision)))
            {
                records.Add(new ImpactRecord(ImpactCategory.InspectionPlan, part.PartNumber, $"Inspection after op {plan.Sequence}", $"{plan.Title} ({(plan.Revision is null ? "every revision" : "rev " + plan.Revision)}): check it still fits the new revision.", null, null));
            }

            var built = part.FromRevision;
            foreach (var requirement in input.Requirements.Where(r => r.PartNumber == part.PartNumber && r.Revision == built && r.RemovedAt is null))
            {
                records.Add(new ImpactRecord(ImpactCategory.Document, part.PartNumber, requirement.DocumentNumber, $"Rev {built} is built to it{(requirement.OperationSequence is { } op ? $" (op {op})" : string.Empty)}{(requirement.DocumentRevision is { } pinned ? $", pinned to rev {pinned}" : string.Empty)}.", null, "document:" + requirement.DocumentNumber));
            }
        }

        var counts = Enum.GetValues<ImpactCategory>().ToDictionary(c => c, c => records.Count(r => r.Category == c));
        var summary = input.Parts.Count == 0
            ? "The change lists no revisions yet."
            : $"{input.Parts.Count} part(s): {counts[ImpactCategory.FutureOrder]} future order(s), {counts[ImpactCategory.ReleasedNotStarted]} released not started, {counts[ImpactCategory.WorkInProgress]} in progress, "
              + $"{counts[ImpactCategory.Completed]} completed (unchanged), {counts[ImpactCategory.OpenPurchase]} open purchase line(s), {counts[ImpactCategory.WhereUsed]} assembly(ies) using them"
              + (hidden > 0 ? $"; {hidden} order(s) in programs you cannot see." : ".");
        return new ChangeImpact(input.Change.Number, input.Parts, records, counts, hidden, summary);
    }
}
