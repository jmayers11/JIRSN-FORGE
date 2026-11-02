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
public sealed record ApproveAllocation(long OrderNumber, string PartNumber, decimal Quantity, string? AlternateFor);

public sealed record SetAllocationPolicy(IReadOnlyList<AllocationRule> Rules);

public sealed record AllocationPolicyChanged(string Rules) : IDomainEvent
{
    public static string EventType => nameof(AllocationPolicyChanged);

    public static int SchemaVersion => 1;
}

public interface IAllocationStore
{
    Task InsertAllocationPolicyAsync(AllocationPolicy policy, CancellationToken ct);

    Task InsertReservationAlternateAsync(ReservationAlternate alternate, CancellationToken ct);
}

/// <summary>
/// The V5 material allocation optimizer (owner decisions 2026-11-02). It only proposes: free stock (released, usable, not reserved for
/// anyone) goes to released orders that are short, ranked by the plant's rules (by default due date, then priority, then the smallest
/// shortage); what free stock cannot cover is offered from approved alternates the BOM line allows, automatic or for review. It never
/// takes stock that is reserved for other work, and every line says why. A person approves a line, which reserves the stock.
/// </summary>
public static class AllocationOptimizer
{
    private sealed record Need(OrderInfo Order, string Part, decimal Shortage, SubstitutionPolicy Policy);

    public static async Task<IReadOnlyList<AllocationProposal>> ProposeAsync(IKernelUnitOfWork uow, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var execution = uow.Store<IExecutionLookup>();
        var engineering = uow.Store<IEngineeringLookup>();
        var policy = await uow.Store<IAllocationLookup>().AllocationPolicyAsync(ct);
        var issued = (await uow.Store<IInventoryLookup>().ListOrderIssuesAsync(ct)).ToDictionary(i => (i.OrderNumber, i.PartNumber), i => i.NetIssued);
        var outstanding = await StockReservationQueries.OutstandingAsync(uow, null, ct);
        var alternates = (await uow.Store<IAllocationLookup>().ListReservationAlternatesAsync(ct)).ToDictionary(a => a.ReservationId);
        var today = await uow.SiteDateAsync(now, ct);

        // What each released order is still short of, after issues and its own reservations (alternates counted at their factor).
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
                var shortage = Math.Round(group.Sum(m => m.RequiredQuantity) - issued.GetValueOrDefault((order.OrderNumber, group.Key)) - direct - viaAlternates, 4, MidpointRounding.AwayFromZero);
                if (shortage <= 0) continue;
                var line = bom.FirstOrDefault(b => b.ComponentPartNumber == group.Key);
                needs.Add(new Need(order, group.Key, shortage, line?.Policy ?? SubstitutionPolicy.Suggest));
            }
        }

        if (needs.Count == 0) return [];
        var allAlternates = await uow.Store<IAlternateLookup>().ListAlternatesAsync(ct);
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
            var ranked = Rank(byPart, policy.Rules).ToList();
            var onHand = (await uow.Store<IInventoryLookup>().ListBalancesAsync(byPart.Key, ct)).Where(b => b.PartNumber == byPart.Key).Sum(b => b.Quantity);
            var available = await StockReservationQueries.AvailableAsync(uow, byPart.Key, now, ct);
            var reservedForOthers = outstanding.Where(b => b.Reservation.PartNumber == byPart.Key).Sum(b => b.Outstanding);
            var startFree = await FreeAsync(byPart.Key);
            var lines = new List<AllocationLine>();
            var gaps = new List<AllocationGap>();
            var ruledOut = new List<string>();
            if (onHand > available)
                ruledOut.Add(string.Create(CultureInfo.InvariantCulture, $"{onHand - available:0.####} {byPart.Key} on hand is on hold, expired, in inspection or otherwise not usable."));
            if (reservedForOthers > 0)
                ruledOut.Add(string.Create(CultureInfo.InvariantCulture, $"{reservedForOthers:0.####} {byPart.Key} is reserved for other work and is never taken from it."));
            var rule = string.Join(", then ", policy.Rules.Select(Words));
            for (var i = 0; i < ranked.Count; i++)
            {
                var need = ranked[i];
                var remaining = need.Shortage;
                var why = string.Create(CultureInfo.InvariantCulture,
                    $"Ranked {i + 1} of {ranked.Count} orders short of {need.Part} ({rule}): due {need.Order.DueDate:yyyy-MM-dd}, priority {need.Order.Priority}, short {need.Shortage:0.####}.");
                var take = Math.Min(remaining, free[need.Part]);
                if (take > 0)
                {
                    lines.Add(new AllocationLine(need.Order.OrderNumber, need.Order.DueDate, need.Order.Priority, i + 1, need.Part, need.Shortage, need.Part, take, take, AllocationMode.Primary, null, why));
                    free[need.Part] -= take;
                    remaining -= take;
                }

                if (remaining <= 0) continue;
                // What free stock cannot cover: approved alternates, where the BOM line allows them.
                if (need.Policy == SubstitutionPolicy.Never)
                {
                    gaps.Add(new AllocationGap(need.Order.OrderNumber, need.Part, remaining, "No free stock left, and the BOM line does not allow alternates."));
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
                            + (automatic ? "which the BOM line allows automatically." : "which the BOM line only suggests: review it before use.")));
                    free[offered] = altFree - quantity;
                    remaining -= cover;
                }

                if (remaining > 0)
                    gaps.Add(new AllocationGap(need.Order.OrderNumber, need.Part, remaining, "No free stock and no usable alternate with free stock: buy or make it."));
            }

            // What each line means for the orders ranked below it.
            var impacted = lines.Select(l =>
            {
                var below = gaps.Where(g => ranked.FindIndex(r => r.Order.OrderNumber == g.OrderNumber) + 1 > l.Rank).Select(g => g.OrderNumber).Distinct().ToList();
                return below.Count == 0 ? l : l with { Why = l.Why + $" Orders ranked lower that stay short: {string.Join(", ", below)}." };
            }).ToList();
            proposals.Add(new AllocationProposal(byPart.Key, onHand, Math.Max(0, onHand - available), reservedForOthers, startFree, impacted, gaps, ruledOut.Distinct().ToList()));
        }

        return proposals;
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

