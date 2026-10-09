using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Engines;
using Forge.Maintenance.Contracts;

namespace Forge.Planning.Engines;

public sealed record ObsolescenceInstall(long ComponentId, string AssetCode, string AssetName, AssetStatus AssetStatus, string? WorkCenterCode, string? MachineCode, string Position, decimal Quantity, long? InstalledByWorkOrder);

public sealed record ObsolescenceOpenPurchase(long PoNumber, int LineNumber, decimal OpenQuantity, DateOnly PromisedDate);

public sealed record ObsolescenceDemand(string Source, string Kind, decimal Quantity, DateOnly NeedDate);

public sealed record ObsolescenceWhereUsed(string ParentPart, string Revision, int LineNumber, decimal QuantityPer);

/// <summary>Everything recorded about one obsolete item, gathered from its owners. No guesses.</summary>
public sealed record ObsolescenceSnapshot(
    ObsolescenceInfo Record,
    string UnitOfMeasure,
    IReadOnlyList<ObsolescenceInstall> Installed,
    decimal SparesAvailable,
    decimal SparesHeld,
    IReadOnlyList<ObsolescenceOpenPurchase> OpenPurchases,
    IReadOnlyList<AlternateInfo> Alternates,
    IReadOnlyList<ObsolescenceWhereUsed> WhereUsed,
    IReadOnlyList<ObsolescenceDemand> Demand,
    DateOnly Today);

public sealed record ObsolescenceImpact(
    long RecordId,
    string PartNumber,
    ObsolescenceStatus Status,
    DateOnly? EndOfSupport,
    bool ActionRequired,
    string Headline,
    IReadOnlyList<ObsolescenceInstall> Installed,
    IReadOnlyList<string> Assets,
    IReadOnlyList<string> WorkCenters,
    decimal InstalledQuantity,
    decimal SparesAvailable,
    decimal SparesHeld,
    decimal OnOrder,
    IReadOnlyList<ObsolescenceOpenPurchase> OpenPurchases,
    IReadOnlyList<string> ApprovedAlternates,
    IReadOnlyList<ObsolescenceWhereUsed> WhereUsed,
    IReadOnlyList<ObsolescenceDemand> Demand,
    string Replacement,
    IReadOnlyList<string> Findings);

