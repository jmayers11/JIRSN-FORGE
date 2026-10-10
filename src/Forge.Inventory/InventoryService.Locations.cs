using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Procurement.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Inventory;

/// <summary>Changes a location's place and role (V3.1). Kind and site never change: retire it and create another.</summary>
public sealed record ChangeLocation(
    string Code,
    string Name,
    string? ParentCode,
    LocationLevel Level,
    bool Nettable,
    string? WorkCenterCode = null,
    string? MachineCode = null,
    string? CustodianSupplier = null);

/// <summary>
/// Locations (V3.1, V3 brief H). A location sits in a hierarchy (area, zone, rack, shelf, bin, position), may
/// serve a work center or a machine (stock there is at the point of use), says whether released stock there
/// counts for planning, and says who has custody: the company, an outside processor (a supplier holding
/// company-owned material) or nobody here (in transit).
/// </summary>
public sealed partial class InventoryService
{
    public Task<CommandResult> CreateLocationAsync(CreateLocation c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Location");
        var spec = new CommandSpec(SourceContext, nameof(CreateLocation), Permissions.InventoryManage, ResourceRef.Of("Location", code));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var site = BusinessCode.Require(c.SiteCode, "Site");
            if (!(await uow.Store<IResourcesLookup>().ListSitesAsync(ct)).Any(s => s.Code == site))
            {
                throw new CommandRejectedException("unknown_site", $"Site {site} does not exist.");
            }

            var locations = await uow.Store<IInventoryLookup>().ListLocationsAsync(ct);
            var parent = OptionalCode(c.ParentCode);
            var level = c.Level ?? (parent is not null && locations.FirstOrDefault(l => l.Code == parent) is { } p ? Deeper(p.Level) : LocationLevel.Area);
            var nettable = c.Nettable ?? c.Kind is not (LocationKind.ExternalProcessor or LocationKind.InTransit or LocationKind.Quarantine);
            var location = await ValidateAsync(uow, locations, new LocationInfo(
                code, site, BusinessCode.RequireName(c.Name, "Location"), c.Kind, true, parent, level, nettable,
                OptionalCode(c.WorkCenterCode), OptionalCode(c.MachineCode), OptionalCode(c.CustodianSupplier)), ct);
            if (!await uow.Store<IInventoryStore>().InsertLocationAsync(location, now, context.Actor, ct))
            {
                throw new CommandRejectedException("code_exists", $"Location {code} already exists.");
            }

            return new CommandOutcome(
                CommandResult.Ok(code, $"Location {code} ({c.Kind}{(parent is null ? string.Empty : $" in {parent}")}) created."),
                [pipeline.Event(new LocationChanged(code, "Created"), context, SourceContext, "Location", code)],
                new AuditChange("Location", code, null, CanonicalJson.Serialize(location)));
        }, ct);
    }

    public Task<CommandResult> ChangeLocationAsync(ChangeLocation c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Location");
        var spec = new CommandSpec(SourceContext, nameof(ChangeLocation), Permissions.InventoryManage, ResourceRef.Of("Location", code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var lookup = uow.Store<IInventoryLookup>();
            var locations = await lookup.ListLocationsAsync(ct);
            var current = locations.FirstOrDefault(l => l.Code == code)
                ?? throw new CommandRejectedException("location_not_found", $"Location {code} does not exist.");
            if (!current.Active)
            {
                throw new CommandRejectedException("location_retired", $"Location {code} is retired.");
            }

            var changed = await ValidateAsync(uow, locations, current with
            {
                Name = BusinessCode.RequireName(c.Name, "Location"),
                ParentCode = OptionalCode(c.ParentCode),
                Level = c.Level,
                Nettable = c.Nettable,
                WorkCenterCode = OptionalCode(c.WorkCenterCode),
                MachineCode = OptionalCode(c.MachineCode),
                CustodianSupplier = OptionalCode(c.CustodianSupplier),
            }, ct);
            if (changed == current)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(code, "Nothing to change."));
            }

            // Custody moves with stock, never by editing a location that holds some.
            if (changed.CustodianSupplier != current.CustodianSupplier && (await lookup.ListBalancesAsync(null, ct)).Any(b => b.LocationCode == code))
            {
                throw new CommandRejectedException("location_not_empty", $"{code} holds stock in {current.CustodianSupplier}'s custody; transfer it out before changing the processor.");
            }

            await uow.Store<IInventoryStore>().UpdateLocationAsync(changed, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(code, $"Location {code} changed."),
                [pipeline.Event(new LocationChanged(code, "Changed"), context, SourceContext, "Location", code)],
                new AuditChange("Location", code, CanonicalJson.Serialize(current), CanonicalJson.Serialize(changed)));
        }, ct);
    }

    private static async Task<LocationInfo> ValidateAsync(IKernelUnitOfWork uow, IReadOnlyList<LocationInfo> locations, LocationInfo l, CancellationToken ct)
    {
        if (l.ParentCode is { } parentCode)
        {
            var parent = locations.FirstOrDefault(x => x.Code == parentCode)
                ?? throw new CommandRejectedException("unknown_parent", $"Location {parentCode} does not exist.");
            if (!parent.Active || parent.SiteCode != l.SiteCode)
            {
                throw new CommandRejectedException("invalid_parent", $"{parentCode} is {(parent.Active ? $"at site {parent.SiteCode}, not {l.SiteCode}" : "retired")}.");
            }

            if (l.Level <= parent.Level)
            {
                throw new CommandRejectedException("invalid_level", $"{l.Code} sits in {parentCode} ({parent.Level}), so it must be narrower than {parent.Level}.");
            }

            var byCode = locations.ToDictionary(x => x.Code, StringComparer.Ordinal);
            for (var up = parent; up is not null; up = up.ParentCode is { } next ? byCode.GetValueOrDefault(next) : null)
            {
                if (up.Code == l.Code)
                {
                    throw new CommandRejectedException("location_cycle", $"{l.Code} cannot sit inside itself ({parentCode} is inside {l.Code}).");
                }
            }
        }

        if (l.Kind is LocationKind.ExternalProcessor or LocationKind.InTransit && l.Nettable)
        {
            throw new CommandRejectedException("away_not_nettable", $"Stock {(l.Kind == LocationKind.InTransit ? "in transit" : "at an outside processor")} is not in the company's hands and never counts for planning.");
        }

        if (l.Kind == LocationKind.ExternalProcessor)
        {
            var supplier = await uow.Store<IProcurementLookup>().FindSupplierAsync(l.CustodianSupplier
                ?? throw new CommandRejectedException("custodian_required", "An outside-processor location names the supplier that holds the material."), ct)
                ?? throw new CommandRejectedException("unknown_supplier", $"Supplier {l.CustodianSupplier} does not exist.");
            if (supplier.Status == SupplierStatus.Inactive)
            {
                throw new CommandRejectedException("supplier_inactive", $"Supplier {supplier.Code} is inactive.");
            }
        }
        else if (l.CustodianSupplier is not null)
        {
            throw new CommandRejectedException("custodian_not_allowed", "Only an outside-processor location is in a supplier's custody.");
        }

        var resources = uow.Store<IResourcesLookup>();
        if (l.Kind == LocationKind.MachineSide)
        {
            var machine = await resources.FindMachineAsync(l.MachineCode
                ?? throw new CommandRejectedException("machine_required", "A machine-side location names its machine."), ct)
                ?? throw new CommandRejectedException("unknown_machine", $"Machine {l.MachineCode} does not exist.");
            if (l.WorkCenterCode is { } wc && wc != machine.WorkCenterCode)
            {
                throw new CommandRejectedException("work_center_mismatch", $"{machine.Code} is in {machine.WorkCenterCode}, not {wc}.");
            }

            l = l with { WorkCenterCode = machine.WorkCenterCode };
        }
        else if (l.MachineCode is not null)
        {
            throw new CommandRejectedException("machine_not_allowed", "Only a machine-side location names a machine.");
        }

        if (l.WorkCenterCode is { } workCenter)
        {
            if (l.Kind is not (LocationKind.Production or LocationKind.Staging or LocationKind.MachineSide or LocationKind.WipStorage))
            {
                throw new CommandRejectedException("work_center_not_allowed", $"A {l.Kind} location does not serve a work center.");
            }

            if (await resources.FindWorkCenterAsync(workCenter, ct) is null)
            {
                throw new CommandRejectedException("unknown_work_center", $"Work center {workCenter} does not exist.");
            }
        }
        else if (l.Kind == LocationKind.Staging)
        {
            throw new CommandRejectedException("work_center_required", "A staging location names the work center it stages for.");
        }

        return l;
    }

    private static LocationLevel Deeper(LocationLevel level) => level == LocationLevel.Position ? LocationLevel.Position : level + 1;

    private static string? OptionalCode(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToUpperInvariant();
}
