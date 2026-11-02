using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;
using Forge.Sales.Contracts;
using static Forge.Sales.QuoteService;

namespace Forge.Sales;

public sealed record NewSalesLine(string PartNumber, decimal Quantity, decimal UnitPrice, DateOnly DueDate);

/// <param name="CustomerPo">The customer's purchase order number; one customer PO is entered once.</param>
public sealed record CreateSalesOrder(string Customer, string? CustomerPo, string? ShipTo, IReadOnlyList<NewSalesLine> Lines);

/// <summary>Turns an approved, unexpired quote into a draft sales order at its prices; each line is due its quoted lead time from today.</summary>
public sealed record CreateSalesOrderFromQuote(long QuoteNumber, string? CustomerPo, string? ShipTo);

public sealed record AddSalesLine(long OrderNumber, string PartNumber, decimal Quantity, decimal UnitPrice, DateOnly DueDate);

public sealed record ChangeSalesLineDate(long OrderNumber, int LineNumber, DateOnly DueDate);

public sealed record CancelSalesLine(long OrderNumber, int LineNumber);

public sealed record ApproveSalesOrder(long OrderNumber);

public sealed record CloseSalesOrder(long OrderNumber);

public sealed record CancelSalesOrder(long OrderNumber);

/// <summary>
/// Sales orders. A Sales Representative drafts one (or makes it from an approved quote); a Sales Manager who did not write it approves
/// it, and only then do its open lines become customer-order demand in planning. Lines are added only to drafts; an approved line can
/// move its date or be cancelled while nothing has shipped against it.
/// </summary>
public sealed class SalesOrderService(CommandPipeline pipeline, IKernelUnitOfWorkFactory unitOfWork, ISalesShipmentResolver shipments)
{
    private const string Entity = "SalesOrder";

