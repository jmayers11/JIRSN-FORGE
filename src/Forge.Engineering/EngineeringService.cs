using System.Text.RegularExpressions;
using Forge.Competency.Contracts;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Engineering;

/// <param name="Class">Null takes the default: Make for made items, Unclassified for bought ones.</param>
public sealed record CreateItem(string PartNumber, string Description, string UnitOfMeasure, MakeBuy MakeBuy, int LeadTimeDays, bool LotTracked, bool Serialized, ItemClass? Class = null);

public sealed record CreateRevision(string PartNumber, string Revision);

public sealed record AddBomLine(string PartNumber, string Revision, int LineNumber, string ComponentPartNumber, decimal QuantityPer, decimal ScrapPercent, int? ConsumedAtSequence = null);

/// <param name="ConsumedAtSequence">The consuming routing operation; null keeps the current one and 0 clears it (from the first operation).</param>
public sealed record UpdateBomLine(string PartNumber, string Revision, int LineNumber, string ComponentPartNumber, decimal QuantityPer, decimal ScrapPercent, int? ConsumedAtSequence = null);

public sealed record RemoveBomLine(string PartNumber, string Revision, int LineNumber);

public sealed record AddRoutingOperation(
    string PartNumber,
    string Revision,
    int Sequence,
    string Description,
    string WorkCenterCode,
    string ActivityCode,
    IReadOnlyList<string>? EligibleMachines,
    IReadOnlyList<string>? RequiredQualifications,
    decimal SetupMinutes,
    decimal RunMinutesPerUnit,
    int CrewSize,
    string? SetupFamily = null,
    decimal? FamilySetupMinutes = null);

public sealed record RemoveRoutingOperation(string PartNumber, string Revision, int Sequence);

public sealed record UpdateRoutingOperation(
    string PartNumber,
    string Revision,
    int Sequence,
    string Description,
    string WorkCenterCode,
    string ActivityCode,
    IReadOnlyList<string>? EligibleMachines,
    IReadOnlyList<string>? RequiredQualifications,
    decimal SetupMinutes,
    decimal RunMinutesPerUnit,
    int CrewSize,
    string? SetupFamily = null,
    decimal? FamilySetupMinutes = null);

public sealed record ReleaseRevision(string PartNumber, string Revision, DateTimeOffset? EffectiveFrom);

public sealed record WithdrawRevision(string PartNumber, string Revision);

/// <summary>Sets how MRP plans an item. Lead time is in calendar days.</summary>
public sealed record SetItemPlanning(string PartNumber, int LeadTimeDays, decimal SafetyStock, LotPolicy LotPolicy, decimal OrderQuantity, decimal OrderMultiple);

public sealed record ItemPlanningChanged(string PartNumber) : IDomainEvent
{
    public static string EventType => nameof(ItemPlanningChanged);

    public static int SchemaVersion => 1;
}

public sealed record ItemCreated(string PartNumber, string MakeBuy) : IDomainEvent
{
    public static string EventType => nameof(ItemCreated);

    public static int SchemaVersion => 1;
}

public sealed record RevisionChanged(string PartNumber, string Revision, string Change) : IDomainEvent
{
    public static string EventType => nameof(RevisionChanged);

    public static int SchemaVersion => 1;
}

public sealed record RevisionReleased(string PartNumber, string Revision, DateTimeOffset EffectiveFrom, string? Supersedes) : IDomainEvent
{
    public static string EventType => nameof(RevisionReleased);

    public static int SchemaVersion => 1;
}

public sealed record RevisionWithdrawn(string PartNumber, string Revision, DateTimeOffset EffectiveTo) : IDomainEvent
{
    public static string EventType => nameof(RevisionWithdrawn);

    public static int SchemaVersion => 1;
}

