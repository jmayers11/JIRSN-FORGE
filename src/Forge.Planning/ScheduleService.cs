using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Engines;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Planning.Engines;

namespace Forge.Planning;

public sealed record ProposeSchedule(int HorizonDays);

public sealed record ApproveSchedule(long VersionId);

/// <summary>Re-plan at once after a quality event on an order (ADR 0057 §2.4); the approved schedule stays in force.</summary>
public sealed record ReplanAfterQuality(long OrderNumber, string Cause);

/// <summary>One order whose projected completion moved in a re-plan, against the approved schedule.</summary>
public sealed record ScheduleImpactLine(long OrderNumber, DateTimeOffset? Was, DateTimeOffset? Now, string WasStatus, string NowStatus, string Why);

/// <summary>
/// A re-plan found orders whose projected completion moves (ADR 0057 §2.4). The proposal waits for a planner's approval;
/// planners can be notified of this event by a notification rule.
/// </summary>
public sealed record ScheduleImpactFound(long VersionId, long ApprovedVersionId, long CauseOrderNumber, string Cause, int OrdersMoved, string Message,
    IReadOnlyList<ScheduleImpactLine> Lines) : IDomainEvent
{
    public static string EventType => nameof(ScheduleImpactFound);

    public static int SchemaVersion => 1;
}

public sealed record InvestigateConstraint(long ConstraintId);

public sealed record ResolveConstraint(long ConstraintId, string Resolution);

public sealed record VerifyConstraint(long ConstraintId);

public sealed record ScheduleProposed(long VersionId, int Orders, int NotOnTime, int Constraints) : IDomainEvent
{
    public static string EventType => nameof(ScheduleProposed);

    public static int SchemaVersion => 1;
}

public sealed record ScheduleChanged(long VersionId, long? SupersededVersionId) : IDomainEvent
{
    public static string EventType => nameof(ScheduleChanged);

    public static int SchemaVersion => 1;
}

public sealed record ConstraintStatusChanged(long ConstraintId, string Key, string From, string To) : IDomainEvent
{
    public static string EventType => nameof(ConstraintStatusChanged);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Schedule runs and the constraints they reveal. A run is deterministic and kept as a proposal;
/// it changes nothing on the floor until a different person approves it. Constraints found by a
/// run stay open until a later run no longer finds them or a person resolves them, and are closed
/// by a person verifying the resolution.
/// </summary>
public sealed class ScheduleService(CommandPipeline pipeline, IKernelUnitOfWorkFactory unitOfWork, TimeProvider time)
{
    public const string SourceContext = "Planning";
    private const string ScheduleEntity = "Schedule";
    private const string ConstraintEntity = "Constraint";

