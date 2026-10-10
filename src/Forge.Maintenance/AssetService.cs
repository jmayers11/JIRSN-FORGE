using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Maintenance.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Maintenance;

public sealed record RegisterAsset(
    string AssetCode,
    string Name,
    AssetType Type,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? Location,
    string? WorkCenterCode,
    string? MachineCode,
    AssetCriticality Criticality,
    int? ExpectedLifeYears,
    decimal? ReplacementCost);

public sealed record ChangeAsset(
    string AssetCode,
    string Name,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? Location,
    AssetCriticality Criticality,
    int? ExpectedLifeYears,
    decimal? ReplacementCost);

/// <param name="Target">Active (commission, or return to service), Restricted or Obsolete. Decommissioning has its own two-person steps.</param>
public sealed record SetAssetStatus(string AssetCode, AssetStatus Target, DateOnly? CommissionedOn);

public sealed record RequestDecommission(string AssetCode);

public sealed record DecideDecommission(string AssetCode, bool Approve);

/// <param name="WorkOrderId">The work order that installs it; empty only while the asset is Planned (as delivered).</param>
public sealed record InstallComponent(string AssetCode, string PartNumber, string? Revision, string Position, decimal Quantity, long? WorkOrderId);

public sealed record RemoveComponent(long ComponentId, long? WorkOrderId);

/// <summary>
/// A part issued to a work order whose required-part line names a position (V2.9): record it as installed
/// there, replacing what earlier work installed at that position. <paramref name="AlternateFor"/> is the
/// required part an approved alternate was issued in place of.
/// </summary>
public sealed record RecordIssuedInstall(long TransactionId, long WorkOrderId, string PartNumber, decimal Quantity, string? Revision, string? AlternateFor, long? AlternateId);

public sealed record AssetChanged(string AssetCode, string Change, string Status) : IDomainEvent
{
    public static string EventType => nameof(AssetChanged);

    public static int SchemaVersion => 1;
}

public sealed record AssetComponentChanged(string AssetCode, long ComponentId, string PartNumber, decimal Quantity, string Change, long? WorkOrderId) : IDomainEvent
{
    public static string EventType => nameof(AssetComponentChanged);

    public static int SchemaVersion => 1;
}

public interface IAssetStore
{
    Task InsertAssetAsync(AssetInfo asset, CancellationToken ct);

    Task UpdateAssetAsync(AssetInfo asset, CancellationToken ct);

    Task InsertEventAsync(AssetEvent e, CancellationToken ct);

    Task<long> InsertComponentAsync(AssetComponentInfo component, CancellationToken ct);

    Task RemoveComponentAsync(long componentId, long? workOrderId, string actor, DateTimeOffset at, string reason, CancellationToken ct);
}

/// <summary>
/// Assets and their lifecycle (V2.8). Every status change, edit and component movement is written to the
/// asset's history, which is never changed. Components are installed and removed by a maintenance work
/// order on the asset's machine — or, while the asset is still Planned, recorded as delivered. Decommissioning
/// is requested by one person and approved by another; a restricted or decommissioned production asset takes
/// its machine out of capacity until it is returned to service.
/// </summary>
public sealed class AssetService(CommandPipeline pipeline)
{
    public const string SourceContext = MaintenanceService.SourceContext;
    private const string Entity = "Asset";

