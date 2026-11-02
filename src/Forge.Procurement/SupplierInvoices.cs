using System.Globalization;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Procurement.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Procurement;

public sealed record SupplierInvoiceLineEntry(int PoLine, decimal Quantity, decimal UnitPrice);

/// <param name="DueDate">The supplier's due date; empty means 30 days from the invoice date.</param>
public sealed record EnterSupplierInvoice(long PoNumber, string SupplierInvoiceNumber, DateOnly InvoiceDate, DateOnly? DueDate, decimal Freight, decimal Tax, IReadOnlyList<SupplierInvoiceLineEntry> Lines);

public sealed record DecideSupplierInvoice(long InvoiceId, bool Approve, string Note);

public sealed record VoidSupplierInvoice(long InvoiceId);

public sealed record MarkSupplierInvoicePaid(long InvoiceId, DateOnly PaidOn, string? Reference);

public sealed record SetInvoiceMatchPolicy(decimal TolerancePercent, decimal ToleranceAmount);

/// <summary>A supplier invoice was entered or changed status; <see cref="Message"/> is the plain-words notice.</summary>
public sealed record SupplierInvoiceChanged(long InvoiceId, long PoNumber, string Status, string Message) : IDomainEvent
{
    public static string EventType => nameof(SupplierInvoiceChanged);

    public static int SchemaVersion => 1;
}

/// <summary>A match exception was approved or rejected (or the invoice voided): clears the purchasing managers' notice.</summary>
public sealed record SupplierInvoiceDecided(long InvoiceId, string Status) : IDomainEvent
{
    public static string EventType => nameof(SupplierInvoiceDecided);

    public static int SchemaVersion => 1;
}

public sealed record InvoiceMatchPolicyChanged(decimal TolerancePercent, decimal ToleranceAmount) : IDomainEvent
{
    public static string EventType => nameof(InvoiceMatchPolicyChanged);

    public static int SchemaVersion => 1;
}

public interface ISupplierInvoiceStore
{
    Task LockPurchaseOrderInvoicesAsync(long poNumber, CancellationToken ct);

    Task<bool> SupplierInvoiceNumberTakenAsync(string supplierCode, string number, CancellationToken ct);

    Task<long> InsertSupplierInvoiceAsync(SupplierInvoiceInfo invoice, CancellationToken ct);

    Task SetSupplierInvoiceStatusAsync(long invoiceId, SupplierInvoiceStatus status, string actor, DateTimeOffset at, string? note, CancellationToken ct);

    Task MarkSupplierInvoicePaidAsync(long invoiceId, DateOnly paidOn, string? reference, CancellationToken ct);

    Task InsertMatchPolicyAsync(InvoiceMatchPolicy policy, CancellationToken ct);
}

/// <summary>Supplier invoices and the three-way match (owner decisions 2026-11-01).</summary>
public sealed class SupplierInvoiceService(CommandPipeline pipeline, IPurchaseReceiptSource receipts)
{
    private const string Entity = "SupplierInvoice";

