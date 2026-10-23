using Forge.Competency.Contracts;
using Forge.Kernel.Access;
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

public sealed record CreateWorkCenter(string Code, string SafetyAreaCode, string Name, string? Picture = null);

public sealed record CreateMachine(string Code, string WorkCenterCode, string Name);

public sealed record DefineActivityType(string Code, string Name, string? Description);

public sealed record SetMachineActivity(string MachineCode, string ActivityCode, IReadOnlyList<string> OperatorQualifications, bool RequiresMachineAuthorization = false);

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

public sealed record MachineRequirementChanged(string MachineCode, string ActivityCode, IReadOnlyList<string> OperatorQualifications, bool RequiresMachineAuthorization = false) : IDomainEvent
{
    public static string EventType => nameof(MachineRequirementChanged);

    public static int SchemaVersion => 1;
}

/// <summary>Changes a site's name or time zone. The code never changes.</summary>
public sealed record UpdateSite(string Code, string Name, string? TimeZone);

public sealed record UpdateSafetyArea(string Code, string Name);

/// <summary>Renames a work center, moves it to another safety area of the same site, or changes its picture.</summary>
public sealed record UpdateWorkCenter(string Code, string Name, string SafetyAreaCode, string? Picture);

/// <summary>A work center's cell in the configurable plant floor map.</summary>
public sealed record WorkCenterPlacement(string Code, int LayoutX, int LayoutY);

public sealed record UpdateWorkCenterLayouts(IReadOnlyList<WorkCenterPlacement> Placements);

public sealed record WorkCenterMapPlacement(string Code, int MapX, int MapY);

public sealed record UpdateWorkCenterMapLayout(IReadOnlyList<WorkCenterMapPlacement> Placements);

/// <summary>Renames a machine or moves it to another work center.</summary>
public sealed record UpdateMachine(string Code, string Name, string WorkCenterCode);

public sealed record UpdateActivityType(string Code, string Name, string? Description);

/// <summary>Retires (Active = false) or reactivates a safety area, work center, machine or activity type.</summary>
public sealed record SetResourceActive(string Kind, string Code, bool Active);

public sealed record RemoveMachineActivity(string MachineCode, string ActivityCode);

public sealed record RemoveWorkCenterActivity(string WorkCenterCode, string ActivityCode);

public sealed record UnassignWorkCenterShift(string WorkCenterCode, string ShiftCode);

/// <summary>Any resource record as stored. Parent is the site, safety area or work center; Detail is a site's time zone or an activity's description.</summary>
public sealed record ResourceRecord(string Kind, string Code, string Name, string? Parent, bool Active, string? Detail, string? Picture, int? LayoutX = null, int? LayoutY = null, int? MapX = null, int? MapY = null);

public sealed record ResourceChanged(string Kind, string Code, string Change) : IDomainEvent
{
    public static string EventType => nameof(ResourceChanged);

    public static int SchemaVersion => 1;
}

public interface IResourcesStore
{
    Task<ResourceRecord?> FindAnyAsync(string kind, string code, CancellationToken ct);

    Task UpdateAsync(ResourceRecord record, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> DeleteMachineActivityAsync(string machineCode, string activityCode, CancellationToken ct);

    Task<bool> DeleteWorkCenterActivityAsync(string workCenterCode, string activityCode, CancellationToken ct);

    Task<bool> DeleteWorkCenterShiftAsync(WorkCenterShiftInfo link, CancellationToken ct);

    Task SetWorkCenterPictureAsync(string code, string? picture, CancellationToken ct);
    Task SetSiteMapAsync(string siteCode, string? sha256, string? contentType, DateTimeOffset at, string actor, CancellationToken ct);
    Task SetWorkCenterMapPositionAsync(string code, int mapX, int mapY, DateTimeOffset at, string actor, CancellationToken ct);

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
public sealed class ResourcesService(CommandPipeline pipeline, Contracts.SiteClock siteClock)
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

        return RememberZone(CreateAsync(nameof(CreateSite), "Site", c.Code, c.Name, null, null, c, context, (_, _) => Task.CompletedTask, ct, (store, code, token) => store.SetSiteTimeZoneAsync(code, zone, token)), c.Code, zone);
    }