    public Task<CommandResult> CreateAsync(CreateSalesOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(CustomerService.SourceContext, nameof(CreateSalesOrder), Permissions.SalesManage, ResourceRef.Of(Entity));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var customer = await CustomerService.RequireActiveAsync(uow, c.Customer, ct);
            if (c.Lines is null || c.Lines.Count is 0 or > 200)
                throw new CommandRejectedException("lines_required", "A sales order needs 1 to 200 lines.");
            var today = await uow.SiteDateAsync(now, ct);
            var lines = new List<SalesOrderLineInfo>();
            foreach (var (line, index) in c.Lines.Select((l, i) => (l, i)))
                lines.Add(await ValidLineAsync(uow, index + 1, line.PartNumber, line.Quantity, line.UnitPrice, line.DueDate, today, ct));
            return await InsertAsync(uow, customer, c.CustomerPo, c.ShipTo, null, lines, context, now, ct);
        }, ct);
    }

    public Task<CommandResult> CreateFromQuoteAsync(CreateSalesOrderFromQuote c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var quoteId = Id(c.QuoteNumber);
        var spec = new CommandSpec(CustomerService.SourceContext, nameof(CreateSalesOrderFromQuote), Permissions.SalesManage, ResourceRef.Of("Quote", quoteId));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<ISalesStore>();
            await store.LockQuoteAsync(c.QuoteNumber, ct);
            var quote = await uow.Store<ISalesLookup>().FindQuoteAsync(c.QuoteNumber, ct)
                ?? throw new CommandRejectedException("quote_not_found", $"Quote {quoteId} does not exist.");
            if (quote.Status != QuoteStatus.Approved)
                throw new CommandRejectedException("quote_not_approved", $"Quote {quoteId} is {quote.Status}; only an approved quote becomes a sales order.");
            var today = await uow.SiteDateAsync(now, ct);
            if (quote.ValidUntil < today)
                throw new CommandRejectedException("quote_expired", $"Quote {quoteId} expired on {quote.ValidUntil:yyyy-MM-dd}; quote again.");
            var customer = await CustomerService.RequireActiveAsync(uow, quote.CustomerCode, ct);
            var lines = new List<SalesOrderLineInfo>();
            foreach (var line in quote.Lines.OrderBy(l => l.LineNumber))
            {
                var (item, part) = await SellableAsync(uow, line.LineNumber, line.PartNumber, ct);
                lines.Add(new SalesOrderLineInfo(lines.Count + 1, part, line.Quantity, item.UnitOfMeasure, line.UnitPrice, today.AddDays(line.LeadTimeDays), SalesLineStatus.Open));
            }

            var outcome = await InsertAsync(uow, customer, c.CustomerPo, c.ShipTo, quote.QuoteNumber, lines, context, now, ct);
            var orderNumber = long.Parse(outcome.Result.EntityId!, System.Globalization.CultureInfo.InvariantCulture);
            await store.SetQuoteStatusAsync(quote.QuoteNumber, QuoteStatus.Accepted, now, context.Actor, orderNumber, ct);
            return outcome with
            {
                Result = CommandResult.Ok(outcome.Result.EntityId!, $"{outcome.Result.Message} Quote {quoteId} accepted."),
                Events = [.. outcome.Events, pipeline.Event(new QuoteChanged(quote.QuoteNumber, "Accepted"), context, CustomerService.SourceContext, "Quote", quoteId)],
            };
        }, ct);
    }

    public Task<CommandResult> AddLineAsync(AddSalesLine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(AddSalesLine), c.OrderNumber, c, context, Permissions.SalesManage, false, async (uow, order, now) =>
        {
            RequireDraft(order);
            if (order.Lines.Count >= 200) throw new CommandRejectedException("too_many_lines", "A sales order has at most 200 lines.");
            var line = await ValidLineAsync(uow, order.Lines.Count == 0 ? 1 : order.Lines.Max(l => l.LineNumber) + 1, c.PartNumber, c.Quantity, c.UnitPrice, c.DueDate,
                await uow.SiteDateAsync(now, ct), ct);
            await uow.Store<ISalesStore>().InsertOrderLineAsync(order.OrderNumber, line, ct);
            return ($"line {line.LineNumber} added: {Qty(line.Quantity)} {line.PartNumber} at {Amount(line.UnitPrice, order.Currency)}, due {line.DueDate:yyyy-MM-dd}", "LineAdded", null, CanonicalJson.Serialize(line));
        }, ct);
    }

    public Task<CommandResult> ChangeLineDateAsync(ChangeSalesLineDate c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(ChangeSalesLineDate), c.OrderNumber, c, context, Permissions.SalesManage, true, async (uow, order, now) =>
        {
            var line = OpenLine(order, c.LineNumber);
            if (line.DueDate == c.DueDate) return null;
            if (c.DueDate < await uow.SiteDateAsync(now, ct))
                throw new CommandRejectedException("due_in_past", $"Line {line.LineNumber}: the due date {c.DueDate:yyyy-MM-dd} is in the past.");
            await uow.Store<ISalesStore>().UpdateOrderLineAsync(order.OrderNumber, line with { DueDate = c.DueDate }, ct);
            return ($"line {line.LineNumber} {line.PartNumber} now due {c.DueDate:yyyy-MM-dd} (was {line.DueDate:yyyy-MM-dd})", "DateChanged",
                line.DueDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), c.DueDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        }, ct);
    }

    public Task<CommandResult> CancelLineAsync(CancelSalesLine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(CancelSalesLine), c.OrderNumber, c, context, Permissions.SalesManage, true, async (uow, order, _) =>
        {
            var line = OpenLine(order, c.LineNumber);
            if ((await shipments.ShippedAsync(uow, order.OrderNumber, ct)).Any(s => s.OrderLine == line.LineNumber))
                throw new CommandRejectedException("line_shipped", $"Line {line.LineNumber} has shipped; close the order instead of cancelling the line.");
            if (order.Lines.Count(l => l.Status == SalesLineStatus.Open) == 1)
                throw new CommandRejectedException("last_line", "This is the order's last open line; cancel or close the order instead.");
            await uow.Store<ISalesStore>().UpdateOrderLineAsync(order.OrderNumber, line with { Status = SalesLineStatus.Cancelled }, ct);
            return ($"line {line.LineNumber} ({line.PartNumber}) cancelled", "LineCancelled", CanonicalJson.Serialize(line), nameof(SalesLineStatus.Cancelled));
        }, ct);
    }

    public Task<CommandResult> ApproveAsync(ApproveSalesOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(ApproveSalesOrder), c.OrderNumber, c, context, Permissions.SalesApprove, false, async (uow, order, now) =>
        {
            RequireDraft(order);
            if (order.CreatedBy == context.Actor)
                throw new CommandRejectedException("separation_of_duties", "You wrote this sales order; a different person must approve it.");
            await CustomerService.RequireActiveAsync(uow, order.CustomerCode, ct);
            var today = await uow.SiteDateAsync(now, ct);
            if (order.Lines.Where(l => l.Status == SalesLineStatus.Open).FirstOrDefault(l => l.DueDate < today) is { } late)
                throw new CommandRejectedException("due_in_past", $"Line {late.LineNumber} was due {late.DueDate:yyyy-MM-dd}; change its date before approving.");
            // Owner decision 2026-11-01: a customer on credit hold waits for a credit release of this order.
            await CreditGate.RequireOrderAsync(uow, order.CustomerCode, order.OrderNumber, order.Total, today, ct);
            await uow.Store<ISalesStore>().SetOrderStatusAsync(order.OrderNumber, SalesOrderStatus.Approved, now, context.Actor, ct);
            return ($"approved at {Amount(order.Total, order.Currency)}; its {order.Lines.Count(l => l.Status == SalesLineStatus.Open)} open line(s) become customer-order demand", "Approved",
                nameof(SalesOrderStatus.Draft), nameof(SalesOrderStatus.Approved));
        }, ct);
    }

    public Task<CommandResult> CloseAsync(CloseSalesOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(CloseSalesOrder), c.OrderNumber, c, context, Permissions.SalesManage, true, async (uow, order, now) =>
        {
            if (order.Status != SalesOrderStatus.Approved)
                throw new CommandRejectedException("not_approved", $"Sales order {order.OrderNumber} is {order.Status}; only an approved order is closed.");
            await uow.Store<ISalesStore>().SetOrderStatusAsync(order.OrderNumber, SalesOrderStatus.Closed, now, context.Actor, ct);
            return ("closed; nothing more will ship and its demand is closed. Shipped work can still be invoiced", "Closed", nameof(SalesOrderStatus.Approved), nameof(SalesOrderStatus.Closed));
        }, ct);
    }

    public Task<CommandResult> CancelAsync(CancelSalesOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(CancelSalesOrder), c.OrderNumber, c, context, Permissions.SalesManage, true, async (uow, order, now) =>
        {
            if (order.Status is not (SalesOrderStatus.Draft or SalesOrderStatus.Approved))
                throw new CommandRejectedException("order_closed", $"Sales order {order.OrderNumber} is {order.Status}.");
            if (order.Status == SalesOrderStatus.Approved && (await shipments.ShippedAsync(uow, order.OrderNumber, ct)).Count > 0)
                throw new CommandRejectedException("order_shipped", $"Sales order {order.OrderNumber} has shipments; close it instead of cancelling it.");
            await uow.Store<ISalesStore>().SetOrderStatusAsync(order.OrderNumber, SalesOrderStatus.Cancelled, now, context.Actor, ct);
            return ("cancelled", "Cancelled", order.Status.ToString(), nameof(SalesOrderStatus.Cancelled));
        }, ct);
    }

    private async Task<CommandOutcome> InsertAsync(IKernelUnitOfWork uow, CustomerInfo customer, string? customerPo, string? shipTo, long? quoteNumber,
        IReadOnlyList<SalesOrderLineInfo> lines, CommandContext context, DateTimeOffset now, CancellationToken ct)
    {
        var po = CustomerService.Text(customerPo, 60);
        if (po is not null && (await uow.Store<ISalesLookup>().ListOrdersAsync(ct)).FirstOrDefault(o => o.CustomerCode == customer.Code && o.Status != SalesOrderStatus.Cancelled
                && string.Equals(o.CustomerPo, po, StringComparison.OrdinalIgnoreCase)) is { } duplicate)
            throw new CommandRejectedException("duplicate_customer_po", $"{customer.Name}'s PO {po} is already on sales order {duplicate.OrderNumber}.");
        var order = new SalesOrderInfo(0, customer.Code, customer.Name, po, quoteNumber, CustomerService.Text(shipTo, 500) ?? customer.ShipTo, customer.Currency,
            SalesOrderStatus.Draft, context.Actor, now, null, null, null, null, lines);
        var store = uow.Store<ISalesStore>();
        var number = await store.InsertOrderAsync(order, ct);
        foreach (var line in lines) await store.InsertOrderLineAsync(number, line, ct);
        var id = Id(number);
        return new CommandOutcome(
            CommandResult.Ok(id, $"Sales order {id} drafted for {customer.Name}{(po is null ? string.Empty : $" (PO {po})")}: {lines.Count} line(s), {Amount(order.Total, order.Currency)}. A different person must approve it."),
            [pipeline.Event(new SalesOrderChanged(number, "Drafted"), context, CustomerService.SourceContext, Entity, id)],
            new AuditChange(Entity, id, null, CanonicalJson.Serialize(order with { OrderNumber = number })));
    }

    private static async Task<SalesOrderLineInfo> ValidLineAsync(IKernelUnitOfWork uow, int lineNumber, string rawPart, decimal quantity, decimal unitPrice, DateOnly dueDate, DateOnly today, CancellationToken ct)
    {
        Validate(lineNumber, quantity, unitPrice);
        if (dueDate < today || dueDate > today.AddYears(5))
            throw new CommandRejectedException("due_in_past", $"Line {lineNumber}: the due date must be from today up to five years out.");
        var (item, part) = await SellableAsync(uow, lineNumber, rawPart, ct);
        return new SalesOrderLineInfo(lineNumber, part, quantity, item.UnitOfMeasure, unitPrice, dueDate, SalesLineStatus.Open);
    }

    private static void RequireDraft(SalesOrderInfo order)
    {
        if (order.Status != SalesOrderStatus.Draft)
            throw new CommandRejectedException("not_draft", $"Sales order {order.OrderNumber} is {order.Status}; lines are added and approval happens only on a draft.");
    }

    private static SalesOrderLineInfo OpenLine(SalesOrderInfo order, int lineNumber)
    {
        if (order.Status is not (SalesOrderStatus.Draft or SalesOrderStatus.Approved))
            throw new CommandRejectedException("order_closed", $"Sales order {order.OrderNumber} is {order.Status}.");
        var line = order.Lines.FirstOrDefault(l => l.LineNumber == lineNumber)
            ?? throw new CommandRejectedException("line_not_found", $"Sales order {order.OrderNumber} has no line {lineNumber}.");
        return line.Status == SalesLineStatus.Open ? line : throw new CommandRejectedException("line_cancelled", $"Line {lineNumber} is cancelled.");
    }

    private async Task<CommandResult> ChangeAsync<TCommand>(string commandType, long orderNumber, TCommand command, CommandContext context, string permission, bool reasonRequired,
        Func<IKernelUnitOfWork, SalesOrderInfo, DateTimeOffset, Task<(string Message, string Change, string? Old, string? New)?>> change, CancellationToken ct)
    {
        var id = Id(orderNumber);
        var resource = new ResourceRef(Entity, id);
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            if (await read.Store<ISalesLookup>().FindOrderAsync(orderNumber, ct) is { } current)
            {
                resource = resource with { CustomerId = current.CustomerCode };
                // The approval carries the order's value so configured approval limits apply to it.
                if (commandType == nameof(ApproveSalesOrder)) resource = resource with { Amount = current.Total, Currency = current.Currency };
            }
        }

        var spec = new CommandSpec(CustomerService.SourceContext, commandType, permission, resource, reasonRequired);
        return await pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            await uow.Store<ISalesStore>().LockOrderAsync(orderNumber, ct);
            var order = await uow.Store<ISalesLookup>().FindOrderAsync(orderNumber, ct)
                ?? throw new CommandRejectedException("sales_order_not_found", $"Sales order {id} does not exist.");
            if (resource.Amount is { } approved && approved != order.Total)
                throw new CommandRejectedException("sales_order_changed", "The sales order changed while you were approving it. Review it and approve again.");
            var result = await change(uow, order, now);
            if (result is not { } r) return CommandOutcome.NoChange(CommandResult.NoChange(id, "Nothing changed."));
            EventEnvelope[] events = [pipeline.Event(new SalesOrderChanged(orderNumber, r.Change), context, CustomerService.SourceContext, Entity, id)];
            return new CommandOutcome(CommandResult.Ok(id, $"Sales order {id}: {r.Message}."), events, new AuditChange(Entity, id, r.Old, r.New));
        }, ct);
    }
}