    public Task<CommandResult> EnterAsync(EnterSupplierInvoice c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var poKey = c.PoNumber.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(PurchasingService.SourceContext, nameof(EnterSupplierInvoice), Permissions.SupplierInvoicesEnter, ResourceRef.Of("PurchaseOrder", poKey));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await uow.RequireOnAsync(SiteFeatures.SupplierInvoices, ct);
            var store = uow.Store<ISupplierInvoiceStore>();
            await store.LockPurchaseOrderInvoicesAsync(c.PoNumber, ct);
            var lookup = uow.Store<IProcurementLookup>();
            var po = await lookup.FindPurchaseOrderAsync(c.PoNumber, ct) ?? throw new CommandRejectedException("po_not_found", $"Purchase order {c.PoNumber} does not exist.");
            if (po.Status is not (PurchaseOrderStatus.Approved or PurchaseOrderStatus.Closed))
                throw new CommandRejectedException("po_not_approved", $"Purchase order {c.PoNumber} is {po.Status}; invoices are entered against approved orders.");
            var supplierCode = po.SupplierCode ?? po.Supplier;
            var number = (c.SupplierInvoiceNumber ?? string.Empty).Trim();
            if (number.Length is 0 or > 60) throw new CommandRejectedException("invalid_invoice_number", "Give the supplier's invoice number (up to 60 characters).");
            if (await store.SupplierInvoiceNumberTakenAsync(supplierCode, number, ct))
                throw new CommandRejectedException("duplicate_invoice", $"{po.Supplier} invoice {number} is already entered.");
            var today = await uow.SiteDateAsync(now, ct);
            if (c.InvoiceDate > today) throw new CommandRejectedException("invalid_invoice_date", "The invoice date cannot be in the future.");
            var due = c.DueDate ?? c.InvoiceDate.AddDays(30);
            if (due < c.InvoiceDate) throw new CommandRejectedException("invalid_due_date", "The due date cannot be before the invoice date.");
            if (c.Freight < 0 || c.Tax < 0) throw new CommandRejectedException("invalid_amount", "Freight and tax cannot be negative.");
            if (c.Lines is not { Count: > 0 }) throw new CommandRejectedException("lines_required", "Enter at least one invoice line.");

            var received = await receipts.ReceivedAsync(uow, c.PoNumber, ct);
            var billed = (await uow.Store<ISupplierInvoiceLookup>().ListSupplierInvoicesAsync(ct))
                .Where(i => i.PoNumber == c.PoNumber && i.Status is SupplierInvoiceStatus.Approved or SupplierInvoiceStatus.Exception or SupplierInvoiceStatus.Paid)
                .SelectMany(i => i.Lines).GroupBy(l => l.PoLine).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
            var policy = await uow.Store<ISupplierInvoiceLookup>().MatchPolicyAsync(ct);
            var lines = new List<SupplierInvoiceLine>();
            var n = 0;
            foreach (var entry in c.Lines)
            {
                var poLine = po.Lines.FirstOrDefault(l => l.LineNumber == entry.PoLine)
                    ?? throw new CommandRejectedException("po_line_not_found", $"Purchase order {c.PoNumber} has no line {entry.PoLine}.");
                if (entry.Quantity <= 0 || entry.UnitPrice < 0) throw new CommandRejectedException("invalid_line", $"Line {entry.PoLine}: quantity must be above zero and price not negative.");
                var open = Math.Max(0, received.GetValueOrDefault((c.PoNumber, entry.PoLine)) - billed.GetValueOrDefault(entry.PoLine)
                    - lines.Where(l => l.PoLine == entry.PoLine).Sum(l => l.Quantity));
                var amount = Round(entry.Quantity * entry.UnitPrice);
                string? problem = null;
                decimal variance;
                if (poLine.UnitPrice is not { } poPrice)
                {
                    variance = 0;
                    problem = "the purchase order line has no price to match";
                }
                else
                {
                    var payable = Math.Min(entry.Quantity, open);
                    variance = Round(amount - payable * poPrice);
                    if (entry.Quantity > open)
                        problem = string.Create(CultureInfo.InvariantCulture, $"billed {entry.Quantity} but only {open} received and not yet invoiced");
                    else if (entry.UnitPrice != poPrice)
                        problem = string.Create(CultureInfo.InvariantCulture, $"price {entry.UnitPrice} against PO price {poPrice}");
                }

                lines.Add(new SupplierInvoiceLine(++n, entry.PoLine, poLine.PartNumber, entry.Quantity, entry.UnitPrice, amount, poLine.UnitPrice, open, variance, problem));
            }

            var subtotal = lines.Sum(l => l.Amount);
            var totalVariance = lines.Sum(l => l.Variance);
            var expected = subtotal - totalVariance;
            var overReceived = lines.Any(l => l.Quantity > l.ReceivedOpen);
            var unpriced = lines.Any(l => l.PoUnitPrice is null);
            var within = !overReceived && !unpriced && policy.Within(totalVariance, expected);
            var currency = po.Lines.Count > 0 ? po.Lines[0].Currency : "USD";
            var status = within ? SupplierInvoiceStatus.Approved : SupplierInvoiceStatus.Exception;
            var summary = within
                ? (totalVariance == 0 ? "Matches the purchase order and receipts." : string.Create(CultureInfo.InvariantCulture, $"Within tolerance: variance {totalVariance:0.00} {currency}."))
                : string.Join("; ", lines.Where(l => l.Problem is not null).Select(l => string.Create(CultureInfo.InvariantCulture, $"line {l.PoLine}: {l.Problem}"))
                    .DefaultIfEmpty(string.Create(CultureInfo.InvariantCulture, $"variance {totalVariance:0.00} is beyond the tolerance ({policy.TolerancePercent}% and {policy.ToleranceAmount:0.00})")));
            var invoice = new SupplierInvoiceInfo(0, supplierCode, po.Supplier, number, c.PoNumber, c.InvoiceDate, due, currency, subtotal, Round(c.Freight), Round(c.Tax),
                subtotal + Round(c.Freight) + Round(c.Tax), totalVariance, status, summary, context.Actor, now, within ? "system:three-way-match" : null, within ? now : null, null, null, null, lines);
            var id = await store.InsertSupplierInvoiceAsync(invoice, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            var message = within
                ? $"{po.Supplier} invoice {number} on PO {c.PoNumber} is approved for payment ({summary})"
                : $"{po.Supplier} invoice {number} on PO {c.PoNumber} needs a purchasing manager: {summary}.";
            return new CommandOutcome(CommandResult.Ok(key, message),
                [pipeline.Event(new SupplierInvoiceChanged(id, c.PoNumber, status.ToString(), message), context, PurchasingService.SourceContext, Entity, key)],
                new AuditChange(Entity, key, null, CanonicalJson.Serialize(invoice with { InvoiceId = id })));
        }, ct);
    }

    public Task<CommandResult> DecideAsync(DecideSupplierInvoice c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(DecideSupplierInvoice), c.InvoiceId, c, context, Permissions.SupplierInvoicesApprove, true, async (uow, invoice, now) =>
        {
            if (invoice.Status != SupplierInvoiceStatus.Exception)
                throw new CommandRejectedException("not_exception", $"Invoice {invoice.SupplierInvoiceNumber} is {invoice.Status}; only a match exception is decided.");
            if (invoice.EnteredBy == context.Actor)
                throw new CommandRejectedException("separation_of_duties", "You entered this invoice; a different person must decide it.");
            var note = BusinessCode.RequireName(c.Note, "Note");
            var status = c.Approve ? SupplierInvoiceStatus.Approved : SupplierInvoiceStatus.Rejected;
            await uow.Store<ISupplierInvoiceStore>().SetSupplierInvoiceStatusAsync(invoice.InvoiceId, status, context.Actor, now, note, ct);
            return (status, c.Approve ? "approved for payment despite the match exception" : "rejected; ask the supplier for a corrected invoice", note);
        }, ct);
    }

    public Task<CommandResult> VoidAsync(VoidSupplierInvoice c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(VoidSupplierInvoice), c.InvoiceId, c, context, Permissions.SupplierInvoicesEnter, true, async (uow, invoice, now) =>
        {
            if (invoice.Status is SupplierInvoiceStatus.Paid or SupplierInvoiceStatus.Voided)
                throw new CommandRejectedException("not_voidable", $"Invoice {invoice.SupplierInvoiceNumber} is {invoice.Status}.");
            await uow.Store<ISupplierInvoiceStore>().SetSupplierInvoiceStatusAsync(invoice.InvoiceId, SupplierInvoiceStatus.Voided, context.Actor, now, context.Reason, ct);
            return (SupplierInvoiceStatus.Voided, "voided; its quantities can be invoiced again", context.Reason);
        }, ct);
    }

    public Task<CommandResult> MarkPaidAsync(MarkSupplierInvoicePaid c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(MarkSupplierInvoicePaid), c.InvoiceId, c, context, Permissions.SupplierInvoicesEnter, false, async (uow, invoice, now) =>
        {
            if (invoice.Status != SupplierInvoiceStatus.Approved)
                throw new CommandRejectedException("not_approved", $"Invoice {invoice.SupplierInvoiceNumber} is {invoice.Status}; only an approved invoice is paid.");
            if (c.PaidOn > await uow.SiteDateAsync(now, ct)) throw new CommandRejectedException("invalid_paid_on", "The payment date cannot be in the future.");
            var reference = string.IsNullOrWhiteSpace(c.Reference) ? null : c.Reference.Trim();
            var store = uow.Store<ISupplierInvoiceStore>();
            await store.MarkSupplierInvoicePaidAsync(invoice.InvoiceId, c.PaidOn, reference, ct);
            await store.SetSupplierInvoiceStatusAsync(invoice.InvoiceId, SupplierInvoiceStatus.Paid, invoice.DecidedBy ?? context.Actor, invoice.DecidedAt ?? now, invoice.DecisionNote, ct);
            return (SupplierInvoiceStatus.Paid, string.Create(CultureInfo.InvariantCulture, $"paid on {c.PaidOn:yyyy-MM-dd}{(reference is null ? string.Empty : $" ({reference})")}"), invoice.DecisionNote);
        }, ct);
    }

    public Task<CommandResult> SetPolicyAsync(SetInvoiceMatchPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(PurchasingService.SourceContext, nameof(SetInvoiceMatchPolicy), Permissions.SupplierInvoicesApprove, ResourceRef.Of("InvoiceMatchPolicy", "site"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (c.TolerancePercent is < 0 or > 25 || c.ToleranceAmount is < 0 or > 100_000)
                throw new CommandRejectedException("invalid_tolerance", "Tolerance is 0 to 25 percent and 0 to 100,000 in amount.");
            var before = await uow.Store<ISupplierInvoiceLookup>().MatchPolicyAsync(ct);
            var policy = new InvoiceMatchPolicy(c.TolerancePercent, Round(c.ToleranceAmount), context.Actor, now);
            if (before.TolerancePercent == policy.TolerancePercent && before.ToleranceAmount == policy.ToleranceAmount)
                return CommandOutcome.NoChange(CommandResult.NoChange("site", "The tolerance is already that."));
            await uow.Store<ISupplierInvoiceStore>().InsertMatchPolicyAsync(policy, ct);
            return new CommandOutcome(
                CommandResult.Ok("site", string.Create(CultureInfo.InvariantCulture, $"Invoices within {policy.TolerancePercent}% and {policy.ToleranceAmount:0.00} of the purchase order are approved for payment automatically.")),
                [pipeline.Event(new InvoiceMatchPolicyChanged(policy.TolerancePercent, policy.ToleranceAmount), context, PurchasingService.SourceContext, "InvoiceMatchPolicy", "site")],
                new AuditChange("InvoiceMatchPolicy", "site", CanonicalJson.Serialize(before), CanonicalJson.Serialize(policy)));
        }, ct);
    }

    private Task<CommandResult> ChangeAsync<TCommand>(string type, long invoiceId, TCommand command, CommandContext context, string permission, bool reasonRequired,
        Func<Forge.Kernel.Access.IKernelUnitOfWork, SupplierInvoiceInfo, DateTimeOffset, Task<(SupplierInvoiceStatus Status, string What, string? Note)>> change, CancellationToken ct)
    {
        var key = invoiceId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(PurchasingService.SourceContext, type, permission, ResourceRef.Of(Entity, key), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            await uow.RequireOnAsync(SiteFeatures.SupplierInvoices, ct);
            var invoice = await uow.Store<ISupplierInvoiceLookup>().FindSupplierInvoiceAsync(invoiceId, ct)
                ?? throw new CommandRejectedException("invoice_not_found", $"Supplier invoice {key} does not exist.");
            await uow.Store<ISupplierInvoiceStore>().LockPurchaseOrderInvoicesAsync(invoice.PoNumber, ct);
            var (status, what, _) = await change(uow, invoice, now);
            var message = $"{invoice.SupplierName} invoice {invoice.SupplierInvoiceNumber} (PO {invoice.PoNumber}) {what}.";
            var events = new List<EventEnvelope> { pipeline.Event(new SupplierInvoiceChanged(invoice.InvoiceId, invoice.PoNumber, status.ToString(), message), context, PurchasingService.SourceContext, Entity, key) };
            if (invoice.Status == SupplierInvoiceStatus.Exception)
                events.Add(pipeline.Event(new SupplierInvoiceDecided(invoice.InvoiceId, status.ToString()), context, PurchasingService.SourceContext, Entity, key));
            var after = await uow.Store<ISupplierInvoiceLookup>().FindSupplierInvoiceAsync(invoiceId, ct);
            return new CommandOutcome(CommandResult.Ok(key, message), events, new AuditChange(Entity, key, CanonicalJson.Serialize(invoice), CanonicalJson.Serialize(after)));
        }, ct);
    }

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