    public async Task<CommandResult> ProposeAsync(ProposeSchedule c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        if (c.HorizonDays is < 1 or > 62)
        {
            throw new CommandRejectedException("invalid_horizon", "The horizon must be between 1 and 62 days.");
        }

        // Compute from one consistent snapshot first; the command only stores the result.
        var (run, findings) = await ComputeAsync(c.HorizonDays, ct);
        var spec = new CommandSpec(SourceContext, nameof(ProposeSchedule), Permissions.SchedulePropose, ResourceRef.Of(ScheduleEntity));
        return await pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var notOnTime = run.Output.Orders.Count(o => o.Status != DeliveryStatus.OnTime);
            var summary = $"{run.Output.Orders.Count} order(s), {notOnTime} at risk, late or unschedulable, {findings.Count} constraint(s).";
            var (versionId, closed) = await StoreProposalAsync(uow, run, findings, summary, context.Actor, now, ct);
            var id = versionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Schedule version {id} proposed: {summary}" + (closed > 0 ? $" {closed} earlier constraint(s) no longer found." : string.Empty)),
                [pipeline.Event(new ScheduleProposed(versionId, run.Output.Orders.Count, notOnTime, findings.Count), context, SourceContext, ScheduleEntity, id)],
                new AuditChange(ScheduleEntity, id, null, CanonicalJson.Serialize(new { run.InputFingerprint, run.OutputFingerprint, summary })));
        }, ct);
    }

    /// <summary>
    /// Re-plans at once after an inspection failed or an NCR was raised on an order (ADR 0057 §2.4), over the approved
    /// schedule's horizon. When orders' projected completions move against the approved schedule, the re-plan is stored
    /// as the current proposal and <see cref="ScheduleImpactFound"/> names each order, by how much, and why. The approved
    /// schedule stays in force until a planner approves the proposal. With no approved schedule, or nothing moved,
    /// nothing is stored.
    /// </summary>
    public async Task<CommandResult> ReplanAfterQualityAsync(ReplanAfterQuality c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        ScheduleVersion? approved;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            approved = await read.Store<IPlanningStore>().LatestAsync(ScheduleStatus.Approved, true, ct);
        }

        var order = c.OrderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (approved?.Result is null)
        {
            return CommandResult.Ok(order, "No approved schedule to compare with; nothing re-planned.");
        }

        var approvedId = approved.VersionId;
        var approvedText = approvedId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var horizonDays = (int)Math.Clamp(Math.Ceiling((approved.To - approved.From).TotalDays), 1, 62);
        var (run, findings) = await ComputeAsync(horizonDays, ct);
        var lines = Impact(approved.Result, run.Output);
        if (lines.Count == 0)
        {
            return CommandResult.Ok(order, $"Re-planned after {c.Cause}: no order's projected completion moves.");
        }

        var message = $"Re-plan after {c.Cause}: {lines.Count} order(s) move against approved schedule {approvedText}. "
            + string.Join(" ", lines.Take(10).Select(Describe)) + (lines.Count > 10 ? $" And {lines.Count - 10} more." : string.Empty);
        var spec = new CommandSpec(SourceContext, nameof(ReplanAfterQuality), Permissions.SchedulePropose, ResourceRef.Of(ScheduleEntity));
        return await pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var notOnTime = run.Output.Orders.Count(o => o.Status != DeliveryStatus.OnTime);
            var summary = Shorten(message, 1000);
            var (versionId, _) = await StoreProposalAsync(uow, run, findings, summary, context.Actor, now, ct);
            var id = versionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Schedule version {id} proposed. {message}"),
                [
                    pipeline.Event(new ScheduleProposed(versionId, run.Output.Orders.Count, notOnTime, findings.Count), context, SourceContext, ScheduleEntity, id),
                    pipeline.Event(new ScheduleImpactFound(versionId, approvedId, c.OrderNumber, c.Cause, lines.Count, summary, lines), context, SourceContext, ScheduleEntity, id),
                ],
                new AuditChange(ScheduleEntity, id, approvedText, CanonicalJson.Serialize(new { run.InputFingerprint, run.OutputFingerprint, summary })));
        }, ct);
    }

    /// <summary>Orders whose projected completion is later than in the approved schedule, or that can no longer be scheduled, latest move first.</summary>
    public static IReadOnlyList<ScheduleImpactLine> Impact(ScheduleResult approved, ScheduleResult replanned)
    {
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(replanned);
        var before = approved.Orders.ToDictionary(o => o.OrderNumber);
        var lines = new List<ScheduleImpactLine>();
        foreach (var now in replanned.Orders)
        {
            if (!before.TryGetValue(now.OrderNumber, out var was))
            {
                continue;
            }

            var later = now.ProjectedCompletion is { } n && was.ProjectedCompletion is { } w && n - w >= TimeSpan.FromMinutes(1);
            var lost = now.Status == DeliveryStatus.Unschedulable && was.Status != DeliveryStatus.Unschedulable;
            if (later || lost)
            {
                lines.Add(new ScheduleImpactLine(now.OrderNumber, was.ProjectedCompletion, now.ProjectedCompletion, was.Status.ToString(), now.Status.ToString(), now.Explanation));
            }
        }

        return lines
            .OrderByDescending(l => l.Now is { } n && l.Was is { } w ? n - w : TimeSpan.MaxValue)
            .ThenBy(l => l.OrderNumber)
            .ToList();
    }

    private static string Describe(ScheduleImpactLine l) => l.Now is { } now
        ? $"Order {l.OrderNumber}: projected {l.Was:u}, now {now:u} ({l.NowStatus}). {l.Why}"
        : $"Order {l.OrderNumber}: projected {l.Was:u}, now cannot be scheduled. {l.Why}";

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private async Task<(EngineRun<ScheduleResult> Run, IReadOnlyList<ConstraintFinding> Findings)> ComputeAsync(int horizonDays, CancellationToken ct)
    {
        await using var read = await unitOfWork.BeginSnapshotAsync(ct);
        var snapshot = await SnapshotLoader.LoadAsync(read, time, ct);
        var lookup = read.Store<IExecutionLookup>();
        var orders = new List<OrderInfo>();
        foreach (var summary in (await lookup.ListOrdersAsync(ct)).Where(o => o.Status == OrderStatus.Released).OrderBy(o => o.OrderNumber))
        {
            orders.Add((await lookup.FindOrderAsync(summary.OrderNumber, ct))!);
        }

        var material = (await MaterialPlanningService.RunMaterialAsync(read, time, ct)).Output;
        var quality = await ScheduleQuality.LoadAsync(read, orders, ct);
        var request = new ScheduleRequest(snapshot.AsOf, snapshot.AsOf.AddDays(horizonDays), orders, material, quality);
        var run = EngineRunner.Run(new SchedulingEngine(), snapshot, request, snapshot.AsOf);
        var findings = new ConstraintEngine().Evaluate(snapshot, new ConstraintRequest(run.Output), snapshot.AsOf).Output;
        return (run, findings);
    }

    private static async Task<(long VersionId, int Closed)> StoreProposalAsync(IKernelUnitOfWork uow, EngineRun<ScheduleResult> run, IReadOnlyList<ConstraintFinding> findings,
        string summary, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var store = uow.Store<IPlanningStore>();
        if (await store.LatestAsync(ScheduleStatus.Proposed, false, ct) is { } pending)
        {
            await store.SetStatusAsync(pending.VersionId, ScheduleStatus.Discarded, null, null, ct);
        }

        var versionId = await store.InsertProposalAsync(
            new ScheduleVersion(0, ScheduleStatus.Proposed, run.Output.From, run.Output.To, $"{run.EngineName}@{run.EngineVersion}", run.InputFingerprint, run.OutputFingerprint, summary, actor, now, null, null, null),
            CanonicalJson.Serialize(run.Output),
            ct);

        foreach (var finding in findings)
        {
            await store.UpsertFindingAsync(finding, versionId, now, ct);
        }

        var closed = await store.ResolveMissingAsync(findings.Select(f => f.Key).ToList(), versionId, now, ct);
        return (versionId, closed);
    }

    public Task<CommandResult> ApproveAsync(ApproveSchedule c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = c.VersionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(ApproveSchedule), Permissions.ScheduleApprove, ResourceRef.Of(ScheduleEntity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IPlanningStore>();
            var version = await store.FindVersionAsync(c.VersionId, false, ct)
                ?? throw new CommandRejectedException("schedule_not_found", $"Schedule version {id} does not exist.");
            if (version.Status != ScheduleStatus.Proposed)
            {
                throw new CommandRejectedException("not_proposed", $"Schedule version {id} is {version.Status}; only the current proposal can be approved.");
            }

            if (version.ProposedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You proposed this schedule; a different person must approve it.");
            }

            var previous = await store.LatestAsync(ScheduleStatus.Approved, false, ct);
            if (previous is not null)
            {
                await store.SetStatusAsync(previous.VersionId, ScheduleStatus.Superseded, null, null, ct);
            }

            await store.SetStatusAsync(c.VersionId, ScheduleStatus.Approved, context.Actor, now, ct);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Schedule version {id} approved" + (previous is null ? "." : $"; replaces version {previous.VersionId}.")),
                [pipeline.Event(new ScheduleChanged(c.VersionId, previous?.VersionId), context, SourceContext, ScheduleEntity, id)],
                new AuditChange(ScheduleEntity, id, previous?.VersionId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Approved"));
        }, ct);
    }

    public Task<CommandResult> InvestigateAsync(InvestigateConstraint c, CommandContext context, CancellationToken ct) =>
        LifecycleAsync(nameof(InvestigateConstraint), c?.ConstraintId ?? 0, c!, context, false, (record, actor, _) => record.Status == ConstraintStatus.Open
            ? record with { Status = ConstraintStatus.Investigating, Owner = actor }
            : throw new CommandRejectedException("invalid_transition", $"Constraint is {record.Status}; only an open constraint can be picked up."), ct);

    public Task<CommandResult> ResolveAsync(ResolveConstraint c, CommandContext context, CancellationToken ct) =>
        LifecycleAsync(nameof(ResolveConstraint), c?.ConstraintId ?? 0, c!, context, true, (record, actor, now) => record.Status is ConstraintStatus.Open or ConstraintStatus.Investigating
            ? record with { Status = ConstraintStatus.Resolved, Resolution = string.IsNullOrWhiteSpace(c!.Resolution) ? throw new CommandRejectedException("resolution_required", "Describe what was done.") : c.Resolution.Trim(), ResolvedBy = actor }
            : throw new CommandRejectedException("invalid_transition", $"Constraint is {record.Status}."), ct);

    public Task<CommandResult> VerifyAsync(VerifyConstraint c, CommandContext context, CancellationToken ct) =>
        LifecycleAsync(nameof(VerifyConstraint), c?.ConstraintId ?? 0, c!, context, true, (record, actor, _) => record.Status != ConstraintStatus.Resolved
            ? throw new CommandRejectedException("invalid_transition", $"Constraint is {record.Status}; only a resolved constraint can be verified.")
            : record.ResolvedBy == actor
                ? throw new CommandRejectedException("separation_of_duties", "You resolved this constraint; a different person must verify it.")
                : record with { Status = ConstraintStatus.Verified, VerifiedBy = actor }, ct);

    private Task<CommandResult> LifecycleAsync<TCommand>(
        string commandType,
        long constraintId,
        TCommand command,
        CommandContext context,
        bool reasonRequired,
        Func<ConstraintRecord, string, DateTimeOffset, ConstraintRecord> transition,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var id = constraintId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, commandType, Permissions.ConstraintsManage, ResourceRef.Of(ConstraintEntity, id), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var store = uow.Store<IPlanningStore>();
            var record = await store.FindConstraintAsync(constraintId, ct)
                ?? throw new CommandRejectedException("constraint_not_found", $"Constraint {id} does not exist.");
            var next = transition(record, context.Actor, now);
            await store.UpdateLifecycleAsync(
                constraintId,
                next.Status,
                next.Owner,
                next.Resolution,
                next.ResolvedBy,
                next.Status == ConstraintStatus.Resolved && record.Status != ConstraintStatus.Resolved ? now : null,
                next.VerifiedBy,
                ct);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Constraint {id}: {record.Status} → {next.Status}."),
                [pipeline.Event(new ConstraintStatusChanged(constraintId, record.Key, record.Status.ToString(), next.Status.ToString()), context, SourceContext, ConstraintEntity, id)],
                new AuditChange(ConstraintEntity, id, record.Status.ToString(), CanonicalJson.Serialize(new { status = next.Status.ToString(), next.Owner, next.Resolution })));
        }, ct);
    }
}