public interface IEngineeringStore
{
    Task<bool> InsertItemAsync(ItemInfo item, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> InsertRevisionAsync(string partNumber, string revision, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> InsertBomLineAsync(string partNumber, string revision, BomLineInfo line, CancellationToken ct);

    Task<bool> UpdateBomLineAsync(string partNumber, string revision, int lineNumber, BomLineInfo line, CancellationToken ct);

    Task<bool> DeleteBomLineAsync(string partNumber, string revision, int lineNumber, CancellationToken ct);

    Task<bool> InsertOperationAsync(string partNumber, string revision, RoutingOperationInfo operation, CancellationToken ct);

    Task<bool> UpdateOperationAsync(string partNumber, string revision, int sequence, RoutingOperationInfo operation, CancellationToken ct);

    Task<bool> DeleteOperationAsync(string partNumber, string revision, int sequence, CancellationToken ct);

    /// <summary>Parent → component edges across every revision, for loop detection.</summary>
    Task<IReadOnlyList<(string Parent, string Component)>> ListBomEdgesAsync(CancellationToken ct);

    /// <summary>Records that this person changed the draft; they may no longer release it.</summary>
    Task AddContributorAsync(string partNumber, string revision, string actor, CancellationToken ct);

    Task<IReadOnlyList<string>> GetContributorsAsync(string partNumber, string revision, CancellationToken ct);

    Task SetEffectivityAsync(string partNumber, string revision, RevisionStatus status, DateTimeOffset? from, DateTimeOffset? to, string actor, CancellationToken ct);

    Task UpdatePlanningAsync(ItemInfo item, DateTimeOffset at, string actor, CancellationToken ct);

    Task SetClassAsync(string partNumber, ItemClass itemClass, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> InsertManufacturerPartAsync(ManufacturerPartInfo part, CancellationToken ct);

    Task UpdateManufacturerPartAsync(ManufacturerPartInfo part, DateTimeOffset at, string actor, CancellationToken ct);
}

/// <summary>
/// Engineering definition of what is built and how. A revision's BOM and routing change only while
/// it is a draft; release, by someone who did not create or edit it, validates everything (no BOM loops, released components, a valid routing)
/// and freezes it. Orders snapshot a released revision, so later engineering changes never alter
/// work already released.
/// </summary>
public sealed partial class EngineeringService(CommandPipeline pipeline)
{
    public const string SourceContext = "Engineering";
    private const string ItemEntity = "Item";
    private const string RevisionEntity = "ItemRevision";

    public Task<CommandResult> CreateItemAsync(CreateItem c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(CreateItem), Permissions.EngineeringManage, ResourceRef.Of(ItemEntity, part));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var description = BusinessCode.RequireName(c.Description, "Item");
            var uom = BusinessCode.Require(c.UnitOfMeasure, "Unit of measure");
            if (c.LeadTimeDays is < 0 or > 730)
            {
                throw new CommandRejectedException("invalid_lead_time", "Lead time must be between 0 and 730 days.");
            }

            var itemClass = c.Class ?? ItemClasses.DefaultFor(c.MakeBuy);
            RequireClassFits(itemClass, c.MakeBuy, part);
            var item = new ItemInfo(part, description, uom, c.MakeBuy, c.LeadTimeDays, c.LotTracked, c.Serialized, Class: itemClass);
            if (!await uow.Store<IEngineeringStore>().InsertItemAsync(item, now, context.Actor, ct))
            {
                throw new CommandRejectedException("part_exists", $"Part {part} already exists.");
            }

            return new CommandOutcome(
                CommandResult.Ok(part, $"Part {part} created."),
                [pipeline.Event(new ItemCreated(part, c.MakeBuy.ToString()), context, SourceContext, ItemEntity, part)],
                new AuditChange(ItemEntity, part, null, CanonicalJson.Serialize(item)));
        }, ct);
    }

    /// <summary>Planning parameters belong to the item, not a revision, so they can change at any time; every change is audited.</summary>
    public Task<CommandResult> SetItemPlanningAsync(SetItemPlanning c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(SetItemPlanning), Permissions.ItemPlanningManage, ResourceRef.Of(ItemEntity, part));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (c.LeadTimeDays is < 0 or > 730 || c.SafetyStock < 0 || c.OrderQuantity < 0 || c.OrderMultiple < 0 || c.SafetyStock > 100_000_000 || c.OrderQuantity > 100_000_000 || c.OrderMultiple > 100_000_000)
            {
                throw new CommandRejectedException("invalid_planning", "Lead time 0-730 days; safety stock, order quantity and multiple zero or more.");
            }

