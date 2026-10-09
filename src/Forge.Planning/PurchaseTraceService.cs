using System.Globalization;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Engines;
using Forge.Procurement.Contracts;

namespace Forge.Planning;

/// <summary>One ledger entry in a purchase's trace.</summary>
public sealed record TraceEntry(long TransactionId, InventoryTransactionKind Kind, string PartNumber, decimal Quantity, string? LotNumber, string? Location, long? OrderNumber, long? WorkOrderId, DateTimeOffset At);

/// <summary>Why a purchase order line was bought, what arrived and where it went (V2.7).</summary>
public sealed record PurchaseWhy(
    long PoNumber,
    int LineNumber,
    string PartNumber,
    string Supplier,
    decimal Quantity,
    PurchaseSourceKind? SourceKind,
    string? SourceKey,
    string? SourceLabel,
    RequisitionInfo? Requisition,
    DemandInfo? Demand,
    IReadOnlyList<TraceEntry> Receipts,
    IReadOnlyList<TraceEntry> Uses,
    string Answer);

/// <summary>Why the stock in one ledger entry was purchased: the purchase lines it came from.</summary>
public sealed record TransactionWhy(TraceEntry Transaction, IReadOnlyList<PurchaseWhy> Purchases, string Answer);

/// <summary>Where a planned purchase comes from: the requirements at the root of its pegging.</summary>
public sealed record PlannedSource(PurchaseSourceKind Kind, string Key, string Label, long? DemandId, IReadOnlyList<string> Roots);

/// <summary>
/// Answers "why was this purchased?" (V2.7): from a purchase order line (or a receipt or issue of its stock)
/// back to the requisition, its source and the demand behind it, and forward to the receipts and where the
/// stock went. Stock of parts that are not lot-tracked is traced to the receipt only; it is said, not guessed.
/// </summary>
public sealed class PurchaseTraceService(IKernelUnitOfWorkFactory unitOfWork)
{
    public async Task<PurchaseWhy> WhyLineAsync(ForgePrincipal principal, long poNumber, int lineNumber, CancellationToken ct)
    {
        Demand(principal);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return await LineAsync(uow, principal, poNumber, lineNumber, ct)
            ?? throw new CommandRejectedException("line_not_found", $"Purchase order {poNumber} has no line {lineNumber}.");
    }

    public async Task<TransactionWhy> WhyTransactionAsync(ForgePrincipal principal, long transactionId, string partNumber, CancellationToken ct)
    {
        Demand(principal);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var part = (partNumber ?? string.Empty).Trim().ToUpperInvariant();
        var ledger = await uow.Store<IInventoryLookup>().ListTransactionsAsync(part, null, 1000, ct);
        var txn = ledger.FirstOrDefault(t => t.TransactionId == transactionId)
            ?? throw new CommandRejectedException("transaction_not_found", $"Transaction {transactionId} of {part} does not exist.");
        var entry = Entry(txn);
        var lines = new List<(long Po, int Line)>();
        if (txn.PoNumber is { } po && txn.PoLine is { } line)
        {
            lines.Add((po, line));
        }
        else if (txn.LotNumber is { } lot)
        {
            lines.AddRange(ledger.Where(t => t.Kind == InventoryTransactionKind.Receipt && t.LotNumber == lot && t.PoNumber is not null && t.PoLine is not null)
                .Select(t => (t.PoNumber!.Value, t.PoLine!.Value)).Distinct());
        }

        var purchases = new List<PurchaseWhy>();
        foreach (var (poNumber, lineNumber) in lines)
        {
            if (await LineAsync(uow, principal, poNumber, lineNumber, ct) is { } why)
            {
                purchases.Add(why);
            }
        }

        var answer = purchases.Count > 0
            ? string.Join(" ", purchases.Select(p => p.Answer))
            : txn.LotNumber is null && txn.PoNumber is null
                ? $"{part} is not lot-tracked, so this stock cannot be traced to the purchase it came from; receipts record their purchase order line."
                : $"Lot {txn.LotNumber} of {part} was not received against a purchase order.";
        return new TransactionWhy(entry, purchases, answer);
    }

    /// <summary>The root requirements a planned order covers, walking through parent planned orders, and the source they make together.</summary>
    public static PlannedSource SourceOf(PlannedOrder planned, IReadOnlyList<PlannedOrder> all, IReadOnlyDictionary<long, DemandInfo> demands)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(demands);
        var byKey = all.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var roots = new List<MrpPeg>();
        void Walk(PlannedOrder p, int depth)
        {
            foreach (var peg in p.Pegging)
            {
                var parentKey = peg.Kind == "PlannedOrder" ? peg.RequirementKey[..Math.Max(0, peg.RequirementKey.LastIndexOf('/'))] : null;
                if (parentKey is not null && depth < 20 && byKey.TryGetValue(parentKey, out var parent))
                {
                    Walk(parent, depth + 1);
                }
                else
                {
                    roots.Add(peg);
                }
            }
        }

