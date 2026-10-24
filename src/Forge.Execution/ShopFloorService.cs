using Forge.Competency.Contracts;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Execution.Engines;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Engines;
using Forge.Kernel.Events;
using Forge.Kernel.Holds;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Maintenance.Contracts;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Execution;

/// <summary>
/// The site's safety and machine evaluation of the person (or, for supervised training, the trainer)
/// working at the operation, made by the readiness engines before the command runs.
/// </summary>
public sealed record SafetyFinding(bool Blocked, string Detail);

/// <summary>Start (or join) an operation. A trainer badge is given when the operator works under supervision.</summary>
/// <param name="ToolCode">A tool used for the operation (V3.8): judged valid for the machine and activity at the start, and its use counted.</param>
public sealed record StartOperation(Secret Badge, long OrderNumber, int Sequence, string? MachineCode, Secret? TrainerBadge, SafetyFinding? Safety = null, string? ToolCode = null, LaborPhase Phase = LaborPhase.Run);

/// <summary>End the badged person's current direct labor.</summary>
public sealed record StopWork(Secret Badge);

/// <param name="ScrapReasonCode">A scrap reason code (V3.5); the free-text reason defaults to its description.</param>
public sealed record ReportProduction(Secret Badge, long OrderNumber, int Sequence, decimal GoodQuantity, decimal ScrapQuantity, string? ScrapReason, bool CompleteOperation, string? ScrapReasonCode = null);

/// <summary>A trainer's evaluation, signed with badge and PIN.</summary>
public sealed record EvaluateTraining(Secret TrainerBadge, Secret Pin, long SessionId, TrainingEvaluation Evaluation, string? Notes);

public sealed record OperationStarted(long OrderNumber, int Sequence, Guid PersonId, string Role, string? MachineCode, long LaborId, LaborPhase Phase = LaborPhase.Run) : IDomainEvent
{
    public static string EventType => nameof(OperationStarted);

    public static int SchemaVersion => 1;
}

public sealed record WorkStopped(long OrderNumber, int Sequence, Guid PersonId, string Role, long LaborId, decimal Hours) : IDomainEvent
{
    public static string EventType => nameof(WorkStopped);

    public static int SchemaVersion => 1;
}

public sealed record ProductionReported(long OrderNumber, int Sequence, Guid PersonId, decimal GoodQuantity, decimal ScrapQuantity, string? ScrapReason, long ReportId) : IDomainEvent
{
    public static string EventType => nameof(ProductionReported);

    public static int SchemaVersion => 1;
}

public sealed record OperationCompleted(long OrderNumber, int Sequence, decimal GoodQuantity, decimal ScrapQuantity) : IDomainEvent
{
    public static string EventType => nameof(OperationCompleted);

    public static int SchemaVersion => 1;
}

public sealed record SupervisedTrainingStarted(long SessionId, Guid TraineeId, Guid TrainerId, IReadOnlyList<string> Qualifications, long OrderNumber, int Sequence) : IDomainEvent
{
    public static string EventType => nameof(SupervisedTrainingStarted);

    public static int SchemaVersion => 1;
}

public sealed record TrainingEvaluated(long SessionId, Guid TraineeId, Guid TrainerId, string Evaluation) : IDomainEvent
{
    public static string EventType => nameof(TrainingEvaluated);

    public static int SchemaVersion => 1;
}

public interface IShopFloorStore
{
    /// <param name="qualifications">The qualifications the person held for the operation when starting it (kept with the record, V2.11).</param>
    Task<long> InsertLaborAsync(Guid person, long orderNumber, int sequence, string workCenter, string? machine, LaborRole role, LaborPhase phase, long? sessionId, IReadOnlyList<string> qualifications, DateTimeOffset at, string actor, CancellationToken ct);

    Task<LaborEntry?> FindOpenLaborAsync(Guid person, CancellationToken ct);

    /// <returns>The entries ended.</returns>
    Task<IReadOnlyList<LaborEntry>> EndLaborAsync(IReadOnlyCollection<long> laborIds, DateTimeOffset at, CancellationToken ct);

    Task MarkOperationStartedAsync(long orderNumber, int sequence, DateTimeOffset at, CancellationToken ct);

    Task<long> InsertReportAsync(long orderNumber, int sequence, string workCenter, Guid person, decimal good, decimal scrap, string? reason, string? reasonCode, bool complete, bool final, DateTimeOffset at, string actor, CancellationToken ct);