/// <summary>Turns approved proposal lines into reservations and keeps the plant's ranking rules (owner decisions 2026-11-02).</summary>
public sealed class AllocationService(CommandPipeline pipeline)
{
    private const string Entity = "StockReservation";

    public Task<CommandResult> ApproveAsync(ApproveAllocation c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var orderKey = c.OrderNumber.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(InventoryService.SourceContext, nameof(ApproveAllocation), Permissions.AllocationApprove, ResourceRef.Of("ProductionOrder", orderKey));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (c.Quantity <= 0) throw new CommandRejectedException("invalid_quantity", "Quantity must be above zero.");
            await uow.Store<IStockReservationStore>().LockPartAsync(part, ct);
            var primary = string.IsNullOrWhiteSpace(c.AlternateFor) ? part : BusinessCode.Require(c.AlternateFor, "Part");
            // Proposals are read again now: stock and shortages may have moved since the planner looked.
            var proposal = (await AllocationOptimizer.ProposeAsync(uow, now, ct)).FirstOrDefault(p => p.PartNumber == primary);
            var line = proposal?.Lines.FirstOrDefault(l => l.OrderNumber == c.OrderNumber && l.OfferedPart == part)
                ?? throw new CommandRejectedException("not_proposed", $"Nothing of {part} is proposed for order {orderKey} now; look at the proposals again.");
            if (c.Quantity > line.Quantity)
                throw new CommandRejectedException("over_proposal", string.Create(CultureInfo.InvariantCulture, $"Only {line.Quantity:0.####} {part} can go to order {orderKey} now."));
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

            var key = id.ToString(CultureInfo.InvariantCulture);
            var message = string.Create(CultureInfo.InvariantCulture,
                $"{c.Quantity:0.####} {part} reserved for order {orderKey}{(line.Mode == AllocationMode.Primary ? string.Empty : $" in place of {primary}")}. {line.Why}");
            return new CommandOutcome(CommandResult.Ok(key, message),
                [pipeline.Event(new StockReservationChanged(id, part, "order " + orderKey, existing is null ? "Reserved" : "Changed"), context, InventoryService.SourceContext, Entity, key)],
                new AuditChange(Entity, key, existing is null ? null : CanonicalJson.Serialize(existing), CanonicalJson.Serialize(new { c.OrderNumber, part, c.Quantity, primary, line.Mode, line.Why })));
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
