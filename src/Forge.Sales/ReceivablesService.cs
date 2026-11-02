using System.Globalization;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;
using Forge.Sales.Contracts;

namespace Forge.Sales;

/// <param name="Applications">Invoices to apply the payment to; empty applies it to the oldest open invoices first. Anything left is on account.</param>
public sealed record RecordPayment(string CustomerCode, DateOnly ReceivedOn, decimal Amount, PaymentMethod Method, string? Reference, string? Note, IReadOnlyList<PaymentApplication>? Applications);

public sealed record VoidPayment(long PaymentId);

/// <param name="CreditLimit">Null removes the limit.</param>
public sealed record SetCreditLimit(string CustomerCode, decimal? CreditLimit);

public sealed record SetCreditPolicy(int? PastDueDays, bool IncludeOpenOrders);

/// <summary>Lets one sales order (its approval and shipments) or one shipment through a customer's credit hold.</summary>
public sealed record ReleaseCreditHold(string CustomerCode, long? SalesOrderNumber, long? ShipmentId);

public sealed record PaymentChanged(long PaymentId, string CustomerCode, string Change) : IDomainEvent
{
    public static string EventType => nameof(PaymentChanged);

    public static int SchemaVersion => 1;
}

public sealed record CreditChanged(string CustomerCode, string Change, string Message) : IDomainEvent
{
    public static string EventType => nameof(CreditChanged);

    public static int SchemaVersion => 1;
}

public interface IReceivablesStore
{
    Task LockCustomerReceivablesAsync(string customerCode, CancellationToken ct);

    Task<long> InsertPaymentAsync(PaymentInfo payment, CancellationToken ct);

    Task VoidPaymentAsync(long paymentId, string actor, DateTimeOffset at, string reason, CancellationToken ct);

    Task UpsertCustomerCreditAsync(CustomerCredit credit, CancellationToken ct);

    Task InsertCreditPolicyAsync(CreditPolicy policy, CancellationToken ct);

    Task<long> InsertCreditReleaseAsync(CreditRelease release, CancellationToken ct);
}

/// <summary>Customer payments, credit limits and credit holds (owner decisions 2026-11-01).</summary>
public sealed class ReceivablesService(CommandPipeline pipeline)
{
    private const string PaymentEntity = "Payment";
    private const string CreditEntity = "CustomerCredit";

