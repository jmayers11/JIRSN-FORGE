using Forge.Competency.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Resources;

public sealed record CreateSite(string Code, string Name);

public sealed record CreateSafetyArea(string Code, string SiteCode, string Name);

public sealed record CreateWorkCenter(string Code, string SafetyAreaCode, string Name);

public sealed record CreateMachine(string Code, string WorkCenterCode, string Name);

public sealed record DefineActivityType(string Code, string Name, string? Description);

public sealed record SetMachineActivity(string MachineCode, string ActivityCode, IReadOnlyList<string> OperatorQualifications);

public sealed record AllowWorkCenterActivity(string WorkCenterCode, string ActivityCode);

public sealed record WorkCenterActivityAllowed(string WorkCenterCode, string ActivityCode) : IDomainEvent
{
    public static string EventType => nameof(WorkCenterActivityAllowed);

    public static int SchemaVersion => 1;
}

public sealed record ResourceDefined(string Kind, string Code, string Name, string? Parent) : IDomainEvent
{
    public static string EventType => nameof(ResourceDefined);

    public static int SchemaVersion => 1;
}

public sealed record MachineRequirementChanged(string MachineCode, string ActivityCode, IReadOnlyList<string> OperatorQualifications) : IDomainEvent
{
    public static string EventType => nameof(MachineRequirementChanged);

    public static int SchemaVersion => 1;
}

public interface IResourcesStore
{
    Task<bool> InsertAsync(string kind, string code, string name, string? parent, string? description, DateTimeOffset at, string actor, CancellationToken ct);

    Task UpsertMachineActivityAsync(MachineActivityInfo profile, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> InsertWorkCenterActivityAsync(WorkCenterActivityInfo activity, DateTimeOffset at, string actor, CancellationToken ct);
}

/// <summary>
/// The physical and organizational model: site → safety area → work center → machine, the
/// activity types performed there, and what each machine requires to run each activity.
/// </summary>
public sealed class ResourcesService(CommandPipeline pipeline)
{
    public const string SourceContext = "Resources";

    public Task<CommandResult> CreateSiteAsync(CreateSite c, CommandContext context, CancellationToken ct) =>
        CreateAsync(nameof(CreateSite), "Site", c.Code, c.Name, null, null, c, context, (_, _) => Task.CompletedTask, ct);

    public Task<CommandResult> CreateSafetyAreaAsync(CreateSafetyArea c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var site = BusinessCode.Require(c.SiteCode, "Site");
        return CreateAsync(nameof(CreateSafetyArea), "SafetyArea", c.Code, c.Name, site, null, c, context, async (lookup, token) =>
        {
            if (!(await lookup.ListSitesAsync(token)).Any(s => s.Code == site))
            {
                throw new CommandRejectedException("unknown_site", $"Site {site} does not exist.");
            }
        }, ct);
    }

    public Task<CommandResult> CreateWorkCenterAsync(CreateWorkCenter c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var area = BusinessCode.Require(c.SafetyAreaCode, "Safety area");
        return CreateAsync(nameof(CreateWorkCenter), "WorkCenter", c.Code, c.Name, area, null, c, context, async (lookup, token) =>
        {
            if (await lookup.FindSafetyAreaAsync(area, token) is null)
            {
                throw new CommandRejectedException("unknown_safety_area", $"Safety area {area} does not exist.");
            }
        }, ct);
    }

    public Task<CommandResult> CreateMachineAsync(CreateMachine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var workCenter = BusinessCode.Require(c.WorkCenterCode, "Work center");
        return CreateAsync(nameof(CreateMachine), "Machine", c.Code, c.Name, workCenter, null, c, context, async (lookup, token) =>
        {
            if (await lookup.FindWorkCenterAsync(workCenter, token) is null)
            {
                throw new CommandRejectedException("unknown_work_center", $"Work center {workCenter} does not exist.");
            }
        }, ct);
    }

    public Task<CommandResult> DefineActivityTypeAsync(DefineActivityType c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return CreateAsync(nameof(DefineActivityType), "ActivityType", c.Code, c.Name, null, c.Description?.Trim(), c, context, (_, _) => Task.CompletedTask, ct);
    }

