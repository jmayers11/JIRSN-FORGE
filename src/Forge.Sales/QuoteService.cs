using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;
using Forge.Sales.Contracts;

namespace Forge.Sales;

public sealed record NewQuoteLine(string PartNumber, decimal Quantity, decimal UnitPrice, int LeadTimeDays, string? Description = null);

public sealed record CreateQuote(string Customer, DateOnly ValidUntil, IReadOnlyList<NewQuoteLine> Lines, string? Notes = null);

public sealed record AddQuoteLine(long QuoteNumber, string PartNumber, decimal Quantity, decimal UnitPrice, int LeadTimeDays, string? Description = null);

public sealed record RemoveQuoteLine(long QuoteNumber, int LineNumber);

public sealed record ApproveQuote(long QuoteNumber);

/// <summary>The customer said no, or the quote lapsed.</summary>
public sealed record DeclineQuote(long QuoteNumber);

public sealed record CancelQuote(long QuoteNumber);

/// <summary>
/// Quotes. A Sales Representative drafts one at the customer's prices and lead times; a Sales Manager who did not write it approves it
/// (dollar limits on that approval are configurable); an approved quote that has not expired becomes a sales order.
/// </summary>
public sealed class QuoteService(CommandPipeline pipeline, IKernelUnitOfWorkFactory unitOfWork)
{
    private const string Entity = "Quote";

