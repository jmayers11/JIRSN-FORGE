using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Quality;

/// <param name="NcrId">The NCR whose rework or repair disposition this carries out; otherwise a rework reason code says why.</param>
public sealed record OpenRework(ReworkKind Kind, long OrderNumber, int Sequence, decimal Quantity, string Instructions, long? NcrId = null, string? ReasonCode = null);

public sealed record CloseRework(long ReworkId, decimal GoodQuantity, decimal ScrapQuantity, string Note);

public sealed record ReworkChanged(long ReworkId, long OrderNumber, int Sequence, string Kind, string Change, decimal Quantity) : IDomainEvent
{
    public static string EventType => nameof(ReworkChanged);

    public static int SchemaVersion => 1;
}

/// <summary>A rework plan was proposed, approved or rejected (Sprint 10 WW-S10-14/29).</summary>
public sealed record ReworkPlanChanged(Guid PlanId, long ReworkId, long OrderNumber, string Status, string Message) : IDomainEvent
{
    public static string EventType => nameof(ReworkPlanChanged);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Rework and repair (V3.5, V3 brief K). Rework brings output back to the drawing; repair, approved on an NCR,
/// makes it usable without conforming. Both take good output back from an order operation without touching the
/// production reports it came from; closing says what came out good and what was scrapped. Use as is needs no
/// further work and scrap ends there, so neither opens a rework record.
/// </summary>
public sealed class ReworkService(CommandPipeline pipeline)
{
    public const string SourceContext = "Quality";
    private const string Entity = "Rework";

    public Task<CommandResult> OpenAsync(OpenRework c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var opId = $"{c.OrderNumber.ToString(CultureInfo.InvariantCulture)}/{c.Sequence.ToString(CultureInfo.InvariantCulture)}";
        var spec = new CommandSpec(SourceContext, nameof(OpenRework), Permissions.ShopFloorRecord, ResourceRef.Of("OrderOperation", opId), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var (order, program) = await OrderAsync(uow, context, c.OrderNumber, ct);
            var op = order.Operations.FirstOrDefault(o => o.Sequence == c.Sequence)
                ?? throw new CommandRejectedException("operation_not_found", $"Order {order.OrderNumber} has no operation {c.Sequence}.");
            if (c.Quantity <= 0 || c.Quantity > 1_000_000)
            {
                throw new CommandRejectedException("invalid_quantity", "Rework a positive quantity.");
            }

            var lookup = uow.Store<IReworkLookup>();
            var history = await lookup.ListReworkAsync(order.OrderNumber, false, ct);
            string? reasonCode = null;
            if (c.NcrId is { } ncrId)
            {
                var ncr = await uow.Store<IQualityLookup>().FindNcrAsync(ncrId, ct)
                    ?? throw new CommandRejectedException("ncr_not_found", $"NCR {ncrId} does not exist.");
                var wanted = c.Kind switch { ReworkKind.Repair => Disposition.Repair, ReworkKind.Scrap => Disposition.Scrap, _ => Disposition.Rework };
                if (ncr.OrderNumber != order.OrderNumber || ncr.Status != NcrStatus.Dispositioned || ncr.Disposition != wanted)
                {
                    throw new CommandRejectedException("ncr_not_dispositioned", ncr.OrderNumber != order.OrderNumber
                        ? $"NCR {ncrId} is not about order {order.OrderNumber}."
                        : $"NCR {ncrId} is {ncr.Status}{(ncr.Disposition is { } d ? $" ({d})" : string.Empty)}; {c.Kind.ToString().ToLowerInvariant()} follows a decided{(c.Kind == ReworkKind.Repair ? ", approved" : string.Empty)} {c.Kind.ToString().ToLowerInvariant()} disposition.");
                }

                var taken = history.Where(r => r.NcrId == ncrId).Sum(r => r.Quantity);
                if (taken + c.Quantity > ncr.Quantity)
                {
                    throw new CommandRejectedException("over_ncr", $"NCR {ncrId} covers {ncr.Quantity:0.###}; {taken:0.###} already went to {c.Kind.ToString().ToLowerInvariant()}.");
                }
            }
            else if (c.Kind is ReworkKind.Repair or ReworkKind.Scrap)
            {
                throw new CommandRejectedException("ncr_required", $"A {c.Kind.ToString().ToLowerInvariant()} carries out that disposition on an NCR; name the NCR.");
            }

            if (!string.IsNullOrWhiteSpace(c.ReasonCode))
            {
                reasonCode = await ReworkReasonAsync(uow, c.ReasonCode, ct);
            }
            else if (c.NcrId is null)
            {
                throw new CommandRejectedException("reason_required", "Rework without an NCR names a rework reason code.");
            }

            var available = op.GoodQuantity - history.Where(r => r.Sequence == op.Sequence).Sum(r => r.Open ? r.Quantity : r.ScrapQuantity ?? 0);
            if (c.Quantity > available)
            {
                throw new CommandRejectedException("over_rework", $"Operation {opId} has {available:0.###} good to take back ({op.GoodQuantity:0.###} reported, less rework open or scrapped).");
            }

            var rework = new ReworkInfo(0, c.Kind, order.OrderNumber, op.Sequence, order.PartNumber, c.Quantity, c.NcrId, reasonCode,
                BusinessCode.RequireName(c.Instructions, "Instructions"), now, context.Actor);
            var id = await uow.Store<IReworkStore>().InsertReworkAsync(rework, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            if (c.Kind == ReworkKind.Scrap)
            {
                // Scrap ends there: the output comes back as scrapped in the same step, and the production reports stay as recorded.
                var note = $"Scrapped under NCR {c.NcrId}.";
                await uow.Store<IReworkStore>().CloseReworkAsync(id, 0m, c.Quantity, note, now, context.Actor, ct);
                var scrapped = rework with { ReworkId = id, ClosedAt = now, ClosedBy = context.Actor, GoodQuantity = 0m, ScrapQuantity = c.Quantity, CloseNote = note };
                return new CommandOutcome(
                    CommandResult.Ok(key, $"{c.Quantity:0.###} of {opId} scrapped under NCR {c.NcrId}. The production reports stay as recorded."),
                    [pipeline.Event(new ReworkChanged(id, order.OrderNumber, op.Sequence, c.Kind.ToString(), "Closed", c.Quantity), context, SourceContext, Entity, key, program)],
                    new AuditChange(Entity, key, null, CanonicalJson.Serialize(scrapped), program));
            }

            return new CommandOutcome(
                CommandResult.Ok(key, $"{c.Quantity:0.###} of {opId} taken back for {c.Kind.ToString().ToLowerInvariant()} ({(c.NcrId is { } n ? $"NCR {n}" : reasonCode)}). The production reports stay as recorded."),
                [pipeline.Event(new ReworkChanged(id, order.OrderNumber, op.Sequence, c.Kind.ToString(), "Opened", c.Quantity), context, SourceContext, Entity, key, program)],
                new AuditChange(Entity, key, null, CanonicalJson.Serialize(rework with { ReworkId = id }), program));
        }, ct);
    }