    public Task<CommandResult> SetMachineActivityAsync(SetMachineActivity c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var machine = BusinessCode.Require(c.MachineCode, "Machine");
        var activity = BusinessCode.Require(c.ActivityCode, "Activity");
        var qualifications = (c.OperatorQualifications ?? []).Select(q => BusinessCode.Require(q, "Qualification")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var spec = new CommandSpec(SourceContext, nameof(SetMachineActivity), Permissions.ResourcesManage, ResourceRef.Of("Machine", machine));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (qualifications.Count == 0)
            {
                throw new CommandRejectedException("qualification_required", "At least one operator qualification is required.");
            }

            var lookup = uow.Store<IResourcesLookup>();
            if (await lookup.FindMachineAsync(machine, ct) is null)
            {
                throw new CommandRejectedException("unknown_machine", $"Machine {machine} does not exist.");
            }

            if (!await lookup.ActivityTypeExistsAsync(activity, ct))
            {
                throw new CommandRejectedException("unknown_activity", $"Activity type {activity} does not exist.");
            }

            var competency = uow.Store<ICompetencyLookup>();
            foreach (var q in qualifications)
            {
                if (!await competency.QualificationTypeExistsAsync(q, ct))
                {
                    throw new CommandRejectedException("unknown_qualification", $"Qualification type {q} does not exist.");
                }
            }

            var previous = await lookup.FindMachineActivityAsync(machine, activity, ct);
            var profile = new MachineActivityInfo(machine, activity, qualifications);
            await uow.Store<IResourcesStore>().UpsertMachineActivityAsync(profile, now, context.Actor, ct);
            var id = $"{machine}:{activity}";
            return new CommandOutcome(
                CommandResult.Ok(id, $"{activity} on {machine} requires one of: {string.Join(", ", qualifications)}."),
                [pipeline.Event(new MachineRequirementChanged(machine, activity, qualifications), context, SourceContext, "Machine", machine)],
                new AuditChange("MachineActivity", id, previous is null ? null : CanonicalJson.Serialize(previous), CanonicalJson.Serialize(profile)));
        }, ct);
    }

    public Task<CommandResult> AllowWorkCenterActivityAsync(AllowWorkCenterActivity c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var workCenter = BusinessCode.Require(c.WorkCenterCode, "Work center");
        var activity = BusinessCode.Require(c.ActivityCode, "Activity");
        var spec = new CommandSpec(SourceContext, nameof(AllowWorkCenterActivity), Permissions.ResourcesManage, ResourceRef.Of("WorkCenter", workCenter));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var lookup = uow.Store<IResourcesLookup>();
            if (await lookup.FindWorkCenterAsync(workCenter, ct) is null)
            {
                throw new CommandRejectedException("unknown_work_center", $"Work center {workCenter} does not exist.");
            }

            if (!await lookup.ActivityTypeExistsAsync(activity, ct))
            {
                throw new CommandRejectedException("unknown_activity", $"Activity type {activity} does not exist.");
            }

            var id = $"{workCenter}:{activity}";
            var allowed = new WorkCenterActivityInfo(workCenter, activity);
            if (!await uow.Store<IResourcesStore>().InsertWorkCenterActivityAsync(allowed, now, context.Actor, ct))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Already allowed."));
            }

            return new CommandOutcome(
                CommandResult.Ok(id, $"{activity} may be performed at {workCenter}."),
                [pipeline.Event(new WorkCenterActivityAllowed(workCenter, activity), context, SourceContext, "WorkCenter", workCenter)],
                new AuditChange("WorkCenterActivity", id, null, CanonicalJson.Serialize(allowed)));
        }, ct);
    }

    private Task<CommandResult> CreateAsync<TCommand>(
        string commandType,
        string kind,
        string rawCode,
        string rawName,
        string? parent,
        string? description,
        TCommand command,
        CommandContext context,
        Func<IResourcesLookup, CancellationToken, Task> validateParent,
        CancellationToken ct)
    {
        var code = BusinessCode.Require(rawCode, kind);
        var spec = new CommandSpec(SourceContext, commandType, Permissions.ResourcesManage, ResourceRef.Of(kind, code));
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var name = BusinessCode.RequireName(rawName, kind);
            await validateParent(uow.Store<IResourcesLookup>(), ct);
            if (!await uow.Store<IResourcesStore>().InsertAsync(kind, code, name, parent, description, now, context.Actor, ct))
            {
                throw new CommandRejectedException("code_exists", $"{kind} {code} already exists.");
            }

            return new CommandOutcome(
                CommandResult.Ok(code, $"{kind} {code} created."),
                [pipeline.Event(new ResourceDefined(kind, code, name, parent), context, SourceContext, kind, code)],
                new AuditChange(kind, code, null, CanonicalJson.Serialize(new { code, name, parent, description })));
        }, ct);
    }
}