    Task AddQuantitiesAsync(long orderNumber, int sequence, decimal good, decimal scrap, CancellationToken ct);

    Task CompleteOperationAsync(long orderNumber, int sequence, DateTimeOffset at, CancellationToken ct);

    Task CompleteOrderAsync(long orderNumber, DateTimeOffset at, string actor, CancellationToken ct);

    Task<long> InsertTrainingSessionAsync(Guid trainee, Guid trainer, IReadOnlyList<string> qualifications, long orderNumber, int sequence, string? machine, DateTimeOffset at, CancellationToken ct);

    Task EndTrainingSessionAsync(long sessionId, DateTimeOffset at, CancellationToken ct);

    Task<int?> EvaluateTrainingSessionAsync(long sessionId, TrainingEvaluation evaluation, string? notes, DateTimeOffset at, CancellationToken ct);

    Task InsertSignoffAsync(Guid person, string method, string signerRole, string assurance, int recordVersion, string action, string subject, string meaning, DateTimeOffset at, string actor, CancellationToken ct);
}

/// <summary>
/// Shop-floor execution through badge terminals. The badge identifies the person; the terminal's
/// command says what they are doing. Direct labor is recorded here, separately from attendance
/// (Workforce), and electronic signoff is recorded separately again. Starting work checks
/// presence, machine status and qualifications; a person in training may start only under a
/// present, authorized trainer, and that never makes them qualified.
/// </summary>
public sealed class ShopFloorService(CommandPipeline pipeline, OperatorAuthorizationEngine engine, IBadgePinVerifier pinVerifier, IOperationStartCheck startCheck)
{
    public const string SourceContext = "Execution";
    private const string OperationEntity = "OrderOperation";
    private const string TrainingEntity = "TrainingSession";