    public Task<CommandResult> RecordPaymentAsync(RecordPayment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var customerCode = BusinessCode.Require(c.CustomerCode, "Customer");
        var spec = new CommandSpec(CustomerService.SourceContext, nameof(RecordPayment), Permissions.SalesPaymentsRecord, ResourceRef.Of(CreditEntity, customerCode) with { CustomerId = customerCode });
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await uow.RequireOnAsync(SiteFeatures.Receivables, ct);
            var customer = await uow.Store<ISalesLookup>().FindCustomerAsync(customerCode, ct) ?? throw new CommandRejectedException("customer_not_found", $"Customer {customerCode} does not exist.");
            if (c.Amount <= 0 || c.Amount > 1_000_000_000) throw new CommandRejectedException("invalid_amount", "The payment amount must be above zero.");
            var amount = Money.Round(c.Amount);
            var today = await uow.SiteDateAsync(now, ct);
            if (c.ReceivedOn > today) throw new CommandRejectedException("invalid_received_on", "The payment cannot be received in the future.");
            var store = uow.Store<IReceivablesStore>();
            await store.LockCustomerReceivablesAsync(customerCode, ct);
            var open = (await Receivables.BalancesAsync(uow, today, customerCode, ct)).Where(b => b.Balance > 0).ToList();
            var applications = new List<PaymentApplication>();
            if (c.Applications is { Count: > 0 })
            {
                foreach (var a in c.Applications)
                {
                    var invoice = open.FirstOrDefault(b => b.InvoiceNumber == a.InvoiceNumber)
                        ?? throw new CommandRejectedException("invoice_not_open", $"Invoice {a.InvoiceNumber} is not an open invoice of {customer.Name}.");
                    var applied = Money.Round(a.Amount);
                    if (applied <= 0 || applied > invoice.Balance)
                        throw new CommandRejectedException("invalid_application", string.Create(CultureInfo.InvariantCulture, $"Invoice {a.InvoiceNumber} has {invoice.Balance:N2} open; apply more than zero and no more than that."));
                    applications.Add(new PaymentApplication(a.InvoiceNumber, applied));
                }

                if (applications.Sum(a => a.Amount) > amount) throw new CommandRejectedException("over_applied", "The applications add up to more than the payment.");
            }
            else
            {
                var left = amount;
                foreach (var invoice in open)
                {
                    if (left <= 0) break;
                    var applied = Math.Min(left, invoice.Balance);
                    applications.Add(new PaymentApplication(invoice.InvoiceNumber, applied));
                    left -= applied;
                }
            }

            var payment = new PaymentInfo(0, customerCode, c.ReceivedOn, amount, customer.Currency, c.Method, Optional(c.Reference), Optional(c.Note), PaymentStatus.Recorded,
                context.Actor, now, null, null, null, applications);
            var id = await store.InsertPaymentAsync(payment, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            var message = string.Create(CultureInfo.InvariantCulture,
                $"Payment of {amount:N2} {customer.Currency} from {customer.Name} recorded{(applications.Count > 0 ? $", applied to invoice(s) {string.Join(", ", applications.Select(a => a.InvoiceNumber))}" : string.Empty)}{(payment.Unapplied > 0 ? $"; {payment.Unapplied:N2} on account" : string.Empty)}.");
            return new CommandOutcome(CommandResult.Ok(key, message),
                [pipeline.Event(new PaymentChanged(id, customerCode, "Recorded"), context, CustomerService.SourceContext, PaymentEntity, key)],
                new AuditChange(PaymentEntity, key, null, CanonicalJson.Serialize(payment with { PaymentId = id })));
        }, ct);
    }

    public Task<CommandResult> VoidPaymentAsync(VoidPayment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.PaymentId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(CustomerService.SourceContext, nameof(VoidPayment), Permissions.SalesPaymentsRecord, ResourceRef.Of(PaymentEntity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await uow.RequireOnAsync(SiteFeatures.Receivables, ct);
            var payment = await uow.Store<IReceivablesLookup>().FindPaymentAsync(c.PaymentId, ct) ?? throw new CommandRejectedException("payment_not_found", $"Payment {key} does not exist.");
            if (payment.Status == PaymentStatus.Voided) return CommandOutcome.NoChange(CommandResult.NoChange(key, "Already voided."));
            await uow.Store<IReceivablesStore>().LockCustomerReceivablesAsync(payment.CustomerCode, ct);
            await uow.Store<IReceivablesStore>().VoidPaymentAsync(c.PaymentId, context.Actor, now, context.Reason!, ct);
            return new CommandOutcome(CommandResult.Ok(key, $"Payment {key} voided; the invoices it paid are open again."),
                [pipeline.Event(new PaymentChanged(c.PaymentId, payment.CustomerCode, "Voided"), context, CustomerService.SourceContext, PaymentEntity, key)],
                new AuditChange(PaymentEntity, key, CanonicalJson.Serialize(payment),
                    CanonicalJson.Serialize(payment with { Status = PaymentStatus.Voided, VoidedBy = context.Actor, VoidedAt = now, VoidReason = context.Reason })));
        }, ct);
    }

    public Task<CommandResult> SetCreditLimitAsync(SetCreditLimit c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var customerCode = BusinessCode.Require(c.CustomerCode, "Customer");
        var spec = new CommandSpec(CustomerService.SourceContext, nameof(SetCreditLimit), Permissions.SalesCreditManage, ResourceRef.Of(CreditEntity, customerCode) with { CustomerId = customerCode },
            ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await uow.RequireOnAsync(SiteFeatures.Receivables, ct);
            var customer = await uow.Store<ISalesLookup>().FindCustomerAsync(customerCode, ct) ?? throw new CommandRejectedException("customer_not_found", $"Customer {customerCode} does not exist.");
            if (c.CreditLimit is < 0 or > 1_000_000_000) throw new CommandRejectedException("invalid_credit_limit", "The credit limit cannot be negative.");
            var limit = c.CreditLimit is { } l ? Money.Round(l) : (decimal?)null;
            var before = (await uow.Store<IReceivablesLookup>().ListCustomerCreditAsync(ct)).FirstOrDefault(x => x.CustomerCode == customerCode);
            if (before?.CreditLimit == limit && (before is not null || limit is null)) return CommandOutcome.NoChange(CommandResult.NoChange(customerCode, "The credit limit is already that."));
            var credit = new CustomerCredit(customerCode, limit, context.Actor, now);
            await uow.Store<IReceivablesStore>().UpsertCustomerCreditAsync(credit, ct);
            var message = limit is { } v ? string.Create(CultureInfo.InvariantCulture, $"{customer.Name}'s credit limit is {v:N2} {customer.Currency}.") : $"{customer.Name} has no credit limit.";
            return new CommandOutcome(CommandResult.Ok(customerCode, message),
                [pipeline.Event(new CreditChanged(customerCode, "Limit", message), context, CustomerService.SourceContext, CreditEntity, customerCode)],
                new AuditChange(CreditEntity, customerCode, before is null ? null : CanonicalJson.Serialize(before), CanonicalJson.Serialize(credit)));
        }, ct);
    }

    public Task<CommandResult> SetCreditPolicyAsync(SetCreditPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(CustomerService.SourceContext, nameof(SetCreditPolicy), Permissions.SalesCreditManage, ResourceRef.Of("CreditPolicy", "site"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await uow.RequireOnAsync(SiteFeatures.Receivables, ct);
            if (c.PastDueDays is < 0 or > 365) throw new CommandRejectedException("invalid_past_due_days", "Past-due days are 0 to 365, or empty to never hold for lateness.");
            var before = await uow.Store<IReceivablesLookup>().CreditPolicyAsync(ct);
            var policy = new CreditPolicy(c.PastDueDays, c.IncludeOpenOrders, context.Actor, now);
            if (before.PastDueDays == policy.PastDueDays && before.IncludeOpenOrders == policy.IncludeOpenOrders)
                return CommandOutcome.NoChange(CommandResult.NoChange("site", "The credit policy is already that."));
            await uow.Store<IReceivablesStore>().InsertCreditPolicyAsync(policy, ct);
            var message = (policy.PastDueDays is { } d ? $"Customers with an invoice more than {d} days past due go on credit hold" : "Lateness alone never puts a customer on credit hold")
                + (policy.IncludeOpenOrders ? "; open orders count toward credit limits." : "; only open invoices count toward credit limits.");
            return new CommandOutcome(CommandResult.Ok("site", message),
                [pipeline.Event(new CreditChanged("*", "Policy", message), context, CustomerService.SourceContext, "CreditPolicy", "site")],
                new AuditChange("CreditPolicy", "site", CanonicalJson.Serialize(before), CanonicalJson.Serialize(policy)));
        }, ct);
    }

    public Task<CommandResult> ReleaseAsync(ReleaseCreditHold c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var customerCode = BusinessCode.Require(c.CustomerCode, "Customer");
        var spec = new CommandSpec(CustomerService.SourceContext, nameof(ReleaseCreditHold), Permissions.SalesCreditRelease, ResourceRef.Of(CreditEntity, customerCode) with { CustomerId = customerCode },
            ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await uow.RequireOnAsync(SiteFeatures.Receivables, ct);
            if ((c.SalesOrderNumber is null) == (c.ShipmentId is null))
                throw new CommandRejectedException("release_target_required", "Release either one sales order (its approval and shipments) or one shipment.");
            var customer = await uow.Store<ISalesLookup>().FindCustomerAsync(customerCode, ct) ?? throw new CommandRejectedException("customer_not_found", $"Customer {customerCode} does not exist.");
            string what;
            if (c.SalesOrderNumber is { } so)
            {
                var order = await uow.Store<ISalesLookup>().FindOrderAsync(so, ct);
                if (order is null || order.CustomerCode != customerCode) throw new CommandRejectedException("order_not_found", $"{customer.Name} has no sales order {so}.");
                if (order.CreatedBy == context.Actor) throw new CommandRejectedException("separation_of_duties", "You wrote this sales order; a different person must release it from credit hold.");
                what = string.Create(CultureInfo.InvariantCulture, $"sales order {so} (its approval and shipments)");
            }
            else
            {
                what = string.Create(CultureInfo.InvariantCulture, $"shipment {c.ShipmentId}");
            }

            var releases = await uow.Store<IReceivablesLookup>().ListCreditReleasesAsync(customerCode, ct);
            if (releases.Any(r => r.SalesOrderNumber == c.SalesOrderNumber && r.ShipmentId == c.ShipmentId))
                return CommandOutcome.NoChange(CommandResult.NoChange(customerCode, $"{what} is already released."));
            var release = new CreditRelease(0, customerCode, c.SalesOrderNumber, c.ShipmentId, context.Reason!, context.Actor, now);
            var id = await uow.Store<IReceivablesStore>().InsertCreditReleaseAsync(release, ct);
            var message = $"{customer.Name}: {what} released from credit hold ({context.Reason}).";
            return new CommandOutcome(CommandResult.Ok(customerCode, message),
                [pipeline.Event(new CreditChanged(customerCode, "Released", message), context, CustomerService.SourceContext, CreditEntity, customerCode)],
                new AuditChange("CreditRelease", id.ToString(CultureInfo.InvariantCulture), null, CanonicalJson.Serialize(release with { ReleaseId = id })));
        }, ct);
    }

    private static string? Optional(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
