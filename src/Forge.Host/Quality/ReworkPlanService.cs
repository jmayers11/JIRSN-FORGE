using System.Globalization;
using Forge.Competency.Contracts;
using Forge.Engineering.Contracts;
using Forge.Execution;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;
using Forge.Workforce;
using Forge.Workforce.Contracts;

namespace Forge.Host.Quality;

public sealed record ProposeReworkPlan(long ReworkId, IReadOnlyList<ReworkPlanOperation> Operations, IReadOnlyList<ReworkPlanMaterial>? Materials,
    IReadOnlyList<Guid>? Trainees, string Rationale);

public sealed record DecideReworkPlan(Guid PlanId, bool Approve, string Note);

/// <summary>
/// Rework routing expansion (Sprint 10 WW-S10-14/29). Carrying out a rework record can need work its order never had: a solder touch-up,
/// a retest, a new connector. A manufacturing engineer proposes that work as a plan; someone else with Quality approval authority decides
/// it. Approval adds the operations (each with its own labor standard and required qualifications, inspection steps included) and the
/// material lines (released demand, so MRP and material requests see it) to the order, reopens a completed order, and asks technical
/// trainers to train the people named for qualifications they do not hold, all in one transaction. The NCR's disposition, the rework
/// record and every existing order line stay exactly as they were. Nothing is certified and nobody is assigned by a plan.
/// </summary>
/// <remarks>This orchestration spans Quality, Execution and Workforce, so it lives in the host, which may use each context's services.</remarks>
public sealed class ReworkPlanService(CommandPipeline pipeline)
{
    private const string SourceContext = "Quality";
    private const string Entity = "ReworkPlan";