    public Task<CommandResult> StartAsync(StartOperation c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = OperationId(c.OrderNumber, c.Sequence);
        var spec = new CommandSpec(SourceContext, nameof(StartOperation), Permissions.ShopFloorRecord, ResourceRef.Of(OperationEntity, id));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var worker = await HolderAsync(uow, c.Badge, ct);
            var (order, op, program) = await OperationAsync(uow, context, worker, c.OrderNumber, c.Sequence, ct);
            if (op.Status == OperationStatus.Complete)
            {
                throw new CommandRejectedException("operation_complete", $"Operation {id} is already complete.");
            }
            if (!Enum.IsDefined(c.Phase))
            {
                throw new CommandRejectedException("invalid_labor_phase", "Choose setup, run, inspection, or rework for this labor interval.");
            }

            await RequireNoHoldAsync(uow, order, c.Sequence, wholeOrderOnly: false, ct);

            var store = uow.Store<IShopFloorStore>();
            var current = await store.FindOpenLaborAsync(worker.PersonId, ct);
            if (current is not null)
            {
                if (current.OrderNumber == c.OrderNumber && current.Sequence == c.Sequence)
                {
                    return CommandOutcome.NoChange(CommandResult.NoChange(id, $"{worker.DisplayName} is already on operation {id}."));
                }

                throw new CommandRejectedException("already_working", $"{worker.DisplayName} is on operation {OperationId(current.OrderNumber, current.Sequence)}. Stop that first.");
            }

            var machine = await MachineAsync(uow, op, c.MachineCode, now, ct);
            await HoldGate.RequireNoneAsync(uow, HoldCheckFor(HoldActivity.StartWork, order, op, machine), ct);
            var required = op.RequiredQualifications.ToList();
            if (machine is not null && await uow.Store<IResourcesLookup>().FindMachineActivityAsync(machine, op.ActivityCode, ct) is { } activity)
            {
                required.AddRange(activity.OperatorQualifications);
            }

            BadgeHolder? trainer = c.TrainerBadge is null ? null : await HolderAsync(uow, c.TrainerBadge, ct);
            var snapshot = await SnapshotAsync(uow, now, ct);
            var check = engine.Evaluate(snapshot, new OperatorRequest(worker.PersonId, required, trainer?.PersonId), now).Output;
            switch (check.Decision)
            {
                case OperatorDecision.NotPresent:
                    throw new CommandRejectedException("clock_in_required", check.Explanation);
                case OperatorDecision.NotEligible:
                    throw new CommandRejectedException("not_qualified", check.Explanation);
                case OperatorDecision.TrainerRequired:
                    throw new CommandRejectedException("trainer_required", check.Explanation);
            }

            var readinessEvidence = await startCheck.RequireReadyAsync(uow, order, op, machine, worker.PersonId,
                check.Decision == OperatorDecision.SupervisedTraining ? trainer?.PersonId : null, context.Principal, now, ct);
            if (!string.IsNullOrWhiteSpace(c.ToolCode))
            {
                await ToolUse.RecordAsync(uow, c.ToolCode, machine, op.ActivityCode, $"order {id}", now, context.Actor, ct);
            }

            await store.MarkOperationStartedAsync(c.OrderNumber, c.Sequence, now, ct);
            var events = new List<EventEnvelope>();
            string message;
            object audit;
            if (check.Decision == OperatorDecision.Authorized)
            {
                var phase = c.Phase == LaborPhase.Training ? LaborPhase.Run : c.Phase;
                var laborId = await store.InsertLaborAsync(worker.PersonId, c.OrderNumber, c.Sequence, op.WorkCenterCode, machine, LaborRole.Operator, phase, null, required, now, context.Actor, ct);
                events.Add(pipeline.Event(new OperationStarted(c.OrderNumber, c.Sequence, worker.PersonId, nameof(LaborRole.Operator), machine, laborId, phase), context, SourceContext, OperationEntity, id, program));
                message = $"{worker.DisplayName} started operation {id}{(machine is null ? string.Empty : " on " + machine)}.";
                audit = new { person = worker.PersonId, role = nameof(LaborRole.Operator), phase, machine, laborId, check = check.Explanation, readiness = readinessEvidence.Detail, requiredDocuments = readinessEvidence.RequiredDocuments };
            }
            else
            {
                var trainerPerson = trainer!;
                var trainerLabor = await store.FindOpenLaborAsync(trainerPerson.PersonId, ct);
                if (trainerLabor is not null && (trainerLabor.OrderNumber != c.OrderNumber || trainerLabor.Sequence != c.Sequence))
                {
                    throw new CommandRejectedException("trainer_busy", $"{trainerPerson.DisplayName} is on operation {OperationId(trainerLabor.OrderNumber, trainerLabor.Sequence)}. A trainer supervises at the trainee's operation.");
                }

                var sessionId = await store.InsertTrainingSessionAsync(worker.PersonId, trainerPerson.PersonId, check.Missing, c.OrderNumber, c.Sequence, machine, now, ct);
                var traineeLabor = await store.InsertLaborAsync(worker.PersonId, c.OrderNumber, c.Sequence, op.WorkCenterCode, machine, LaborRole.Trainee, LaborPhase.Training, sessionId, required.Except(check.Missing).ToList(), now, context.Actor, ct);
                if (trainerLabor is not null)
                {
                    await store.EndLaborAsync([trainerLabor.LaborId], now, ct);
                }

                var trainerLaborId = await store.InsertLaborAsync(trainerPerson.PersonId, c.OrderNumber, c.Sequence, op.WorkCenterCode, machine, LaborRole.Trainer, LaborPhase.Training, sessionId, check.Missing, now, context.Actor, ct);
                events.Add(pipeline.Event(new SupervisedTrainingStarted(sessionId, worker.PersonId, trainerPerson.PersonId, check.Missing, c.OrderNumber, c.Sequence), context, SourceContext, TrainingEntity, sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture), program));
                events.Add(pipeline.Event(new OperationStarted(c.OrderNumber, c.Sequence, worker.PersonId, nameof(LaborRole.Trainee), machine, traineeLabor, LaborPhase.Training), context, SourceContext, OperationEntity, id, program));
                events.Add(pipeline.Event(new OperationStarted(c.OrderNumber, c.Sequence, trainerPerson.PersonId, nameof(LaborRole.Trainer), machine, trainerLaborId, LaborPhase.Training), context, SourceContext, OperationEntity, id, program));
                message = $"Supervised training started: {worker.DisplayName} on operation {id} under {trainerPerson.DisplayName}. No qualification is granted by this session.";
                audit = new { trainee = worker.PersonId, trainer = trainerPerson.PersonId, sessionId, qualifications = check.Missing, machine, check = check.Explanation, readiness = readinessEvidence.Detail, requiredDocuments = readinessEvidence.RequiredDocuments };
            }