    public Task<CommandResult> RegisterAsync(RegisterAsset c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var code = BusinessCode.Require(c.AssetCode, "Asset");
        var spec = new CommandSpec(SourceContext, nameof(RegisterAsset), Permissions.AssetsManage, ResourceRef.Of(Entity, code));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var lookup = uow.Store<IAssetLookup>();
            if (await lookup.FindAssetAsync(code, ct) is not null)
            {
                throw new CommandRejectedException("asset_exists", $"Asset {code} already exists.");
            }

            var name = BusinessCode.RequireName(c.Name, "Asset name");
            var (workCenter, machine) = await PlaceAsync(uow, lookup, code, c.Type, c.WorkCenterCode, c.MachineCode, ct);
            Validate(c.ExpectedLifeYears, c.ReplacementCost);
            var asset = new AssetInfo(code, name, c.Type, Opt(c.Manufacturer), Opt(c.Model), Opt(c.SerialNumber), Opt(c.Location), workCenter, machine, c.Criticality,
                null, c.ExpectedLifeYears, c.ReplacementCost, AssetStatus.Planned, context.Actor, now);
            var store = uow.Store<IAssetStore>();
            await store.InsertAssetAsync(asset, ct);
            await store.InsertEventAsync(new AssetEvent(0, code, "Created", null, AssetStatus.Planned, $"{c.Type} asset {name}{(machine is null ? string.Empty : $" for machine {machine}")} registered", context.Actor, now, context.Reason), ct);
            return new CommandOutcome(
                CommandResult.Ok(code, $"Asset {code} ({name}) registered as Planned. Record what it was delivered with, then commission it."),
                [pipeline.Event(new AssetChanged(code, "Created", nameof(AssetStatus.Planned)), context, SourceContext, Entity, code)],
                new AuditChange(Entity, code, null, CanonicalJson.Serialize(asset)));
        }, ct);
    }

    public Task<CommandResult> ChangeAsync(ChangeAsset c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return AssetCommandAsync(nameof(ChangeAsset), c.AssetCode, c, Permissions.AssetsManage, true, context, (uow, asset, now) =>
        {
            Validate(c.ExpectedLifeYears, c.ReplacementCost);
            var updated = asset with
            {
                Name = BusinessCode.RequireName(c.Name, "Asset name"),
                Manufacturer = Opt(c.Manufacturer),
                Model = Opt(c.Model),
                SerialNumber = Opt(c.SerialNumber),
                Location = Opt(c.Location),
                Criticality = c.Criticality,
                ExpectedLifeYears = c.ExpectedLifeYears,
                ReplacementCost = c.ReplacementCost,
            };
            var changes = Differences(asset, updated);
            return Task.FromResult(changes.Count == 0
                ? (asset, (AssetEvent?)null, "No change.")
                : (updated, Event(asset, "Changed", asset.Status, string.Join("; ", changes), context, now), $"Asset {asset.AssetCode} changed: {string.Join("; ", changes)}."));
        }, ct);
    }

    public Task<CommandResult> SetStatusAsync(SetAssetStatus c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return AssetCommandAsync(nameof(SetAssetStatus), c.AssetCode, c, Permissions.AssetsManage, true, context, (uow, asset, now) =>
        {
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var (kind, message) = (asset.Status, c.Target) switch
            {
                (AssetStatus.Planned, AssetStatus.Active) => ("Commissioned", "commissioned and in service"),
                (AssetStatus.Restricted, AssetStatus.Active) => ("ReturnedToService", "returned to service"),
                (AssetStatus.Obsolete, AssetStatus.Active) => ("ReturnedToService", "returned to supported service"),
                (AssetStatus.Active or AssetStatus.Obsolete, AssetStatus.Restricted) => ("Restricted", asset.MachineCode is { } m ? $"restricted; machine {m} is out of capacity until it is returned to service" : "restricted"),
                (AssetStatus.Active or AssetStatus.Restricted, AssetStatus.Obsolete) => ("Obsolete", "marked obsolete; it still runs but is no longer supported"),
                _ => throw new CommandRejectedException("invalid_transition", $"Asset {asset.AssetCode} is {asset.Status}; it cannot become {c.Target} this way."
                    + (c.Target == AssetStatus.Decommissioned ? " Request decommissioning; someone else approves it." : string.Empty)),
            };
            var commissioned = asset.CommissionedOn;
            if (kind == "Commissioned")
            {
                commissioned = c.CommissionedOn ?? today;
                if (commissioned > today)
                {
                    throw new CommandRejectedException("invalid_date", "An asset is commissioned today or earlier.");
                }
            }

            var updated = asset with { Status = c.Target, CommissionedOn = commissioned };
            return Task.FromResult<(AssetInfo, AssetEvent?, string)>((updated, Event(asset, kind, c.Target, message, context, now), $"Asset {asset.AssetCode} {message}."));
        }, ct);
    }

    public Task<CommandResult> RequestDecommissionAsync(RequestDecommission c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return AssetCommandAsync(nameof(RequestDecommission), c.AssetCode, c, Permissions.AssetsManage, true, context, async (uow, asset, now) =>
        {
            if (asset.DecommissionRequestedBy is not null)
            {
                throw new CommandRejectedException("already_requested", $"Decommissioning of {asset.AssetCode} was already requested by {asset.DecommissionRequestedBy}.");
            }

            await RequireNoOpenWorkAsync(uow, asset, ct);
            var updated = asset with { DecommissionRequestedBy = context.Actor, DecommissionRequestedAt = now, DecommissionReason = context.Reason };
            return (updated, Event(asset, "DecommissionRequested", asset.Status, "Decommissioning requested", context, now),
                $"Decommissioning of {asset.AssetCode} requested; a different person with maintenance approval decides it.");
        }, ct);
    }

    public Task<CommandResult> DecideDecommissionAsync(DecideDecommission c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return AssetCommandAsync(nameof(DecideDecommission), c.AssetCode, c, Permissions.MaintenanceApprove, true, context, async (uow, asset, now) =>
        {
            if (asset.DecommissionRequestedBy is null)
            {
                throw new CommandRejectedException("not_requested", $"Nobody has requested decommissioning of {asset.AssetCode}.");
            }

            if (asset.DecommissionRequestedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You requested this; a different person decides it.");
            }

            var cleared = asset with { DecommissionRequestedBy = null, DecommissionRequestedAt = null, DecommissionReason = null };
            if (!c.Approve)
            {
                return (cleared, Event(asset, "DecommissionRejected", asset.Status, $"Decommissioning requested by {asset.DecommissionRequestedBy} rejected", context, now),
                    $"Decommissioning of {asset.AssetCode} rejected; it stays {asset.Status}.");
            }

            await RequireNoOpenWorkAsync(uow, asset, ct);
            var installed = (await uow.Store<IAssetLookup>().ListComponentsAsync(asset.AssetCode, null, false, ct)).Count;
            return (cleared with { Status = AssetStatus.Decommissioned },
                Event(asset, "Decommissioned", AssetStatus.Decommissioned, $"Decommissioned (requested by {asset.DecommissionRequestedBy}: {asset.DecommissionReason}){(installed > 0 ? $"; {installed} component row(s) stay recorded as installed" : string.Empty)}", context, now),
                $"Asset {asset.AssetCode} decommissioned{(asset.MachineCode is { } m ? $"; machine {m} is out of capacity" : string.Empty)}. Its history stays.");
        }, ct);
    }

    public Task<CommandResult> InstallAsync(InstallComponent c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var permission = c.WorkOrderId is null ? Permissions.AssetsManage : Permissions.MaintenanceWork;
        return AssetCoreAsync(nameof(InstallComponent), c.AssetCode, c, permission, false, context, async (uow, asset, now) =>
        {
            var workOrder = await WorkOrderAsync(uow, asset, c.WorkOrderId, "installed", ct);
            var engineering = uow.Store<IEngineeringLookup>();
            var part = BusinessCode.Require(c.PartNumber, "Part");
            _ = await engineering.FindItemAsync(part, ct) ?? throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            string? revision = null;
            if (!string.IsNullOrWhiteSpace(c.Revision))
            {
                revision = c.Revision.Trim().ToUpperInvariant();
                if ((await engineering.ListRevisionsAsync(part, ct)).All(r => r.Revision != revision || r.Status != RevisionStatus.Released))
                {
                    throw new CommandRejectedException("revision_not_released", $"{part} has no released revision {revision}.");
                }
            }

            var position = BusinessCode.RequireName(c.Position, "Position");
            if (c.Quantity <= 0 || c.Quantity > 100_000)
            {
                throw new CommandRejectedException("invalid_quantity", "Quantity must be above zero.");
            }

            var component = new AssetComponentInfo(0, asset.AssetCode, part, revision, position, c.Quantity, workOrder, context.Actor, now, null, null, null, null);
            var id = await uow.Store<IAssetStore>().InsertComponentAsync(component, ct);
            var how = workOrder is { } w ? $"by work order {w}" : "as delivered";
            var detail = $"{Q(c.Quantity)} {part}{(revision is null ? string.Empty : " rev " + revision)} installed at {position} {how}";
            await uow.Store<IAssetStore>().InsertEventAsync(Event(asset, "ComponentInstalled", asset.Status, detail, context, now), ct);
            return ((AssetInfo?)null, (AssetEvent?)null, $"Asset {asset.AssetCode}: {detail} (component {id}).",
                pipeline.Event(new AssetComponentChanged(asset.AssetCode, id, part, c.Quantity, "Installed", workOrder), context, SourceContext, Entity, asset.AssetCode),
                id.ToString(CultureInfo.InvariantCulture),
                CanonicalJson.Serialize(component with { ComponentId = id }));
        }, ct);
    }

    public Task<CommandResult> RemoveAsync(RemoveComponent c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var key = c.ComponentId.ToString(CultureInfo.InvariantCulture);
        var permission = c.WorkOrderId is null ? Permissions.AssetsManage : Permissions.MaintenanceWork;
        var spec = new CommandSpec(SourceContext, nameof(RemoveComponent), permission, ResourceRef.Of("AssetComponent", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var lookup = uow.Store<IAssetLookup>();
            var component = (await lookup.ListComponentsAsync(null, null, true, ct)).FirstOrDefault(x => x.ComponentId == c.ComponentId)
                ?? throw new CommandRejectedException("component_not_found", $"Component {key} does not exist.");
            if (!component.Installed)
            {
                throw new CommandRejectedException("already_removed", $"Component {key} was removed {component.RemovedAt:yyyy-MM-dd} by {component.RemovedBy}.");
            }

            var asset = (await lookup.FindAssetAsync(component.AssetCode, ct))!;
            if (asset.Status == AssetStatus.Decommissioned)
            {
                throw new CommandRejectedException("asset_decommissioned", $"Asset {asset.AssetCode} is decommissioned; its record stays as it was.");
            }

            var workOrder = await WorkOrderAsync(uow, asset, c.WorkOrderId, "removed", ct);
            var store = uow.Store<IAssetStore>();
            await store.RemoveComponentAsync(component.ComponentId, workOrder, context.Actor, now, context.Reason!, ct);
            var detail = $"{Q(component.Quantity)} {component.PartNumber} removed from {component.Position} {(workOrder is { } w ? $"by work order {w}" : "before commissioning")}";
            await store.InsertEventAsync(Event(asset, "ComponentRemoved", asset.Status, detail, context, now), ct);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Asset {asset.AssetCode}: {detail}. The installation stays in its history."),
                [pipeline.Event(new AssetComponentChanged(asset.AssetCode, component.ComponentId, component.PartNumber, component.Quantity, "Removed", workOrder), context, SourceContext, Entity, asset.AssetCode)],
                new AuditChange("AssetComponent", key, "Installed", "Removed"));
        }, ct);
    }

    /// <summary>
    /// Records a part issued to a work order as installed on its asset (V2.9), when the work order's required
    /// part line names a position. Runs as the installation system actor, once per issue.
    /// </summary>
    public Task<CommandResult> RecordIssuedInstallAsync(RecordIssuedInstall c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.TransactionId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(RecordIssuedInstall), Permissions.MaintenanceWork, ResourceRef.Of("AssetComponent", key));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var lookup = uow.Store<IMaintenanceLookup>();
            var order = await lookup.FindWorkOrderAsync(c.WorkOrderId, ct);
            var needed = c.AlternateFor ?? c.PartNumber;
            var line = (await lookup.ListRequiredPartsAsync(c.WorkOrderId, ct)).FirstOrDefault(p => p.PartNumber == needed);
            var assets = uow.Store<IAssetLookup>();
            var asset = order?.AssetCode is { } code ? await assets.FindAssetAsync(code, ct) : null;
            if (order is null || line?.Position is not { } position || asset is null || asset.Status == AssetStatus.Decommissioned)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(key, "Nothing to install: the work order names no position for this part on an asset in service."));
            }

            var store = uow.Store<IAssetStore>();
            var replaced = (await assets.ListComponentsAsync(asset.AssetCode, null, false, ct))
                .Where(x => string.Equals(x.Position, position, StringComparison.OrdinalIgnoreCase) && x.InstalledByWorkOrder != order.WorkOrderId).ToList();
            foreach (var old in replaced)
            {
                await store.RemoveComponentAsync(old.ComponentId, order.WorkOrderId, context.Actor, now, $"Replaced by work order {order.WorkOrderId}", ct);
                await store.InsertEventAsync(Event(asset, "ComponentRemoved", asset.Status, $"{Q(old.Quantity)} {old.PartNumber} removed from {old.Position} by work order {order.WorkOrderId} (replaced)", context, now), ct);
            }

            var component = new AssetComponentInfo(0, asset.AssetCode, c.PartNumber, c.Revision, position, c.Quantity, order.WorkOrderId, context.Actor, now, null, null, null, null, c.TransactionId);
            var id = await store.InsertComponentAsync(component, ct);
            var detail = $"{Q(c.Quantity)} {c.PartNumber}{(c.Revision is null ? string.Empty : " rev " + c.Revision)} installed at {position} by work order {order.WorkOrderId}"
                + (c.AlternateFor is null ? string.Empty : $", in place of {c.AlternateFor} (approved alternate {c.AlternateId})")
                + $" from issue {key}";
            await store.InsertEventAsync(Event(asset, "ComponentInstalled", asset.Status, detail, context, now), ct);
            return new CommandOutcome(
                CommandResult.Ok(id.ToString(CultureInfo.InvariantCulture), $"Asset {asset.AssetCode}: {detail}."),
                [pipeline.Event(new AssetComponentChanged(asset.AssetCode, id, c.PartNumber, c.Quantity, "Installed", order.WorkOrderId), context, SourceContext, Entity, asset.AssetCode)],
                new AuditChange("AssetComponent", id.ToString(CultureInfo.InvariantCulture), null, CanonicalJson.Serialize(component with { ComponentId = id })));
        }, ct);
    }

    /// <summary>One command on one asset: load it, apply the change, write the asset and its history line together.</summary>
    private Task<CommandResult> AssetCommandAsync<TCommand>(
        string type,
        string rawCode,
        TCommand command,
        string permission,
        bool reasonRequired,
        CommandContext context,
        Func<IKernelUnitOfWork, AssetInfo, DateTimeOffset, Task<(AssetInfo Updated, AssetEvent? Event, string Message)>> change,
        CancellationToken ct) =>
        AssetCoreAsync(type, rawCode, command, permission, reasonRequired, context, async (uow, asset, now) =>
        {
            var (updated, e, message) = await change(uow, asset, now);
            return ((AssetInfo?)updated, e, message, (EventEnvelope?)null, (string?)null, (string?)null);
        }, ct);

    private Task<CommandResult> AssetCoreAsync<TCommand>(
        string type,
        string rawCode,
        TCommand command,
        string permission,
        bool reasonRequired,
        CommandContext context,
        Func<IKernelUnitOfWork, AssetInfo, DateTimeOffset, Task<(AssetInfo? Updated, AssetEvent? Event, string Message, EventEnvelope? Extra, string? AuditEntityId, string? AuditAfter)>> change,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var code = BusinessCode.Require(rawCode, "Asset");
        var spec = new CommandSpec(SourceContext, type, permission, ResourceRef.Of(Entity, code), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var asset = await uow.Store<IAssetLookup>().FindAssetAsync(code, ct)
                ?? throw new CommandRejectedException("asset_not_found", $"Asset {code} does not exist.");
            if (asset.Status == AssetStatus.Decommissioned)
            {
                throw new CommandRejectedException("asset_decommissioned", $"Asset {code} is decommissioned; its record stays as it was.");
            }

            var (updated, e, message, extra, auditEntityId, auditAfter) = await change(uow, asset, now);
            if (extra is not null)
            {
                return new CommandOutcome(CommandResult.Ok(code, message), [extra], new AuditChange("AssetComponent", auditEntityId ?? code, null, auditAfter));
            }

            if (e is null || updated is null)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(code, message));
            }

            var store = uow.Store<IAssetStore>();
            await store.InsertEventAsync(e, ct);
            await store.UpdateAssetAsync(updated, ct);
            return new CommandOutcome(
                CommandResult.Ok(code, message),
                [pipeline.Event(new AssetChanged(code, e.Kind, updated.Status.ToString()), context, SourceContext, Entity, code)],
                new AuditChange(Entity, code, CanonicalJson.Serialize(asset), CanonicalJson.Serialize(updated)));
        }, ct);
    }

    private static async Task<(string? WorkCenter, string? Machine)> PlaceAsync(IKernelUnitOfWork uow, IAssetLookup lookup, string code, AssetType type, string? rawWorkCenter, string? rawMachine, CancellationToken ct)
    {
        var resources = uow.Store<IResourcesLookup>();
        string? workCenter = string.IsNullOrWhiteSpace(rawWorkCenter) ? null : BusinessCode.Require(rawWorkCenter, "Work center");
        if (workCenter is not null && await resources.FindWorkCenterAsync(workCenter, ct) is null)
        {
            throw new CommandRejectedException("unknown_work_center", $"Work center {workCenter} does not exist.");
        }

        if (string.IsNullOrWhiteSpace(rawMachine))
        {
            return (workCenter, null);
        }

        if (type != AssetType.Production)
        {
            throw new CommandRejectedException("machine_needs_production", "Only a production asset points at a machine.");
        }

        var machineCode = BusinessCode.Require(rawMachine, "Machine");
        var machine = await resources.FindMachineAsync(machineCode, ct) ?? throw new CommandRejectedException("unknown_machine", $"Machine {machineCode} does not exist.");
        if (workCenter is not null && workCenter != machine.WorkCenterCode)
        {
            throw new CommandRejectedException("work_center_mismatch", $"Machine {machineCode} is in work center {machine.WorkCenterCode}, not {workCenter}.");
        }

        if ((await lookup.ListAssetsAsync(ct)).FirstOrDefault(a => a.MachineCode == machineCode) is { } other)
        {
            throw new CommandRejectedException("machine_has_asset", $"Machine {machineCode} is already asset {other.AssetCode}.");
        }

        return (machine.WorkCenterCode, machineCode);
    }

    /// <summary>
    /// The work order a component moves under: open, on this asset's machine. Without one, only an asset
    /// that is still Planned (being delivered) may have components recorded.
    /// </summary>
    private static async Task<long?> WorkOrderAsync(IKernelUnitOfWork uow, AssetInfo asset, long? workOrderId, string verb, CancellationToken ct)
    {
        if (workOrderId is not { } id)
        {
            return asset.Status == AssetStatus.Planned
                ? null
                : throw new CommandRejectedException("work_order_required", $"Asset {asset.AssetCode} is in service; components are {verb} by a maintenance work order.");
        }

        var order = await uow.Store<IMaintenanceLookup>().FindWorkOrderAsync(id, ct)
            ?? throw new CommandRejectedException("work_order_not_found", $"Work order {id} does not exist.");
        if (order.Status is not (WorkOrderStatus.Open or WorkOrderStatus.Scheduled or WorkOrderStatus.InProgress))
        {
            throw new CommandRejectedException("work_order_not_open", $"Work order {id} is {order.Status}.");
        }

        return order.AssetCode == asset.AssetCode || (asset.MachineCode is { } machine && order.MachineCode == machine)
            ? id
            : throw new CommandRejectedException("work_order_not_for_asset", $"Work order {id} is for {order.Target}, not asset {asset.AssetCode}.");
    }

    private static async Task RequireNoOpenWorkAsync(IKernelUnitOfWork uow, AssetInfo asset, CancellationToken ct)
    {
        var machine = asset.MachineCode;
        var open = (await uow.Store<IMaintenanceLookup>().ListWorkOrdersAsync(false, ct)).Where(w => ((machine is not null && w.MachineCode == machine) || w.AssetCode == asset.AssetCode) && w.IsOpen).Select(w => w.WorkOrderId).ToList();
        if (open.Count > 0)
        {
            throw new CommandRejectedException("open_work_orders", $"Asset {asset.AssetCode} has open work orders ({string.Join(", ", open)}); finish or cancel them first.");
        }
    }

    private static List<string> Differences(AssetInfo a, AssetInfo b)
    {
        var list = new List<string>();
        void Diff(string what, object? before, object? after)
        {
            if (!Equals(before, after))
            {
                list.Add($"{what} {Show(before)} → {Show(after)}");
            }
        }

        Diff("name", a.Name, b.Name);
        Diff("manufacturer", a.Manufacturer, b.Manufacturer);
        Diff("model", a.Model, b.Model);
        Diff("serial", a.SerialNumber, b.SerialNumber);
        Diff("location", a.Location, b.Location);
        Diff("criticality", a.Criticality, b.Criticality);
        Diff("expected life", a.ExpectedLifeYears, b.ExpectedLifeYears);
        Diff("replacement cost", a.ReplacementCost, b.ReplacementCost);
        return list;
    }

    private static string Show(object? value) => value switch
    {
        null => "(none)",
        decimal d => d.ToString("0.##", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static AssetEvent Event(AssetInfo asset, string kind, AssetStatus status, string detail, CommandContext context, DateTimeOffset now) =>
        new(0, asset.AssetCode, kind, asset.Status, status, detail, context.Actor, now, context.Reason);

    private static void Validate(int? expectedLife, decimal? cost)
    {
        if (expectedLife is < 1 or > 200 || cost is < 0 or > 1_000_000_000)
        {
            throw new CommandRejectedException("invalid_asset", "Expected life is 1-200 years and replacement cost zero or more.");
        }
    }

    private static string? Opt(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > 200 ? text[..200] : text;
    }

    private static string Q(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
