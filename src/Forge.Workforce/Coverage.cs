using Forge.Competency.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Workforce;

/// <param name="ForecastExpected">The Section 21 forecast the supervisor reviewed (expected good units); stored as evidence, never as the goal.</param>
public sealed record PlanCoverageAssignment(
    Guid PersonId,
    string DestinationWorkCenter,
    string? MachineCode,
    string ActivityCode,
    DateTimeOffset PlannedStart,
    DateTimeOffset PlannedEnd,
    string Reason,
    decimal GoalGoodUnits,
    int GoalProductiveMinutes,
    string GoalRationale,
    long? OrderNumber = null,
    int? Sequence = null,
    decimal? ForecastConservative = null,
    decimal? ForecastExpected = null,
    decimal? ForecastOptimistic = null,
    string? ForecastConfidence = null,
    string? ForecastBasis = null);

public sealed record StartCoverage(Guid AssignmentId);

public sealed record CompleteCoverage(Guid AssignmentId);

public sealed record CancelCoverage(Guid AssignmentId);

/// <summary>A new goal version: a revision (before or after work began), or a reviewed time-adjusted goal after a disruption.</summary>
public sealed record ReviseCoverageGoal(Guid AssignmentId, decimal GoodUnits, int ProductiveMinutes, string Rationale, bool TimeAdjusted = false);

/// <summary>A reviewed decision that a disrupted assignment is not comparable and stays out of goal percentages (§22.3).</summary>
public sealed record ReviewCoverageComparability(Guid AssignmentId, bool Comparable, string? ExclusionReason);

public sealed record CoverageChanged(Guid AssignmentId, Guid PersonId, string State) : IDomainEvent
{
    public static string EventType => nameof(CoverageChanged);

    public static int SchemaVersion => 1;
}

public interface ICoverageStore
{
    Task InsertCoverageAsync(CoverageAssignment assignment, CancellationToken ct);

    Task<bool> UpdateCoverageAsync(CoverageAssignment assignment, CoverageState expected, CancellationToken ct);

    Task InsertCoverageGoalAsync(CoverageGoal goal, CancellationToken ct);
}

/// <summary>
/// Coverage assignments and goals (Sprint 10 §22). A supervisor approves the assignment and sets a separate goal after seeing the forecast;
/// a lapsed qualification blocks it; goals are versioned, never rewritten; nothing here decides anything about pay, promotion or discipline.
/// </summary>
public sealed class CoverageService(CommandPipeline pipeline)
{
    private const string SourceContext = "Workforce";
    private const string Entity = "CoverageAssignment";

