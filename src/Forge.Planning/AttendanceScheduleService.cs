using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;
using Forge.Workforce;
using Forge.Workforce.Contracts;
using Forge.Planning.Engines;

namespace Forge.Planning;

public sealed record GenerateAttendanceSchedule(DateTimeOffset From, DateTimeOffset Through);

public sealed record AttendanceScheduleGenerated(int OpportunitiesCreated, DateTimeOffset From, DateTimeOffset Through) : IDomainEvent
{
    public static string EventType => nameof(AttendanceScheduleGenerated);
    public static int SchemaVersion => 1;
}

/// <summary>Creates dated attendance expectations from the canonical shift calendar and worker shift assignments.</summary>
public sealed class AttendanceScheduleService(CommandPipeline pipeline)
{
    public Task<CommandResult> GenerateAsync(GenerateAttendanceSchedule c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.Through <= c.From || c.Through - c.From > TimeSpan.FromDays(90))
            throw new CommandRejectedException("invalid_schedule_range", "Choose a range up to 90 days long.");
        var spec = new CommandSpec("Workforce", nameof(GenerateAttendanceSchedule), Permissions.AttendanceScheduleManage,
            new ResourceRef("AttendanceSchedule", $"{c.From:O}/{c.Through:O}"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var resources = uow.Store<IResourcesLookup>();
            var lookup = uow.Store<IWorkforceLookup>();
            var sites = await resources.ListSitesAsync(ct);
            var shifts = await resources.ListShiftsAsync(ct);
            var closures = await resources.ListSiteClosuresAsync(ct);
            var workers = await lookup.GetWorkerStatesAsync(now, ct);
            var snapshot = new SiteSnapshot(now, [], [], [], [], [], [], [], []) { Sites = sites, Shifts = shifts, Closures = closures };
            var shiftsByCode = shifts.ToDictionary(shift => shift.Code, StringComparer.Ordinal);
            var store = uow.Store<IAttendanceReliabilityStore>();
            var created = 0;
            foreach (var worker in workers.Where(worker => worker.IsActive && !string.IsNullOrWhiteSpace(worker.ShiftCode)).OrderBy(worker => worker.PersonId))
            {
                if (!shiftsByCode.TryGetValue(worker.ShiftCode!, out var shift)) continue;
                foreach (var span in WorkCalendar.Instances(shift, snapshot, c.From.AddDays(-2), c.Through.AddDays(2))
                    .Where(span => span.Start >= c.From && span.Start < c.Through))
                {
                    var start = span.Start.ToUniversalTime();
                    var sourceKey = $"schedule:{worker.PersonId:D}:{shift.Code}:{start:O}";
                    var opportunity = new ScheduledAttendanceOpportunity(ForgeIds.New(pipeline.Time), worker.PersonId, shift.Code, start, span.End.ToUniversalTime());
                    if (await store.InsertAttendanceOpportunityAsync(opportunity, "ShiftCalendar", sourceKey, now, context.Actor, ct)) created++;
                }
            }

            return new CommandOutcome(
                CommandResult.Ok("attendance-schedule", $"Created {created} dated attendance opportunity(ies) from assigned shifts; existing schedule entries were retained."),
                [pipeline.Event(new AttendanceScheduleGenerated(created, c.From, c.Through), context, "Workforce", "AttendanceSchedule", "shift-calendar")],
                new AuditChange("AttendanceSchedule", "shift-calendar", null, CanonicalJson.Serialize(new { c.From, c.Through, created })));
        }, ct);
    }
}
