using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Procurement.Contracts;

namespace Forge.Procurement;

public sealed record CreateSupplier(string Code, string Name, string? Notes);

public sealed record ChangeSupplier(string Code, string Name, string? Notes);

/// <summary>Puts a supplier on hold, makes it inactive or active again. Needs a reason and purchasing approval authority.</summary>
public sealed record SetSupplierStatus(string Code, SupplierStatus Status);

public sealed record ApproveSource(string PartNumber, string SupplierCode, string? Note);

public sealed record RemoveSource(string PartNumber, string SupplierCode);

public sealed record SupplierChanged(string Code, string Change) : IDomainEvent
{
    public static string EventType => nameof(SupplierChanged);

    public static int SchemaVersion => 1;
}

public sealed record ApprovedSourceChanged(string PartNumber, string SupplierCode, string Change) : IDomainEvent
{
    public static string EventType => nameof(ApprovedSourceChanged);

    public static int SchemaVersion => 1;
}

/// <summary>
/// The supplier master (V2.1). New purchase orders name a supplier record, not free text; a supplier on
/// hold or inactive takes no new orders. An item with approved sources is bought only from them.
/// </summary>
public sealed class SupplierService(CommandPipeline pipeline)
{
    private const string Entity = "Supplier";
    private const string SourceEntity = "ApprovedSource";