    public Task<CommandResult> PlanAsync(PlanCoverageAssignment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var id = ForgeIds.New(pipeline.Time);
        var key = id.ToString("D");
        var spec = new CommandSpec(SourceContext, nameof(PlanCoverageAssignment), Permissions.WorkforceSupervise, ResourceRef.Of(Entity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (c.PlannedEnd <= c.PlannedStart || c.PlannedEnd - c.PlannedStart > TimeSpan.FromHours(16))
                throw new CommandRejectedException("invalid_interval", "The assignment ends after it starts, at most 16 hours later.");
            if ((c.OrderNumber is null) != (c.Sequence is null))
                throw new CommandRejectedException("invalid_operation", "Give both the order and the operation, or neither.");
            var reason = Text(c.Reason, 800, "reason");
            var rationale = Text(c.GoalRationale, 800, "goal rationale");
            if (c.GoalGoodUnits <= 0 || c.GoalProductiveMinutes <= 0)
                throw new CommandRejectedException("invalid_goal", "A coverage goal is a positive number of good units in positive productive minutes.");
            var person = await uow.People.FindAsync(new PersonId(c.PersonId), ct);
            var worker = person is { IsActive: true } ? (await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(now, ct)).FirstOrDefault(w => w.PersonId == c.PersonId && w.IsActive) : null;
            if (worker is null)
                throw new CommandRejectedException("worker_not_found", "Coverage needs an active Forge worker.");

            var destination = BusinessCode.Require(c.DestinationWorkCenter, "Work center");
            var activity = BusinessCode.Require(c.ActivityCode, "Activity");
            var machine = string.IsNullOrWhiteSpace(c.MachineCode) ? null : BusinessCode.Require(c.MachineCode, "Machine");
            if (await uow.Store<IResourcesLookup>().FindWorkCenterAsync(destination, ct) is null)
                throw new CommandRejectedException("work_center_not_found", $"There is no work center {destination}.");
            if (destination == worker.HomeWorkCenterCode)
                throw new CommandRejectedException("not_coverage", "Coverage is work outside the worker's usual work center.");

            // Safety and qualification rules are hard constraints, whatever the worker achieved before (§22.3, acceptance 11).
            if (machine is not null)
            {
                var requirement = (await uow.Store<IResourcesLookup>().ListMachineActivitiesAsync(ct)).FirstOrDefault(a => a.MachineCode == machine && a.ActivityCode == activity)
                    ?? throw new CommandRejectedException("activity_not_on_machine", $"{machine} has no activity {activity}.");
                var held = await uow.Store<ICompetencyLookup>().GetQualificationsAsync([new PersonId(c.PersonId)], ct);
                var missing = requirement.OperatorQualifications.Where(q => !held.Any(h => h.Code == q && h.IsValidAt(c.PlannedStart))).ToList();
                if (missing.Count > 0)
                    throw new CommandRejectedException("qualification_not_current", $"{person!.DisplayName} has no current {string.Join(", ", missing)} at the planned start; coverage is blocked.");
            }

            var assignment = new CoverageAssignment(id, c.PersonId, worker.HomeWorkCenterCode, destination, machine, activity, c.OrderNumber, c.Sequence,
                c.PlannedStart, c.PlannedEnd, null, null, reason, c.ForecastConservative, c.ForecastExpected, c.ForecastOptimistic,
                Optional(c.ForecastConfidence, 20), Optional(c.ForecastBasis, 800), CoverageState.Planned, true, null, null, null, context.Actor, now);
            var store = uow.Store<ICoverageStore>();
            await store.InsertCoverageAsync(assignment, ct);
            await store.InsertCoverageGoalAsync(new CoverageGoal(0, id, 1, CoverageGoalKind.Original, c.GoalGoodUnits, c.GoalProductiveMinutes, rationale, false, context.Actor, now), ct);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Coverage for {person!.DisplayName} at {destination} approved with a goal of {c.GoalGoodUnits:0.##} good units. Share the goal and safe-work instructions with them before work starts."),
                [pipeline.Event(new CoverageChanged(id, c.PersonId, nameof(CoverageState.Planned)), context, SourceContext, Entity, key)],
                new AuditChange(Entity, key, null, CanonicalJson.Serialize(assignment)));
        }, ct);
    }

    public Task<CommandResult> StartAsync(StartCoverage c, CommandContext context, CancellationToken ct) =>
        Transition(nameof(StartCoverage), c?.AssignmentId ?? throw new ArgumentNullException(nameof(c)), c, context, CoverageState.Planned,
            (a, now) => a with { State = CoverageState.Started, ActualStart = now }, "started", ct);

    public Task<CommandResult> CompleteAsync(CompleteCoverage c, CommandContext context, CancellationToken ct) =>
        Transition(nameof(CompleteCoverage), c?.AssignmentId ?? throw new ArgumentNullException(nameof(c)), c, context, CoverageState.Started,
            (a, now) => a with { State = CoverageState.Completed, ActualEnd = now }, "completed", ct);

    public Task<CommandResult> CancelAsync(CancelCoverage c, CommandContext context, CancellationToken ct) =>
        Transition(nameof(CancelCoverage), c?.AssignmentId ?? throw new ArgumentNullException(nameof(c)), c, context, CoverageState.Planned,
            (a, _) => a with { State = CoverageState.Cancelled }, "cancelled", ct);

