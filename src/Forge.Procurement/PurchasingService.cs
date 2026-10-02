using Forge.Engineering.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Procurement.Contracts;

namespace Forge.Procurement;

public sealed record NewPurchaseLine(string PartNumber, decimal Quantity, DateOnly PromisedDate);

public sealed record CreatePurchaseOrder(string Supplier, IReadOnlyList<NewPurchaseLine> Lines);

public sealed record AddPurchaseLine(long PoNumber, string PartNumber, decimal Quantity, DateOnly PromisedDate);

public sealed record ApprovePurchaseOrder(long PoNumber);

/// <summary>The supplier moved its date. MRP and material readiness pick the new date up on their next run.</summary>
public sealed record ChangePromisedDate(long PoNumber, int LineNumber, DateOnly PromisedDate);

public sealed record CancelPurchaseLine(long PoNumber, int LineNumber);

public sealed record ClosePurchaseOrder(long PoNumber);

public sealed record CancelPurchaseOrder(long PoNumber);

public sealed record PurchaseOrderChanged(long PoNumber, string Change) : IDomainEvent
{
    public static string EventType => nameof(PurchaseOrderChanged);

    public static int SchemaVersion => 1;
}

public sealed record SupplierDateChanged(long PoNumber, int LineNumber, string PartNumber, DateOnly From, DateOnly To) : IDomainEvent
{
    public static string EventType => nameof(SupplierDateChanged);

    public static int SchemaVersion => 1;
}

public interface IProcurementStore
{
    Task<long> InsertOrderAsync(string supplier, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> InsertLineAsync(long poNumber, PurchaseLineInfo line, CancellationToken ct);

    Task SetStatusAsync(long poNumber, PurchaseOrderStatus status, DateTimeOffset at, string actor, CancellationToken ct);

    Task UpdateLineAsync(long poNumber, PurchaseLineInfo line, CancellationToken ct);
}

/// <summary>
/// Purchase orders. A buyer writes a draft; a different person approves it, and only then do its open
/// lines count as scheduled receipts. Supplier date changes are recorded with a reason so MRP and
/// readiness can explain why material moved.
/// </summary>
public sealed class PurchasingService(CommandPipeline pipeline)
{
    public const string SourceContext = "Procurement";
    private const string Entity = "PurchaseOrder";

    public Task<CommandResult> CreateAsync(CreatePurchaseOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(CreatePurchaseOrder), Permissions.PurchasingManage, ResourceRef.Of(Entity));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var supplier = BusinessCode.RequireName(c.Supplier, "Supplier");
            if (c.Lines is null || c.Lines.Count is 0 or > 999)
            {
                throw new CommandRejectedException("lines_required", "A purchase order needs 1 to 999 lines.");
            }

            var lines = new List<PurchaseLineInfo>();
            foreach (var (line, index) in c.Lines.Select((l, i) => (l, i)))
            {
                lines.Add(await ValidLineAsync(uow, index + 1, line.PartNumber, line.Quantity, line.PromisedDate, now, ct));
            }

            var store = uow.Store<IProcurementStore>();
            var number = await store.InsertOrderAsync(supplier, now, context.Actor, ct);
            foreach (var line in lines)
            {
                await store.InsertLineAsync(number, line, ct);
            }