/// <summary>
/// "Obsolescence action required" (V2.8, scenario 7): what an end-of-life item touches — the assets and work
/// centers it is installed in, spares, open purchases, approved alternates, where it is used in released bills
/// of materials and the demand still asking for it. It states facts and counts; it gives no score and never
/// names a replacement nobody recorded. Whether to act (for example open a capital project) is for people.
/// </summary>
public static class ObsolescenceImpactEngine
{
    public static ObsolescenceImpact Evaluate(ObsolescenceSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var r = s.Record;
        var uom = s.UnitOfMeasure;
        var installed = s.Installed.Where(i => i.AssetStatus != AssetStatus.Decommissioned).OrderBy(i => i.AssetCode, StringComparer.Ordinal).ThenBy(i => i.ComponentId).ToList();
        var installedQty = installed.Sum(i => i.Quantity);
        var assets = installed.Select(i => i.AssetCode).Distinct().ToList();
        var workCenters = installed.Where(i => i.WorkCenterCode is not null).Select(i => i.WorkCenterCode!).Distinct().Order(StringComparer.Ordinal).ToList();
        var onOrder = s.OpenPurchases.Sum(p => p.OpenQuantity);
        var approved = s.Alternates
            .Where(a => a.Status == AlternateStatus.Approved && a.PrimaryPart == r.PartNumber && (a.ValidTo is null || a.ValidTo >= s.Today))
            .Select(a => $"{a.AlternatePart} ({Alternates.Label(a.Kind)}, {a.Conditions})")
            .ToList();
        var demandQty = s.Demand.Sum(d => d.Quantity);

        var replacement = r.Replacement switch
        {
            ReplacementStatus.Approved => $"{r.ReplacementPart} approved by {r.ReplacementDecidedBy}",
            ReplacementStatus.Proposed => $"{r.ReplacementPart} proposed by {r.ReplacementProposedBy}, not yet approved",
            ReplacementStatus.Rejected => $"None approved ({r.ReplacementPart} was rejected)",
            _ => "None recorded",
        };

        var findings = new List<string>
        {
            $"{r.PartNumber} is {Label(r.Status)}"
                + (r.LastTimeBuy is { } ltb ? $"; last-time buy {Day(ltb)}" : string.Empty)
                + (r.EndOfLife is { } eol ? $"; end of life {Day(eol)}" : string.Empty)
                + (r.EndOfSupport is { } eos ? $"; end of support {Day(eos)}" : string.Empty)
                + $" (evidence: {r.Evidence}).",
            installed.Count == 0
                ? "Not installed in any asset in service."
                : $"Installed: {Q(installedQty)} {uom} in {assets.Count} asset(s) — {string.Join("; ", installed.Select(i => $"{i.AssetCode} {i.Position} ×{Q(i.Quantity)}"))}"
                    + (workCenters.Count > 0 ? $"; work centers {string.Join(", ", workCenters)}." : "."),
            $"Spares: {Q(s.SparesAvailable)} {uom} available" + (s.SparesHeld > 0 ? $", {Q(s.SparesHeld)} in inspection or on hold." : "."),
            s.OpenPurchases.Count == 0
                ? "No open purchase order lines."
                : $"On order: {Q(onOrder)} {uom} — {string.Join("; ", s.OpenPurchases.Select(p => $"PO {p.PoNumber} line {p.LineNumber}, {Q(p.OpenQuantity)} due {Day(p.PromisedDate)}"))}.",
            approved.Count == 0 ? "Approved alternates: none." : $"Approved alternates: {string.Join("; ", approved)}.",
            s.WhereUsed.Count == 0
                ? "Not used in any released bill of materials."
                : $"Used in released bills of materials: {string.Join("; ", s.WhereUsed.Select(w => $"{w.ParentPart} rev {w.Revision} line {w.LineNumber} ({Q(w.QuantityPer)} per)"))}.",
            s.Demand.Count == 0
                ? "No open demand or requisitions ask for it."
                : $"Still asked for: {Q(demandQty)} {uom} — {string.Join("; ", s.Demand.OrderBy(d => d.NeedDate).Select(d => $"{d.Kind} {d.Source} {Q(d.Quantity)} by {Day(d.NeedDate)}"))}.",
            $"Replacement: {replacement}.",
        };

        var uses = installed.Count > 0 || s.WhereUsed.Count > 0 || s.Demand.Count > 0;
        var action = r.Flagged && uses;
        string headline;
        if (action)
        {
            var cover = s.SparesAvailable + onOrder;
            headline = $"Obsolescence action required: {r.PartNumber} — installed {Q(installedQty)}, spares {Q(s.SparesAvailable)}, on order {Q(onOrder)}, "
                + $"approved alternates {(approved.Count == 0 ? "none" : approved.Count.ToString(CultureInfo.InvariantCulture))}, replacement {replacement.ToLowerInvariant()}.";
            if (installedQty > 0 && cover < installedQty)
            {
                findings.Add($"Spares and open orders ({Q(cover)}) would not replace what is installed ({Q(installedQty)}) once.");
            }
        }
        else if (!r.Flagged)
        {
            headline = $"{r.PartNumber}: replacement {r.ReplacementPart} approved. Assets, bills of materials and alternates still change only through their own records.";
        }
        else
        {
            headline = $"{r.PartNumber} is {Label(r.Status)}, and nothing recorded uses it now.";
        }

        return new ObsolescenceImpact(r.RecordId, r.PartNumber, r.Status, r.EndOfSupport, action, headline, installed, assets, workCenters, installedQty,
            s.SparesAvailable, s.SparesHeld, onOrder, s.OpenPurchases, approved, s.WhereUsed, s.Demand, replacement, findings);
    }

    private static string Label(ObsolescenceStatus status) => status switch
    {
        ObsolescenceStatus.NotRecommended => "not recommended for new designs",
        ObsolescenceStatus.LastTimeBuy => "at last-time buy",
        ObsolescenceStatus.EndOfLife => "at end of life",
        _ => "obsolete",
    };

    private static string Q(decimal value) => Quantities.Text(value);

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
