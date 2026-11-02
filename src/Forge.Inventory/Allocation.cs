using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Inventory;

/// <summary>A planner or inventory controller accepts one line of an allocation proposal: it becomes a reservation for the order.</summary>
/// <param name="AlternateFor">The BOM part an alternate is reserved in place of; null for the part itself.</param>
/// <param name="WipOrderNumber">For a work-in-progress line: the open order whose output is promised to this order.</param>
/// <param name="LotNumber">For a size-matched line: the lot (often a remnant) set aside for the order.</param>
public sealed record ApproveAllocation(long OrderNumber, string PartNumber, decimal Quantity, string? AlternateFor, long? WipOrderNumber = null, string? LotNumber = null);

public sealed record SetAllocationPolicy(IReadOnlyList<AllocationRule> Rules);

/// <summary>A planner releases a promise of work-in-progress output, with a reason.</summary>
public sealed record ReleaseWipAllocation(long AllocationId);

/// <summary>A planner releases a lot set aside for an order, with a reason; the reservation stays, only the specific lot is freed.</summary>
public sealed record ReleaseLotEarmark(long EarmarkId);

public sealed record AllocationPolicyChanged(string Rules) : IDomainEvent
{
    public static string EventType => nameof(AllocationPolicyChanged);

    public static int SchemaVersion => 1;
}

public sealed record WipAllocationChanged(long AllocationId, long WipOrderNumber, long ConsumerOrderNumber, string PartNumber, string Change) : IDomainEvent
{
    public static string EventType => nameof(WipAllocationChanged);

    public static int SchemaVersion => 1;
}

public interface IAllocationStore
{
    Task InsertAllocationPolicyAsync(AllocationPolicy policy, CancellationToken ct);

    Task InsertReservationAlternateAsync(ReservationAlternate alternate, CancellationToken ct);

    Task UpsertLotDimensionAsync(LotDimension dimension, CancellationToken ct);

    Task UpsertPieceSizeRuleAsync(PieceSizeRule rule, CancellationToken ct);

    Task<bool> DeletePieceSizeRuleAsync(string parentPart, string componentPart, CancellationToken ct);

    Task<long> InsertLotEarmarkAsync(LotEarmark earmark, CancellationToken ct);

    Task<bool> ReleaseLotEarmarkAsync(long earmarkId, string actor, DateTimeOffset at, string reason, CancellationToken ct);

    Task<long> InsertWipAllocationAsync(WipAllocation allocation, CancellationToken ct);

    Task UpdateWipAllocationAsync(long allocationId, decimal fulfilled, WipAllocationStatus status, string? closedBy, DateTimeOffset? closedAt, string? reason, CancellationToken ct);
}

/// <summary>
/// The V5 material allocation optimizer (owner decisions 2026-11-02). It only proposes. For each released order that is short, ranked by
/// the plant's rules (by default due date, then priority, then the smallest shortage), it offers in turn: free stock (released, usable,
/// not reserved for anyone), and where the BOM line sets a minimum piece size, the smallest lot that fits (leftover pieces first); then
/// output of open production orders of the part that no customer or forecast demand is counting on, finishing every remaining
/// operation and inspection; then approved alternates the BOM line allows. It never takes stock that is reserved or set aside for other
/// work, and every line says why. A person approves a line.
/// </summary>
public static class AllocationOptimizer
{
    private sealed record Need(OrderInfo Order, string Part, decimal Shortage, SubstitutionPolicy Policy, PieceSizeRule? Size, string? ComponentRevision);

    private sealed record Piece(string? LotNumber, decimal Free, decimal Measure, PieceSize? Size, string Label);

    public static Task<IReadOnlyList<AllocationProposal>> ProposeAsync(IKernelUnitOfWork uow, DateTimeOffset now, CancellationToken ct) => ProposeAsync(uow, now, null, ct);