    public Task<CommandResult> CloseAsync(CloseRework c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.ReworkId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(CloseRework), Permissions.ShopFloorRecord, ResourceRef.Of(Entity, key));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var rework = (await uow.Store<IReworkLookup>().ListReworkAsync(null, false, ct)).FirstOrDefault(r => r.ReworkId == c.ReworkId)
                ?? throw new CommandRejectedException("rework_not_found", $"Rework {key} does not exist.");
            var (order, program) = await OrderAsync(uow, context, rework.OrderNumber, ct);
            if (!rework.Open)
            {
                throw new CommandRejectedException("rework_closed", $"Rework {key} was closed at {rework.ClosedAt:u}.");
            }

            // Work an approved rework plan added to the order is done before the rework says what came out of it.
            var unfinished = order.Operations.Where(o => o.ReworkId == rework.ReworkId && o.Status != OperationStatus.Complete).Select(o => o.Sequence).ToList();
            if (unfinished.Count > 0)
            {
                throw new CommandRejectedException("rework_work_open", $"Rework {key} still has added operation(s) {string.Join(", ", unfinished)} to complete on order {order.OrderNumber}.");
            }

            if (c.GoodQuantity < 0 || c.ScrapQuantity < 0 || c.GoodQuantity + c.ScrapQuantity != rework.Quantity)
            {
                throw new CommandRejectedException("quantity_mismatch", $"Rework {key} took back {rework.Quantity:0.###}; good and scrap must add up to that.");
            }

            var note = BusinessCode.RequireName(c.Note, "Note");
            await uow.Store<IReworkStore>().CloseReworkAsync(c.ReworkId, c.GoodQuantity, c.ScrapQuantity, note, now, context.Actor, ct);
            var closed = rework with { ClosedAt = now, ClosedBy = context.Actor, GoodQuantity = c.GoodQuantity, ScrapQuantity = c.ScrapQuantity, CloseNote = note };
            return new CommandOutcome(
                CommandResult.Ok(key, $"{rework.Kind} {key} closed: {c.GoodQuantity:0.###} good{(rework.Kind == ReworkKind.Repair ? " (repaired, not to drawing)" : string.Empty)}, {c.ScrapQuantity:0.###} scrapped."),
                [pipeline.Event(new ReworkChanged(c.ReworkId, rework.OrderNumber, rework.Sequence, rework.Kind.ToString(), "Closed", rework.Quantity), context, SourceContext, Entity, key, program)],
                new AuditChange(Entity, key, CanonicalJson.Serialize(rework), CanonicalJson.Serialize(closed), program));
        }, ct);
    }

    private static async Task<(OrderInfo Order, ProgramId? Program)> OrderAsync(IKernelUnitOfWork uow, CommandContext context, long orderNumber, CancellationToken ct)
    {
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
        var program = order?.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
        if (order is null || (program is { } required && !context.Principal.Programs.Contains(required)))
        {
            throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
        }

        return order.Status == OrderStatus.Cancelled
            ? throw new CommandRejectedException("order_cancelled", $"Order {orderNumber} is cancelled.")
            : (order, program);
    }

    private static async Task<string> ReworkReasonAsync(IKernelUnitOfWork uow, string raw, CancellationToken ct)
    {
        var code = BusinessCode.Require(raw, "Reason code");
        var reason = (await uow.Store<IProductionActualsLookup>().ListReasonCodesAsync(ct)).FirstOrDefault(r => r.Code == code)
            ?? throw new CommandRejectedException("unknown_reason_code", $"Reason code {code} does not exist.");
        return reason is { Active: true, Kind: ReasonKind.Rework }
            ? reason.Code
            : throw new CommandRejectedException("wrong_reason_kind", reason.Active ? $"{code} is a {reason.Kind.ToString().ToLowerInvariant()} reason, not a rework reason." : $"Reason code {code} is retired.");
    }
}
