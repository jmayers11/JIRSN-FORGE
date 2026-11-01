using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Sales.Contracts;

namespace Forge.Sales;

/// <param name="TaxRatePercent">Sales tax as a percent; must be zero when a tax-exemption certificate is given.</param>
public sealed record CreateCustomer(string Code, string Name, string? BillTo, string? ShipTo, int PaymentTermsDays, decimal TaxRatePercent, string? TaxExemptCertificate, string? Currency, string? Notes);

public sealed record ChangeCustomer(string Code, string Name, string? BillTo, string? ShipTo, int PaymentTermsDays, decimal TaxRatePercent, string? TaxExemptCertificate, string? Notes);

/// <summary>Puts a customer on hold, makes it inactive or active again: a Sales Manager's decision, with a reason.</summary>
public sealed record SetCustomerStatus(string Code, CustomerStatus Status);

/// <summary>
/// The customer master. Sales Representatives add and keep customers; changing terms, tax or addresses needs a reason because
/// invoices are built from them. Only a Sales Manager puts a customer on hold or takes it off.
/// </summary>
public sealed class CustomerService(CommandPipeline pipeline)
{
    public const string SourceContext = "Sales";
    private const string Entity = "Customer";

    public Task<CommandResult> CreateAsync(CreateCustomer c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Customer");
        var spec = new CommandSpec(SourceContext, nameof(CreateCustomer), Permissions.SalesManage, new ResourceRef(Entity, code, CustomerId: code));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var name = BusinessCode.RequireName(c.Name, "Customer");
            var store = uow.Store<ISalesStore>();
            if (await store.CustomerNameTakenAsync(name, code, ct))
                throw new CommandRejectedException("customer_name_exists", $"Another customer is already called {name}.");
            var (rate, certificate) = Tax(c.TaxRatePercent, c.TaxExemptCertificate);
            var customer = new CustomerInfo(code, name, CustomerStatus.Active, Text(c.BillTo, 500), Text(c.ShipTo, 500), Terms(c.PaymentTermsDays), rate, certificate,
                Currency(c.Currency), Text(c.Notes, 1000), context.Actor, now);
            if (!await store.InsertCustomerAsync(customer, ct))
                throw new CommandRejectedException("customer_exists", $"Customer {code} already exists.");
            return new CommandOutcome(
                CommandResult.Ok(code, $"Customer {code} ({name}) added: net {customer.PaymentTermsDays} days, {TaxText(customer)}."),
                [pipeline.Event(new CustomerChanged(code, "Created"), context, SourceContext, Entity, code)],
                new AuditChange(Entity, code, null, CanonicalJson.Serialize(customer)));
        }, ct);
    }

    public Task<CommandResult> ChangeAsync(ChangeCustomer c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return UpdateAsync(nameof(ChangeCustomer), c.Code, c, context, Permissions.SalesManage, async (uow, current) =>
        {
            var name = BusinessCode.RequireName(c.Name, "Customer");
            if (await uow.Store<ISalesStore>().CustomerNameTakenAsync(name, current.Code, ct))
                throw new CommandRejectedException("customer_name_exists", $"Another customer is already called {name}.");
            var (rate, certificate) = Tax(c.TaxRatePercent, c.TaxExemptCertificate);
            return current with
            {
                Name = name, BillTo = Text(c.BillTo, 500), ShipTo = Text(c.ShipTo, 500), PaymentTermsDays = Terms(c.PaymentTermsDays),
                TaxRatePercent = rate, TaxExemptCertificate = certificate, Notes = Text(c.Notes, 1000),
            };
        }, ct);
    }

    public Task<CommandResult> SetStatusAsync(SetCustomerStatus c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return UpdateAsync(nameof(SetCustomerStatus), c.Code, c, context, Permissions.SalesApprove, (_, current) => Task.FromResult(current with { Status = c.Status }), ct);
    }

    /// <summary>The customer a new quote or order names, and active.</summary>
    internal static async Task<CustomerInfo> RequireActiveAsync(IKernelUnitOfWork uow, string? raw, CancellationToken ct)
    {
        var code = BusinessCode.Require(raw, "Customer");
        var customer = await uow.Store<ISalesLookup>().FindCustomerAsync(code, ct)
            ?? throw new CommandRejectedException("customer_not_found", $"Customer {code} is not in the customer master. Add the customer first.");
        return customer.Status == CustomerStatus.Active
            ? customer
            : throw new CommandRejectedException("customer_not_active", $"{customer.Name} is {customer.Status}; it takes no new quotes or orders.");
    }

    internal static string TaxText(CustomerInfo customer) => customer.TaxExemptCertificate is { } certificate
        ? $"tax exempt (certificate {certificate})"
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"sales tax {customer.TaxRatePercent:0.###} %");

    private static (decimal Rate, string? Certificate) Tax(decimal rate, string? certificate)
    {
        var exempt = Text(certificate, 60);
        if (rate is < 0 or > 25)
            throw new CommandRejectedException("invalid_tax_rate", "The sales-tax rate is a percent from 0 to 25.");
        if (decimal.Round(rate, 3) != rate)
            throw new CommandRejectedException("invalid_tax_rate", "Give the sales-tax rate to at most three decimal places.");
        if (exempt is not null && rate != 0)
            throw new CommandRejectedException("exempt_with_rate", "A tax-exempt customer is charged no tax: set the rate to 0 or remove the certificate.");
        return (rate, exempt);
    }

    private static int Terms(int days) => days is >= 0 and <= 365
        ? days
        : throw new CommandRejectedException("invalid_terms", "Payment terms are 0 to 365 days.");

    internal static string Currency(string? currency)
    {
        var value = string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();
        return Money.Currencies.Contains(value) ? value : throw new CommandRejectedException("invalid_currency", "Choose USD, EUR, GBP or CAD.");
    }

    internal static string? Text(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }

    private Task<CommandResult> UpdateAsync<TCommand>(string commandType, string rawCode, TCommand command, CommandContext context, string permission,
        Func<IKernelUnitOfWork, CustomerInfo, Task<CustomerInfo>> change, CancellationToken ct)
    {
        var code = BusinessCode.Require(rawCode, "Customer");
        var spec = new CommandSpec(SourceContext, commandType, permission, new ResourceRef(Entity, code, CustomerId: code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var current = await uow.Store<ISalesLookup>().FindCustomerAsync(code, ct)
                ?? throw new CommandRejectedException("customer_not_found", $"Customer {code} does not exist.");
            var updated = await change(uow, current);
            if (updated == current)
                return CommandOutcome.NoChange(CommandResult.NoChange(code, "Nothing changed."));
            await uow.Store<ISalesStore>().UpdateCustomerAsync(updated, now, context.Actor, ct);
            var statusChanged = updated.Status != current.Status;
            return new CommandOutcome(
                CommandResult.Ok(code, statusChanged ? $"Customer {code} ({updated.Name}) is now {updated.Status}." : $"Customer {code} ({updated.Name}) updated: net {updated.PaymentTermsDays} days, {TaxText(updated)}."),
                [pipeline.Event(new CustomerChanged(code, statusChanged ? updated.Status.ToString() : "Changed"), context, SourceContext, Entity, code)],
                new AuditChange(Entity, code, CanonicalJson.Serialize(current), CanonicalJson.Serialize(updated)));
        }, ct);
    }
}