    public static async Task<IReadOnlyList<AllocationProposal>> ProposeAsync(IKernelUnitOfWork uow, DateTimeOffset now, IWipDemandSource? demand, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var execution = uow.Store<IExecutionLookup>();
        var engineering = uow.Store<IEngineeringLookup>();
        var lookup = uow.Store<IAllocationLookup>();
        var policy = await lookup.AllocationPolicyAsync(ct);
        var issued = (await uow.Store<IInventoryLookup>().ListOrderIssuesAsync(ct)).ToDictionary(i => (i.OrderNumber, i.PartNumber), i => i.NetIssued);
        var outstanding = await StockReservationQueries.OutstandingAsync(uow, null, ct);
        var alternates = (await lookup.ListReservationAlternatesAsync(ct)).ToDictionary(a => a.ReservationId);
        var promised = await OpenWipAllocationsAsync(uow, ct);
        var sizeRules = (await lookup.ListPieceSizeRulesAsync(ct)).ToDictionary(r => (r.ParentPart, r.ComponentPart));
        var today = await uow.SiteDateAsync(now, ct);

        // What each released order is still short of, after issues, its own reservations (alternates counted at their factor) and
        // work-in-progress output already promised to it.
        var needs = new List<Need>();
        foreach (var summary in (await execution.ListOrdersAsync(ct)).Where(o => o.Status == OrderStatus.Released))
        {
            if (await execution.FindOrderAsync(summary.OrderNumber, ct) is not { } order) continue;
            var bom = (await engineering.GetRevisionDetailAsync(order.PartNumber, order.Revision, ct))?.Bom ?? [];
            foreach (var group in order.Materials.GroupBy(m => m.ComponentPartNumber, StringComparer.Ordinal))
            {
                var mine = outstanding.Where(b => b.Reservation.Target == ReservationTarget.ProductionOrder && b.Reservation.OrderNumber == order.OrderNumber).ToList();
                var direct = mine.Where(b => b.Reservation.PartNumber == group.Key && !alternates.ContainsKey(b.Reservation.ReservationId)).Sum(b => b.Outstanding);
                var viaAlternates = mine.Where(b => alternates.TryGetValue(b.Reservation.ReservationId, out var a) && a.PrimaryPart == group.Key)
                    .Sum(b => b.Outstanding / alternates[b.Reservation.ReservationId].Factor);
                var viaWip = promised.Where(w => w.ConsumerOrderNumber == order.OrderNumber && w.PartNumber == group.Key).Sum(w => w.Outstanding);
                var shortage = Math.Round(group.Sum(m => m.RequiredQuantity) - issued.GetValueOrDefault((order.OrderNumber, group.Key)) - direct - viaAlternates - viaWip, 4, MidpointRounding.AwayFromZero);
                if (shortage <= 0) continue;
                var line = bom.FirstOrDefault(b => b.ComponentPartNumber == group.Key);
                needs.Add(new Need(order, group.Key, shortage, line?.Policy ?? SubstitutionPolicy.Suggest, sizeRules.GetValueOrDefault((order.PartNumber, group.Key)),
                    group.Select(m => m.ComponentRevision).FirstOrDefault(r => r is not null)));
            }
        }

        if (needs.Count == 0) return [];
        var allAlternates = await uow.Store<IAlternateLookup>().ListAlternatesAsync(ct);
        var earmarks = (await lookup.ListLotEarmarksAsync(true, ct)).Where(e => e.Outstanding > 0).ToList();
        var free = new Dictionary<string, decimal>(StringComparer.Ordinal);
        async Task<decimal> FreeAsync(string part)
        {
            if (!free.TryGetValue(part, out var f))
            {
                var available = await StockReservationQueries.AvailableAsync(uow, part, now, ct);
                f = Math.Max(0, available - outstanding.Where(b => b.Reservation.PartNumber == part).Sum(b => b.Outstanding));
                free[part] = f;
            }

            return f;
        }

        var proposals = new List<AllocationProposal>();
        foreach (var byPart in needs.GroupBy(n => n.Part, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var part = byPart.Key;
            var ranked = Rank(byPart, policy.Rules).ToList();
            var onHand = (await uow.Store<IInventoryLookup>().ListBalancesAsync(part, ct)).Where(b => b.PartNumber == part).Sum(b => b.Quantity);
            var available = await StockReservationQueries.AvailableAsync(uow, part, now, ct);
            var reservedForOthers = outstanding.Where(b => b.Reservation.PartNumber == part).Sum(b => b.Outstanding);
            var startFree = await FreeAsync(part);
            var lines = new List<AllocationLine>();
            var gaps = new List<AllocationGap>();
            var ruledOut = new List<string>();
            if (onHand > available)
                ruledOut.Add(string.Create(CultureInfo.InvariantCulture, $"{onHand - available:0.####} {part} on hand is on hold, expired, in inspection or otherwise not usable."));
            if (reservedForOthers > 0)
                ruledOut.Add(string.Create(CultureInfo.InvariantCulture, $"{reservedForOthers:0.####} {part} is reserved for other work and is never taken from it."));
            var setAside = earmarks.Where(e => e.PartNumber == part).ToList();
            if (setAside.Count > 0)
                ruledOut.Add($"Lot(s) {string.Join(", ", setAside.Select(e => e.LotNumber).Distinct())} of {part} are set aside for order(s) {string.Join(", ", setAside.Select(e => e.OrderNumber).Distinct())}.");

            var pieces = ranked.Any(n => n.Size is not null) ? await PiecesAsync(uow, part, setAside, now, ruledOut, ct) : [];
            var pieceUsed = new Dictionary<string, decimal>(StringComparer.Ordinal);
            IReadOnlyList<WipSupply>? wip = null;
            var wipUsed = new Dictionary<long, decimal>();
            var rule = string.Join(", then ", policy.Rules.Select(Words));
            for (var i = 0; i < ranked.Count; i++)
            {
                var need = ranked[i];
                var remaining = need.Shortage;
                var why = string.Create(CultureInfo.InvariantCulture,
                    $"Ranked {i + 1} of {ranked.Count} orders short of {need.Part} ({rule}): due {need.Order.DueDate:yyyy-MM-dd}, priority {need.Order.Priority}, short {need.Shortage:0.####}.");
                if (need.Size is { } size)
                {
                    // The BOM line sets a minimum piece size: only lots that fit, smallest first, so leftover pieces are used before full stock is cut.
                    foreach (var piece in pieces)
                    {
                        if (remaining <= 0 || free[part] <= 0) break;
                        if (piece.Size is { } measured && !PieceFit.Fits(measured, size.Minimum, out var notFit))
                        {
                            ruledOut.Add($"Lot {piece.LotNumber} for order {need.Order.OrderNumber}: {notFit}.");
                            continue;
                        }

                        var key = piece.LotNumber ?? string.Empty;
                        var take = Math.Min(Math.Min(remaining, piece.Free - pieceUsed.GetValueOrDefault(key)), free[part]);
                        if (take <= 0) continue;
                        lines.Add(new AllocationLine(need.Order.OrderNumber, need.Order.DueDate, need.Order.Priority, i + 1, need.Part, need.Shortage, need.Part, take, take, AllocationMode.Primary, null,
                            why + $" The job needs a piece at least {size.Minimum.Describe()}; {(piece.LotNumber is null ? "stock without lots" : "lot " + piece.LotNumber)} is the smallest that fits ({piece.Label}).",
                            LotNumber: piece.LotNumber));
                        pieceUsed[key] = pieceUsed.GetValueOrDefault(key) + take;
                        free[part] -= take;
                        remaining -= take;
                    }

                    if (remaining == need.Shortage && free[part] > 0)
                        ruledOut.Add($"Order {need.Order.OrderNumber} needs a piece at least {size.Minimum.Describe()}; no free lot of {part} fits.");
                }
                else
                {
                    var take = Math.Min(remaining, free[part]);
                    if (take > 0)
                    {
                        lines.Add(new AllocationLine(need.Order.OrderNumber, need.Order.DueDate, need.Order.Priority, i + 1, need.Part, need.Shortage, need.Part, take, take, AllocationMode.Primary, null, why));
                        free[part] -= take;
                        remaining -= take;
                    }
                }

                if (remaining <= 0) continue;

                // Work in progress: output of open orders of the part that no demand is counting on. The order finishes every remaining operation,
                // inspections included; only where its output goes changes.
                wip ??= await WipSupplyAsync(uow, part, now, demand, ct);
                foreach (var w in wip)
                {
                    if (remaining <= 0) break;
                    var left = w.Unpegged - wipUsed.GetValueOrDefault(w.OrderNumber);
                    if (left <= 0) continue;
                    if (w.Status == nameof(OrderStatus.Held))
                    {
                        ruledOut.Add($"Order {w.OrderNumber} ({part}) is on hold; its output is not offered.");
                        continue;
                    }

                    if (w.ProgramId is { } program && program != need.Order.ProgramId)
                    {
                        ruledOut.Add($"Order {w.OrderNumber} ({part}) is program work and does not go to order {need.Order.OrderNumber}.");
                        continue;
                    }

                    if (need.ComponentRevision is { } rev && !string.Equals(rev, w.Revision, StringComparison.Ordinal))
                    {
                        ruledOut.Add($"Order {w.OrderNumber} makes {part} rev {w.Revision}; order {need.Order.OrderNumber} needs rev {rev}.");
                        continue;
                    }

                    var take = Math.Min(remaining, left);
                    var late = w.DueDate > need.Order.DueDate
                        ? string.Create(CultureInfo.InvariantCulture, $" It is due {w.DueDate:yyyy-MM-dd}, {w.DueDate.DayNumber - need.Order.DueDate.DayNumber} day(s) after order {need.Order.OrderNumber}: move it up or order {need.Order.OrderNumber} waits.")
                        : string.Create(CultureInfo.InvariantCulture, $" It is due {w.DueDate:yyyy-MM-dd}.");
                    lines.Add(new AllocationLine(need.Order.OrderNumber, need.Order.DueDate, need.Order.Priority, i + 1, need.Part, need.Shortage, need.Part, take, take, AllocationMode.WorkInProgress, null,
                        why + string.Create(CultureInfo.InvariantCulture, $" Order {w.OrderNumber} ({w.Status}, rev {w.Revision}) will make {w.Unpegged:0.####} no customer or forecast demand is counting on.")
                            + late + (w.RemainingOperations.Count == 0 ? " Its operations are done; it is waiting to be received." : $" It still runs every remaining operation, inspections included: {string.Join("; ", w.RemainingOperations)}."),
                        WipOrderNumber: w.OrderNumber));
                    wipUsed[w.OrderNumber] = wipUsed.GetValueOrDefault(w.OrderNumber) + take;
                    remaining -= take;
                }

                if (remaining <= 0) continue;
                // What free stock and work in progress cannot cover: approved alternates, where the BOM line allows them.
                if (need.Policy == SubstitutionPolicy.Never)
                {
                    gaps.Add(new AllocationGap(need.Order.OrderNumber, need.Part, remaining, "No free stock or unpromised work in progress left, and the BOM line does not allow alternates."));
                    continue;
                }

                var context = new AlternateContext(today, need.Order.PartNumber, need.Order.ProgramId);
                foreach (var (relationship, offered, factor) in Alternates.For(allAlternates, need.Part))
                {
                    if (remaining <= 0) break;
                    var (usability, reasons) = Alternates.Evaluate(relationship, context);
                    if (usability != AlternateUsability.Usable)
                    {
                        ruledOut.Add($"Alternate {offered} for order {need.Order.OrderNumber}: {string.Join(" ", reasons)}");
                        continue;
                    }

                    var altFree = await FreeAsync(offered);
                    if (altFree <= 0)
                    {
                        ruledOut.Add($"Alternate {offered} is approved but has no free stock.");
                        continue;
                    }

                    var cover = Math.Min(remaining, Math.Round(altFree / factor, 4, MidpointRounding.ToZero));
                    if (cover <= 0) continue;
                    var quantity = Math.Round(cover * factor, 4, MidpointRounding.AwayFromZero);
                    var automatic = need.Policy == SubstitutionPolicy.Automatic && Alternates.IsEquivalent(relationship.Kind);
                    lines.Add(new AllocationLine(need.Order.OrderNumber, need.Order.DueDate, need.Order.Priority, i + 1, need.Part, need.Shortage, offered, quantity, cover,
                        automatic ? AllocationMode.AutomaticAlternate : AllocationMode.ReviewAlternate, relationship.AlternateId,
                        why + string.Create(CultureInfo.InvariantCulture, $" No free {need.Part} left; approved alternate {offered} ({relationship.Kind}, {factor:0.####} per unit{(relationship.Conditions.Length > 0 ? ", " + relationship.Conditions : string.Empty)}) ")
                            + (automatic ? "which the BOM line allows automatically." : "which the BOM line only suggests: review it before use.")
                            + (need.Size is null ? string.Empty : " Check the piece size before use.")));
                    free[offered] = altFree - quantity;
                    remaining -= cover;
                }

                if (remaining > 0)
                    gaps.Add(new AllocationGap(need.Order.OrderNumber, need.Part, remaining, "No free stock, no unpromised work in progress and no usable alternate with free stock: buy or make it."));
            }

            // What each line means for the orders ranked below it.
            var impacted = lines.Select(l =>
            {
                var below = gaps.Where(g => ranked.FindIndex(r => r.Order.OrderNumber == g.OrderNumber) + 1 > l.Rank).Select(g => g.OrderNumber).Distinct().ToList();
                return below.Count == 0 ? l : l with { Why = l.Why + $" Orders ranked lower that stay short: {string.Join(", ", below)}." };
            }).ToList();
            var covered = promised.Where(w => w.PartNumber == part && w.Outstanding > 0).ToList();
            proposals.Add(new AllocationProposal(part, onHand, Math.Max(0, onHand - available), reservedForOthers, startFree, impacted, gaps, ruledOut.Distinct().ToList(), covered));
        }

        return proposals;
    }

    /// <summary>Promises of work-in-progress output whose order is still open (a cancelled order's promise counts for nothing).</summary>
    public static async Task<IReadOnlyList<WipAllocation>> OpenWipAllocationsAsync(IKernelUnitOfWork uow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var execution = uow.Store<IExecutionLookup>();
        var list = new List<WipAllocation>();
        foreach (var w in await uow.Store<IAllocationLookup>().ListWipAllocationsAsync(true, ct))
            if (await execution.FindOrderAsync(w.WipOrderNumber, ct) is { Status: not OrderStatus.Cancelled }) list.Add(w);
        return list;
    }

    /// <summary>
    /// Open production orders of a part and where their output is going: free stock first, then these orders by due date, cover open
    /// customer and forecast demand for the part; contract work stays with its contract; what is left and not already promised to an
    /// order is unpegged and may be offered.
    /// </summary>
    public static async Task<IReadOnlyList<WipSupply>> WipSupplyAsync(IKernelUnitOfWork uow, string partNumber, DateTimeOffset now, IWipDemandSource? demand, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var execution = uow.Store<IExecutionLookup>();
        var received = (await uow.Store<IInventoryLookup>().ListProductionReceiptsAsync(ct)).GroupBy(r => r.OrderNumber).ToDictionary(g => g.Key, g => g.Sum(r => r.Received));
        var promised = (await uow.Store<IAllocationLookup>().ListWipAllocationsAsync(true, ct)).GroupBy(w => w.WipOrderNumber).ToDictionary(g => g.Key, g => g.Sum(w => w.Outstanding));
        var orders = new List<OrderInfo>();
        foreach (var header in (await execution.ListOrdersAsync(ct)).Where(o => o.PartNumber == partNumber && o.Status is OrderStatus.FirmPlanned or OrderStatus.Released or OrderStatus.Held))
            if (await execution.FindOrderAsync(header.OrderNumber, ct) is { } order) orders.Add(order);
        if (orders.Count == 0) return [];

        // Open demand, after what free stock covers.
        var outstanding = await StockReservationQueries.OutstandingAsync(uow, partNumber, ct);
        var freeStock = Math.Max(0, await StockReservationQueries.AvailableAsync(uow, partNumber, now, ct) - outstanding.Sum(b => b.Outstanding));
        var open = new Queue<(string Reference, decimal Left)>();
        foreach (var d in (demand is null ? [] : await demand.OpenDemandAsync(uow, partNumber, ct)).OrderBy(d => d.NeedDate).ThenBy(d => d.DemandId))
        {
            var left = d.Quantity;
            var fromStock = Math.Min(left, freeStock);
            freeStock -= fromStock;
            left -= fromStock;
            if (left > 0) open.Enqueue((string.Create(CultureInfo.InvariantCulture, $"{d.Reference} (due {d.NeedDate:yyyy-MM-dd})"), left));
        }

        var list = new List<WipSupply>();
        foreach (var order in orders.OrderBy(o => o.DueDate).ThenBy(o => o.OrderNumber))
        {
            var scrap = order.Operations.Sum(op => op.ScrapQuantity);
            var projected = Math.Max(0, order.Quantity - scrap - received.GetValueOrDefault(order.OrderNumber));
            var promisedHere = Math.Min(projected, promised.GetValueOrDefault(order.OrderNumber));
            var pegged = 0m;
            var to = new List<string>();
            if (order.ContractId is { } contract)
            {
                pegged = projected - promisedHere;
                if (pegged > 0) to.Add(string.Create(CultureInfo.InvariantCulture, $"contract {contract}{(order.ContractClin is null ? string.Empty : " CLIN " + order.ContractClin)}"));
            }
            else
            {
                var spare = projected - promisedHere;
                while (spare > 0 && open.Count > 0)
                {
                    var (reference, left) = open.Dequeue();
                    var take = Math.Min(spare, left);
                    pegged += take;
                    spare -= take;
                    to.Add(string.Create(CultureInfo.InvariantCulture, $"{take:0.####} for {reference}"));
                    if (left > take)
                    {
                        // Put the rest back at the front.
                        var rest = new Queue<(string, decimal)>([(reference, left - take), .. open]);
                        open.Clear();
                        foreach (var x in rest) open.Enqueue(x);
                    }
                }
            }

            var remaining = order.Operations.Where(op => op.Status != OperationStatus.Complete).OrderBy(op => op.Sequence)
                .Select(op => string.Create(CultureInfo.InvariantCulture, $"{op.Sequence} {op.Description}{(op.Status == OperationStatus.InProgress ? " (in progress)" : string.Empty)}")).ToList();
            list.Add(new WipSupply(order.OrderNumber, order.PartNumber, order.Revision, order.Status.ToString(), order.DueDate, order.ProgramId, projected, pegged, promisedHere,
                Math.Max(0, projected - promisedHere - pegged), remaining, to));
        }

        return list;
    }

    /// <summary>Free lots of a part that a size-limited job could use, smallest first: measured lots, then stock with no size recorded.</summary>
    private static async Task<List<Piece>> PiecesAsync(IKernelUnitOfWork uow, string part, IReadOnlyList<LotEarmark> setAside, DateTimeOffset now, List<string> ruledOut, CancellationToken ct)
    {
        var dims = (await uow.Store<IAllocationLookup>().ListLotDimensionsAsync(part, ct)).ToDictionary(d => d.LotNumber, StringComparer.Ordinal);
        var inventory = uow.Store<IInventoryLookup>();
        var pieces = new List<Piece>();
        foreach (var lot in (await ShelfLifeQueries.PositionsAsync(uow, part, now, ct)).Where(p => p.PartNumber == part && p.AvailableForPlanning && p.Quantity > 0)
                     .GroupBy(p => p.LotNumber).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var quantity = lot.Sum(p => p.Quantity) - (lot.Key is null ? 0 : setAside.Where(e => e.LotNumber == lot.Key).Sum(e => e.Outstanding));
            if (quantity <= 0) continue;
            if (lot.Key is not null && dims.TryGetValue(lot.Key, out var d))
            {
                var remnant = await inventory.IsRecordedRemnantAsync(part, lot.Key, ct);
                pieces.Add(new Piece(lot.Key, quantity, PieceFit.Measure(d.Size), d.Size, $"{(remnant ? "leftover piece" : "measured lot")}, {d.Size.Describe()}"));
            }
            else if (lot.Key is not null && await inventory.IsRecordedRemnantAsync(part, lot.Key, ct))
            {
                ruledOut.Add($"Leftover lot {lot.Key} of {part} has no size recorded: measure it so it can be offered.");
            }
            else
            {
                pieces.Add(new Piece(lot.Key, quantity, decimal.MaxValue, null, "full stock, no size recorded, taken to fit"));
            }
        }

        return pieces.OrderBy(p => p.Measure).ThenBy(p => p.LotNumber, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<Need> Rank(IEnumerable<Need> needs, IReadOnlyList<AllocationRule> rules)
    {
        IOrderedEnumerable<Need>? ordered = null;
        foreach (var rule in rules)
        {
            Func<Need, IComparable> key = rule switch
            {
                AllocationRule.DueDate => n => n.Order.DueDate,
                AllocationRule.Priority => n => n.Order.Priority,
                AllocationRule.SmallestShortage => n => n.Shortage,
                _ => n => n.Order.ReleasedAt ?? DateTimeOffset.MaxValue,
            };
            ordered = ordered is null ? needs.OrderBy(key) : ordered.ThenBy(key);
        }

        return (ordered ?? needs.OrderBy(n => n.Order.DueDate)).ThenBy(n => n.Order.OrderNumber);
    }

    public static string Words(AllocationRule rule) => rule switch
    {
        AllocationRule.DueDate => "earliest due date",
        AllocationRule.Priority => "highest priority",
        AllocationRule.SmallestShortage => "smallest shortage",
        _ => "released first",
    };
}

/// <summary>Turns approved proposal lines into reservations, promises and set-asides, and keeps the plant's ranking rules (owner decisions 2026-11-02).</summary>
public sealed class AllocationService(CommandPipeline pipeline, IWipDemandSource? demand = null)
{
    private const string Entity = "StockReservation";

    public Task<CommandResult> ApproveAsync(ApproveAllocation c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var orderKey = c.OrderNumber.ToString(CultureInfo.InvariantCulture);
        var lotNumber = string.IsNullOrWhiteSpace(c.LotNumber) ? null : c.LotNumber.Trim();
        var spec = new CommandSpec(InventoryService.SourceContext, nameof(ApproveAllocation), Permissions.AllocationApprove, ResourceRef.Of("ProductionOrder", orderKey));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (c.Quantity <= 0) throw new CommandRejectedException("invalid_quantity", "Quantity must be above zero.");
            await uow.Store<IStockReservationStore>().LockPartAsync(part, ct);
            var primary = string.IsNullOrWhiteSpace(c.AlternateFor) ? part : BusinessCode.Require(c.AlternateFor, "Part");
            // Proposals are read again now: stock, work in progress and shortages may have moved since the planner looked.
            var proposal = (await AllocationOptimizer.ProposeAsync(uow, now, demand, ct)).FirstOrDefault(p => p.PartNumber == primary);
            var line = proposal?.Lines.FirstOrDefault(l => l.OrderNumber == c.OrderNumber && l.OfferedPart == part && l.WipOrderNumber == c.WipOrderNumber && l.LotNumber == lotNumber)
                ?? throw new CommandRejectedException("not_proposed", $"Nothing of {part} is proposed for order {orderKey} {(c.WipOrderNumber is { } w ? $"from order {w} " : lotNumber is null ? string.Empty : $"from lot {lotNumber} ")}now; look at the proposals again.");
            if (c.Quantity > line.Quantity)
                throw new CommandRejectedException("over_proposal", string.Create(CultureInfo.InvariantCulture, $"Only {line.Quantity:0.####} {part} can go to order {orderKey} now."));
            if (line.Mode == AllocationMode.WorkInProgress)
                return await PromiseAsync(uow, c, line, context, now, ct);

            var store = uow.Store<IStockReservationStore>();
            var alternates = (await uow.Store<IAllocationLookup>().ListReservationAlternatesAsync(ct)).Select(a => a.ReservationId).ToHashSet();
            var existing = (await uow.Store<IStockReservationLookup>().ListReservationsAsync(ReservationTarget.ProductionOrder, c.OrderNumber, ct))
                .FirstOrDefault(r => r.Status == ReservationStatus.Active && r.PartNumber == part && alternates.Contains(r.ReservationId) == (line.Mode != AllocationMode.Primary));
            long id;
            if (existing is not null)
            {
                id = existing.ReservationId;
                await store.SetReservationQuantityAsync(id, existing.Quantity + c.Quantity, ct);
            }
            else
            {
                id = await store.InsertReservationAsync(new StockReservation(0, ReservationTarget.ProductionOrder, c.OrderNumber, null, part, c.Quantity, ReservationStatus.Active, context.Actor, now,
                    null, null, null), ct);
                if (line.Mode != AllocationMode.Primary)
                    await uow.Store<IAllocationStore>().InsertReservationAlternateAsync(new ReservationAlternate(id, primary, line.Quantity / line.PrimaryQuantity, line.AlternateId), ct);
            }

            // A size-matched lot is set aside for the order, so no one else cuts the piece that fits.
            if (lotNumber is not null)
                await uow.Store<IAllocationStore>().InsertLotEarmarkAsync(new LotEarmark(0, c.OrderNumber, part, lotNumber, c.Quantity, 0, context.Actor, now, null), ct);

            var key = id.ToString(CultureInfo.InvariantCulture);
            var message = string.Create(CultureInfo.InvariantCulture,
                $"{c.Quantity:0.####} {part} reserved for order {orderKey}{(line.Mode == AllocationMode.Primary ? string.Empty : $" in place of {primary}")}{(lotNumber is null ? string.Empty : $"; lot {lotNumber} is set aside for it")}. {line.Why}");
            return new CommandOutcome(CommandResult.Ok(key, message),
                [pipeline.Event(new StockReservationChanged(id, part, "order " + orderKey, existing is null ? "Reserved" : "Changed"), context, InventoryService.SourceContext, Entity, key)],
                new AuditChange(Entity, key, existing is null ? null : CanonicalJson.Serialize(existing), CanonicalJson.Serialize(new { c.OrderNumber, part, c.Quantity, primary, line.Mode, lotNumber, line.Why })));
        }, ct);
    }

    private async Task<CommandOutcome> PromiseAsync(IKernelUnitOfWork uow, ApproveAllocation c, AllocationLine line, CommandContext context, DateTimeOffset now, CancellationToken ct)
    {
        var wip = line.WipOrderNumber!.Value;
        var allocation = new WipAllocation(0, wip, c.OrderNumber, line.OfferedPart, c.Quantity, 0, WipAllocationStatus.Active, line.Why, context.Actor, now);
        var id = await uow.Store<IAllocationStore>().InsertWipAllocationAsync(allocation, ct);
        var key = id.ToString(CultureInfo.InvariantCulture);
        var message = string.Create(CultureInfo.InvariantCulture,
            $"{c.Quantity:0.####} {line.OfferedPart} from order {wip} is promised to order {c.OrderNumber}; it is reserved for it when received. {line.Why}");
        return new CommandOutcome(CommandResult.Ok(key, message),
            [pipeline.Event(new WipAllocationChanged(id, wip, c.OrderNumber, line.OfferedPart, "Promised"), context, InventoryService.SourceContext, "WipAllocation", key)],
            new AuditChange("WipAllocation", key, null, CanonicalJson.Serialize(allocation with { AllocationId = id })));
    }

    public Task<CommandResult> ReleaseWipAsync(ReleaseWipAllocation c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.AllocationId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(InventoryService.SourceContext, nameof(ReleaseWipAllocation), Permissions.AllocationApprove, ResourceRef.Of("WipAllocation", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var w = (await uow.Store<IAllocationLookup>().ListWipAllocationsAsync(false, ct)).FirstOrDefault(x => x.AllocationId == c.AllocationId)
                ?? throw new CommandRejectedException("allocation_not_found", $"Promise {key} does not exist.");
            if (w.Status != WipAllocationStatus.Active) return CommandOutcome.NoChange(CommandResult.NoChange(key, $"Promise {key} is already {w.Status}."));
            await uow.Store<IAllocationStore>().UpdateWipAllocationAsync(w.AllocationId, w.Fulfilled, WipAllocationStatus.Released, context.Actor, now, context.Reason, ct);
            return new CommandOutcome(CommandResult.Ok(key, $"Order {w.WipOrderNumber}'s output is no longer promised to order {w.ConsumerOrderNumber}."),
                [pipeline.Event(new WipAllocationChanged(w.AllocationId, w.WipOrderNumber, w.ConsumerOrderNumber, w.PartNumber, "Released"), context, InventoryService.SourceContext, "WipAllocation", key)],
                new AuditChange("WipAllocation", key, nameof(WipAllocationStatus.Active), nameof(WipAllocationStatus.Released)));
        }, ct);
    }

    public Task<CommandResult> ReleaseEarmarkAsync(ReleaseLotEarmark c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.EarmarkId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(InventoryService.SourceContext, nameof(ReleaseLotEarmark), Permissions.AllocationApprove, ResourceRef.Of("LotEarmark", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var e = (await uow.Store<IAllocationLookup>().ListLotEarmarksAsync(false, ct)).FirstOrDefault(x => x.EarmarkId == c.EarmarkId)
                ?? throw new CommandRejectedException("earmark_not_found", $"Set-aside {key} does not exist.");
            if (!await uow.Store<IAllocationStore>().ReleaseLotEarmarkAsync(e.EarmarkId, context.Actor, now, context.Reason!, ct))
                return CommandOutcome.NoChange(CommandResult.NoChange(key, $"Set-aside {key} is already released."));
            return new CommandOutcome(CommandResult.Ok(key, $"Lot {e.LotNumber} of {e.PartNumber} is no longer set aside for order {e.OrderNumber}; the order's reservation stays."),
                [pipeline.Event(new StockReservationChanged(0, e.PartNumber, "order " + e.OrderNumber.ToString(CultureInfo.InvariantCulture), "LotReleased"), context, InventoryService.SourceContext, "LotEarmark", key)],
                new AuditChange("LotEarmark", key, "Active", "Released"));
        }, ct);
    }

    public Task<CommandResult> SetPolicyAsync(SetAllocationPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(InventoryService.SourceContext, nameof(SetAllocationPolicy), Permissions.AllocationApprove, ResourceRef.Of("AllocationPolicy", "site"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var rules = (c.Rules ?? []).Distinct().ToList();
            if (rules.Count == 0) throw new CommandRejectedException("rules_required", "Choose at least one ranking rule.");
            await uow.Store<IAllocationStore>().InsertAllocationPolicyAsync(new AllocationPolicy(rules, context.Actor, now), ct);
            var words = string.Join(", then ", rules.Select(AllocationOptimizer.Words));
            return new CommandOutcome(CommandResult.Ok("site", $"Short orders are ranked by {words}."),
                [pipeline.Event(new AllocationPolicyChanged(words), context, InventoryService.SourceContext, "AllocationPolicy", "site")],
                new AuditChange("AllocationPolicy", "site", null, words));
        }, ct);
    }
}

/// <summary>
/// Owner decision 2026-11-02: when a work-in-progress order's output is received, what was promised to other orders is reserved for them
/// in the same transaction, as far as they still need it. A promise to an order that no longer holds material is released.
/// </summary>
internal static class WipFulfilment
{
    public static async Task<(IReadOnlyList<EventEnvelope> Events, string Said)> ReceiveAsync(IKernelUnitOfWork uow, CommandPipeline pipeline, CommandContext context, long wipOrder,
        string partNumber, decimal received, DateTimeOffset now, CancellationToken ct)
    {
        var lookup = uow.Store<IAllocationLookup>();
        var promises = (await lookup.ListWipAllocationsAsync(true, ct)).Where(w => w.WipOrderNumber == wipOrder && w.Outstanding > 0).OrderBy(w => w.ApprovedAt).ThenBy(w => w.AllocationId).ToList();
        if (promises.Count == 0) return ([], string.Empty);
        var store = uow.Store<IAllocationStore>();
        var reservations = uow.Store<IStockReservationStore>();
        var execution = uow.Store<IExecutionLookup>();
        var events = new List<EventEnvelope>();
        var said = new List<string>();
        var left = received;
        foreach (var w in promises)
        {
            if (left <= 0) break;
            var give = Math.Min(left, w.Outstanding);
            left -= give;
            var consumer = await execution.FindOrderAsync(w.ConsumerOrderNumber, ct);
            var fulfilled = w.Fulfilled + give;
            if (consumer is not { Status: OrderStatus.Released or OrderStatus.Held })
            {
                await store.UpdateWipAllocationAsync(w.AllocationId, w.Fulfilled, WipAllocationStatus.Released, context.Actor, now, $"Order {w.ConsumerOrderNumber} is {consumer?.Status.ToString() ?? "gone"}", ct);
                left += give;
                continue;
            }

            // Only what the consuming order still needs is reserved; the rest stays free.
            var required = consumer.Materials.Where(m => m.ComponentPartNumber == partNumber).Sum(m => m.RequiredQuantity);
            var issued = (await uow.Store<IInventoryLookup>().ListOrderIssuesAsync(ct)).FirstOrDefault(i => i.OrderNumber == consumer.OrderNumber && i.PartNumber == partNumber)?.NetIssued ?? 0;
            var alternates = (await lookup.ListReservationAlternatesAsync(ct)).Select(a => a.ReservationId).ToHashSet();
            var balances = await StockReservationQueries.OutstandingAsync(uow, partNumber, ct);
            var mine = balances.FirstOrDefault(b => b.Reservation.Target == ReservationTarget.ProductionOrder && b.Reservation.OrderNumber == consumer.OrderNumber && !alternates.Contains(b.Reservation.ReservationId));
            var reserve = Math.Max(0, Math.Min(give, required - issued - (mine?.Outstanding ?? 0)));
            if (reserve > 0)
            {
                long id;
                if (mine is not null)
                {
                    id = mine.Reservation.ReservationId;
                    await reservations.SetReservationQuantityAsync(id, mine.Reservation.Quantity + reserve, ct);
                }
                else
                {
                    id = await reservations.InsertReservationAsync(new StockReservation(0, ReservationTarget.ProductionOrder, consumer.OrderNumber, null, partNumber, reserve, ReservationStatus.Active,
                        context.Actor, now, null, null, null), ct);
                }

                events.Add(pipeline.Event(new StockReservationChanged(id, partNumber, "order " + consumer.OrderNumber.ToString(CultureInfo.InvariantCulture), mine is null ? "Reserved" : "Changed"),
                    context, InventoryService.SourceContext, "StockReservation", id.ToString(CultureInfo.InvariantCulture)));
                said.Add(string.Create(CultureInfo.InvariantCulture, $"{reserve:0.####} reserved for order {consumer.OrderNumber} as promised"));
            }

            var status = fulfilled >= w.Quantity || reserve < give ? WipAllocationStatus.Fulfilled : WipAllocationStatus.Active;
            await store.UpdateWipAllocationAsync(w.AllocationId, fulfilled, status, status == WipAllocationStatus.Active ? null : context.Actor, status == WipAllocationStatus.Active ? null : now,
                status == WipAllocationStatus.Active ? null : reserve < give ? "Order needed less by the time the output arrived" : "Received", ct);
            left += give - reserve;
            events.Add(pipeline.Event(new WipAllocationChanged(w.AllocationId, wipOrder, w.ConsumerOrderNumber, partNumber, status == WipAllocationStatus.Fulfilled ? "Fulfilled" : "PartlyFulfilled"),
                context, InventoryService.SourceContext, "WipAllocation", w.AllocationId.ToString(CultureInfo.InvariantCulture)));
        }

        return (events, said.Count == 0 ? string.Empty : " " + string.Join("; ", said) + ".");
    }
}