            var id = Id(number);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Purchase order {id} drafted for {supplier}: {lines.Count} line(s). A different person must approve it."),
                [pipeline.Event(new PurchaseOrderChanged(number, "Drafted"), context, SourceContext, Entity, id)],
                new AuditChange(Entity, id, null, CanonicalJson.Serialize(new { supplier, lines })));
        }, ct);
    }

    public Task<CommandResult> AddLineAsync(AddPurchaseLine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(AddPurchaseLine), c.PoNumber, c, context, false, Permissions.PurchasingManage, async (uow, po, now) =>
        {
            if (po.Status != PurchaseOrderStatus.Draft)
            {
                throw new CommandRejectedException("not_draft", $"Purchase order {po.PoNumber} is {po.Status}; lines are added only to drafts.");
            }

            var line = await ValidLineAsync(uow, po.Lines.Count == 0 ? 1 : po.Lines.Max(l => l.LineNumber) + 1, c.PartNumber, c.Quantity, c.PromisedDate, now, ct);
            await uow.Store<IProcurementStore>().InsertLineAsync(po.PoNumber, line, ct);
            return ($"line {line.LineNumber} added: {line.Quantity} {line.UnitOfMeasure} {line.PartNumber} by {line.PromisedDate:yyyy-MM-dd}", "LineAdded", null, CanonicalJson.Serialize(line), null);
        }, ct);
    }

    public Task<CommandResult> ApproveAsync(ApprovePurchaseOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(ApprovePurchaseOrder), c.PoNumber, c, context, true, Permissions.PurchasingApprove, async (uow, po, now) =>
        {
            if (po.Status != PurchaseOrderStatus.Draft)
            {
                throw new CommandRejectedException("not_draft", $"Purchase order {po.PoNumber} is {po.Status}; only a draft can be approved.");
            }

            if (po.CreatedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You raised this purchase order; a different person must approve it.");
            }

            if (po.Lines.All(l => l.Status != PurchaseLineStatus.Open))
            {
                throw new CommandRejectedException("no_open_lines", "The purchase order has no open lines.");
            }

            await uow.Store<IProcurementStore>().SetStatusAsync(po.PoNumber, PurchaseOrderStatus.Approved, now, context.Actor, ct);
            return ("approved", "Approved", nameof(PurchaseOrderStatus.Draft), nameof(PurchaseOrderStatus.Approved), null);
        }, ct);
    }

    public Task<CommandResult> ChangePromisedDateAsync(ChangePromisedDate c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(ChangePromisedDate), c.PoNumber, c, context, true, Permissions.PurchasingManage, async (uow, po, _) =>
        {
            var line = OpenLine(po, c.LineNumber);
            if (line.PromisedDate == c.PromisedDate)
            {
                return null;
            }

            await uow.Store<IProcurementStore>().UpdateLineAsync(po.PoNumber, line with { PromisedDate = c.PromisedDate }, ct);
            return ($"line {line.LineNumber} {line.PartNumber} now promised {c.PromisedDate:yyyy-MM-dd} (was {line.PromisedDate:yyyy-MM-dd})", "DateChanged", line.PromisedDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), c.PromisedDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                pipeline.Event(new SupplierDateChanged(po.PoNumber, line.LineNumber, line.PartNumber, line.PromisedDate, c.PromisedDate), context, SourceContext, Entity, Id(po.PoNumber)));
        }, ct);
    }

    public Task<CommandResult> CancelLineAsync(CancelPurchaseLine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(CancelPurchaseLine), c.PoNumber, c, context, true, Permissions.PurchasingManage, async (uow, po, _) =>
        {
            var line = OpenLine(po, c.LineNumber);
            await uow.Store<IProcurementStore>().UpdateLineAsync(po.PoNumber, line with { Status = PurchaseLineStatus.Cancelled }, ct);
            return ($"line {line.LineNumber} cancelled", "LineCancelled", CanonicalJson.Serialize(line), nameof(PurchaseLineStatus.Cancelled), null);
        }, ct);
    }

    public Task<CommandResult> CloseAsync(ClosePurchaseOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(ClosePurchaseOrder), c.PoNumber, c, context, true, Permissions.PurchasingManage, async (uow, po, now) =>
        {
            if (po.Status != PurchaseOrderStatus.Approved)
            {
                throw new CommandRejectedException("not_approved", $"Purchase order {po.PoNumber} is {po.Status}; only an approved order is closed.");
            }

            await uow.Store<IProcurementStore>().SetStatusAsync(po.PoNumber, PurchaseOrderStatus.Closed, now, context.Actor, ct);
            return ("closed; nothing more is expected", "Closed", nameof(PurchaseOrderStatus.Approved), nameof(PurchaseOrderStatus.Closed), null);
        }, ct);
    }

    public Task<CommandResult> CancelAsync(CancelPurchaseOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(CancelPurchaseOrder), c.PoNumber, c, context, true, Permissions.PurchasingManage, async (uow, po, now) =>
        {
            if (po.Status != PurchaseOrderStatus.Draft)
            {
                throw new CommandRejectedException("not_draft", $"Purchase order {po.PoNumber} is {po.Status}; an approved order is closed, not cancelled.");
            }

            await uow.Store<IProcurementStore>().SetStatusAsync(po.PoNumber, PurchaseOrderStatus.Cancelled, now, context.Actor, ct);
            return ("cancelled", "Cancelled", nameof(PurchaseOrderStatus.Draft), nameof(PurchaseOrderStatus.Cancelled), null);
        }, ct);
    }

    private static string Id(long number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static PurchaseLineInfo OpenLine(PurchaseOrderInfo po, int lineNumber)
    {
        if (po.Status is not (PurchaseOrderStatus.Draft or PurchaseOrderStatus.Approved))
        {
            throw new CommandRejectedException("not_open", $"Purchase order {po.PoNumber} is {po.Status}.");
        }

        var line = po.Lines.FirstOrDefault(l => l.LineNumber == lineNumber)
            ?? throw new CommandRejectedException("line_not_found", $"Purchase order {po.PoNumber} has no line {lineNumber}.");
        return line.Status == PurchaseLineStatus.Open
            ? line
            : throw new CommandRejectedException("line_cancelled", $"Line {lineNumber} is cancelled.");
    }

    private static async Task<PurchaseLineInfo> ValidLineAsync(Kernel.Access.IKernelUnitOfWork uow, int lineNumber, string rawPart, decimal quantity, DateOnly promised, DateTimeOffset now, CancellationToken ct)
    {
        var part = BusinessCode.Require(rawPart, "Part");
        if (quantity <= 0 || quantity > 100_000_000)
        {
            throw new CommandRejectedException("invalid_quantity", $"Line {lineNumber}: quantity must be above zero.");
        }

        if (promised < DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1))
        {
            throw new CommandRejectedException("promised_in_past", $"Line {lineNumber}: the promised date {promised:yyyy-MM-dd} is in the past.");
        }

        var item = await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct)
            ?? throw new CommandRejectedException("unknown_part", $"Line {lineNumber}: part {part} does not exist.");
        if (item.MakeBuy != MakeBuy.Buy)
        {
            throw new CommandRejectedException("not_purchased", $"Line {lineNumber}: {part} is made, not bought.");
        }

        return new PurchaseLineInfo(lineNumber, part, quantity, item.UnitOfMeasure, promised, PurchaseLineStatus.Open);
    }

    private Task<CommandResult> ChangeAsync<TCommand>(
        string commandType,
        long poNumber,
        TCommand command,
        CommandContext context,
        bool reasonRequired,
        string permission,
        Func<Kernel.Access.IKernelUnitOfWork, PurchaseOrderInfo, DateTimeOffset, Task<(string Message, string Change, string? Old, string? New, EventEnvelope? Extra)?>> change,
        CancellationToken ct)
    {
        var id = Id(poNumber);
        var spec = new CommandSpec(SourceContext, commandType, permission, ResourceRef.Of(Entity, id), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var po = await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(poNumber, ct)
                ?? throw new CommandRejectedException("purchase_order_not_found", $"Purchase order {id} does not exist.");
            var result = await change(uow, po, now);
            if (result is not { } r)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Nothing changed."));
            }

            var events = new List<EventEnvelope> { pipeline.Event(new PurchaseOrderChanged(poNumber, r.Change), context, SourceContext, Entity, id) };
            if (r.Extra is { } extra)
            {
                events.Add(extra);
            }
            return new CommandOutcome(
                CommandResult.Ok(id, $"Purchase order {id}: {r.Message}."),
                events,
                new AuditChange(Entity, id, r.Old, r.New));
        }, ct);
    }
}
