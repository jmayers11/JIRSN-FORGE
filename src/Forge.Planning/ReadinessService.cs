using Forge.Competency.Contracts;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Engines;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Engines;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;
using Forge.SafetyRules.Contracts;
using Forge.Workforce;
using Forge.Workforce.Contracts;

namespace Forge.Planning;

public sealed record ReallocationPlanProposal(
    Guid PersonId,
    string? SafetyAreaCode,
    string? WorkCenterCode,
    string? MachineCode,
    string? ActivityCode,
    bool AsTrainee,
    bool Unassign = false);

/// <summary>
/// Read-side entry point for Phase 2 planning: loads one consistent snapshot, runs the deterministic
/// engines and returns their results with evidence and fingerprints. Nothing here writes data.
/// </summary>
public sealed class ReadinessService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    private readonly OperationReadinessEngine readiness = new();
    private readonly ReallocationImpactEngine reallocation = new();
    private readonly OrderReadinessEngine orders = new();
    private readonly AttainmentEngine attainment = new();

    /// <summary>Readiness of each operation of a production order, now.</summary>
    public async Task<EngineRun<OrderReadinessResult>> EvaluateOrderAsync(ForgePrincipal principal, long orderNumber, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Demand(principal);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
        if (order is null || (order.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program))))
        {
            // A program-segregated order is reported exactly like a missing one.
            throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
        }

        var revision = (await uow.Store<IEngineeringLookup>().ListRevisionsAsync(order.PartNumber, ct)).SingleOrDefault(r => r.Revision == order.Revision);
        var material = order.Status == OrderStatus.Released
            ? (await MaterialPlanningService.RunMaterialAsync(uow, time, ct, principal)).Output.FirstOrDefault(m => m.OrderNumber == order.OrderNumber)
            : null;
        var quality = order.Status is OrderStatus.Released or OrderStatus.Held
            ? await uow.Store<IQualityLookup>().GetOrderQualityAsync(order.OrderNumber, order.PartNumber, ct)
            : null;
        var snapshot = await LoadSnapshotAsync(uow, ct);
        var documentation = await DocumentationService.ForOrderAsync(uow, order, snapshot.AsOf, principal, ct);
        var pointOfUse = material is null ? null : await PointOfUseAsync(uow, snapshot.AsOf, ct);
        var training = await TrainingAsync(uow, documentation, ct);
        var continuations = await WipContinuationFacts.LoadAsync(uow, [order], snapshot.AsOf, ct);
        return EngineRunner.Run(orders, snapshot, new OrderReadinessRequest(order, revision, material, quality, documentation, pointOfUse, training, continuations.GetValueOrDefault(order.OrderNumber)), snapshot.AsOf);
    }

    /// <summary>Released, usable stock at points of use, and the work centers that have a point-of-use location (V3.9).</summary>
    internal static async Task<OrderPointOfUse> PointOfUseAsync(IKernelUnitOfWork uow, DateTimeOffset now, CancellationToken ct)
    {
        var positions = (await Forge.Inventory.Contracts.ShelfLifeQueries.PositionsAsync(uow, null, now, ct)).Where(p => p.PointOfUseWorkCenter is not null).ToList();
        var centers = (await uow.Store<Forge.Inventory.Contracts.IInventoryLookup>().ListLocationsAsync(ct))
            .Where(l => l.Active && l.PointOfUse)
            .Select(l => l.WorkCenterCode!)
            .ToHashSet(StringComparer.Ordinal);
        return new OrderPointOfUse(positions, centers);
    }

    /// <summary>The training policy and the controlled-document revisions of the order that need training (V3.9).</summary>
    internal static async Task<OrderTraining> TrainingAsync(IKernelUnitOfWork uow, OrderDocumentation documentation, CancellationToken ct)
    {
        var policy = await uow.Store<IDocumentTrainingLookup>().GetTrainingPolicyAsync(ct);
        if (policy.Policy == TrainingPolicy.Off)
        {
            return new OrderTraining(TrainingPolicy.Off, []);
        }

        var requirements = new List<TrainingRequirement>();
        var lookup = uow.Store<IDocumentLookup>();
        foreach (var d in documentation.Documents)
        {
            if ((d.AtRelease ?? d.Applicable) is { } revision && await lookup.FindDocumentAsync(d.DocumentNumber, ct) is { } info && policy.DocumentTypes.Contains(info.Type))
            {
                requirements.Add(new TrainingRequirement(d.DocumentNumber, revision, d.OperationSequence));
            }
        }

        return new OrderTraining(policy.Policy, requirements.Distinct().ToList());
    }

    public async Task<EngineRun<ReadinessResult>> EvaluateMachineAsync(ForgePrincipal principal, string machineCode, string activityCode, CancellationToken ct)
    {
        Demand(principal);
        var machine = BusinessCode.Require(machineCode, "Machine");
        var activity = BusinessCode.Require(activityCode, "Activity");
        var snapshot = await LoadSnapshotAsync(ct);
        var info = snapshot.Machines.FirstOrDefault(m => m.Code == machine)
            ?? throw new CommandRejectedException("unknown_machine", $"Machine {machine} does not exist.");
        var work = new WorkItem(info.SafetyAreaCode, info.WorkCenterCode, info.Code, activity);
        return EngineRunner.Run(readiness, snapshot, work, snapshot.AsOf);
    }

    /// <summary>Readiness of everything people are working on now, optionally for one safety area.</summary>
    public async Task<IReadOnlyList<EngineRun<ReadinessResult>>> EvaluateActiveWorkAsync(ForgePrincipal principal, string? safetyAreaCode, CancellationToken ct)
    {
        Demand(principal);
        var area = safetyAreaCode is null ? null : BusinessCode.Require(safetyAreaCode, "Safety area");
        var snapshot = await LoadSnapshotAsync(ct);
        return snapshot.ActiveWork()
            .Where(w => area is null || w.SafetyAreaCode == area)
            .Select(w => EngineRunner.Run(readiness, snapshot, w, snapshot.AsOf))
            .ToList();
    }

    /// <summary>
    /// Each worker's presence level now (V3.9): not present, scheduled (never counted), clocked in, assigned to a work
    /// center, active on an operation. Optionally for one work center (home, assigned or working there).
    /// </summary>
    public async Task<IReadOnlyList<PersonPresence>> EvaluatePresenceAsync(ForgePrincipal principal, string? workCenterCode, CancellationToken ct)
    {
        Demand(principal);
        var wc = string.IsNullOrWhiteSpace(workCenterCode) ? null : BusinessCode.Require(workCenterCode, "Work center");
        var snapshot = await LoadSnapshotAsync(ct);
        return ExecutableChecks.Presence(snapshot).Where(p => wc is null || p.HomeWorkCenter == wc || p.WorkCenter == wc).ToList();
    }

    /// <summary>Every open operation of released and held orders: can it happen now, and if not, why not (V3.9).</summary>
    public async Task<IReadOnlyList<OperationAnswer>> EvaluateOperationsAsync(ForgePrincipal principal, string? workCenterCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Demand(principal);
        var wc = string.IsNullOrWhiteSpace(workCenterCode) ? null : BusinessCode.Require(workCenterCode, "Work center");
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return (await ExecutableOperations.EvaluateAsync(uow, time, principal, ct)).Where(o => wc is null || o.WorkCenter == wc).ToList();
    }

    /// <summary>Impact of moving a person, evaluated on a copy of the current state. Nothing is committed.</summary>
    public async Task<EngineRun<ReallocationPreview>> PreviewReallocationAsync(ForgePrincipal principal, AssignWork proposal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        Demand(principal);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var proposed = await WorkforceService.ResolveAssignmentAsync(uow.Store<IResourcesLookup>(), proposal, ct);
        var snapshot = await LoadSnapshotAsync(uow, ct);
        if (snapshot.Person(proposal.PersonId.Value) is null)
        {
            throw new CommandRejectedException("worker_not_found", "This person is not a registered worker.");
        }

        return EngineRunner.Run(reallocation, snapshot, new ReallocationRequest(proposal.PersonId.Value, proposed), snapshot.AsOf);
    }

    /// <summary>Ranks currently present, visible workers for a machine/activity gap without writing assignments.</summary>
    public async Task<EngineRun<WorkforceRecoveryResult>> RecommendWorkforceRecoveryAsync(
        ForgePrincipal principal,
        AssignWork targetRequest,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targetRequest);
        Demand(principal);
        if (targetRequest.AsTrainee)
            throw new CommandRejectedException("invalid_recovery_target", "A recovery target must represent independently authorized work.");

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var resources = uow.Store<IResourcesLookup>();
        // Assignment resolution validates the target machine/activity against the canonical Resources model.
        var targetAssignment = await WorkforceService.ResolveAssignmentAsync(resources,
            targetRequest with { PersonId = new PersonId(Guid.Empty) }, ct);
        var target = new WorkItem(targetAssignment.SafetyAreaCode, targetAssignment.WorkCenterCode,
            targetAssignment.MachineCode, targetAssignment.ActivityCode);
        var snapshot = await LoadSnapshotAsync(uow, ct);
        var visibleCandidates = snapshot.People
            .Where(person => CanSuperviseWorkforceScope(principal, person.PersonId, person.Assignment?.WorkCenterCode ?? person.HomeWorkCenterCode)
                && CanSuperviseWorkforceScope(principal, person.PersonId, target.WorkCenterCode))
            .Select(person => person.PersonId)
            .ToHashSet();

        return EngineRunner.Run(new WorkforceRecoveryEngine(), snapshot,
            new WorkforceRecoveryRequest(target, visibleCandidates), snapshot.AsOf);
    }

    /// <summary>Previews a cascading worker move scenario against one copied Forge snapshot. Nothing is committed.</summary>
    public async Task<EngineRun<ReallocationPlanResult>> PreviewReallocationPlanAsync(
        ForgePrincipal principal,
        IReadOnlyList<ReallocationPlanProposal> proposals,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(proposals);
        Demand(principal);
        if (proposals.Count is < 1 or > 100)
            throw new CommandRejectedException("invalid_reallocation_plan", "A scenario must contain between 1 and 100 proposed moves.");
        if (proposals.Select(proposal => proposal.PersonId).Distinct().Count() != proposals.Count)
            throw new CommandRejectedException("duplicate_worker_move", "Each worker can appear only once in a scenario. Update that worker's proposed destination instead.");

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var snapshot = await LoadSnapshotAsync(uow, ct);
        var resources = uow.Store<IResourcesLookup>();
        var moves = new List<ReallocationPlanMove>(proposals.Count);
        foreach (var proposal in proposals)
        {
            var worker = snapshot.Person(proposal.PersonId)
                ?? throw new CommandRejectedException("worker_not_found", "A worker in the scenario is not registered or visible.");
            var currentCenter = worker.Assignment?.WorkCenterCode ?? worker.HomeWorkCenterCode;
            DemandWorkforceScope(principal, proposal.PersonId, currentCenter);
            if (proposal.Unassign)
            {
                moves.Add(new ReallocationPlanMove(proposal.PersonId, null));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(proposal.WorkCenterCode))
                DemandWorkforceScope(principal, proposal.PersonId, proposal.WorkCenterCode);
            if (string.IsNullOrWhiteSpace(proposal.ActivityCode))
                throw new CommandRejectedException("activity_required", "Choose an activity for every proposed work-center assignment.");

            var proposed = await WorkforceService.ResolveAssignmentAsync(resources,
                new AssignWork(new PersonId(proposal.PersonId), proposal.SafetyAreaCode, proposal.WorkCenterCode, proposal.MachineCode, proposal.ActivityCode, proposal.AsTrainee), ct);
            moves.Add(new ReallocationPlanMove(proposal.PersonId, proposed));
        }

        return EngineRunner.Run(new ReallocationPlanEngine(), snapshot, moves, snapshot.AsOf);
    }

    public async Task<SiteSnapshot> LoadSnapshotAsync(CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return await LoadSnapshotAsync(uow, ct);
    }

    private Task<SiteSnapshot> LoadSnapshotAsync(IKernelUnitOfWork uow, CancellationToken ct) => SnapshotLoader.LoadAsync(uow, time, ct);

    /// <summary>Executable capacity over a horizon (at most 62 days), projected from schedules.</summary>
    public async Task<EngineRun<CapacityResult>> EvaluateCapacityAsync(ForgePrincipal principal, DateTimeOffset? from, DateTimeOffset? to, string? workCenterCode, CancellationToken ct)
    {
        Demand(principal);
        var snapshot = await LoadSnapshotAsync(ct);
        var start = from?.ToUniversalTime() ?? snapshot.AsOf;
        var end = to?.ToUniversalTime() ?? start.AddDays(7);
        if (end <= start || end - start > TimeSpan.FromDays(62))
        {
            throw new CommandRejectedException("invalid_horizon", "The horizon must end after it starts and span at most 62 days.");
        }

        var wc = string.IsNullOrWhiteSpace(workCenterCode) ? null : BusinessCode.Require(workCenterCode, "Work center");
        return EngineRunner.Run(new ExecutableCapacityEngine(), snapshot, new CapacityRequest(start, end, wc), snapshot.AsOf);
    }

    /// <summary>Latest dates that still meet the order's due date (spec §42).</summary>
    public async Task<EngineRun<DropDeadResult>> EvaluateDropDeadDatesAsync(ForgePrincipal principal, long orderNumber, int shipDays, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Demand(principal);
        if (shipDays is < 0 or > 60)
        {
            throw new CommandRejectedException("invalid_ship_days", "Shipping days must be between 0 and 60.");
        }

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
        if (order is null || (order.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program))))
        {
            throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
        }

        var engineering = uow.Store<IEngineeringLookup>();
        var detail = await engineering.GetRevisionDetailAsync(order.PartNumber, order.Revision, ct);
        if (order.Operations.Count == 0 && detail is not null)
        {
            // Not released yet: use the routing of the order's revision.
            order = order with
            {
                Operations = detail.Routing.Select(op => new OrderOperationInfo(
                    op.Sequence, op.Description, op.WorkCenterCode, op.ActivityCode, op.EligibleMachines, op.RequiredQualifications,
                    op.SetupMinutes + (op.RunMinutesPerUnit * order.Quantity), op.CrewSize, OperationStatus.Pending)).ToList(),
            };
        }

        var purchased = new List<PurchasedComponent>();
        foreach (var line in detail?.Bom ?? [])
        {
            if (await engineering.FindItemAsync(line.ComponentPartNumber, ct) is { MakeBuy: MakeBuy.Buy } item)
            {
                purchased.Add(new PurchasedComponent(item.PartNumber, item.LeadTimeDays));
            }
        }

        var snapshot = await LoadSnapshotAsync(uow, ct);
        return EngineRunner.Run(new DropDeadDateEngine(), snapshot, new DropDeadRequest(order, shipDays, purchased), snapshot.AsOf);
    }

    /// <summary>
    /// Schedule attainment for a past (or current) period, against the schedule approved and in force
    /// when the period began. Orders of programs the caller cannot see are left out entirely.
    /// </summary>
    public async Task<EngineRun<AttainmentResult>> EvaluateAttainmentAsync(ForgePrincipal principal, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decision = authorizer.Authorize(principal, Permissions.ScheduleRead, ResourceRef.Of("Schedule"));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }

        if (to <= from || to - from > TimeSpan.FromDays(62))
        {
            throw new CommandRejectedException("invalid_period", "The period must end after it starts and span at most 62 days.");
        }

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var version = await uow.Store<IPlanningStore>().ApprovedAsOfAsync(from, ct);
        var plan = version?.Result;
        if (plan is not null)
        {
            plan = plan with
            {
                Operations = plan.Operations.Where(o => o.ProgramId is not { } p || principal.Programs.Contains(new ProgramId(p))).ToList(),
                Orders = plan.Orders.Where(o => o.ProgramId is not { } p || principal.Programs.Contains(new ProgramId(p))).ToList(),
            };
        }

        var lookup = uow.Store<IExecutionLookup>();
        var executed = new List<OrderInfo>();
        foreach (var number in (plan?.Operations ?? []).Select(o => o.OrderNumber).Distinct().Order())
        {
            if (await lookup.FindOrderAsync(number, ct) is { } order)
            {
                executed.Add(order);
            }
        }

        var snapshot = new AttainmentSnapshot(version?.VersionId, version?.ApprovedAt, plan, executed);
        return EngineRunner.Run(attainment, snapshot, new AttainmentRequest(from.ToUniversalTime(), to.ToUniversalTime()), time.GetUtcNow());
    }

    private void Demand(ForgePrincipal principal)
    {
        var decision = authorizer.Authorize(principal, Permissions.ReadinessRead, ResourceRef.Of("Readiness"));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }
    }

    private void DemandWorkforceScope(ForgePrincipal principal, Guid personId, string? workCenterCode)
    {
        var resource = new ResourceRef("Worker", personId.ToString("D"), WorkCenterId: workCenterCode);
        var decision = authorizer.Authorize(principal, Permissions.WorkforceSupervise, resource);
        if (!decision.IsPermitted && !authorizer.Authorize(principal, Permissions.WorkforceManage, resource).IsPermitted)
            throw new ForgeAuthorizationException(decision);
    }

    private bool CanSuperviseWorkforceScope(ForgePrincipal principal, Guid personId, string? workCenterCode)
    {
        var resource = new ResourceRef("Worker", personId.ToString("D"), WorkCenterId: workCenterCode);
        return authorizer.Authorize(principal, Permissions.WorkforceSupervise, resource).IsPermitted
            || authorizer.Authorize(principal, Permissions.WorkforceManage, resource).IsPermitted;
    }
}