    /// <summary>Keeps the site clock current once a site command has succeeded (ADR 0057 §1).</summary>
    private async Task<CommandResult> RememberZone(Task<CommandResult> command, string? siteCode, string? zone)
    {
        var result = await command;
        if (zone is not null && !string.IsNullOrWhiteSpace(siteCode))
        {
            siteClock.Remember(BusinessCode.Require(siteCode, "Site"), zone);
        }

        return result;
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
        var picture = Picture(c.Picture);
        return CreateAsync(
            nameof(CreateWorkCenter),
            "WorkCenter",
            c.Code,
            c.Name,
            area,
            null,
            c,
            context,
            async (lookup, token) =>
            {
                if (await lookup.FindSafetyAreaAsync(area, token) is null)
                {
                    throw new CommandRejectedException("unknown_safety_area", $"Safety area {area} does not exist or is retired.");
                }
            },
            ct,
            picture is null ? null : (store, code, token) => store.SetWorkCenterPictureAsync(code, picture, token));
    }

    public Task<CommandResult> CreateMachineAsync(CreateMachine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var workCenter = BusinessCode.Require(c.WorkCenterCode, "Work center");
        return CreateAsync(nameof(CreateMachine), "Machine", c.Code, c.Name, workCenter, null, c, context, async (lookup, token) =>
        {
            if (await lookup.FindWorkCenterAsync(workCenter, token) is null)
            {
                throw new CommandRejectedException("unknown_work_center", $"Work center {workCenter} does not exist or is retired.");
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
            var profile = new MachineActivityInfo(machine, activity, qualifications, c.RequiresMachineAuthorization);
            await uow.Store<IResourcesStore>().UpsertMachineActivityAsync(profile, now, context.Actor, ct);
            var id = $"{machine}:{activity}";
            return new CommandOutcome(
                CommandResult.Ok(id, $"{activity} on {machine} requires one of: {string.Join(", ", qualifications)}."),
                [pipeline.Event(new MachineRequirementChanged(machine, activity, qualifications, c.RequiresMachineAuthorization), context, SourceContext, "Machine", machine)],
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

    public Task<CommandResult> UpdateSiteAsync(UpdateSite c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var zone = string.IsNullOrWhiteSpace(c.TimeZone) ? null : c.TimeZone.Trim();
        if (zone is not null && NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(zone) is null)
        {
            throw new CommandRejectedException("invalid_time_zone", $"'{zone}' is not an IANA time zone (for example America/New_York).");
        }

        return RememberZone(UpdateAsync(nameof(UpdateSite), "Site", c.Code, c, context, (current, _, _) =>
            Task.FromResult(current with { Name = BusinessCode.RequireName(c.Name, "Site"), Detail = zone ?? current.Detail }), ct), c.Code, zone);
    }

    public Task<CommandResult> UpdateSafetyAreaAsync(UpdateSafetyArea c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return UpdateAsync(nameof(UpdateSafetyArea), "SafetyArea", c.Code, c, context, (current, _, _) =>
            Task.FromResult(current with { Name = BusinessCode.RequireName(c.Name, "Safety area") }), ct);
    }

    public Task<CommandResult> UpdateWorkCenterAsync(UpdateWorkCenter c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var area = BusinessCode.Require(c.SafetyAreaCode, "Safety area");
        var picture = Picture(c.Picture);
        return UpdateAsync(nameof(UpdateWorkCenter), "WorkCenter", c.Code, c, context, async (current, uow, now) =>
        {
            if (area != current.Parent)
            {
                var lookup = uow.Store<IResourcesLookup>();
                var target = await lookup.FindSafetyAreaAsync(area, ct)
                    ?? throw new CommandRejectedException("unknown_safety_area", $"Safety area {area} does not exist or is retired.");
                var from = (await lookup.ListSafetyAreasAsync(ct)).FirstOrDefault(a => a.Code == current.Parent);
                if (from is not null && from.SiteCode != target.SiteCode)
                {
                    throw new CommandRejectedException("site_mismatch", $"{current.Code} can only move to a safety area of site {from.SiteCode}. Create a new work center at the other site instead.");
                }

                await RequireUnusedAsync(uow, "WorkCenter", current.Code, null, now, "moved", ct);
            }

            return current with { Name = BusinessCode.RequireName(c.Name, "Work center"), Parent = area, Picture = picture };
        }, ct);
    }

    /// <summary>Persists a complete floor layout in one audited command so positions remain unique.</summary>
    public Task<CommandResult> UpdateWorkCenterLayoutsAsync(UpdateWorkCenterLayouts c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var placements = c.Placements?.ToArray() ?? [];
        if (placements.Length == 0 || placements.Length > 1000 || placements.Any(p => p is null))
            throw new CommandRejectedException("invalid_layout", "A floor layout must contain each active work center once.");
        foreach (var placement in placements) _ = BusinessCode.Require(placement.Code, "Work center");
        if (placements.Select(p => p.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != placements.Length)
            throw new CommandRejectedException("invalid_layout", "A floor layout must contain each active work center once.");
        if (placements.Any(p => p.LayoutX is < 0 or > 3 || p.LayoutY is < 0 or > 99))
            throw new CommandRejectedException("invalid_layout", "Floor positions use columns 0–3 and rows 0–99.");

        var spec = new CommandSpec(SourceContext, nameof(UpdateWorkCenterLayouts), Permissions.ResourcesManage, ResourceRef.Of("WorkCenter", placements[0].Code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var currentCenters = await uow.Store<IResourcesLookup>().ListWorkCentersAsync(ct);
            var active = currentCenters.Where(w => w.Active).ToDictionary(w => w.Code, StringComparer.OrdinalIgnoreCase);
            var requested = placements.ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);
            if (requested.Count != active.Count || active.Keys.Any(code => !requested.ContainsKey(code)) || requested.Keys.Any(code => !active.ContainsKey(code)))
                throw new CommandRejectedException("invalid_layout", "Refresh the plant map and submit every active work center exactly once.");

            var collisions = placements.GroupBy(p => active[p.Code].SafetyAreaCode + ":" + p.LayoutX + ":" + p.LayoutY, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (collisions is not null)
                throw new CommandRejectedException("layout_collision", "Two work centers cannot occupy the same floor cell in one safety area.");

            var store = uow.Store<IResourcesStore>();
            var oldPositions = new List<object>();
            var newPositions = new List<WorkCenterPlacement>();
            var events = new List<Forge.Kernel.Events.EventEnvelope>();
            foreach (var placement in placements.OrderBy(p => p.Code, StringComparer.Ordinal))
            {
                var center = active[placement.Code];
                oldPositions.Add(new { code = center.Code, layoutX = center.LayoutX, layoutY = center.LayoutY });
                newPositions.Add(placement);
                if (center.LayoutX == placement.LayoutX && center.LayoutY == placement.LayoutY) continue;

                var record = await store.FindAnyAsync("WorkCenter", center.Code, ct)
                    ?? throw new CommandRejectedException("work_center_not_found", $"Work center {center.Code} no longer exists.");
                await store.UpdateAsync(record with { LayoutX = placement.LayoutX, LayoutY = placement.LayoutY }, now, context.Actor, ct);
                events.Add(pipeline.Event(new ResourceChanged("WorkCenter", center.Code, "LayoutChanged"), context, SourceContext, "WorkCenter", center.Code));
            }

            if (events.Count == 0)
                return CommandOutcome.NoChange(CommandResult.NoChange("Plant", "Floor layout unchanged."));

            return new CommandOutcome(
                CommandResult.Ok("Plant", $"Floor layout saved for {events.Count} work center{(events.Count == 1 ? string.Empty : "s")}.") ,
                events,
                new AuditChange("WorkCenterLayout", "Plant", CanonicalJson.Serialize(oldPositions), CanonicalJson.Serialize(newPositions)));
        }, ct, authority: async (uow, _) =>
        {
            var active = (await uow.Store<IResourcesLookup>().ListWorkCentersAsync(ct)).Where(w => w.Active).ToList();
            var resources = active.Select(w => ResourceRef.Of("WorkCenter", w.Code)).ToArray();
            var primary = resources.FirstOrDefault() ?? ResourceRef.Of("WorkCenter", placements[0].Code);
            return new CommandAuthority(primary, AdditionalResources: resources.Skip(1).ToArray());
        });
    }

    /// <summary>Moves work-center markers over the site's plant image in one audited command.</summary>
    public Task<CommandResult> UpdateWorkCenterMapLayoutAsync(UpdateWorkCenterMapLayout c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var placements = c.Placements?.ToArray() ?? [];
        if (placements.Length == 0 || placements.Length > 1000 || placements.Any(p => p is null)
            || placements.Any(p => p.MapX is < 0 or > 1000 || p.MapY is < 0 or > 1000)
            || placements.Select(p => p.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != placements.Length)
            throw new CommandRejectedException("invalid_map_layout", "Map positions must contain unique work-center codes with coordinates from 0 to 1000.");
        foreach (var placement in placements) _ = BusinessCode.Require(placement.Code, "Work center");

        var spec = new CommandSpec(SourceContext, nameof(UpdateWorkCenterMapLayout), Permissions.ResourcesFloorMapManage,
            ResourceRef.Of("WorkCenter", placements[0].Code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var lookup = uow.Store<IResourcesLookup>();
            var active = (await lookup.ListWorkCentersAsync(ct)).Where(w => w.Active).ToDictionary(w => w.Code, StringComparer.OrdinalIgnoreCase);
            var requested = placements.ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);
            if (requested.Count != active.Count || active.Keys.Any(code => !requested.ContainsKey(code)) || requested.Keys.Any(code => !active.ContainsKey(code)))
                throw new CommandRejectedException("invalid_map_layout", "Refresh the plant map and submit every active work center exactly once.");

            var areas = (await lookup.ListSafetyAreasAsync(ct)).ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);
            var sites = (await lookup.ListSitesAsync(ct)).ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
            var collisions = placements.GroupBy(p =>
            {
                var area = areas[active[p.Code].SafetyAreaCode];
                return $"{sites[area.SiteCode].Code}:{p.MapX}:{p.MapY}";
            }, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (collisions is not null)
                throw new CommandRejectedException("map_layout_collision", "Two work centers cannot occupy the exact same marker position on one site map.");

            var store = uow.Store<IResourcesStore>();
            var oldPositions = new List<object>();
            var newPositions = new List<WorkCenterMapPlacement>();
            var events = new List<Forge.Kernel.Events.EventEnvelope>();
            foreach (var position in placements.OrderBy(p => p.Code, StringComparer.Ordinal))
            {
                var center = active[position.Code];
                oldPositions.Add(new { code = center.Code, mapX = center.MapX, mapY = center.MapY });
                newPositions.Add(position);
                if (center.MapX == position.MapX && center.MapY == position.MapY) continue;
                await store.SetWorkCenterMapPositionAsync(center.Code, position.MapX, position.MapY, now, context.Actor, ct);
                events.Add(pipeline.Event(new ResourceChanged("WorkCenter", center.Code, "MapPositionChanged"), context, SourceContext, "WorkCenter", center.Code));
            }

            if (events.Count == 0) return CommandOutcome.NoChange(CommandResult.NoChange("Plant", "Plant-map positions unchanged."));
            return new CommandOutcome(
                CommandResult.Ok("Plant", $"Map positions saved for {events.Count} work center{(events.Count == 1 ? string.Empty : "s")}."),
                events,
                new AuditChange("WorkCenterMapLayout", "Plant", CanonicalJson.Serialize(oldPositions), CanonicalJson.Serialize(newPositions)));
        }, ct, authority: async (uow, _) =>
        {
            var active = (await uow.Store<IResourcesLookup>().ListWorkCentersAsync(ct)).Where(w => w.Active).ToList();
            var resources = active.Select(w => ResourceRef.Of("WorkCenter", w.Code)).ToArray();
            var primary = resources.FirstOrDefault() ?? ResourceRef.Of("WorkCenter", placements[0].Code);
            return new CommandAuthority(primary, AdditionalResources: resources.Skip(1).ToArray());
        });
    }

    public Task<CommandResult> UpdateMachineAsync(UpdateMachine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var workCenter = BusinessCode.Require(c.WorkCenterCode, "Work center");
        return UpdateAsync(nameof(UpdateMachine), "Machine", c.Code, c, context, async (current, uow, now) =>
        {
            if (workCenter != current.Parent)
            {
                if (await uow.Store<IResourcesLookup>().FindWorkCenterAsync(workCenter, ct) is null)
                {
                    throw new CommandRejectedException("unknown_work_center", $"Work center {workCenter} does not exist or is retired.");
                }

                await RequireUnusedAsync(uow, "Machine", current.Code, null, now, "moved", ct);
            }

            return current with { Name = BusinessCode.RequireName(c.Name, "Machine"), Parent = workCenter };
        }, ct);
    }

    public Task<CommandResult> UpdateActivityTypeAsync(UpdateActivityType c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return UpdateAsync(nameof(UpdateActivityType), "ActivityType", c.Code, c, context, (current, _, _) =>
            Task.FromResult(current with { Name = BusinessCode.RequireName(c.Name, "Activity type"), Detail = string.IsNullOrWhiteSpace(c.Description) ? null : c.Description.Trim() }), ct);
    }

    /// <summary>
    /// Retires or reactivates a record. A retired record stays in history, but nothing new can be
    /// planned, assigned or run there. Retiring is refused while anything still uses it.
    /// </summary>
    public Task<CommandResult> SetResourceActiveAsync(SetResourceActive c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var kind = c.Kind switch
        {
            "SafetyArea" or "WorkCenter" or "Machine" or "ActivityType" => c.Kind,
            _ => throw new CommandRejectedException("invalid_kind", "Only safety areas, work centers, machines and activity types can be retired."),
        };
        var commandType = c.Active ? "ReactivateResource" : "RetireResource";
        return UpdateAsync(commandType, kind, c.Code, c, context, async (current, uow, now) =>
        {
            if (current.Active == c.Active)
            {
                return current;
            }

            var lookup = uow.Store<IResourcesLookup>();
            if (c.Active)
            {
                var parentActive = kind switch
                {
                    "WorkCenter" => await lookup.FindSafetyAreaAsync(current.Parent!, ct) is not null,
                    "Machine" => await lookup.FindWorkCenterAsync(current.Parent!, ct) is not null,
                    _ => true,
                };
                if (!parentActive)
                {
                    throw new CommandRejectedException("parent_retired", $"{current.Parent} is retired. Reactivate it first.");
                }

                return current with { Active = true };
            }

            var children = kind switch
            {
                "SafetyArea" => (await lookup.ListWorkCentersAsync(ct)).Where(w => w.Active && w.SafetyAreaCode == current.Code).Select(w => w.Code).ToList(),
                "WorkCenter" => (await lookup.ListMachinesAsync(ct)).Where(m => m.Active && m.WorkCenterCode == current.Code).Select(m => m.Code).ToList(),
                _ => new List<string>(),
            };
            if (children.Count > 0)
            {
                throw new CommandRejectedException("has_active_children", $"{current.Code} still has active {(kind == "SafetyArea" ? "work centers" : "machines")}: {string.Join(", ", children)}. Retire or move them first.");
            }

            if (kind == "ActivityType")
            {
                var users = (await lookup.ListMachineActivitiesAsync(ct)).Where(a => a.ActivityCode == current.Code).Select(a => a.MachineCode)
                    .Concat((await lookup.ListWorkCenterActivitiesAsync(ct)).Where(a => a.ActivityCode == current.Code).Select(a => a.WorkCenterCode))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (users.Count > 0)
                {
                    throw new CommandRejectedException("in_use", $"{current.Code} is still set up on {string.Join(", ", users)}. Remove it there first.");
                }
            }
            else
            {
                await RequireUnusedAsync(uow, kind, current.Code, null, now, "retired", ct);
            }

            return current with { Active = false };
        }, ct, reasonRequired: true);
    }

    public Task<CommandResult> RemoveMachineActivityAsync(RemoveMachineActivity c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var machine = BusinessCode.Require(c.MachineCode, "Machine");
        var activity = BusinessCode.Require(c.ActivityCode, "Activity");
        var spec = new CommandSpec(SourceContext, nameof(RemoveMachineActivity), Permissions.ResourcesManage, ResourceRef.Of("Machine", machine), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var id = $"{machine}:{activity}";
            var previous = await uow.Store<IResourcesLookup>().FindMachineActivityAsync(machine, activity, ct);
            if (previous is null)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, $"{activity} is not set up on {machine}."));
            }

            await RequireUnusedAsync(uow, "Machine", machine, activity, now, "changed", ct);
            await uow.Store<IResourcesStore>().DeleteMachineActivityAsync(machine, activity, ct);
            return new CommandOutcome(
                CommandResult.Ok(id, $"{activity} no longer runs on {machine}."),
                [pipeline.Event(new ResourceChanged("MachineActivity", id, "Removed"), context, SourceContext, "Machine", machine)],
                new AuditChange("MachineActivity", id, CanonicalJson.Serialize(previous), null));
        }, ct);
    }

    public Task<CommandResult> RemoveWorkCenterActivityAsync(RemoveWorkCenterActivity c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var workCenter = BusinessCode.Require(c.WorkCenterCode, "Work center");
        var activity = BusinessCode.Require(c.ActivityCode, "Activity");
        var spec = new CommandSpec(SourceContext, nameof(RemoveWorkCenterActivity), Permissions.ResourcesManage, ResourceRef.Of("WorkCenter", workCenter), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var id = $"{workCenter}:{activity}";
            var previous = new WorkCenterActivityInfo(workCenter, activity);
            if (!(await uow.Store<IResourcesLookup>().ListWorkCenterActivitiesAsync(ct)).Contains(previous))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, $"{activity} is not allowed at {workCenter}."));
            }

            await RequireUnusedAsync(uow, "WorkCenter", workCenter, activity, now, "changed", ct);
            await uow.Store<IResourcesStore>().DeleteWorkCenterActivityAsync(workCenter, activity, ct);
            return new CommandOutcome(
                CommandResult.Ok(id, $"{activity} is no longer done at {workCenter} without a machine."),
                [pipeline.Event(new ResourceChanged("WorkCenterActivity", id, "Removed"), context, SourceContext, "WorkCenter", workCenter)],
                new AuditChange("WorkCenterActivity", id, CanonicalJson.Serialize(previous), null));
        }, ct);
    }

    public Task<CommandResult> UnassignWorkCenterShiftAsync(UnassignWorkCenterShift c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var workCenter = BusinessCode.Require(c.WorkCenterCode, "Work center");
        var shift = BusinessCode.Require(c.ShiftCode, "Shift");
        var spec = new CommandSpec(SourceContext, nameof(UnassignWorkCenterShift), Permissions.ResourcesManage, ResourceRef.Of("WorkCenter", workCenter), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, _) =>
        {
            var id = $"{workCenter}:{shift}";
            if (!await uow.Store<IResourcesStore>().DeleteWorkCenterShiftAsync(new WorkCenterShiftInfo(workCenter, shift), ct))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, $"{workCenter} does not run shift {shift}."));
            }

            return new CommandOutcome(
                CommandResult.Ok(id, $"{workCenter} no longer runs shift {shift}."),
                [pipeline.Event(new CalendarChanged("WorkCenterShiftRemoved", id), context, SourceContext, "WorkCenter", workCenter)],
                new AuditChange("WorkCenterShift", id, id, null));
        }, ct);
    }

    private static string? Picture(string? value)
    {
        var picture = string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
        if (picture is not null && !WorkCenterPictures.All.Contains(picture))
        {
            throw new CommandRejectedException("invalid_picture", $"Picture must be one of: {string.Join(", ", WorkCenterPictures.All)}.");
        }

        return picture;
    }

    private static async Task RequireUnusedAsync(IKernelUnitOfWork uow, string kind, string code, string? activity, DateTimeOffset now, string verb, CancellationToken ct)
    {
        var uses = await uow.Store<IResourceUsage>().FindUsesAsync(kind, code, activity, now, ct);
        if (uses.Count > 0)
        {
            var shown = string.Join("; ", uses.Take(8)) + (uses.Count > 8 ? $"; and {uses.Count - 8} more" : string.Empty);
            throw new CommandRejectedException("in_use", $"{code}{(activity is null ? string.Empty : " " + activity)} cannot be {verb} while it is in use: {shown}.");
        }
    }

    private Task<CommandResult> UpdateAsync<TCommand>(
        string commandType,
        string kind,
        string rawCode,
        TCommand command,
        CommandContext context,
        Func<ResourceRecord, IKernelUnitOfWork, DateTimeOffset, Task<ResourceRecord>> change,
        CancellationToken ct,
        bool reasonRequired = true)
    {
        var code = BusinessCode.Require(rawCode, kind);
        var spec = new CommandSpec(SourceContext, commandType, Permissions.ResourcesManage, ResourceRef.Of(kind, code), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var store = uow.Store<IResourcesStore>();
            var current = await store.FindAnyAsync(kind, code, ct)
                ?? throw new CommandRejectedException($"{Snake(kind)}_not_found", $"{kind} {code} does not exist.");
            var updated = await change(current, uow, now);
            if (updated == current)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(code, "Nothing changed."));
            }

            await store.UpdateAsync(updated, now, context.Actor, ct);
            var what = updated.Active != current.Active ? (updated.Active ? "Reactivated" : "Retired")
                : updated.Parent != current.Parent ? "Moved"
                : "Updated";
            return new CommandOutcome(
                CommandResult.Ok(code, what switch
                {
                    "Moved" => $"{kind} {code} moved to {updated.Parent}.",
                    _ => $"{kind} {code} {what.ToLowerInvariant()}.",
                }),
                [pipeline.Event(new ResourceChanged(kind, code, what), context, SourceContext, kind, code)],
                new AuditChange(kind, code, CanonicalJson.Serialize(current), CanonicalJson.Serialize(updated)));
        }, ct);
    }

    private static string Snake(string kind) => kind switch
    {
        "SafetyArea" => "safety_area",
        "WorkCenter" => "work_center",
        "ActivityType" => "activity_type",
        _ => kind.ToLowerInvariant(),
    };

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
