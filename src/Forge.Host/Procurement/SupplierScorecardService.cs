using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Procurement.Contracts;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Host.Procurement;

/// <summary>A receipt measured against its line's promised date.</summary>
public sealed record ScorecardReceipt(long PoNumber, int LineNumber, string PartNumber, string? LotNumber, decimal Quantity, DateOnly Received, DateOnly Promised, int DaysLate, bool OnTime);

/// <summary>A nonconformance on material a supplier delivered.</summary>
public sealed record ScorecardRejection(long NcrId, string PartNumber, string LotNumber, decimal Quantity, string Defect, DateTimeOffset RaisedAt);

/// <summary>One supplier's raw measures and grade for the period. The grade informs a person; it holds nothing.</summary>
public sealed record SupplierScorecard(
    string SupplierCode,
    string SupplierName,
    int Receipts,
    int OnTimeReceipts,
    decimal? OnTimePercent,
    decimal? AverageDaysLate,
    int LateReceipts,
    decimal QuantityReceived,
    decimal QuantityRejected,
    int Nonconformances,
    decimal? QualityPercent,
    decimal? Ppm,
    int PastDueLines,
    string? Grade,
    IReadOnlyList<ScorecardReceipt> Late,
    IReadOnlyList<ScorecardRejection> Rejections);

public sealed record SupplierScorecardReport(DateOnly From, DateOnly To, ScorecardPolicy Policy, IReadOnlyList<SupplierScorecard> Suppliers, IReadOnlyList<string> Notes);

/// <summary>
/// Supplier scorecards (owner decisions 2026-11-01). Delivery: each purchase receipt in the period against its line's promised date, on
/// time per the company's policy. Quality: nonconformances raised in the period on received lots (not ones raised during production,
/// where the cause is not known), as quantity, PPM and percent good. Past due: open lines whose promised date has passed.
/// </summary>
public sealed class SupplierScorecardService(IKernelUnitOfWorkFactory unitOfWork, TimeProvider time, ISiteClock siteClock)
{
    /// <summary>How far back receipts are read to find which supplier delivered a lot named on a nonconformance.</summary>
    public static readonly TimeSpan LotHistory = TimeSpan.FromDays(730);