            if (c.LotPolicy is (LotPolicy.FixedQuantity or LotPolicy.MinimumQuantity) && c.OrderQuantity <= 0)
            {
                throw new CommandRejectedException("order_quantity_required", $"{c.LotPolicy} needs an order quantity above zero.");
            }

            var item = await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct)
                ?? throw new CommandRejectedException("item_not_found", $"Part {part} does not exist.");
            var updated = item with
            {
                LeadTimeDays = c.LeadTimeDays,
                SafetyStock = c.SafetyStock,
                LotPolicy = c.LotPolicy,
                OrderQuantity = c.LotPolicy == LotPolicy.LotForLot ? 0 : c.OrderQuantity,
                OrderMultiple = c.OrderMultiple,
            };
            if (updated == item)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(part, "Planning parameters unchanged."));
            }

            await uow.Store<IEngineeringStore>().UpdatePlanningAsync(updated, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(part, $"{part}: lead time {updated.LeadTimeDays} days, safety stock {updated.SafetyStock}, {updated.LotPolicy}" + (updated.OrderQuantity > 0 ? $" {updated.OrderQuantity}" : string.Empty) + (updated.OrderMultiple > 0 ? $", multiples of {updated.OrderMultiple}." : ".")),
                [pipeline.Event(new ItemPlanningChanged(part), context, SourceContext, ItemEntity, part)],
                new AuditChange(ItemEntity, part, CanonicalJson.Serialize(item), CanonicalJson.Serialize(updated)));
        }, ct);
    }

    public Task<CommandResult> CreateRevisionAsync(CreateRevision c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var (part, rev) = (BusinessCode.Require(c.PartNumber, "Part"), RequireRevision(c.Revision));
        return Draft(nameof(CreateRevision), part, rev, c, context, requireDraft: false, async (uow, now) =>
        {
            var lookup = uow.Store<IEngineeringLookup>();
            if (await lookup.FindItemAsync(part, ct) is null)
            {
                throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            }

            var revisions = await lookup.ListRevisionsAsync(part, ct);
            if (revisions.Any(r => r.Status == RevisionStatus.Draft))
            {
                throw new CommandRejectedException("draft_exists", $"Part {part} already has a draft revision.");
            }

            if (!await uow.Store<IEngineeringStore>().InsertRevisionAsync(part, rev, now, context.Actor, ct))
            {
                throw new CommandRejectedException("revision_exists", $"Revision {part} {rev} already exists.");
            }

            return ("created", null, CanonicalJson.Serialize(new { part, rev, status = "Draft" }));
        }, ct);
    }

    public Task<CommandResult> AddBomLineAsync(AddBomLine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var (part, rev) = (BusinessCode.Require(c.PartNumber, "Part"), RequireRevision(c.Revision));
        return Draft(nameof(AddBomLine), part, rev, c, context, requireDraft: true, async (uow, _) =>
        {
            var component = BusinessCode.Require(c.ComponentPartNumber, "Component");
            if (c.LineNumber is < 1 or > 9999 || c.QuantityPer <= 0 || c.ScrapPercent is < 0 or >= 100)
            {
                throw new CommandRejectedException("invalid_bom_line", "Line 1-9999, quantity above zero and scrap from 0 to under 100 percent.");
            }

            if (await uow.Store<IEngineeringLookup>().FindItemAsync(component, ct) is null)
            {
                throw new CommandRejectedException("unknown_component", $"Component {component} does not exist.");
            }

            var store = uow.Store<IEngineeringStore>();
            var edges = await store.ListBomEdgesAsync(ct);
            if (component == part || Reaches(edges, component, part))
            {
                throw new CommandRejectedException("bom_loop", $"{component} already contains {part} somewhere in its structure; adding it would create a loop.");
            }

            if (c.ConsumedAtSequence is < 1 or > 9999)
            {
                throw new CommandRejectedException("invalid_consumption_point", "The consuming operation is a routing sequence from 1 to 9999.");
            }

            var line = new BomLineInfo(c.LineNumber, component, c.QuantityPer, c.ScrapPercent, ConsumedAtSequence: c.ConsumedAtSequence);
            if (!await store.InsertBomLineAsync(part, rev, line, ct))
            {
                throw new CommandRejectedException("line_exists", $"BOM line {c.LineNumber} already exists.");
            }

            return ("bom_line_added", null, CanonicalJson.Serialize(line));
        }, ct);
    }

    public Task<CommandResult> RemoveBomLineAsync(RemoveBomLine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var (part, rev) = (BusinessCode.Require(c.PartNumber, "Part"), RequireRevision(c.Revision));
        return Draft(nameof(RemoveBomLine), part, rev, c, context, requireDraft: true, async (uow, _) =>
        {
            if (!await uow.Store<IEngineeringStore>().DeleteBomLineAsync(part, rev, c.LineNumber, ct))
            {
                throw new CommandRejectedException("line_not_found", $"BOM line {c.LineNumber} does not exist.");
            }

            return ("bom_line_removed", CanonicalJson.Serialize(new { line = c.LineNumber }), null);
        }, ct);
    }

    public Task<CommandResult> UpdateBomLineAsync(UpdateBomLine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var (part, rev) = (BusinessCode.Require(c.PartNumber, "Part"), RequireRevision(c.Revision));
        return Draft(nameof(UpdateBomLine), part, rev, c, context, requireDraft: true, async (uow, _) =>
        {
            if (c.LineNumber is < 1 or > 9999 || c.QuantityPer <= 0 || c.ScrapPercent is < 0 or >= 100)
            {
                throw new CommandRejectedException("invalid_bom_line", "Line 1-9999, quantity above zero and scrap from 0 to under 100 percent.");
            }

            var component = BusinessCode.Require(c.ComponentPartNumber, "Component");
            if (await uow.Store<IEngineeringLookup>().FindItemAsync(component, ct) is null)
            {
                throw new CommandRejectedException("unknown_component", $"Component {component} does not exist.");
            }

            var lookup = uow.Store<IEngineeringLookup>();
            var oldLine = (await lookup.GetRevisionDetailAsync(part, rev, ct))?.Bom.SingleOrDefault(line => line.LineNumber == c.LineNumber)
                ?? throw new CommandRejectedException("line_not_found", $"BOM line {c.LineNumber} does not exist.");
            var edges = await uow.Store<IEngineeringStore>().ListBomEdgesAsync(ct);
            if (component == part || Reaches(edges, component, part))
            {
                throw new CommandRejectedException("bom_loop", $"{component} already contains {part} somewhere in its structure; changing this line would create a loop.");
            }

            if (c.ConsumedAtSequence is < 0 or > 9999)
            {
                throw new CommandRejectedException("invalid_consumption_point", "The consuming operation is a routing sequence from 1 to 9999, or 0 to clear it.");
            }

            var updated = oldLine with
            {
                ComponentPartNumber = component, QuantityPer = c.QuantityPer, ScrapPercent = c.ScrapPercent,
                ConsumedAtSequence = c.ConsumedAtSequence switch { null => oldLine.ConsumedAtSequence, 0 => null, var sequence => sequence },
            };
            if (!await uow.Store<IEngineeringStore>().UpdateBomLineAsync(part, rev, c.LineNumber, updated, ct))
            {
                throw new CommandRejectedException("line_not_found", $"BOM line {c.LineNumber} does not exist.");
            }

            return ("bom_line_updated", CanonicalJson.Serialize(oldLine), CanonicalJson.Serialize(updated));
        }, ct);
    }

    public Task<CommandResult> AddRoutingOperationAsync(AddRoutingOperation c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var (part, rev) = (BusinessCode.Require(c.PartNumber, "Part"), RequireRevision(c.Revision));
        return Draft(nameof(AddRoutingOperation), part, rev, c, context, requireDraft: true, async (uow, _) =>
        {
            if (c.Sequence is < 1 or > 9999 || c.SetupMinutes < 0 || c.RunMinutesPerUnit < 0 || c.CrewSize is < 1 or > 20)
            {
                throw new CommandRejectedException("invalid_operation", "Sequence 1-9999, non-negative times and a crew of 1-20.");
            }

            var description = BusinessCode.RequireName(c.Description, "Operation");
            var workCenter = BusinessCode.Require(c.WorkCenterCode, "Work center");
            var activity = BusinessCode.Require(c.ActivityCode, "Activity");
            var machines = (c.EligibleMachines ?? []).Select(m => BusinessCode.Require(m, "Machine")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var quals = (c.RequiredQualifications ?? []).Select(q => BusinessCode.Require(q, "Qualification")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

            var resources = uow.Store<IResourcesLookup>();
            if (await resources.FindWorkCenterAsync(workCenter, ct) is null)
            {
                throw new CommandRejectedException("unknown_work_center", $"Work center {workCenter} does not exist.");
            }

            foreach (var machineCode in machines)
            {
                var machine = await resources.FindMachineAsync(machineCode, ct);
                if (machine is null || machine.WorkCenterCode != workCenter)
                {
                    throw new CommandRejectedException("invalid_machine", $"Machine {machineCode} is not in work center {workCenter}.");
                }

                if (await resources.FindMachineActivityAsync(machineCode, activity, ct) is null)
                {
                    throw new CommandRejectedException("activity_not_on_machine", $"{activity} is not defined for machine {machineCode}.");
                }
            }

            if (machines.Count == 0 && !(await resources.ListWorkCenterActivitiesAsync(ct)).Any(a => a.WorkCenterCode == workCenter && a.ActivityCode == activity))
            {
                throw new CommandRejectedException("activity_not_at_work_center", $"{activity} is not an allowed activity at {workCenter}; name eligible machines or allow it at the work center.");
            }

            var competency = uow.Store<ICompetencyLookup>();
            foreach (var q in quals)
            {
                if (!await competency.QualificationTypeExistsAsync(q, ct))
                {
                    throw new CommandRejectedException("unknown_qualification", $"Qualification type {q} does not exist.");
                }
            }

            // ADR 0057 §3.2: a set-up family names what a following job of the same family can keep (a fixture, a gauge).
            var family = string.IsNullOrWhiteSpace(c.SetupFamily) ? null : BusinessCode.Require(c.SetupFamily, "Set-up family");
            if (family is null != c.FamilySetupMinutes is null || c.FamilySetupMinutes is < 0 || c.FamilySetupMinutes > c.SetupMinutes)
            {
                throw new CommandRejectedException("invalid_setup_family", "A set-up family needs its same-family set-up minutes, between 0 and the full set-up.");
            }

            var operation = new RoutingOperationInfo(c.Sequence, description, workCenter, activity, machines, quals, c.SetupMinutes, c.RunMinutesPerUnit, c.CrewSize, family, c.FamilySetupMinutes);
            if (!await uow.Store<IEngineeringStore>().InsertOperationAsync(part, rev, operation, ct))
            {
                throw new CommandRejectedException("operation_exists", $"Operation {c.Sequence} already exists.");
            }

            return ("operation_added", null, CanonicalJson.Serialize(operation));
        }, ct);
    }

    public Task<CommandResult> RemoveRoutingOperationAsync(RemoveRoutingOperation c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var (part, rev) = (BusinessCode.Require(c.PartNumber, "Part"), RequireRevision(c.Revision));
        return Draft(nameof(RemoveRoutingOperation), part, rev, c, context, requireDraft: true, async (uow, _) =>
        {
            if (!await uow.Store<IEngineeringStore>().DeleteOperationAsync(part, rev, c.Sequence, ct))
            {
                throw new CommandRejectedException("operation_not_found", $"Operation {c.Sequence} does not exist.");
            }

            return ("operation_removed", CanonicalJson.Serialize(new { sequence = c.Sequence }), null);
        }, ct);
    }

    public Task<CommandResult> UpdateRoutingOperationAsync(UpdateRoutingOperation c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var (part, rev) = (BusinessCode.Require(c.PartNumber, "Part"), RequireRevision(c.Revision));
        return Draft(nameof(UpdateRoutingOperation), part, rev, c, context, requireDraft: true, async (uow, _) =>
        {
            if (c.Sequence is < 1 or > 9999 || c.SetupMinutes < 0 || c.RunMinutesPerUnit < 0 || c.CrewSize is < 1 or > 20)
            {
                throw new CommandRejectedException("invalid_operation", "Sequence 1-9999, non-negative times and a crew of 1-20.");
            }

            var description = BusinessCode.RequireName(c.Description, "Operation");
            var workCenter = BusinessCode.Require(c.WorkCenterCode, "Work center");
            var activity = BusinessCode.Require(c.ActivityCode, "Activity");
            var machines = (c.EligibleMachines ?? []).Select(m => BusinessCode.Require(m, "Machine")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var quals = (c.RequiredQualifications ?? []).Select(q => BusinessCode.Require(q, "Qualification")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var resources = uow.Store<IResourcesLookup>();
            if (await resources.FindWorkCenterAsync(workCenter, ct) is null)
            {
                throw new CommandRejectedException("unknown_work_center", $"Work center {workCenter} does not exist.");
            }

            foreach (var machineCode in machines)
            {
                var machine = await resources.FindMachineAsync(machineCode, ct);
                if (machine is null || machine.WorkCenterCode != workCenter)
                {
                    throw new CommandRejectedException("invalid_machine", $"Machine {machineCode} is not in work center {workCenter}.");
                }

                if (await resources.FindMachineActivityAsync(machineCode, activity, ct) is null)
                {
                    throw new CommandRejectedException("activity_not_on_machine", $"{activity} is not defined for machine {machineCode}.");
                }
            }

            if (machines.Count == 0 && !(await resources.ListWorkCenterActivitiesAsync(ct)).Any(a => a.WorkCenterCode == workCenter && a.ActivityCode == activity))
            {
                throw new CommandRejectedException("activity_not_at_work_center", $"{activity} is not an allowed activity at {workCenter}; name eligible machines or allow it at the work center.");
            }

            var competency = uow.Store<ICompetencyLookup>();
            foreach (var qualification in quals)
            {
                if (!await competency.QualificationTypeExistsAsync(qualification, ct))
                {
                    throw new CommandRejectedException("unknown_qualification", $"Qualification type {qualification} does not exist.");
                }
            }

            var family = string.IsNullOrWhiteSpace(c.SetupFamily) ? null : BusinessCode.Require(c.SetupFamily, "Set-up family");
            if (family is null != c.FamilySetupMinutes is null || c.FamilySetupMinutes is < 0 || c.FamilySetupMinutes > c.SetupMinutes)
            {
                throw new CommandRejectedException("invalid_setup_family", "A set-up family needs its same-family set-up minutes, between 0 and the full set-up.");
            }

            var updated = new RoutingOperationInfo(c.Sequence, description, workCenter, activity, machines, quals, c.SetupMinutes, c.RunMinutesPerUnit, c.CrewSize, family, c.FamilySetupMinutes);
            var oldOperation = (await uow.Store<IEngineeringLookup>().GetRevisionDetailAsync(part, rev, ct))?.Routing.SingleOrDefault(operation => operation.Sequence == c.Sequence)
                ?? throw new CommandRejectedException("operation_not_found", $"Operation {c.Sequence} does not exist.");
            if (!await uow.Store<IEngineeringStore>().UpdateOperationAsync(part, rev, c.Sequence, updated, ct))
            {
                throw new CommandRejectedException("operation_not_found", $"Operation {c.Sequence} does not exist.");
            }

            return ("routing_operation_updated", CanonicalJson.Serialize(oldOperation), CanonicalJson.Serialize(updated));
        }, ct);
    }

    public Task<CommandResult> ReleaseRevisionAsync(ReleaseRevision c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var (part, rev) = (BusinessCode.Require(c.PartNumber, "Part"), RequireRevision(c.Revision));
        var spec = new CommandSpec(SourceContext, nameof(ReleaseRevision), Permissions.EngineeringRelease, ResourceRef.Of(RevisionEntity, $"{part}:{rev}"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (await ChangeService.OpenChangeForAsync(uow, ChangeLineKind.ItemRevision, part, rev, ct) is { } change)
            {
                throw new CommandRejectedException("under_change", $"{part} rev {rev} is part of {change}; it is released by that change.");
            }

            var (effectiveFrom, superseded, detail) = await ReleaseRevisionCoreAsync(uow, part, rev, c.EffectiveFrom, now, context.Actor, ct);
            var id = $"{part}:{rev}";
            return new CommandOutcome(
                CommandResult.Ok(id, $"{part} rev {rev} released, effective {effectiveFrom:u}" + (superseded is null ? "." : $"; supersedes rev {superseded.Revision}.")),
                [pipeline.Event(new RevisionReleased(part, rev, effectiveFrom, superseded?.Revision), context, SourceContext, RevisionEntity, id)],
                new AuditChange(RevisionEntity, id, "Draft", CanonicalJson.Serialize(new { status = "Released", effectiveFrom, supersedes = superseded?.Revision, bom = detail.Bom, routing = detail.Routing })));
        }, ct);
    }

    /// <summary>
    /// Releases a draft item revision in the caller's transaction: checks contributors, effectivity, routing,
    /// components and loops, then supersedes the revision in force. Used by release and by engineering changes.
    /// </summary>
    internal static async Task<(DateTimeOffset EffectiveFrom, RevisionInfo? Superseded, RevisionDetail Detail)> ReleaseRevisionCoreAsync(
        IKernelUnitOfWork uow, string part, string rev, DateTimeOffset? requestedFrom, DateTimeOffset now, string actor, CancellationToken ct)
    {
        var lookup = uow.Store<IEngineeringLookup>();
        var detail = await lookup.GetRevisionDetailAsync(part, rev, ct)
            ?? throw new CommandRejectedException("unknown_revision", $"Revision {part} {rev} does not exist.");
        if (detail.Revision.Status != RevisionStatus.Draft)
        {
            throw new CommandRejectedException("not_draft", $"Revision {part} {rev} is already released.");
        }

        if ((await uow.Store<IEngineeringStore>().GetContributorsAsync(part, rev, ct)).Contains(actor, StringComparer.Ordinal))
        {
            throw new CommandRejectedException("separation_of_duties", $"You created or edited {part} rev {rev}; a different person must release it.");
        }

        var effectiveFrom = requestedFrom?.ToUniversalTime() ?? now;
        if (effectiveFrom < now.AddMinutes(-1))
        {
            throw new CommandRejectedException("backdated", "A release cannot take effect in the past.");
        }

        var problems = new List<string>();
        if (detail.Item.MakeBuy == MakeBuy.Make && detail.Routing.Count == 0)
        {
            problems.Add($"{part}: a made part needs at least one routing operation.");
        }

        foreach (var line in detail.Bom)
        {
            if (await lookup.GetEffectiveRevisionAsync(line.ComponentPartNumber, effectiveFrom, ct) is null)
            {
                problems.Add($"Component {line.ComponentPartNumber} (line {line.LineNumber}) has no released revision in force at {effectiveFrom:u}.");
            }
        }

        var edges = await uow.Store<IEngineeringStore>().ListBomEdgesAsync(ct);
        if (Reaches(edges.Where(e => e.Parent != part).Concat(detail.Bom.Select(b => (part, b.ComponentPartNumber))).ToList(), part, part, skipStart: true))
        {
            problems.Add($"The bill of materials of {part} contains a loop.");
        }

        if (problems.Count > 0)
        {
            throw new CommandRejectedException("release_blocked", string.Join(" ", problems));
        }

        var released = (await lookup.ListRevisionsAsync(part, ct)).Where(r => r.Status == RevisionStatus.Released).ToList();
        if (released.Any(r => r.EffectiveFrom >= effectiveFrom))
        {
            throw new CommandRejectedException("effectivity_conflict", $"Another released revision of {part} already takes effect at or after this date.");
        }

        var store = uow.Store<IEngineeringStore>();
        var superseded = released.Where(r => r.EffectiveTo is null || r.EffectiveTo > effectiveFrom).OrderBy(r => r.EffectiveFrom).LastOrDefault();
        if (superseded is not null)
        {
            await store.SetEffectivityAsync(part, superseded.Revision, RevisionStatus.Released, superseded.EffectiveFrom, effectiveFrom, actor, ct);
        }

        await store.SetEffectivityAsync(part, rev, RevisionStatus.Released, effectiveFrom, null, actor, ct);
        return (effectiveFrom, superseded, detail);
    }

    public Task<CommandResult> WithdrawRevisionAsync(WithdrawRevision c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var (part, rev) = (BusinessCode.Require(c.PartNumber, "Part"), RequireRevision(c.Revision));
        var spec = new CommandSpec(SourceContext, nameof(WithdrawRevision), Permissions.EngineeringRelease, ResourceRef.Of(RevisionEntity, $"{part}:{rev}"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var revision = (await uow.Store<IEngineeringLookup>().ListRevisionsAsync(part, ct)).SingleOrDefault(r => r.Revision == rev)
                ?? throw new CommandRejectedException("unknown_revision", $"Revision {part} {rev} does not exist.");
            if (revision.Status != RevisionStatus.Released || (revision.EffectiveTo is { } to && to <= now))
            {
                throw new CommandRejectedException("not_in_force", $"Revision {part} {rev} is not released or already ended.");
            }

            await uow.Store<IEngineeringStore>().SetEffectivityAsync(part, rev, RevisionStatus.Released, revision.EffectiveFrom, now, context.Actor, ct);
            var id = $"{part}:{rev}";
            return new CommandOutcome(
                CommandResult.Ok(id, $"{part} rev {rev} withdrawn. Orders already released keep their snapshot."),
                [pipeline.Event(new RevisionWithdrawn(part, rev, now), context, SourceContext, RevisionEntity, id)],
                new AuditChange(RevisionEntity, id, CanonicalJson.Serialize(revision), CanonicalJson.Serialize(revision with { EffectiveTo = now })));
        }, ct);
    }

    private Task<CommandResult> Draft<TCommand>(
        string commandType,
        string part,
        string rev,
        TCommand command,
        CommandContext context,
        bool requireDraft,
        Func<Kernel.Access.IKernelUnitOfWork, DateTimeOffset, Task<(string Change, string? Old, string? New)>> body,
        CancellationToken ct)
    {
        var id = $"{part}:{rev}";
        var spec = new CommandSpec(SourceContext, commandType, Permissions.EngineeringManage, ResourceRef.Of(RevisionEntity, id));
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            if (requireDraft)
            {
                var revision = (await uow.Store<IEngineeringLookup>().ListRevisionsAsync(part, ct)).SingleOrDefault(r => r.Revision == rev)
                    ?? throw new CommandRejectedException("unknown_revision", $"Revision {part} {rev} does not exist.");
                if (revision.Status != RevisionStatus.Draft)
                {
                    throw new CommandRejectedException("revision_released", $"Revision {part} {rev} is released and cannot change. Create a new revision.");
                }
            }

            var (change, oldValue, newValue) = await body(uow, now);
            await uow.Store<IEngineeringStore>().AddContributorAsync(part, rev, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(id, $"{part} rev {rev}: {change.Replace('_', ' ')}."),
                [pipeline.Event(new RevisionChanged(part, rev, change), context, SourceContext, RevisionEntity, id)],
                new AuditChange(RevisionEntity, id, oldValue, newValue));
        }, ct);
    }

    private static string RequireRevision(string? value)
    {
        var rev = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (!RevisionPattern().IsMatch(rev))
        {
            throw new CommandRejectedException("invalid_revision", $"Revision '{value}' is invalid. Use 1-10 characters: A-Z, 0-9, '-' or '.'.");
        }

        return rev;
    }

    // True if 'target' is reachable from 'from' following parent → component edges.
    private static bool Reaches(IReadOnlyList<(string Parent, string Component)> edges, string from, string target, bool skipStart = false)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        if (skipStart)
        {
            foreach (var e in edges.Where(e => e.Parent == from))
            {
                stack.Push(e.Component);
            }
        }
        else
        {
            stack.Push(from);
        }

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == target)
            {
                return true;
            }

            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var e in edges.Where(e => e.Parent == current))
            {
                stack.Push(e.Component);
            }
        }

        return false;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9.-]{0,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex RevisionPattern();
}