            return new CommandOutcome(CommandResult.Ok(id, message), events, new AuditChange(OperationEntity, id, op.Status.ToString(), CanonicalJson.Serialize(audit), program, readinessEvidence.Markings));
        }, ct, authority: (uow, _) => OperationAuthorityAsync(uow, context, c.Badge, c.TrainerBadge, c.OrderNumber, c.Sequence, ct));
    }

    public Task<CommandResult> StopAsync(StopWork c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(StopWork), Permissions.ShopFloorRecord, ResourceRef.Of(OperationEntity));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var worker = await HolderAsync(uow, c.Badge, ct);
            var store = uow.Store<IShopFloorStore>();
            var labor = await store.FindOpenLaborAsync(worker.PersonId, ct);
            if (labor is null)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(worker.PersonId.ToString(), $"{worker.DisplayName} is not on an operation."));
            }

            var ended = await EndLaborAsync(uow, labor, now, ct);
            var id = OperationId(labor.OrderNumber, labor.Sequence);
            var program = (await uow.Store<IExecutionLookup>().FindOrderAsync(labor.OrderNumber, ct))?.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
            var events = ended.Select(e => pipeline.Event(new WorkStopped(e.OrderNumber, e.Sequence, e.PersonId, e.Role.ToString(), e.LaborId, Hours(e, now)), context, SourceContext, OperationEntity, id, program)).ToList();
            return new CommandOutcome(
                CommandResult.Ok(id, labor.TrainingSessionId is null
                    ? $"{worker.DisplayName} stopped work on operation {id} after {Hours(labor, now)} h."
                    : $"Supervised session on {id} ended; trainee and trainer labor both stopped."),
                events,
                new AuditChange(OperationEntity, id, CanonicalJson.Serialize(new { open = ended.Select(e => e.LaborId) }), CanonicalJson.Serialize(new { ended = ended.Select(e => e.LaborId) }), program));
        }, ct, authority: async (uow, _) =>
        {
            var worker = await HolderAsync(uow, c.Badge, ct);
            var person = await uow.People.LoadPrincipalAsync(new PersonId(worker.PersonId), ct);
            if (person is not { IsActive: true }) throw new CommandRejectedException("unknown_badge", "Badge not recognized. See your supervisor.");
            // Stopping remains available for safe shutdown; the retry is nevertheless tied to
            // the currently valid badged person, rather than the terminal alone.
            return new CommandAuthority(spec.Resource, $"operator:{worker.PersonId:D}");
        });
    }

    public Task<CommandResult> ReportAsync(ReportProduction c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = OperationId(c.OrderNumber, c.Sequence);
        var spec = new CommandSpec(SourceContext, nameof(ReportProduction), Permissions.ShopFloorRecord, ResourceRef.Of(OperationEntity, id));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (c.GoodQuantity < 0 || c.ScrapQuantity < 0 || c.GoodQuantity > 1_000_000 || c.ScrapQuantity > 1_000_000)
            {
                throw new CommandRejectedException("invalid_quantity", "Quantities cannot be negative.");
            }

            if (c.GoodQuantity == 0 && c.ScrapQuantity == 0 && !c.CompleteOperation)
            {
                throw new CommandRejectedException("nothing_reported", "Report a good or scrap quantity, or complete the operation.");
            }

            var reason = string.IsNullOrWhiteSpace(c.ScrapReason) ? null : c.ScrapReason.Trim();
            string? reasonCode = null;
            if (!string.IsNullOrWhiteSpace(c.ScrapReasonCode))
            {
                if (c.ScrapQuantity == 0)
                {
                    throw new CommandRejectedException("reason_code_without_scrap", "A scrap reason code goes with a scrap quantity.");
                }

                var code = await ProductionActualsService.ActiveReasonAsync(uow, c.ScrapReasonCode, ReasonKind.Scrap, ct);
                reasonCode = code.Code;
                reason ??= code.Description;
            }

            if (c.ScrapQuantity > 0 && reason is null)
            {
                throw new CommandRejectedException("scrap_reason_required", "Scrap needs a reason.");
            }

            var worker = await HolderAsync(uow, c.Badge, ct);
            var (order, op, program) = await OperationAsync(uow, context, worker, c.OrderNumber, c.Sequence, ct);
            if (op.Status == OperationStatus.Complete)
            {
                throw new CommandRejectedException("operation_complete", $"Operation {id} is already complete.");
            }

            var store = uow.Store<IShopFloorStore>();
            var labor = await store.FindOpenLaborAsync(worker.PersonId, ct);
            if (labor is null || labor.OrderNumber != c.OrderNumber || labor.Sequence != c.Sequence || labor.Role == LaborRole.Trainer)
            {
                throw new CommandRejectedException("not_on_operation", $"{worker.DisplayName} is not working on operation {id}. Start it first; trainers report through the trainee.");
            }

            // Scrap may always be reported; good output and completion wait while the order is on hold.
            if (c.GoodQuantity > 0 || c.CompleteOperation)
            {
                await RequireNoHoldAsync(uow, order, c.Sequence, wholeOrderOnly: true, ct);
                await HoldGate.RequireNoneAsync(uow, HoldCheckFor(HoldActivity.ReportOutput, order, op, labor.MachineCode), ct);
            }

            var good = op.GoodQuantity + c.GoodQuantity;
            if (good > order.Quantity)
            {
                throw new CommandRejectedException("over_reported", $"That would make {good:0.###} good against an order quantity of {order.Quantity:0.###}. Check the count.");
            }

            var final = order.Operations.Max(o => o.Sequence) == c.Sequence;
            var reportId = await store.InsertReportAsync(c.OrderNumber, c.Sequence, op.WorkCenterCode, worker.PersonId, c.GoodQuantity, c.ScrapQuantity, reason, reasonCode, c.CompleteOperation, final, now, context.Actor, ct);
            await store.AddQuantitiesAsync(c.OrderNumber, c.Sequence, c.GoodQuantity, c.ScrapQuantity, ct);
            var events = new List<EventEnvelope>
            {
                pipeline.Event(new ProductionReported(c.OrderNumber, c.Sequence, worker.PersonId, c.GoodQuantity, c.ScrapQuantity, reason, reportId), context, SourceContext, OperationEntity, id, program),
            };
            var message = $"Reported {c.GoodQuantity:0.###} good{(c.ScrapQuantity > 0 ? $", {c.ScrapQuantity:0.###} scrap ({reason})" : string.Empty)} on {id}; {good:0.###} of {order.Quantity:0.###} good so far.";

            if (c.CompleteOperation)
            {
                await store.CompleteOperationAsync(c.OrderNumber, c.Sequence, now, ct);
                foreach (var open in (await uow.Store<IExecutionLookup>().ListOpenLaborAsync(ct)).Where(l => l.OrderNumber == c.OrderNumber && l.Sequence == c.Sequence))
                {
                    foreach (var e in await EndLaborAsync(uow, open, now, ct))
                    {
                        events.Add(pipeline.Event(new WorkStopped(e.OrderNumber, e.Sequence, e.PersonId, e.Role.ToString(), e.LaborId, Hours(e, now)), context, SourceContext, OperationEntity, id, program));
                    }
                }

                events.Add(pipeline.Event(new OperationCompleted(c.OrderNumber, c.Sequence, good, op.ScrapQuantity + c.ScrapQuantity), context, SourceContext, OperationEntity, id, program));
                message += $" Operation {id} complete.";
                if (order.Operations.All(o => o.Sequence == c.Sequence || o.Status == OperationStatus.Complete))
                {
                    await store.CompleteOrderAsync(c.OrderNumber, now, context.Actor, ct);
                    var orderId = c.OrderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    events.Add(pipeline.Event(new ProductionOrderStatusChanged(c.OrderNumber, order.Status.ToString(), nameof(OrderStatus.Completed)), context, SourceContext, "ProductionOrder", orderId, program));
                    message += $" Order {orderId} completed.";
                }
            }

            return new CommandOutcome(
                CommandResult.Ok(id, message),
                events,
                new AuditChange(OperationEntity, id, CanonicalJson.Serialize(new { good = op.GoodQuantity, scrap = op.ScrapQuantity, status = op.Status.ToString() }),
                    CanonicalJson.Serialize(new { good, scrap = op.ScrapQuantity + c.ScrapQuantity, status = c.CompleteOperation ? nameof(OperationStatus.Complete) : nameof(OperationStatus.InProgress), reportId, person = worker.PersonId }), program));
        }, ct, authority: (uow, _) => OperationAuthorityAsync(uow, context, c.Badge, null, c.OrderNumber, c.Sequence, ct));
    }

    public Task<CommandResult> EvaluateTrainingAsync(EvaluateTraining c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = c.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(EvaluateTraining), Permissions.ShopFloorRecord, ResourceRef.Of(TrainingEntity, id));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var session = await uow.Store<IExecutionLookup>().FindTrainingSessionAsync(c.SessionId, ct)
                ?? throw new CommandRejectedException("training_session_not_found", $"Training session {id} does not exist.");
            var trainer = await HolderAsync(uow, c.TrainerBadge, ct);
            if (trainer.PersonId != session.TrainerId)
            {
                throw new CommandRejectedException("not_the_trainer", "Only the trainer who supervised the session can evaluate it.");
            }

            // Consequential signoff: badge and PIN. A wrong PIN is rejected (and the attempt audited).
            if (!await pinVerifier.VerifyAsync(c.TrainerBadge.Reveal(), c.Pin.Reveal(), ct))
            {
                throw new CommandRejectedException("signoff_failed", "Badge and PIN did not match. Set a PIN at the terminal first if you have none.");
            }

            if (session.Evaluation is not null)
            {
                throw new CommandRejectedException("already_evaluated", $"Session {id} was already evaluated ({session.Evaluation}).");
            }

            var notes = string.IsNullOrWhiteSpace(c.Notes) ? null : c.Notes.Trim();
            var store = uow.Store<IShopFloorStore>();
            if (session.EndedAt is null)
            {
                await store.EndTrainingSessionAsync(c.SessionId, now, ct);
                foreach (var open in (await uow.Store<IExecutionLookup>().ListOpenLaborAsync(ct)).Where(l => l.TrainingSessionId == c.SessionId))
                {
                    await store.EndLaborAsync([open.LaborId], now, ct);
                }
            }

            var signedVersion = await store.EvaluateTrainingSessionAsync(c.SessionId, c.Evaluation, notes, now, ct)
                ?? throw new CommandRejectedException("already_evaluated", $"Session {id} was evaluated by another request.");
            await store.InsertSignoffAsync(trainer.PersonId, "BadgeAndPin", "Trainer", "Badge+PIN", signedVersion, nameof(EvaluateTraining), $"{TrainingEntity}:{id}", $"Trainer evaluation: {c.Evaluation}", now, context.Actor, ct);
            var program = (await uow.Store<IExecutionLookup>().FindOrderAsync(session.OrderNumber, ct))?.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
            return new CommandOutcome(
                CommandResult.Ok(id, $"Session {id} evaluated: {c.Evaluation}, signed by {trainer.DisplayName}. This is not certification; a certifier records the qualification."),
                [pipeline.Event(new TrainingEvaluated(c.SessionId, session.TraineeId, session.TrainerId, c.Evaluation.ToString()), context, SourceContext, TrainingEntity, id, program)],
                new AuditChange(TrainingEntity, id, null, CanonicalJson.Serialize(new { evaluation = c.Evaluation.ToString(), notes, signedBy = trainer.PersonId, method = "BadgeAndPin" }), program));
        }, ct, authority: async (uow, _) =>
        {
            var session = await uow.Store<IExecutionLookup>().FindTrainingSessionAsync(c.SessionId, ct)
                ?? throw new CommandRejectedException("training_session_not_found", "The training session does not exist.");
            var trainer = await HolderAsync(uow, c.TrainerBadge, ct);
            if (session.TrainerId != trainer.PersonId)
                throw new CommandRejectedException("not_the_trainer", "Only the recorded trainer evaluates this session.");
            var current = await OperationAuthorityAsync(uow, context, c.TrainerBadge, null, session.OrderNumber, session.Sequence, ct);
            return current with { Resource = current.Resource with { Type = TrainingEntity, Id = c.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
        });
    }

    /// <summary>Builds the engine's snapshot: qualification types and every worker with presence and records.</summary>
    public static async Task<OperatorSnapshot> SnapshotAsync(IKernelUnitOfWork uow, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var competency = uow.Store<ICompetencyLookup>();
        var states = await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(now, ct);
        var quals = await competency.GetQualificationsAsync(states.Select(s => new PersonId(s.PersonId)).ToList(), ct);
        var people = states
            .Select(s => new FloorPerson(s.PersonId, s.DisplayName, s.IsActive && s.PresenceKind is not null && s.Unavailability is null, quals.Where(q => q.PersonId == s.PersonId).ToList()))
            .ToList();
        return new OperatorSnapshot(await competency.ListTypesAsync(ct), people);
    }

    /// <summary>
    /// Holds from other contexts (an undecided NCR, an inspection not yet passed). Starting checks every
    /// hold that reaches the operation; reporting good output checks only whole-order holds, so work
    /// already under way after a passed inspection is not stopped by a later inspection point.
    /// </summary>
    private static async Task RequireNoHoldAsync(IKernelUnitOfWork uow, OrderInfo order, int sequence, bool wholeOrderOnly, CancellationToken ct)
    {
        var holds = (await uow.Store<IWorkHoldLookup>().ListHoldsAsync(order.OrderNumber, order.PartNumber, ct))
            .Where(h => wholeOrderOnly ? h.AfterSequence is null : h.Blocks(sequence))
            .ToList();
        if (holds.Count > 0)
        {
            throw new CommandRejectedException("order_on_hold", $"Order {order.OrderNumber} is on hold: {string.Join(" ", holds.Select(h => h.Detail))} See Quality.");
        }
    }

    /// <summary>The records an activity on an operation touches, for the hold check (V3.10).</summary>
    internal static HoldCheck HoldCheckFor(HoldActivity activity, OrderInfo order, OrderOperationInfo op, string? machine) =>
        new(activity, order.OrderNumber, op.Sequence, [order.PartNumber], null, op.WorkCenterCode, machine, ProgramId: order.ProgramId);

    private static async Task<BadgeHolder> HolderAsync(IKernelUnitOfWork uow, Secret badge, CancellationToken ct)
    {
        var holder = await uow.Store<IWorkforceLookup>().FindBadgeHolderAsync(badge.Reveal(), ct)
            ?? throw new CommandRejectedException("unknown_badge", "Badge not recognized. See your supervisor.");
        return holder.IsWorker ? holder : throw new CommandRejectedException("worker_not_found", $"{holder.DisplayName} is not a registered worker.");
    }

    private static async Task<(OrderInfo Order, OrderOperationInfo Operation, ProgramId? Program)> OperationAsync(
        IKernelUnitOfWork uow, CommandContext context, BadgeHolder worker, long orderNumber, int sequence, CancellationToken ct)
    {
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct)
            ?? throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
        var program = order.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
        if (program is { } required)
        {
            // Both the terminal and the badged person need the program; otherwise the order does not exist for them.
            var person = await uow.People.LoadPrincipalAsync(new PersonId(worker.PersonId), ct);
            if (!context.Principal.Programs.Contains(required) || person is null || !person.Programs.Contains(required))
            {
                throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
            }
        }

        if (order.Status != OrderStatus.Released)
        {
            throw new CommandRejectedException("order_not_released", $"Order {orderNumber} is {order.Status}; only released orders are worked.");
        }

        var op = order.Operations.FirstOrDefault(o => o.Sequence == sequence)
            ?? throw new CommandRejectedException("operation_not_found", $"Order {orderNumber} has no operation {sequence}.");
        return (order, op, program);
    }

    private static async Task<CommandAuthority> OperationAuthorityAsync(IKernelUnitOfWork uow, CommandContext context,
        Secret badge, Secret? trainerBadge, long orderNumber, int sequence, CancellationToken ct)
    {
        var worker = await HolderAsync(uow, badge, ct);
        var trainer = trainerBadge is null ? null : await HolderAsync(uow, trainerBadge, ct);
        await uow.Store<IOrderCommitmentStore>().LockAsync(orderNumber, ct);
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct)
            ?? throw new CommandRejectedException("order_not_found", "The order does not exist.");
        foreach (var personId in new[] { (Guid?)worker.PersonId, trainer?.PersonId }.OfType<Guid>().Distinct())
        {
            var person = await uow.People.LoadPrincipalAsync(new PersonId(personId), ct);
            if (person is not { IsActive: true } || order.ProgramId is { } program &&
                (!context.Principal.Programs.Contains(new ProgramId(program)) || !person.Programs.Contains(new ProgramId(program))))
            {
                throw new CommandRejectedException("order_not_found", "The order is not available to the selected worker.");
            }
        }
        var op = order.Operations.FirstOrDefault(o => o.Sequence == sequence)
            ?? throw new CommandRejectedException("operation_not_found", "The order operation does not exist.");
        var center = await uow.Store<IResourcesLookup>().FindWorkCenterAsync(op.WorkCenterCode, ct);
        var area = center is null ? null : await uow.Store<IResourcesLookup>().FindSafetyAreaAsync(center.SafetyAreaCode, ct);
        var documents = uow.Store<IDocumentLookup>();
        var documentNumbers = (await documents.ListRequirementsAsync(order.PartNumber, order.Revision, false, ct))
            .Where(d => d.OperationSequence is null || d.OperationSequence == sequence).Select(d => d.DocumentNumber)
            .Concat((await documents.ListLinksAsync(DocumentTargetType.ProductionOrder, orderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), null, false, ct))
                .Where(d => d.Purpose == DocumentPurpose.Follow).Select(d => d.DocumentNumber)).Distinct(StringComparer.Ordinal);
        var markings = DataMarking.None;
        foreach (var number in documentNumbers)
        {
            var document = await documents.FindDocumentAsync(number, ct);
            if (document is null) continue;
            markings |= document.Markings;
            if (!DocumentAccess.CanSee(context.Principal, document))
                throw new ForgeAuthorizationException(new AuthorizationDecision(false, ["The terminal cannot access a required controlled document."]));
            foreach (var personId in new[] { (Guid?)worker.PersonId, trainer?.PersonId }.OfType<Guid>().Distinct())
            {
                var person = await uow.People.LoadPrincipalAsync(new PersonId(personId), ct);
                if (person is null || !DocumentAccess.CanSee(person, document))
                    throw new CommandRejectedException("operation_not_ready", "A selected worker cannot access a required controlled document.");
            }
        }
        return new CommandAuthority(new ResourceRef(OperationEntity, OperationId(orderNumber, sequence),
            order.ProgramId is { } p ? new ProgramId(p) : null, markings, FacilityId: area?.SiteCode,
            WorkCenterId: op.WorkCenterCode, ContractId: order.ContractId?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            $"operator:{worker.PersonId:D};trainer:{trainer?.PersonId.ToString("D")}");
    }

    private static async Task<string?> MachineAsync(IKernelUnitOfWork uow, OrderOperationInfo op, string? requested, DateTimeOffset now, CancellationToken ct)
    {
        string? machine;
        if (op.EligibleMachines.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(requested))
            {
                throw new CommandRejectedException("machine_not_used", $"Operation {op.Sequence} is work-center work; it does not run on a machine.");
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(requested))
        {
            machine = op.EligibleMachines.Count == 1 ? op.EligibleMachines[0]
                : throw new CommandRejectedException("machine_required", $"Choose a machine: {string.Join(", ", op.EligibleMachines)}.");
        }
        else
        {
            machine = BusinessCode.Require(requested, "Machine");
            if (!op.EligibleMachines.Contains(machine, StringComparer.Ordinal))
            {
                throw new CommandRejectedException("machine_not_eligible", $"{machine} is not eligible for this operation. Eligible: {string.Join(", ", op.EligibleMachines)}.");
            }
        }

        var down = (await uow.Store<IMaintenanceLookup>().ListDowntimeAsync(now, now.AddTicks(1), ct)).FirstOrDefault(w => w.MachineCode == machine && w.Covers(now));
        if (await uow.Store<IResourcesLookup>().FindMachineAsync(machine, ct) is not { Active: true })
            throw new CommandRejectedException("machine_unavailable", "The selected machine is inactive or no longer exists.");
        if (down is not null)
        {
            throw new CommandRejectedException("machine_down", $"{machine} is down ({down.Kind}: {down.Reason}). Choose another machine or wait for maintenance.");
        }

        return machine;
    }

    /// <summary>Ends a labor entry; ending any part of a supervised session ends the whole session.</summary>
    private static async Task<IReadOnlyList<LaborEntry>> EndLaborAsync(IKernelUnitOfWork uow, LaborEntry labor, DateTimeOffset now, CancellationToken ct)
    {
        var store = uow.Store<IShopFloorStore>();
        if (labor.TrainingSessionId is not { } session)
        {
            return await store.EndLaborAsync([labor.LaborId], now, ct);
        }

        var related = (await uow.Store<IExecutionLookup>().ListOpenLaborAsync(ct)).Where(l => l.TrainingSessionId == session).Select(l => l.LaborId).ToList();
        await store.EndTrainingSessionAsync(session, now, ct);
        return await store.EndLaborAsync(related, now, ct);
    }

    private static decimal Hours(LaborEntry entry, DateTimeOffset now) => decimal.Round((decimal)((entry.EndedAt ?? now) - entry.StartedAt).TotalHours, 2);

    public static string OperationId(long orderNumber, int sequence) => $"{orderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