    public async Task<SupplierScorecardReport> ReportAsync(int? days, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var policy = await uow.Store<IScorecardPolicyLookup>().ScorecardPolicyAsync(ct);
        var period = Math.Clamp(days ?? policy.PeriodDays, 7, 730);
        var now = time.GetUtcNow();
        var from = now - TimeSpan.FromDays(period);
        var today = Local(now);
        var orders = await uow.Store<IProcurementLookup>().ListPurchaseOrdersAsync(ct);
        var suppliers = (await uow.Store<IProcurementLookup>().ListSuppliersAsync(ct)).ToDictionary(s => s.Code, StringComparer.Ordinal);
        var lines = orders.SelectMany(o => o.Lines.Select(l => (Order: o, Line: l))).ToDictionary(x => (x.Order.PoNumber, x.Line.LineNumber));
        string SupplierOf(PurchaseOrderInfo o) => o.SupplierCode ?? o.Supplier;

        var inventory = uow.Store<IInventoryLookup>();
        var receipts = (await inventory.ListTransactionsBetweenAsync(now - LotHistory, now.AddMinutes(1), ct))
            .Where(t => t.Kind == InventoryTransactionKind.Receipt && t.PoNumber is not null && t.PoLine is not null && t.Quantity > 0 && lines.ContainsKey((t.PoNumber.Value, t.PoLine.Value)))
            .ToList();
        var measured = receipts.Where(t => t.At >= from).Select(t =>
        {
            var (order, line) = lines[(t.PoNumber!.Value, t.PoLine!.Value)];
            var received = Local(t.At);
            var late = received.DayNumber - line.PromisedDate.DayNumber;
            return (Supplier: SupplierOf(order), Receipt: new ScorecardReceipt(order.PoNumber, line.LineNumber, t.PartNumber, t.LotNumber, t.Quantity, received, line.PromisedDate, late, policy.OnTime(late)));
        }).ToList();

        // A lot received from more than one supplier cannot be attributed; it is left out and said so.
        var lotSupplier = receipts.Where(t => t.LotNumber is not null)
            .GroupBy(t => (t.PartNumber, Lot: t.LotNumber!))
            .ToDictionary(g => g.Key, g => g.Select(t => SupplierOf(lines[(t.PoNumber!.Value, t.PoLine!.Value)].Order)).Distinct(StringComparer.Ordinal).ToList());
        var ncrs = (await uow.Store<IQualityLookup>().ListNcrsRaisedBetweenAsync(from, now.AddMinutes(1), ct))
            .Where(n => n.ParentNcrId is null && n.OrderNumber is null && n.LotNumber is not null).ToList();
        var rejections = new List<(string Supplier, ScorecardRejection Rejection)>();
        var ambiguous = 0;
        foreach (var n in ncrs)
        {
            if (!lotSupplier.TryGetValue((n.PartNumber, n.LotNumber!), out var who)) continue;
            if (who.Count != 1) { ambiguous++; continue; }
            rejections.Add((who[0], new ScorecardRejection(n.NcrId, n.PartNumber, n.LotNumber!, n.Quantity, n.Defect, n.RaisedAt)));
        }

        var receivedToDate = (await inventory.ListPurchaseReceiptsAsync(ct)).GroupBy(r => (r.PoNumber, r.LineNumber)).ToDictionary(g => g.Key, g => g.Sum(r => r.Received));
        var pastDue = orders.Where(o => o.Status == PurchaseOrderStatus.Approved)
            .SelectMany(o => o.Lines.Where(l => l.Status == PurchaseLineStatus.Open && l.PromisedDate.DayNumber + policy.LateToleranceDays < today.DayNumber
                && receivedToDate.GetValueOrDefault((o.PoNumber, l.LineNumber)) < l.Quantity).Select(_ => SupplierOf(o)))
            .GroupBy(s => s, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var codes = measured.Select(m => m.Supplier).Concat(rejections.Select(r => r.Supplier)).Concat(pastDue.Keys).Distinct(StringComparer.Ordinal);
        var cards = codes.Select(code =>
        {
            var mine = measured.Where(m => m.Supplier == code).Select(m => m.Receipt).ToList();
            var rejected = rejections.Where(r => r.Supplier == code).Select(r => r.Rejection).ToList();
            var onTime = mine.Count(r => r.OnTime);
            var qty = mine.Sum(r => r.Quantity);
            var bad = Math.Min(qty, rejected.Sum(r => r.Quantity));
            decimal? onTimePct = mine.Count == 0 ? null : Math.Round(100m * onTime / mine.Count, 1);
            decimal? qualityPct = qty == 0 ? null : Math.Round(100m * (qty - bad) / qty, 2);
            var lateOnes = mine.Where(r => r.DaysLate > 0).ToList();
            return new SupplierScorecard(code, suppliers.TryGetValue(code, out var s) ? s.Name : code, mine.Count, onTime, onTimePct,
                lateOnes.Count == 0 ? null : Math.Round((decimal)lateOnes.Average(r => r.DaysLate), 1), mine.Count(r => !r.OnTime),
                qty, bad, rejected.Count, qualityPct, qty == 0 ? null : Math.Round(bad / qty * 1_000_000m, 0), pastDue.GetValueOrDefault(code),
                onTimePct is { } t && qualityPct is { } q ? policy.Grade(t, q) : null,
                mine.Where(r => !r.OnTime).OrderByDescending(r => r.Received).Take(25).ToList(), rejected.OrderByDescending(r => r.RaisedAt).ToList());
        }).OrderBy(c => c.Grade ?? "Z", StringComparer.Ordinal).ThenBy(c => c.OnTimePercent ?? 101m).ThenBy(c => c.SupplierCode, StringComparer.Ordinal).ToList();

        var notes = new List<string>
        {
            "Receipts are measured against each line's promised date as it stands now; a date changed with the supplier's agreement counts as the promise.",
            "Quality counts nonconformances raised on received lots, not ones raised during production, where the cause is not yet known.",
            "A grade informs a decision; it does not put a supplier on hold.",
        };
        if (ambiguous > 0) notes.Add($"{ambiguous} nonconformance(s) name a lot received from more than one supplier and are not counted.");
        return new SupplierScorecardReport(Local(from), today, policy, cards, notes);
    }

    private DateOnly Local(DateTimeOffset at) => SiteTime.DateAt(at, siteClock.HomeTimeZone);
}