    public Task<CommandResult> ProposeAsync(ProposeReworkPlan c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.ReworkId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(ProposeReworkPlan), Permissions.OrdersManage, ResourceRef.Of("Rework", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var (rework, order, program) = await ReworkAsync(uow, context, c.ReworkId, ct);
            var operations = await ValidateOperationsAsync(uow, order, c.Operations, ct);
            var materials = await ValidateMaterialsAsync(uow, operations, c.Materials ?? [], now, ct);
            var trainees = (c.Trainees ?? []).Distinct().ToList();
            if (trainees.Count > 20) throw new CommandRejectedException("too_many_trainees", "Name at most 20 people to train.");
            var workers = (await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(now, ct)).ToDictionary(w => w.PersonId);
            foreach (var person in trainees)
                if (!workers.TryGetValue(person, out var worker) || !worker.IsActive)
                    throw new CommandRejectedException("trainee_not_found", "Every person to train must be an active Forge worker.");

            var rationale = BusinessCode.RequireName(c.Rationale, "Rationale");
            if (rationale.Length > 800) throw new CommandRejectedException("rationale_too_long", "Keep the rationale to 800 characters.");
            var plan = new ReworkPlan(ForgeIds.New(pipeline.Time), rework.ReworkId, order.OrderNumber, ReworkPlanStatus.Proposed, operations, materials, trainees,
                rationale, context.Actor, PersonOf(context), now);
            if (!await uow.Store<IReworkPlanStore>().InsertPlanAsync(plan, ct))
                throw new CommandRejectedException("plan_already_proposed", $"Rework {key} already has a plan waiting for a decision.");

            var id = plan.PlanId.ToString("D");
            var minutes = operations.Sum(o => o.SetupMinutes + (o.RunMinutesPerUnit * rework.Quantity));
            var message = $"Rework plan for rework {key} on order {order.OrderNumber} proposed: {operations.Count} operation(s)"
                + $"{(operations.Any(o => o.Inspection) ? " including inspection" : string.Empty)}, {Qty(minutes)} planned minutes"
                + $"{(materials.Count > 0 ? $", {materials.Count} material line(s)" : string.Empty)}. Someone with Quality approval authority decides it; the order is unchanged until then.";
            return new CommandOutcome(CommandResult.Ok(id, message),
                [pipeline.Event(new ReworkPlanChanged(plan.PlanId, rework.ReworkId, order.OrderNumber, nameof(ReworkPlanStatus.Proposed), message), context, SourceContext, Entity, id, program)],
                new AuditChange(Entity, id, null, CanonicalJson.Serialize(plan), program));
        }, ct);
    }

    public Task<CommandResult> DecideAsync(DecideReworkPlan c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = c.PlanId.ToString("D");
        var spec = new CommandSpec(SourceContext, nameof(DecideReworkPlan), Permissions.QualityApprove, ResourceRef.Of(Entity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var plans = uow.Store<IReworkPlanStore>();
            var prior = await plans.FindPlanAsync(c.PlanId, ct) ?? throw new CommandRejectedException("plan_not_found", "The rework plan does not exist.");
            if (prior.Status != ReworkPlanStatus.Proposed)
                throw new CommandRejectedException("plan_decided", $"The rework plan was already {prior.Status.ToString().ToLowerInvariant()}.");
            if (string.Equals(prior.ProposedBy, context.Actor, StringComparison.Ordinal)
                || (prior.ProposedByPersonId is { } proposer && PersonOf(context) == proposer))
                throw new CommandRejectedException("independent_review_required", "The person who proposed a rework plan cannot decide it.");
            var note = BusinessCode.RequireName(c.Note, "Decision note");
            if (note.Length > 800) throw new CommandRejectedException("note_too_long", "Keep the decision note to 800 characters.");

            var (rework, order, program) = await ReworkAsync(uow, context, prior.ReworkId, ct);
            var decided = prior with
            {
                Status = c.Approve ? ReworkPlanStatus.Approved : ReworkPlanStatus.Rejected, DecidedBy = context.Actor, DecidedByPersonId = PersonOf(context),
                DecidedAt = now, DecisionNote = note, Version = prior.Version + 1,
            };
            var events = new List<EventEnvelope>();
            string message;
            if (c.Approve)
            {
                // The order may have moved on since the proposal: the added steps must still come after everything on it.
                await ValidateOperationsAsync(uow, order, prior.Operations, ct);
                var added = prior.Operations.Select(o => new OrderOperationInfo(o.Sequence, o.Description, o.WorkCenterCode, o.ActivityCode, o.EligibleMachines,
                    o.RequiredQualifications, o.SetupMinutes + (o.RunMinutesPerUnit * rework.Quantity), o.CrewSize, OperationStatus.Pending,
                    SetupMinutes: o.SetupMinutes, ReworkId: rework.ReworkId, PlannedQuantity: rework.Quantity)).ToList();
                var engineering = uow.Store<IEngineeringLookup>();
                var nextLine = order.Materials.Count == 0 ? 1 : order.Materials.Max(m => m.LineNumber) + 1;
                var lines = new List<OrderMaterialInfo>();
                foreach (var m in prior.Materials)
                {
                    var item = await engineering.FindItemAsync(m.PartNumber, ct) ?? throw new CommandRejectedException("unknown_part", $"Part {m.PartNumber} does not exist.");
                    var revision = await engineering.GetEffectiveRevisionAsync(m.PartNumber, now, ct)
                        ?? throw new CommandRejectedException("no_released_revision", $"Part {m.PartNumber} has no released revision in force.");
                    lines.Add(new OrderMaterialInfo(nextLine++, m.PartNumber, revision.Revision, m.Quantity, item.UnitOfMeasure, m.ConsumedAtSequence, rework.ReworkId));
                }

                var store = uow.Store<IExecutionStore>();
                await store.AddReworkWorkAsync(order.OrderNumber, added, lines, ct);
                var orderId = order.OrderNumber.ToString(CultureInfo.InvariantCulture);
                if (order.Status == OrderStatus.Completed)
                {
                    await store.SetStatusAsync(order.OrderNumber, OrderStatus.Released, null, now, context.Actor, ct);
                    events.Add(pipeline.Event(new ProductionOrderStatusChanged(order.OrderNumber, nameof(OrderStatus.Completed), nameof(OrderStatus.Released)),
                        context, "Execution", "ProductionOrder", orderId, program));
                }

                var training = await OpenTrainingAsync(uow, decided, order, now, context, ct);
                events.AddRange(training);
                message = $"Rework plan approved: order {orderId} gained operation(s) {string.Join(", ", added.Select(o => o.Sequence))}"
                    + $"{(lines.Count > 0 ? $" and {string.Join(", ", lines.Select(l => $"{Qty(l.RequiredQuantity)} {l.ComponentPartNumber}"))}" : string.Empty)}"
                    + $"{(order.Status == OrderStatus.Completed ? "; the completed order is open again until that work is done" : string.Empty)}"
                    + $"{(training.Count > 0 ? $"; {training.Count} training request(s) sent to the technical trainers" : string.Empty)}. The NCR disposition and the rework record are unchanged.";
            }
            else
            {
                message = $"Rework plan for rework {rework.ReworkId} rejected; order {order.OrderNumber} is unchanged.";
            }

            if (!await plans.DecidePlanAsync(decided, prior.Version, ct))
                throw new CommandRejectedException("plan_changed", "Someone else decided this plan first. Refresh and review it.");
            events.Insert(0, pipeline.Event(new ReworkPlanChanged(decided.PlanId, decided.ReworkId, decided.OrderNumber, decided.Status.ToString(), message),
                context, SourceContext, Entity, id, program));
            return new CommandOutcome(CommandResult.Ok(id, message), events, new AuditChange(Entity, id, CanonicalJson.Serialize(prior), CanonicalJson.Serialize(decided), program));
        }, ct);
    }

    private async Task<List<EventEnvelope>> OpenTrainingAsync(IKernelUnitOfWork uow, ReworkPlan plan, OrderInfo order, DateTimeOffset now, CommandContext context, CancellationToken ct)
    {
        var events = new List<EventEnvelope>();
        if (plan.Trainees.Count == 0) return events;
        var required = plan.Operations.SelectMany(o => o.RequiredQualifications).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var held = (await uow.Store<ICompetencyLookup>().GetQualificationsAsync(plan.Trainees.Select(p => new PersonId(p)).ToList(), ct))
            .Where(q => q.IsValidAt(now)).Select(q => (q.PersonId, q.Code)).ToHashSet();
        var today = await uow.SiteDateAsync(now, ct);
        DateOnly? due = order.DueDate > today ? order.DueDate : null;
        foreach (var person in plan.Trainees)
            foreach (var code in required.Where(code => !held.Contains((person, code))))
            {
                var reason = $"Approved rework plan for order {order.OrderNumber} (rework {plan.ReworkId}) adds work that needs {code}. {plan.Rationale}";
                if (await TrainingRequestService.OpenReworkAsync(pipeline, uow, person, code, $"{plan.PlanId:N}:{code}", reason, due, now, context, ct) is { } opened)
                    events.Add(opened);
            }

        return events;
    }

    private static async Task<(ReworkInfo Rework, OrderInfo Order, ProgramId? Program)> ReworkAsync(IKernelUnitOfWork uow, CommandContext context, long reworkId, CancellationToken ct)
    {
        var rework = (await uow.Store<IReworkLookup>().ListReworkAsync(null, false, ct)).FirstOrDefault(r => r.ReworkId == reworkId)
            ?? throw new CommandRejectedException("rework_not_found", $"Rework {reworkId} does not exist.");
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(rework.OrderNumber, ct);
        var program = order?.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
        if (order is null || (program is { } needed && !context.Principal.Programs.Contains(needed)))
            throw new CommandRejectedException("rework_not_found", $"Rework {reworkId} does not exist.");
        if (!rework.Open)
            throw new CommandRejectedException("rework_closed", $"Rework {reworkId} is closed; a plan adds work to open rework only.");
        if (order.Status is not (OrderStatus.Released or OrderStatus.Held or OrderStatus.Completed))
            throw new CommandRejectedException("order_not_released", $"Order {order.OrderNumber} is {order.Status}; added work goes onto a released order.");
        return (rework, order, program);
    }

    private static async Task<List<ReworkPlanOperation>> ValidateOperationsAsync(IKernelUnitOfWork uow, OrderInfo order, IReadOnlyList<ReworkPlanOperation>? requested, CancellationToken ct)
    {
        if (requested is null || requested.Count is 0 or > 12)
            throw new CommandRejectedException("operations_required", "A rework plan adds 1 to 12 operations.");
        var after = order.Operations.Count == 0 ? 0 : order.Operations.Max(o => o.Sequence);
        var resources = uow.Store<IResourcesLookup>();
        var competency = uow.Store<ICompetencyLookup>();
        var activities = await resources.ListWorkCenterActivitiesAsync(ct);
        var result = new List<ReworkPlanOperation>();
        var previous = after;
        foreach (var o in requested)
        {
            ArgumentNullException.ThrowIfNull(o);
            if (o.Sequence <= previous || o.Sequence > 9999)
                throw new CommandRejectedException("invalid_sequence", $"Added operations follow everything on order {order.OrderNumber} (after {after}) in increasing order, up to 9999.");
            previous = o.Sequence;
            if (o.SetupMinutes is < 0 or > 20160 || o.RunMinutesPerUnit is < 0 or > 20160 || o.CrewSize is < 1 or > 20)
                throw new CommandRejectedException("invalid_operation", "Non-negative times up to 14 days and a crew of 1-20.");
            var description = BusinessCode.RequireName(o.Description, "Operation");
            var workCenter = BusinessCode.Require(o.WorkCenterCode, "Work center");
            var activity = BusinessCode.Require(o.ActivityCode, "Activity");
            var machines = (o.EligibleMachines ?? []).Select(m => BusinessCode.Require(m, "Machine")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var quals = (o.RequiredQualifications ?? []).Select(q => BusinessCode.Require(q, "Qualification")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (await resources.FindWorkCenterAsync(workCenter, ct) is null)
                throw new CommandRejectedException("unknown_work_center", $"Work center {workCenter} does not exist.");
            foreach (var code in machines)
            {
                var machine = await resources.FindMachineAsync(code, ct);
                if (machine is null || machine.WorkCenterCode != workCenter)
                    throw new CommandRejectedException("invalid_machine", $"Machine {code} is not in work center {workCenter}.");
                if (await resources.FindMachineActivityAsync(code, activity, ct) is null)
                    throw new CommandRejectedException("activity_not_on_machine", $"{activity} is not defined for machine {code}.");
            }

            if (machines.Count == 0 && !activities.Any(a => a.WorkCenterCode == workCenter && a.ActivityCode == activity))
                throw new CommandRejectedException("activity_not_at_work_center", $"{activity} is not an allowed activity at {workCenter}; name eligible machines or allow it at the work center.");
            foreach (var q in quals)
                if (!await competency.QualificationTypeExistsAsync(q, ct))
                    throw new CommandRejectedException("unknown_qualification", $"Qualification type {q} does not exist.");
            if (o.Inspection && quals.Count == 0)
                throw new CommandRejectedException("inspection_qualification_required", $"Inspection step {o.Sequence} names the qualification an inspector needs.");
            result.Add(new ReworkPlanOperation(o.Sequence, description, workCenter, activity, machines, quals, o.SetupMinutes, o.RunMinutesPerUnit, o.CrewSize, o.Inspection));
        }

        return result;
    }

    private static async Task<List<ReworkPlanMaterial>> ValidateMaterialsAsync(IKernelUnitOfWork uow, IReadOnlyList<ReworkPlanOperation> operations,
        IReadOnlyList<ReworkPlanMaterial> requested, DateTimeOffset now, CancellationToken ct)
    {
        if (requested.Count > 12) throw new CommandRejectedException("too_many_materials", "A rework plan adds at most 12 material lines.");
        var engineering = uow.Store<IEngineeringLookup>();
        var result = new List<ReworkPlanMaterial>();
        foreach (var m in requested)
        {
            ArgumentNullException.ThrowIfNull(m);
            var part = BusinessCode.Require(m.PartNumber, "Part");
            if (m.Quantity is <= 0 or > 1_000_000)
                throw new CommandRejectedException("invalid_quantity", $"{part}: a quantity above zero.");
            if (operations.All(o => o.Sequence != m.ConsumedAtSequence))
                throw new CommandRejectedException("unknown_operation", $"{part} is consumed at one of the added operations.");
            if (await engineering.FindItemAsync(part, ct) is null)
                throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            if (await engineering.GetEffectiveRevisionAsync(part, now, ct) is null)
                throw new CommandRejectedException("no_released_revision", $"Part {part} has no released revision in force.");
            result.Add(new ReworkPlanMaterial(part, m.Quantity, m.ConsumedAtSequence));
        }

        return result;
    }

    private static Guid? PersonOf(CommandContext context) => context.Principal.PersonId.Value == Guid.Empty ? null : context.Principal.PersonId.Value;

    private static string Qty(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