    public Task<CommandResult> CreateAsync(CreateSupplier c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Supplier");
        var spec = new CommandSpec(PurchasingService.SourceContext, nameof(CreateSupplier), Permissions.PurchasingManage, ResourceRef.Of(Entity, code));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var name = BusinessCode.RequireName(c.Name, "Supplier");
            var store = uow.Store<IProcurementStore>();
            if (await store.NameTakenAsync(name, code, ct))
            {
                throw new CommandRejectedException("supplier_name_exists", $"Another supplier is already called {name}.");
            }

            var supplier = new SupplierInfo(code, name, SupplierStatus.Active, Notes(c.Notes), context.Actor, now);
            if (!await store.InsertSupplierAsync(supplier, ct))
            {
                throw new CommandRejectedException("supplier_exists", $"Supplier {code} already exists.");
            }

            return new CommandOutcome(
                CommandResult.Ok(code, $"Supplier {code} ({name}) added."),
                [pipeline.Event(new SupplierChanged(code, "Created"), context, PurchasingService.SourceContext, Entity, code)],
                new AuditChange(Entity, code, null, CanonicalJson.Serialize(supplier)));
        }, ct);
    }

    public Task<CommandResult> ChangeAsync(ChangeSupplier c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return UpdateAsync(nameof(ChangeSupplier), c.Code, c, context, Permissions.PurchasingManage, false, async (uow, current) =>
        {
            var name = BusinessCode.RequireName(c.Name, "Supplier");
            if (await uow.Store<IProcurementStore>().NameTakenAsync(name, current.Code, ct))
            {
                throw new CommandRejectedException("supplier_name_exists", $"Another supplier is already called {name}.");
            }

            return current with { Name = name, Notes = Notes(c.Notes) };
        }, ct);
    }

    public Task<CommandResult> SetStatusAsync(SetSupplierStatus c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return UpdateAsync(nameof(SetSupplierStatus), c.Code, c, context, Permissions.PurchasingApprove, true, (_, current) => Task.FromResult(current with { Status = c.Status }), ct);
    }

    public Task<CommandResult> ApproveSourceAsync(ApproveSource c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var code = BusinessCode.Require(c.SupplierCode, "Supplier");
        var id = $"{part}/{code}";
        var spec = new CommandSpec(PurchasingService.SourceContext, nameof(ApproveSource), Permissions.PurchasingApprove, ResourceRef.Of(SourceEntity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var item = await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct)
                ?? throw new CommandRejectedException("item_not_found", $"Part {part} does not exist.");
            if (item.MakeBuy != MakeBuy.Buy)
            {
                throw new CommandRejectedException("not_purchased", $"{part} is made, not bought.");
            }

            var supplier = await uow.Store<IProcurementLookup>().FindSupplierAsync(code, ct)
                ?? throw new CommandRejectedException("supplier_not_found", $"Supplier {code} does not exist.");
            var source = new ApprovedSourceInfo(part, supplier.Code, Notes(c.Note), context.Actor, now);
            if (!await uow.Store<IProcurementStore>().InsertApprovedSourceAsync(source, ct))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, $"{supplier.Name} is already an approved source of {part}."));
            }

            return new CommandOutcome(
                CommandResult.Ok(id, $"{supplier.Name} approved to supply {part}. While {part} has approved sources, it is bought only from them."),
                [pipeline.Event(new ApprovedSourceChanged(part, supplier.Code, "Approved"), context, PurchasingService.SourceContext, SourceEntity, id)],
                new AuditChange(SourceEntity, id, null, CanonicalJson.Serialize(source)));
        }, ct);
    }

    public Task<CommandResult> RemoveSourceAsync(RemoveSource c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var code = BusinessCode.Require(c.SupplierCode, "Supplier");
        var id = $"{part}/{code}";
        var spec = new CommandSpec(PurchasingService.SourceContext, nameof(RemoveSource), Permissions.PurchasingApprove, ResourceRef.Of(SourceEntity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, _) =>
        {
            var existing = (await uow.Store<IProcurementLookup>().ListApprovedSourcesAsync(part, ct)).FirstOrDefault(x => x.SupplierCode == code)
                ?? throw new CommandRejectedException("source_not_found", $"{code} is not an approved source of {part}.");
            await uow.Store<IProcurementStore>().DeleteApprovedSourceAsync(part, code, ct);
            return new CommandOutcome(
                CommandResult.Ok(id, $"{code} is no longer an approved source of {part}."),
                [pipeline.Event(new ApprovedSourceChanged(part, code, "Removed"), context, PurchasingService.SourceContext, SourceEntity, id)],
                new AuditChange(SourceEntity, id, CanonicalJson.Serialize(existing), null));
        }, ct);
    }

    /// <summary>The supplier a new order names: by code, or by its exact name, and active.</summary>
    internal static async Task<SupplierInfo> RequireOrderableAsync(IKernelUnitOfWork uow, string? raw, CancellationToken ct)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new CommandRejectedException("supplier_required", "Choose a supplier.");
        }

        var lookup = uow.Store<IProcurementLookup>();
        var supplier = await lookup.FindSupplierAsync(text.ToUpperInvariant(), ct)
            ?? (await lookup.ListSuppliersAsync(ct)).FirstOrDefault(x => string.Equals(x.Name, text, StringComparison.OrdinalIgnoreCase))
            ?? throw new CommandRejectedException("unknown_supplier", $"'{text}' is not in the supplier master. Add the supplier first.");
        return supplier.Status == SupplierStatus.Active
            ? supplier
            : throw new CommandRejectedException("supplier_not_active", $"{supplier.Name} is {supplier.Status}; it takes no new orders.");
    }

    private static string? Notes(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > 1000 ? text[..1000] : text;
    }

    private Task<CommandResult> UpdateAsync<TCommand>(string commandType, string rawCode, TCommand command, CommandContext context, string permission, bool reasonRequired, Func<IKernelUnitOfWork, SupplierInfo, Task<SupplierInfo>> change, CancellationToken ct)
    {
        var code = BusinessCode.Require(rawCode, "Supplier");
        var spec = new CommandSpec(PurchasingService.SourceContext, commandType, permission, ResourceRef.Of(Entity, code), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var current = await uow.Store<IProcurementLookup>().FindSupplierAsync(code, ct)
                ?? throw new CommandRejectedException("supplier_not_found", $"Supplier {code} does not exist.");
            var updated = await change(uow, current);
            if (updated == current)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(code, "Nothing changed."));
            }

            await uow.Store<IProcurementStore>().UpdateSupplierAsync(updated, now, context.Actor, ct);
            var what = updated.Status != current.Status ? $"now {updated.Status}" : "updated";
            return new CommandOutcome(
                CommandResult.Ok(code, $"Supplier {code} ({updated.Name}) {what}."),
                [pipeline.Event(new SupplierChanged(code, updated.Status != current.Status ? updated.Status.ToString() : "Changed"), context, PurchasingService.SourceContext, Entity, code)],
                new AuditChange(Entity, code, CanonicalJson.Serialize(current), CanonicalJson.Serialize(updated)));
        }, ct);
    }
}