    public Task<CommandResult> CreateAsync(CreateQuote c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(CustomerService.SourceContext, nameof(CreateQuote), Permissions.SalesManage, ResourceRef.Of(Entity));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var customer = await CustomerService.RequireActiveAsync(uow, c.Customer, ct);
            if (c.Lines is null || c.Lines.Count is 0 or > 200)
                throw new CommandRejectedException("lines_required", "A quote needs 1 to 200 lines.");
            var today = await uow.SiteDateAsync(now, ct);
            if (c.ValidUntil < today || c.ValidUntil > today.AddDays(366))
                throw new CommandRejectedException("invalid_validity", "A quote is valid from today for at most a year.");
            var lines = new List<QuoteLineInfo>();
            foreach (var (line, index) in c.Lines.Select((l, i) => (l, i)))
                lines.Add(await ValidLineAsync(uow, index + 1, line.PartNumber, line.Quantity, line.UnitPrice, line.LeadTimeDays, line.Description, ct));
            var quote = new QuoteInfo(0, customer.Code, QuoteStatus.Draft, c.ValidUntil, customer.Currency, CustomerService.Text(c.Notes, 2000), context.Actor, now,
                null, null, null, null, null, lines);
            var store = uow.Store<ISalesStore>();
            var number = await store.InsertQuoteAsync(quote, ct);
            foreach (var line in lines) await store.InsertQuoteLineAsync(number, line, ct);
            var id = Id(number);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Quote {id} drafted for {customer.Name}: {lines.Count} line(s), {Amount(quote.Total, quote.Currency)}. A different person must approve it."),
                [pipeline.Event(new QuoteChanged(number, "Drafted"), context, CustomerService.SourceContext, Entity, id)],
                new AuditChange(Entity, id, null, CanonicalJson.Serialize(quote with { QuoteNumber = number })));
        }, ct);
    }

    public Task<CommandResult> AddLineAsync(AddQuoteLine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(AddQuoteLine), c.QuoteNumber, c, context, Permissions.SalesManage, false, async (uow, quote, _) =>
        {
            RequireDraft(quote);
            if (quote.Lines.Count >= 200) throw new CommandRejectedException("too_many_lines", "A quote has at most 200 lines.");
            var line = await ValidLineAsync(uow, quote.Lines.Count == 0 ? 1 : quote.Lines.Max(l => l.LineNumber) + 1, c.PartNumber, c.Quantity, c.UnitPrice, c.LeadTimeDays, c.Description, ct);
            await uow.Store<ISalesStore>().InsertQuoteLineAsync(quote.QuoteNumber, line, ct);
            return ($"line {line.LineNumber} added: {Qty(line.Quantity)} {line.PartNumber} at {Amount(line.UnitPrice, quote.Currency)}", "LineAdded", null, CanonicalJson.Serialize(line));
        }, ct);
    }

    public Task<CommandResult> RemoveLineAsync(RemoveQuoteLine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(RemoveQuoteLine), c.QuoteNumber, c, context, Permissions.SalesManage, false, async (uow, quote, _) =>
        {
            RequireDraft(quote);
            var line = quote.Lines.FirstOrDefault(l => l.LineNumber == c.LineNumber)
                ?? throw new CommandRejectedException("line_not_found", $"Quote {quote.QuoteNumber} has no line {c.LineNumber}.");
            if (quote.Lines.Count == 1) throw new CommandRejectedException("last_line", "A quote keeps at least one line; cancel the quote instead.");
            await uow.Store<ISalesStore>().DeleteQuoteLineAsync(quote.QuoteNumber, line.LineNumber, ct);
            return ($"line {line.LineNumber} ({line.PartNumber}) removed", "LineRemoved", CanonicalJson.Serialize(line), null);
        }, ct);
    }

    public Task<CommandResult> ApproveAsync(ApproveQuote c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(ApproveQuote), c.QuoteNumber, c, context, Permissions.SalesApprove, false, async (uow, quote, now) =>
        {
            RequireDraft(quote);
            if (quote.CreatedBy == context.Actor)
                throw new CommandRejectedException("separation_of_duties", "You wrote this quote; a different person must approve it.");
            await CustomerService.RequireActiveAsync(uow, quote.CustomerCode, ct);
            if (quote.ValidUntil < await uow.SiteDateAsync(now, ct))
                throw new CommandRejectedException("quote_expired", $"Quote {quote.QuoteNumber} expired on {quote.ValidUntil:yyyy-MM-dd}.");
            await uow.Store<ISalesStore>().SetQuoteStatusAsync(quote.QuoteNumber, QuoteStatus.Approved, now, context.Actor, null, ct);
            return ($"approved at {Amount(quote.Total, quote.Currency)}, valid until {quote.ValidUntil:yyyy-MM-dd}", "Approved", nameof(QuoteStatus.Draft), nameof(QuoteStatus.Approved));
        }, ct);
    }

    public Task<CommandResult> DeclineAsync(DeclineQuote c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(DeclineQuote), c.QuoteNumber, c, context, Permissions.SalesManage, true, async (uow, quote, now) =>
        {
            if (quote.Status is not (QuoteStatus.Draft or QuoteStatus.Approved))
                throw new CommandRejectedException("quote_closed", $"Quote {quote.QuoteNumber} is {quote.Status}.");
            await uow.Store<ISalesStore>().SetQuoteStatusAsync(quote.QuoteNumber, QuoteStatus.Declined, now, context.Actor, null, ct);
            return ("declined", "Declined", quote.Status.ToString(), nameof(QuoteStatus.Declined));
        }, ct);
    }

    public Task<CommandResult> CancelAsync(CancelQuote c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(CancelQuote), c.QuoteNumber, c, context, Permissions.SalesManage, true, async (uow, quote, now) =>
        {
            RequireDraft(quote);
            await uow.Store<ISalesStore>().SetQuoteStatusAsync(quote.QuoteNumber, QuoteStatus.Cancelled, now, context.Actor, null, ct);
            return ("cancelled", "Cancelled", nameof(QuoteStatus.Draft), nameof(QuoteStatus.Cancelled));
        }, ct);
    }

    internal static async Task<(ItemInfo Item, string Part)> SellableAsync(IKernelUnitOfWork uow, int lineNumber, string rawPart, CancellationToken ct)
    {
        var part = BusinessCode.Require(rawPart, "Part");
        var item = await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct)
            ?? throw new CommandRejectedException("unknown_part", $"Line {lineNumber}: part {part} does not exist.");
        return (item, part);
    }

    internal static void Validate(int lineNumber, decimal quantity, decimal unitPrice)
    {
        if (quantity <= 0 || quantity > 100_000_000)
            throw new CommandRejectedException("invalid_quantity", $"Line {lineNumber}: quantity must be above zero.");
        if (unitPrice < 0 || unitPrice > 100_000_000m || decimal.Round(unitPrice, 4) != unitPrice)
            throw new CommandRejectedException("invalid_unit_price", $"Line {lineNumber}: the unit price must be zero or more, to at most four decimal places.");
    }

    internal static string Id(long number) => number.ToString(CultureInfo.InvariantCulture);

    internal static string Qty(decimal quantity) => quantity.ToString("0.######", CultureInfo.InvariantCulture);

    internal static string Amount(decimal amount, string currency) => string.Create(CultureInfo.InvariantCulture, $"{amount:#,0.00} {currency}");

    private static async Task<QuoteLineInfo> ValidLineAsync(IKernelUnitOfWork uow, int lineNumber, string rawPart, decimal quantity, decimal unitPrice, int leadTimeDays, string? description, CancellationToken ct)
    {
        Validate(lineNumber, quantity, unitPrice);
        if (leadTimeDays is < 0 or > 730)
            throw new CommandRejectedException("invalid_lead_time", $"Line {lineNumber}: lead time is 0 to 730 days.");
        var (item, part) = await SellableAsync(uow, lineNumber, rawPart, ct);
        return new QuoteLineInfo(lineNumber, part, quantity, unitPrice, leadTimeDays, CustomerService.Text(description, 500) ?? item.Description);
    }

    private static void RequireDraft(QuoteInfo quote)
    {
        if (quote.Status != QuoteStatus.Draft)
            throw new CommandRejectedException("not_draft", $"Quote {quote.QuoteNumber} is {quote.Status}; only a draft can change.");
    }

    private async Task<CommandResult> ChangeAsync<TCommand>(string commandType, long quoteNumber, TCommand command, CommandContext context, string permission, bool reasonRequired,
        Func<IKernelUnitOfWork, QuoteInfo, DateTimeOffset, Task<(string Message, string Change, string? Old, string? New)?>> change, CancellationToken ct)
    {
        var id = Id(quoteNumber);
        var resource = new ResourceRef(Entity, id);
        if (commandType == nameof(ApproveQuote))
        {
            // The approval carries the quote's value so configured approval limits apply to it.
            await using var read = await unitOfWork.BeginSnapshotAsync(ct);
            if (await read.Store<ISalesLookup>().FindQuoteAsync(quoteNumber, ct) is { } current)
                resource = resource with { CustomerId = current.CustomerCode, Amount = current.Total, Currency = current.Currency };
        }

        var spec = new CommandSpec(CustomerService.SourceContext, commandType, permission, resource, reasonRequired);
        return await pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            await uow.Store<ISalesStore>().LockQuoteAsync(quoteNumber, ct);
            var quote = await uow.Store<ISalesLookup>().FindQuoteAsync(quoteNumber, ct)
                ?? throw new CommandRejectedException("quote_not_found", $"Quote {id} does not exist.");
            if (resource.Amount is { } approved && approved != quote.Total)
                throw new CommandRejectedException("quote_changed", "The quote changed while you were approving it. Review it and approve again.");
            var result = await change(uow, quote, now);
            if (result is not { } r) return CommandOutcome.NoChange(CommandResult.NoChange(id, "Nothing changed."));
            EventEnvelope[] events = [pipeline.Event(new QuoteChanged(quoteNumber, r.Change), context, CustomerService.SourceContext, Entity, id)];
            return new CommandOutcome(CommandResult.Ok(id, $"Quote {id}: {r.Message}."), events, new AuditChange(Entity, id, r.Old, r.New));
        }, ct);
    }
}