    public Task<CommandResult> ReviseGoalAsync(ReviseCoverageGoal c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var key = c.AssignmentId.ToString("D");
        var spec = new CommandSpec(SourceContext, nameof(ReviseCoverageGoal), Permissions.WorkforceSupervise, ResourceRef.Of(Entity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var assignment = await FindAsync(uow, c.AssignmentId, ct);
            if (assignment.State is CoverageState.Cancelled)
                throw new CommandRejectedException("coverage_closed", "A cancelled assignment keeps its last goal.");
            if (c.TimeAdjusted ? assignment.State != CoverageState.Completed : assignment.State == CoverageState.Completed)
                throw new CommandRejectedException("coverage_goal_state", c.TimeAdjusted
                    ? "A time-adjusted goal is reviewed after the assignment is complete."
                    : "A completed assignment's goal is not revised; review a time-adjusted goal instead.");
            if (c.TimeAdjusted && assignment.ApprovedBy == context.Actor)
                throw new CommandRejectedException("independent_review_required", "Someone other than the supervisor who approved the assignment reviews an adjusted goal.");
            if (c.GoodUnits <= 0 || c.ProductiveMinutes <= 0)
                throw new CommandRejectedException("invalid_goal", "A coverage goal is a positive number of good units in positive productive minutes.");
            var goals = await uow.Store<ICoverageLookup>().ListCoverageGoalsAsync([c.AssignmentId], ct);
            var version = goals.Count == 0 ? 1 : goals.Max(g => g.Version) + 1;
            var kind = c.TimeAdjusted ? CoverageGoalKind.TimeAdjusted : CoverageGoalKind.Revision;
            await uow.Store<ICoverageStore>().InsertCoverageGoalAsync(new CoverageGoal(0, c.AssignmentId, version, kind, c.GoodUnits, c.ProductiveMinutes,
                Text(c.Rationale, 800, "rationale"), assignment.State != CoverageState.Planned, context.Actor, now), ct);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Goal version {version} ({kind}) set to {c.GoodUnits:0.##} good units; earlier versions stay in the history."),
                [pipeline.Event(new CoverageChanged(c.AssignmentId, assignment.PersonId, $"Goal{kind}"), context, SourceContext, Entity, key)],
                new AuditChange("CoverageGoal", $"{key}/{version}", goals.Count == 0 ? null : goals.MaxBy(g => g.Version)!.GoodUnits.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    c.GoodUnits.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }, ct);
    }

    public Task<CommandResult> ReviewComparabilityAsync(ReviewCoverageComparability c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var key = c.AssignmentId.ToString("D");
        var spec = new CommandSpec(SourceContext, nameof(ReviewCoverageComparability), Permissions.WorkforceSupervise, ResourceRef.Of(Entity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var assignment = await FindAsync(uow, c.AssignmentId, ct);
            if (assignment.State != CoverageState.Completed)
                throw new CommandRejectedException("coverage_not_complete", "Comparability is reviewed once the assignment is complete.");
            if (assignment.ApprovedBy == context.Actor)
                throw new CommandRejectedException("independent_review_required", "Someone other than the approving supervisor reviews comparability.");
            var exclusion = c.Comparable ? null : Text(c.ExclusionReason, 800, "exclusion reason");
            var reviewed = assignment with { Comparable = c.Comparable, ExclusionReason = exclusion, ReviewedBy = context.Actor, ReviewedAt = now };
            if (!await uow.Store<ICoverageStore>().UpdateCoverageAsync(reviewed, CoverageState.Completed, ct))
                throw new CommandRejectedException("coverage_changed", "The assignment changed meanwhile; reload it.");
            return new CommandOutcome(
                CommandResult.Ok(key, c.Comparable ? "Assignment is comparable and counts toward goal results." : "Assignment excluded from goal percentages; the exclusion and its reason are shown with the results."),
                [pipeline.Event(new CoverageChanged(c.AssignmentId, assignment.PersonId, c.Comparable ? "Comparable" : "Excluded"), context, SourceContext, Entity, key)],
                new AuditChange(Entity, key, assignment.Comparable.ToString(), c.Comparable.ToString()));
        }, ct);
    }

    private Task<CommandResult> Transition<T>(string commandType, Guid assignmentId, T command, CommandContext context, CoverageState from,
        Func<CoverageAssignment, DateTimeOffset, CoverageAssignment> change, string verb, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var key = assignmentId.ToString("D");
        var spec = new CommandSpec(SourceContext, commandType, Permissions.WorkforceSupervise, ResourceRef.Of(Entity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var assignment = await FindAsync(uow, assignmentId, ct);
            if (assignment.State != from)
                throw new CommandRejectedException("coverage_state", $"The assignment is {assignment.State}.");
            var changed = change(assignment, now);
            if (!await uow.Store<ICoverageStore>().UpdateCoverageAsync(changed, from, ct))
                throw new CommandRejectedException("coverage_changed", "The assignment changed meanwhile; reload it.");
            return new CommandOutcome(CommandResult.Ok(key, $"Coverage assignment {verb}."),
                [pipeline.Event(new CoverageChanged(assignmentId, assignment.PersonId, changed.State.ToString()), context, SourceContext, Entity, key)],
                new AuditChange(Entity, key, from.ToString(), changed.State.ToString()));
        }, ct);
    }

    private static async Task<CoverageAssignment> FindAsync(Forge.Kernel.Access.IKernelUnitOfWork uow, Guid id, CancellationToken ct) =>
        await uow.Store<ICoverageLookup>().FindCoverageAsync(id, ct) ?? throw new CommandRejectedException("coverage_not_found", "The coverage assignment does not exist.");

    private static string Text(string? value, int length, string what)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > length)
            throw new CommandRejectedException("text_required", $"Give the {what} in at most {length} characters.");
        return text;
    }

    private static string? Optional(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
}
