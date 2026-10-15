using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Inventory;

public sealed record CreateContainer(string Code, ContainerKind Kind);

public sealed record PackContainerContent(string Code, string PartNumber, decimal Quantity, string LocationCode, string? LotNumber, StockStatus Status = StockStatus.Released);

public sealed record SealContainer(string Code);

public sealed record ContainerChanged(string Code, string Change) : IDomainEvent
{
    public static string EventType => nameof(ContainerChanged);
    public static int SchemaVersion => 1;
}

/// <summary>Physical packing identities (V3.14); content records reserve a quantity without changing its inventory ledger identity.</summary>
public sealed partial class InventoryService
{
    public Task<CommandResult> CreateContainerAsync(CreateContainer c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Container");
        var spec = new CommandSpec(SourceContext, nameof(CreateContainer), Permissions.InventoryTransact, ResourceRef.Of("Container", code));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var container = new ContainerInfo(code, c.Kind, ContainerStatus.Open, context.Actor, now, []);
            if (!await uow.Store<IInventoryStore>().InsertContainerAsync(container, ct))
                throw new CommandRejectedException("container_exists", $"Container {code} already exists.");
            return new CommandOutcome(CommandResult.Ok(code, $"{c.Kind} {code} created and ready to pack."),
                [pipeline.Event(new ContainerChanged(code, "Created"), context, SourceContext, "Container", code)],
                new AuditChange("Container", code, null, CanonicalJson.Serialize(container)));
        }, ct);
    }

    public Task<CommandResult> PackContainerContentAsync(PackContainerContent c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Container");
        var spec = new CommandSpec(SourceContext, nameof(PackContainerContent), Permissions.InventoryTransact, ResourceRef.Of("Container", code), true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var lookup = uow.Store<IInventoryLookup>();
            await uow.Store<IProductReleaseInventoryStore>().LockContainerAsync(code, ct);
            var container = await lookup.FindContainerAsync(code, ct)
                ?? throw new CommandRejectedException("container_not_found", $"Container {code} does not exist.");
            if (container.Status != ContainerStatus.Open)
                throw new CommandRejectedException("container_sealed", $"Container {code} is sealed and its contents are immutable.");
            if (container.Contents.Count >= 999)
                throw new CommandRejectedException("container_full", $"Container {code} has reached its 999-line limit.");
            var item = await ItemAsync(uow, BusinessCode.Require(c.PartNumber, "Part"), ct);
            var location = await CompanyLocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            var quantity = RequireQuantity(item, c.Quantity);
            var store = uow.Store<IInventoryStore>();
            await store.LockPartAsync(item.PartNumber, ct);
            await productRelease.RequireReleasedScopeAsync(uow, context.Principal, item.PartNumber, lot, location.Code, quantity, now, ct);
            var balance = await store.BalanceAsync(item.PartNumber, location.Code, lot, c.Status, ct);
            var packed = await store.PackedQuantityAsync(item.PartNumber, location.Code, lot, c.Status, ct);
            if (packed + quantity > balance)
                throw new CommandRejectedException("already_packed", $"Only {Math.Max(0, balance - packed)} {item.UnitOfMeasure} of {item.PartNumber} lot {lot ?? "(untracked)"} at {location.Code} remains unpacked.");
            var content = new ContainerContentInfo(container.Contents.Count + 1, item.PartNumber, quantity, location.Code, lot, c.Status);
            await store.AddContainerContentAsync(code, content, ct);
            var changed = container with { Contents = container.Contents.Append(content).ToList() };
            return new CommandOutcome(CommandResult.Ok(code, $"Packed {quantity} {item.UnitOfMeasure} {item.PartNumber}{(lot is null ? string.Empty : $" lot {lot}")} into {container.Kind.ToString().ToLowerInvariant()} {code}."),
                [pipeline.Event(new ContainerChanged(code, "ContentAdded"), context, SourceContext, "Container", code)],
                new AuditChange("Container", code, CanonicalJson.Serialize(container), CanonicalJson.Serialize(changed)));
        }, ct, async (uow, now) =>
        {
            var actual = await sourceAuthority.ResolveAsync(uow, spec, context, now, ct);
            var container = await uow.Store<IInventoryLookup>().FindContainerAsync(code, ct)
                ?? throw new CommandRejectedException("container_not_found", $"Container {code} does not exist.");
            var part = BusinessCode.Require(c.PartNumber, "Part");
            var location = BusinessCode.Require(c.LocationCode, "Location");
            var lot = string.IsNullOrWhiteSpace(c.LotNumber) ? null : c.LotNumber.Trim().ToUpperInvariant();
            if (container.Contents.Any(line => line.PartNumber == part && line.LotNumber == lot && line.LocationCode == location && line.Quantity == c.Quantity && line.Status == c.Status))
                await productRelease.RecheckReleasedScopeAsync(uow, context.Principal, part, lot, location, now, ct);
            else
                await productRelease.RequireReleasedScopeAsync(uow, context.Principal, part, lot, location, c.Quantity, now, ct);
            return actual;
        });
    }

    public Task<CommandResult> SealContainerAsync(SealContainer c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Container");
        var spec = new CommandSpec(SourceContext, nameof(SealContainer), Permissions.InventoryTransact, ResourceRef.Of("Container", code), true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var lookup = uow.Store<IInventoryLookup>();
            await uow.Store<IProductReleaseInventoryStore>().LockContainerAsync(code, ct);
            var container = await lookup.FindContainerAsync(code, ct)
                ?? throw new CommandRejectedException("container_not_found", $"Container {code} does not exist.");
            if (container.Status != ContainerStatus.Open)
                throw new CommandRejectedException("container_sealed", $"Container {code} is already sealed.");
            if (container.Contents.Count == 0)
                throw new CommandRejectedException("container_empty", "An empty container cannot be sealed as a shipment unit.");
            foreach (var line in container.Contents)
                await productRelease.RequireReleasedScopeAsync(uow, context.Principal, line.PartNumber, line.LotNumber, line.LocationCode, line.Quantity, now, ct);
            await uow.Store<IInventoryStore>().SealContainerAsync(code, now, ct);
            var sealedContainer = container with { Status = ContainerStatus.Sealed };
            return new CommandOutcome(CommandResult.Ok(code, $"Container {code} sealed with {container.Contents.Count} identified content line(s)."),
                [pipeline.Event(new ContainerChanged(code, "Sealed"), context, SourceContext, "Container", code)],
                new AuditChange("Container", code, CanonicalJson.Serialize(container), CanonicalJson.Serialize(sealedContainer)));
        }, ct, async (uow, now) =>
        {
            var actual = await sourceAuthority.ResolveAsync(uow, spec, context, now, ct);
            var container = await uow.Store<IInventoryLookup>().FindContainerAsync(code, ct)
                ?? throw new CommandRejectedException("container_not_found", $"Container {code} does not exist.");
            foreach (var line in container.Contents)
                if (container.Status == ContainerStatus.Shipped)
                    await productRelease.RecheckReleasedScopeAsync(uow, context.Principal, line.PartNumber, line.LotNumber, line.LocationCode, now, ct);
                else
                    await productRelease.RequireReleasedScopeAsync(uow, context.Principal, line.PartNumber, line.LotNumber, line.LocationCode, line.Quantity, now, ct);
            return actual;
        });
    }
}
