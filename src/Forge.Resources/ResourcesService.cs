using Forge.Competency.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Resources;

public sealed record CreateSite(string Code, string Name, string? TimeZone = null);

public sealed record DefineShift(string Code, string SiteCode, string Name, TimeOnly Start, int DurationMinutes, IReadOnlyList<int> Days);

public sealed record AssignWorkCenterShift(string WorkCenterCode, string ShiftCode);

public sealed record AddSiteClosure(string SiteCode, DateOnly Date, string? Note);

public sealed record CalendarChanged(string Change, string Subject) : IDomainEvent
{
    public static string EventType => nameof(CalendarChanged);

    public static int SchemaVersion => 1;
}

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

    Task SetSiteTimeZoneAsync(string siteCode, string timeZone, CancellationToken ct);

    Task<bool> InsertShiftAsync(ShiftInfo shift, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> InsertWorkCenterShiftAsync(WorkCenterShiftInfo link, CancellationToken ct);

    Task<bool> InsertSiteClosureAsync(SiteClosureInfo closure, DateTimeOffset at, string actor, CancellationToken ct);
}

/// <summary>
/// The physical and organizational model: site → safety area → work center → machine, the
/// activity types performed there, and what each machine requires to run each activity.
/// </summary>
public sealed class ResourcesService(CommandPipeline pipeline)
{
    public const string SourceContext = "Resources";

    public Task<CommandResult> CreateSiteAsync(CreateSite c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var zone = string.IsNullOrWhiteSpace(c.TimeZone) ? "America/New_York" : c.TimeZone.Trim();
        if (NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(zone) is null)
        {
            throw new CommandRejectedException("invalid_time_zone", $"'{zone}' is not an IANA time zone (for example America/New_York).");
        }

        return CreateAsync(nameof(CreateSite), "Site", c.Code, c.Name, null, null, c, context, (_, _) => Task.CompletedTask, ct, (store, code, token) => store.SetSiteTimeZoneAsync(code, zone, token));
    }

    public Task<CommandResult> DefineShiftAsync(DefineShift c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Shift");
        var site = BusinessCode.Require(c.SiteCode, "Site");
        var spec = new CommandSpec(SourceContext, nameof(DefineShift), Permissions.ResourcesManage, ResourceRef.Of("Shift", code));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var name = BusinessCode.RequireName(c.Name, "Shift");
            var days = (c.Days ?? []).Distinct().Order().ToList();
            if (c.DurationMinutes is < 1 or > 1440 || days.Count == 0 || days.Any(d => d is < 1 or > 7))
            {
                throw new CommandRejectedException("invalid_shift", "A shift lasts 1-1440 minutes and runs on at least one ISO weekday (1 = Monday to 7 = Sunday).");
            }

            if (!(await uow.Store<IResourcesLookup>().ListSitesAsync(ct)).Any(s => s.Code == site))
            {
                throw new CommandRejectedException("unknown_site", $"Site {site} does not exist.");
            }

            var shift = new ShiftInfo(code, site, name, c.Start, c.DurationMinutes, days);
            if (!await uow.Store<IResourcesStore>().InsertShiftAsync(shift, now, context.Actor, ct))
            {
                throw new CommandRejectedException("code_exists", $"Shift {code} already exists.");
            }

            return new CommandOutcome(
                CommandResult.Ok(code, $"Shift {code} defined: {c.Start:HH\\:mm} for {c.DurationMinutes} minutes on days {string.Join(",", days)}."),
                [pipeline.Event(new CalendarChanged("ShiftDefined", code), context, SourceContext, "Shift", code)],
                new AuditChange("Shift", code, null, CanonicalJson.Serialize(shift)));
        }, ct);
    }

    public Task<CommandResult> AssignWorkCenterShiftAsync(AssignWorkCenterShift c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var workCenter = BusinessCode.Require(c.WorkCenterCode, "Work center");
        var shiftCode = BusinessCode.Require(c.ShiftCode, "Shift");
        var spec = new CommandSpec(SourceContext, nameof(AssignWorkCenterShift), Permissions.ResourcesManage, ResourceRef.Of("WorkCenter", workCenter));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, _) =>
        {
            var lookup = uow.Store<IResourcesLookup>();
            var wc = await lookup.FindWorkCenterAsync(workCenter, ct) ?? throw new CommandRejectedException("unknown_work_center", $"Work center {workCenter} does not exist.");
            var area = await lookup.FindSafetyAreaAsync(wc.SafetyAreaCode, ct);
            var shift = (await lookup.ListShiftsAsync(ct)).FirstOrDefault(s => s.Code == shiftCode)
                ?? throw new CommandRejectedException("unknown_shift", $"Shift {shiftCode} does not exist.");
            if (area?.SiteCode != shift.SiteCode)
            {
                throw new CommandRejectedException("site_mismatch", $"Shift {shiftCode} belongs to site {shift.SiteCode}, not the work center's site.");
            }

            var id = $"{workCenter}:{shiftCode}";
            if (!await uow.Store<IResourcesStore>().InsertWorkCenterShiftAsync(new WorkCenterShiftInfo(workCenter, shiftCode), ct))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Shift already assigned."));
            }

            return new CommandOutcome(
                CommandResult.Ok(id, $"{workCenter} runs shift {shiftCode}."),
                [pipeline.Event(new CalendarChanged("WorkCenterShiftAssigned", id), context, SourceContext, "WorkCenter", workCenter)],
                new AuditChange("WorkCenterShift", id, null, id));
        }, ct);
    }

    public Task<CommandResult> AddSiteClosureAsync(AddSiteClosure c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var site = BusinessCode.Require(c.SiteCode, "Site");
        var spec = new CommandSpec(SourceContext, nameof(AddSiteClosure), Permissions.ResourcesManage, ResourceRef.Of("Site", site), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (!(await uow.Store<IResourcesLookup>().ListSitesAsync(ct)).Any(s => s.Code == site))
            {
                throw new CommandRejectedException("unknown_site", $"Site {site} does not exist.");
            }

            var closure = new SiteClosureInfo(site, c.Date, c.Note?.Trim());
            var id = $"{site}:{c.Date:yyyy-MM-dd}";
            if (!await uow.Store<IResourcesStore>().InsertSiteClosureAsync(closure, now, context.Actor, ct))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Already closed that day."));
            }

            return new CommandOutcome(
                CommandResult.Ok(id, $"{site} closed on {c.Date:yyyy-MM-dd}."),
                [pipeline.Event(new CalendarChanged("SiteClosed", id), context, SourceContext, "Site", site)],
                new AuditChange("SiteClosure", id, null, CanonicalJson.Serialize(closure)));
        }, ct);
    }

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
        CancellationToken ct,
        Func<IResourcesStore, string, CancellationToken, Task>? afterInsert = null)
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

            if (afterInsert is not null)
            {
                await afterInsert(uow.Store<IResourcesStore>(), code, ct);
            }

            return new CommandOutcome(
                CommandResult.Ok(code, $"{kind} {code} created."),
                [pipeline.Event(new ResourceDefined(kind, code, name, parent), context, SourceContext, kind, code)],
                new AuditChange(kind, code, null, CanonicalJson.Serialize(new { code, name, parent, description })));
        }, ct);
    }
}
