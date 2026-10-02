using Forge.Competency.Contracts;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Engines;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Engines;
using Forge.Resources.Contracts;
using Forge.SafetyRules.Contracts;
using Forge.Workforce;
using Forge.Workforce.Contracts;

namespace Forge.Planning;

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
            ? (await MaterialPlanningService.RunMaterialAsync(uow, time, ct)).Output.FirstOrDefault(m => m.OrderNumber == order.OrderNumber)
            : null;
        var snapshot = await LoadSnapshotAsync(uow, ct);
        return EngineRunner.Run(orders, snapshot, new OrderReadinessRequest(order, revision, material), snapshot.AsOf);
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
}