        Walk(planned, 0);
        var kinds = new List<(PurchaseSourceKind Kind, string Key, long? DemandId)>();
        foreach (var peg in roots)
        {
            if (peg.Kind == "Demand" && long.TryParse(peg.RequirementKey.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && demands.TryGetValue(id, out var demand))
            {
                kinds.Add((Enum.Parse<PurchaseSourceKind>(demand.Kind.ToString()), demand.SourceKey ?? $"demand:{id}", id));
            }
            else if (peg.Kind == "MroReplenishment")
            {
                kinds.Add((PurchaseSourceKind.MroReplenishment, peg.RequirementKey, null));
            }
            else if (peg.Kind == "Substitution")
            {
                kinds.Add((PurchaseSourceKind.Production, $"substitution:{peg.RequirementKey}", null));
            }
            else if (peg.Kind == "OrderMaterial")
            {
                var order = peg.RequirementKey[2..peg.RequirementKey.IndexOf('/', StringComparison.Ordinal)];
                kinds.Add((PurchaseSourceKind.Production, $"order:{order}", null));
            }
            else
            {
                kinds.Add((PurchaseSourceKind.SafetyStock, $"stock:{planned.PartNumber}", null));
            }
        }

        var distinct = kinds.Distinct().ToList();
        var labels = roots.Select(r => r.Description).Distinct().ToList();
        var label = string.Join("; ", labels);
        if (label.Length > 480)
        {
            label = label[..480] + "…";
        }

        return distinct.Count == 1
            ? new PlannedSource(distinct[0].Kind, distinct[0].Key, label, distinct[0].DemandId, labels)
            : new PlannedSource(distinct.Select(d => d.Kind).Distinct().Count() == 1 ? distinct[0].Kind : PurchaseSourceKind.Mixed, $"mrp:{planned.Key}@{planned.DueDate:yyyy-MM-dd}", label, null, labels);
    }

    private static async Task<PurchaseWhy?> LineAsync(IKernelUnitOfWork uow, ForgePrincipal principal, long poNumber, int lineNumber, CancellationToken ct)
    {
        var po = await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(poNumber, ct);
        var line = po?.Lines.FirstOrDefault(l => l.LineNumber == lineNumber);
        if (po is null || line is null)
        {
            return null;
        }

        var requisition = line.RequisitionId is { } rid ? await uow.Store<IRequisitionLookup>().FindRequisitionAsync(rid, ct) : null;
        var restricted = requisition?.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program));
        var demand = !restricted && requisition?.DemandId is { } did ? await uow.Store<IDemandStore>().FindAsync(did, ct) : null;
        if (demand?.ProgramId is { } dp && !principal.Programs.Contains(new ProgramId(dp)))
        {
            (demand, restricted) = (null, true);
        }

        var ledger = await uow.Store<IInventoryLookup>().ListTransactionsAsync(line.PartNumber, null, 1000, ct);
        var receipts = ledger.Where(t => t.Kind == InventoryTransactionKind.Receipt && t.PoNumber == poNumber && t.PoLine == lineNumber).OrderBy(t => t.At).ToList();
        var lots = receipts.Where(r => r.LotNumber is not null).Select(r => r.LotNumber!).ToHashSet(StringComparer.Ordinal);
        var uses = ledger.Where(t => t.LotNumber is { } lot && lots.Contains(lot) && t.Kind != InventoryTransactionKind.Receipt).OrderBy(t => t.At).ToList();

        var why = line.SourceKind is null
            ? "No source was recorded (bought before requisitions, or without one)."
            : restricted
                ? $"Bought for {Spaced(line.SourceKind.Value)} work in a program you cannot see."
                : $"Bought for {Spaced(line.SourceKind.Value)} {line.SourceKey}: {line.SourceLabel}{(requisition is null ? string.Empty : $" (requisition {requisition.RequisitionId})")}.";
        var received = receipts.Count == 0 ? "Nothing received yet." : $"Received {Q(receipts.Sum(r => r.Quantity))}{(lots.Count > 0 ? $" in lot {string.Join(", ", lots)}" : string.Empty)}.";
        var went = lots.Count == 0
            ? (receipts.Count == 0 ? string.Empty : $" {line.PartNumber} is not lot-tracked, so where it went is not traced.")
            : uses.Count == 0 ? " Still in stock." : " " + string.Join("; ", uses.Select(Where)) + ".";
        var answer = $"PO {poNumber} line {lineNumber} ({Q(line.Quantity)} {line.PartNumber} from {po.Supplier}): {why} {received}{went}";
        return new PurchaseWhy(poNumber, lineNumber, line.PartNumber, po.Supplier, line.Quantity, line.SourceKind, restricted ? null : line.SourceKey, restricted ? null : line.SourceLabel,
            restricted ? null : requisition, demand, receipts.Select(Entry).ToList(), uses.Select(Entry).ToList(), answer);
    }

    private static string Where(InventoryTransaction t) => t.Kind switch
    {
        InventoryTransactionKind.Issue when t.OrderNumber is { } o => $"{Q(t.Quantity)} issued to order {o}",
        InventoryTransactionKind.MaintenanceIssue => $"{Q(t.Quantity)} issued to work order {t.WorkOrderId}",
        InventoryTransactionKind.MaintenanceReturn => $"{Q(t.Quantity)} returned from work order {t.WorkOrderId}",
        InventoryTransactionKind.Return => $"{Q(t.Quantity)} returned",
        InventoryTransactionKind.Shipment => $"{Q(t.Quantity)} shipped",
        _ => $"{Q(t.Quantity)} {Spaced(t.Kind.ToString()).ToLowerInvariant()}",
    };

    private static TraceEntry Entry(InventoryTransaction t) =>
        new(t.TransactionId, t.Kind, t.PartNumber, t.Quantity, t.LotNumber, t.ToLocation ?? t.FromLocation, t.OrderNumber, t.WorkOrderId, t.At);

    private static void Demand(ForgePrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!principal.HasPermission(Permissions.PurchasingRead) && !principal.HasPermission(Permissions.InventoryRead) && !principal.HasPermission(Permissions.RequisitionRaise))
        {
            throw new ForgeAuthorizationException(new AuthorizationDecision(false, ["Reading why something was purchased needs purchasing, inventory or requisition access."]));
        }
    }

    private static string Spaced<T>(T value)
        where T : struct, Enum => Spaced(value.ToString());

    private static string Spaced(string pascal) =>
        string.Concat(pascal.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : ch.ToString()));

    private static string Q(decimal value) => Quantities.Text(value);
}
