using Forge.Engineering.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Sales.Contracts;

namespace Forge.Sales;

/// <param name="PartNumber">Our part; null removes the customer's number.</param>
public sealed record SetCustomerPart(string Customer, string CustomerPart, string? PartNumber, string? Description);

public sealed record CustomerPartChanged(string CustomerCode, string CustomerPart, string? PartNumber) : IDomainEvent
{
    public static string EventType => nameof(CustomerPartChanged);

    public static int SchemaVersion => 1;
}

public interface ICustomerPartStore
{
    Task UpsertCustomerPartAsync(CustomerPartInfo part, CancellationToken ct);

    Task<bool> DeleteCustomerPartAsync(string customerCode, string customerPart, CancellationToken ct);
}

/// <summary>Sales representatives keep each customer's part numbers so their orders map to ours; nothing is guessed from similar numbers.</summary>
public sealed class CustomerPartService(CommandPipeline pipeline)
{
    private const string Entity = "CustomerPart";

    public Task<CommandResult> SetAsync(SetCustomerPart c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var customer = BusinessCode.Require(c.Customer, "Customer");
        var theirs = (c.CustomerPart ?? string.Empty).Trim().ToUpperInvariant();
        if (theirs.Length is 0 or > 60) throw new CommandRejectedException("invalid_customer_part", "The customer's part number is 1 to 60 characters.");
        var id = $"{customer}/{theirs}";
        var spec = new CommandSpec(CustomerService.SourceContext, nameof(SetCustomerPart), Permissions.SalesManage, ResourceRef.Of(Entity, id) with { CustomerId = customer });
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (await uow.Store<ISalesLookup>().FindCustomerAsync(customer, ct) is null)
                throw new CommandRejectedException("customer_not_found", $"Customer {customer} does not exist.");
            var lookup = uow.Store<ICustomerPartLookup>();
            var before = await lookup.FindCustomerPartAsync(customer, theirs, ct);
            var store = uow.Store<ICustomerPartStore>();
            if (c.PartNumber is null)
            {
                if (!await store.DeleteCustomerPartAsync(customer, theirs, ct)) return CommandOutcome.NoChange(CommandResult.NoChange(id, $"{customer} has no part {theirs}."));
                return new CommandOutcome(CommandResult.Ok(id, $"{customer}'s part {theirs} removed."),
                    [pipeline.Event(new CustomerPartChanged(customer, theirs, null), context, CustomerService.SourceContext, Entity, id)],
                    new AuditChange(Entity, id, CanonicalJson.Serialize(before), null));
            }

            var part = BusinessCode.Require(c.PartNumber, "Part");
            if (await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct) is null)
                throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            var info = new CustomerPartInfo(customer, theirs, part, string.IsNullOrWhiteSpace(c.Description) ? null : c.Description.Trim(), context.Actor, now);
            if (before is not null && before with { UpdatedAt = now, UpdatedBy = context.Actor } == info)
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Nothing changed."));
            await store.UpsertCustomerPartAsync(info, ct);
            return new CommandOutcome(CommandResult.Ok(id, $"{customer}'s part {theirs} is our {part}."),
                [pipeline.Event(new CustomerPartChanged(customer, theirs, part), context, CustomerService.SourceContext, Entity, id)],
                new AuditChange(Entity, id, before is null ? null : CanonicalJson.Serialize(before), CanonicalJson.Serialize(info)));
        }, ct);
    }
}
