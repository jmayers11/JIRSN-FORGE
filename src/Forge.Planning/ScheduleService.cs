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
        EngineRun<ScheduleResult> run;
        IReadOnlyList<ConstraintFinding> findings;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            var snapshot = await SnapshotLoader.LoadAsync(read, time, ct);
            var lookup = read.Store<IExecutionLookup>();
            var orders = new List<OrderInfo>();
            foreach (var summary in (await lookup.ListOrdersAsync(ct)).Where(o => o.Status == OrderStatus.Released).OrderBy(o => o.OrderNumber))
            {
                orders.Add((await lookup.FindOrderAsync(summary.OrderNumber, ct))!);
            }

            var material = (await MaterialPlanningService.RunMaterialAsync(read, time, ct)).Output;
            var quality = await ScheduleQuality.LoadAsync(read, orders, ct);
            var request = new ScheduleRequest(snapshot.AsOf, snapshot.AsOf.AddDays(c.HorizonDays), orders, material, quality);
            run = EngineRunner.Run(new SchedulingEngine(), snapshot, request, snapshot.AsOf);
            findings = new ConstraintEngine().Evaluate(snapshot, new ConstraintRequest(run.Output), snapshot.AsOf).Output;
        }

        var spec = new CommandSpec(SourceContext, nameof(ProposeSchedule), Permissions.SchedulePropose, ResourceRef.Of(ScheduleEntity));
        return await pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IPlanningStore>();
            var notOnTime = run.Output.Orders.Count(o => o.Status != DeliveryStatus.OnTime);
            var summary = $"{run.Output.Orders.Count} order(s), {notOnTime} at risk, late or unschedulable, {findings.Count} constraint(s).";
            if (await store.LatestAsync(ScheduleStatus.Proposed, false, ct) is { } pending)
            {
                await store.SetStatusAsync(pending.VersionId, ScheduleStatus.Discarded, null, null, ct);
            }

            var versionId = await store.InsertProposalAsync(
                new ScheduleVersion(0, ScheduleStatus.Proposed, run.Output.From, run.Output.To, $"{run.EngineName}@{run.EngineVersion}", run.InputFingerprint, run.OutputFingerprint, summary, context.Actor, now, null, null, null),
                CanonicalJson.Serialize(run.Output),
                ct);

            foreach (var finding in findings)
            {
                await store.UpsertFindingAsync(finding, versionId, now, ct);
            }

            var closed = await store.ResolveMissingAsync(findings.Select(f => f.Key).ToList(), versionId, now, ct);
            var id = versionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Schedule version {id} proposed: {summary}" + (closed > 0 ? $" {closed} earlier constraint(s) no longer found." : string.Empty)),
                [pipeline.Event(new ScheduleProposed(versionId, run.Output.Orders.Count, notOnTime, findings.Count), context, SourceContext, ScheduleEntity, id)],
                new AuditChange(ScheduleEntity, id, null, CanonicalJson.Serialize(new { run.InputFingerprint, run.OutputFingerprint, summary })));
        }, ct);
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
