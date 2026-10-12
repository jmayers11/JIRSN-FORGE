using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Engines;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Planning.Reporting;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Planning;

public sealed record PlanningScenarioCreated(Guid ScenarioId, string Name) : IDomainEvent
{
    public static string EventType => nameof(PlanningScenarioCreated);
    public static int SchemaVersion => 1;
}

public sealed record PlanningScenarioDeleted(Guid ScenarioId) : IDomainEvent
{
    public static string EventType => nameof(PlanningScenarioDeleted);
    public static int SchemaVersion => 1;
}

/// <summary>Private planning workspaces. Evaluations run the real finite-capacity and skills-capacity engines on copied inputs.</summary>
public sealed class PlanningScenarioService(
    CommandPipeline pipeline,
    IKernelUnitOfWorkFactory unitOfWork,
    IForgeAuthorizer authorizer,
    TimeProvider time)
{
    private const string Entity = "PlanningScenario";

    public Task<CommandResult> CreateAsync(CreatePlanningScenario command, CommandContext context, CancellationToken ct)
    {
        Validate(command);
        var spec = new CommandSpec("Planning", nameof(CreatePlanningScenario), Permissions.AnalystUse, ResourceRef.Of(Entity), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var id = ForgeIds.New(time);
            var scenario = new PlanningScenarioInfo(id, context.Principal.PersonId, command.Name.Trim(), command.Change, now, now);
            await uow.Store<IPlanningScenarioStore>().InsertAsync(scenario, ct);
            return new CommandOutcome(
                CommandResult.Ok(id.ToString("D"), $"Private scenario '{scenario.Name}' saved."),
                [pipeline.Event(new PlanningScenarioCreated(id, scenario.Name), context, "Planning", Entity, id.ToString("D"))],
                new AuditChange(Entity, id.ToString("D"), null, CanonicalJson.Serialize(new { scenario.Name, scenario.Change })));
        }, ct);
    }

    public async Task<IReadOnlyList<PlanningScenarioInfo>> ListAsync(ForgePrincipal principal, CancellationToken ct)
    {
        Demand(principal, Permissions.AnalystUse);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return await uow.Store<IPlanningScenarioStore>().ListAsync(principal.PersonId, ct);
    }

    public async Task<PlanningScenarioInfo> FindAsync(Guid id, ForgePrincipal principal, CancellationToken ct)
    {
        Demand(principal, Permissions.AnalystUse);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return await OwnedAsync(uow.Store<IPlanningScenarioStore>(), id, principal, ct);
    }

    public Task<CommandResult> DeleteAsync(DeletePlanningScenario command, CommandContext context, CancellationToken ct)
    {
        var spec = new CommandSpec("Planning", nameof(DeletePlanningScenario), Permissions.AnalystUse, ResourceRef.Of(Entity, command.ScenarioId.ToString("D")), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, _) =>
        {
            var existing = await OwnedAsync(uow.Store<IPlanningScenarioStore>(), command.ScenarioId, context.Principal, ct);
            await uow.Store<IPlanningScenarioStore>().DeleteAsync(existing.ScenarioId, context.Principal.PersonId, ct);
            return new CommandOutcome(
                CommandResult.Ok(command.ScenarioId.ToString("D"), $"Private scenario '{existing.Name}' deleted."),
                [pipeline.Event(new PlanningScenarioDeleted(command.ScenarioId), context, "Planning", Entity, command.ScenarioId.ToString("D"))],
                new AuditChange(Entity, command.ScenarioId.ToString("D"), CanonicalJson.Serialize(new { existing.Name, existing.Change }), null));
        }, ct);
    }

    public async Task<PlanningScenarioResult> RunAsync(Guid id, ForgePrincipal principal, CancellationToken ct)
    {
        Demand(principal, Permissions.ScheduleRead);
        Demand(principal, Permissions.ReadinessRead);
        var scenario = await FindAsync(id, principal, ct);
        if (scenario.Change.Kind is ScenarioChangeKind.PersonUnavailable or ScenarioChangeKind.AddOperator or ScenarioChangeKind.AddTrainer)
            Demand(principal, Permissions.WorkforceRead);

        SiteSnapshot snapshot;
        List<OrderInfo> orders;
        IReadOnlyList<OrderMaterialStatus> material;
        IReadOnlyDictionary<long, OrderQuality> quality;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            snapshot = await SnapshotLoader.LoadAsync(read, time, ct);
            var execution = read.Store<IExecutionLookup>();
            orders = [];
            foreach (var row in (await execution.ListOrdersAsync(ct)).Where(o => o.Status == OrderStatus.Released && Visible(principal, o.ProgramId)).OrderBy(o => o.OrderNumber))
            {
                if (await execution.FindOrderAsync(row.OrderNumber, ct) is { } order) orders.Add(order);
            }

            material = (await MaterialPlanningService.RunMaterialAsync(read, time, ct)).Output;
            quality = await ScheduleQuality.LoadAsync(read, orders, ct);
        }

        var asOf = snapshot.AsOf;
        var horizon = asOf.AddDays(14);
        var (proposedSnapshot, proposedOrders, proposedMaterial, description) = Apply(scenario.Change, snapshot, orders, material, principal, asOf);
        var request = new ScheduleRequest(asOf, horizon, orders, material, quality);
        var proposedRequest = new ScheduleRequest(asOf, horizon, proposedOrders, proposedMaterial, quality);
        var baseline = EngineRunner.Run(new SchedulingEngine(), snapshot, request, asOf).Output;
        var proposed = EngineRunner.Run(new SchedulingEngine(), proposedSnapshot, proposedRequest, asOf).Output;
        var before = baseline.Orders.ToDictionary(o => o.OrderNumber);
        var after = proposed.Orders.ToDictionary(o => o.OrderNumber);
        var impacts = new List<ScenarioOrderImpact>();
        foreach (var number in before.Keys.Union(after.Keys).Order())
        {
            before.TryGetValue(number, out var a);
            after.TryGetValue(number, out var b);
            var ops = HorizonHistoryService.CompareOperations(baseline.Operations.Where(o => o.OrderNumber == number).ToList(), proposed.Operations.Where(o => o.OrderNumber == number).ToList());
            var change = HorizonHistoryService.Describe(number, a, b, ops, []);
            if (change.Change != "Unchanged")
                impacts.Add(new(number, (b ?? a)!.PartNumber, change.Change, a?.ProjectedCompletion, b?.ProjectedCompletion, change.ShiftHours,
                    a?.Status.ToString() ?? "Not scheduled", b?.Status.ToString() ?? "Not scheduled", [change.Explanation, .. ops.Take(3).Select(o => o.Explanation)]));
        }

        var capacityRequest = new CapacityRequest(asOf, horizon);
        var capBefore = EngineRunner.Run(new ExecutableCapacityEngine(), snapshot, capacityRequest, asOf).Output;
        var capAfter = EngineRunner.Run(new ExecutableCapacityEngine(), proposedSnapshot, capacityRequest, asOf).Output;
        var capBeforeBy = capBefore.QualifiedLabor.GroupBy(x => (x.Day, x.Qualification)).ToDictionary(g => g.Key, g => g.Sum(x => x.Hours));
        var capAfterBy = capAfter.QualifiedLabor.GroupBy(x => (x.Day, x.Qualification)).ToDictionary(g => g.Key, g => g.Sum(x => x.Hours));
        var skills = capBeforeBy.Keys.Union(capAfterBy.Keys).OrderBy(k => k.Day).ThenBy(k => k.Qualification, StringComparer.Ordinal)
            .Select(k => new ScenarioCapacityImpact(k.Qualification, k.Day, capBeforeBy.GetValueOrDefault(k), capAfterBy.GetValueOrDefault(k),
                (principal.HasPermission(Permissions.WorkforceRead)
                    ? capAfter.QualifiedLabor.Where(x => x.Day == k.Day && x.Qualification == k.Qualification).SelectMany(x => x.People).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
                    : Array.Empty<string>())))
            .Where(x => x.BaselineHours != x.ProposedHours).Take(100).ToArray();

        return new PlanningScenarioResult(id, asOf, 14, description, impacts, skills, RecoveryOptions(scenario.Change, snapshot, orders, principal),
            "Scenario-only comparison. Both plans use the same authorized current snapshot, released-order set, MRP material readiness, quality gates and finite-capacity engines. No source record or approved schedule was changed.");
    }

    private static (SiteSnapshot Snapshot, List<OrderInfo> Orders, IReadOnlyList<OrderMaterialStatus> Material, string Description) Apply(
        ScenarioChange change, SiteSnapshot snapshot, List<OrderInfo> orders, IReadOnlyList<OrderMaterialStatus> material, ForgePrincipal principal, DateTimeOffset at)
    {
        switch (change.Kind)
        {
            case ScenarioChangeKind.MachineUnavailable:
                {
                    var machine = Require(snapshot.Machines.FirstOrDefault(m => m.Code == change.MachineCode), "machine_not_found", "The machine does not exist or is not visible.");
                    var start = change.StartsAt!.Value;
                    var end = start.AddHours(change.DurationHours!.Value);
                    RequireWindow(start, end, at);
                    return (snapshot with { Downtime = [.. snapshot.Downtime, new DowntimeWindow(long.MinValue, machine.Code, DowntimeKind.Unplanned, start, end, "Scenario: machine unavailable")] }, orders, material,
                        $"{machine.Code} unavailable from {start:u} for {change.DurationHours} hour(s).");
                }
            case ScenarioChangeKind.PersonUnavailable:
                {
                    var person = Require(snapshot.Person(change.PersonId!.Value), "person_not_found", "The person is not visible in the current workforce snapshot.");
                    var start = change.StartsAt!.Value;
                    var end = start.AddHours(change.DurationHours!.Value);
                    RequireWindow(start, end, at);
                    return (snapshot with { Unavailability = [.. snapshot.Unavailability, new UnavailabilityWindow(person.PersonId, UnavailabilityKind.Unavailable, start, end)] }, orders, material,
                        $"{person.Name} unavailable from {start:u} for {change.DurationHours} hour(s).");
                }
            case ScenarioChangeKind.PriorityChange:
                {
                    var order = Require(orders.FirstOrDefault(o => o.OrderNumber == change.OrderNumber), "order_not_found", "The released order does not exist or is outside the current program access.");
                    return (snapshot, orders.Select(o => o.OrderNumber == order.OrderNumber ? o with { Priority = change.Priority!.Value } : o).ToList(), material,
                        $"Order {order.OrderNumber} priority {order.Priority} → {change.Priority} (1 is highest).");
                }
            case ScenarioChangeKind.OperationMachine:
                {
                    var order = Require(orders.FirstOrDefault(o => o.OrderNumber == change.OrderNumber), "order_not_found", "The released order does not exist or is outside the current program access.");
                    var machine = Require(snapshot.Machines.FirstOrDefault(m => m.Code == change.MachineCode && m.Active), "machine_not_found", "The target machine is not active or visible.");
                    var operation = Require(order.Operations.FirstOrDefault(o => o.Sequence == change.Sequence), "operation_not_found", "The operation does not exist on this released order.");
                    if (!operation.EligibleMachines.Contains(machine.Code, StringComparer.Ordinal)) throw new CommandRejectedException("machine_not_eligible", $"{machine.Code} is not an eligible machine on operation {operation.Sequence}.");
                    var changed = order with { Operations = order.Operations.Select(o => o.Sequence == operation.Sequence ? o with { EligibleMachines = [machine.Code] } : o).ToList() };
                    return (snapshot, orders.Select(o => o.OrderNumber == order.OrderNumber ? changed : o).ToList(), material,
                        $"Order {order.OrderNumber} operation {operation.Sequence} restricted to eligible machine {machine.Code}.");
                }
            case ScenarioChangeKind.Overtime:
                {
                    var minutes = change.DurationHours!.Value * 60;
                    var shifts = snapshot.Shifts.Select(s => s with { DurationMinutes = Math.Min(1440, s.DurationMinutes + minutes) }).ToList();
                    return (snapshot with { Shifts = shifts }, orders, material, $"Extend every active shift by {change.DurationHours} hour(s), capped at 24 hours per shift.");
                }
            case ScenarioChangeKind.SupplierDelay:
                {
                    var order = Require(orders.FirstOrDefault(o => o.OrderNumber == change.OrderNumber), "order_not_found", "The released order does not exist or is outside the current program access.");
                    var status = material.FirstOrDefault(m => m.OrderNumber == order.OrderNumber);
                    if (status is null || status.State != MaterialState.Expected) throw new CommandRejectedException("no_scheduled_supply", $"Order {order.OrderNumber} has no expected material receipt to delay.");
                    var delayed = status with { ReadyOn = status.ReadyOn?.AddDays(change.DelayDays!.Value), Summary = status.Summary + $" Scenario: expected receipts delayed {change.DelayDays} day(s)." };
                    return (snapshot, orders, material.Select(m => m.OrderNumber == order.OrderNumber ? delayed : m).ToArray(), $"Delay expected material for order {order.OrderNumber} by {change.DelayDays} day(s); MRP records are unchanged.");
                }
            case ScenarioChangeKind.AddOperator:
            case ScenarioChangeKind.AddTrainer:
                {
                    var person = Require(snapshot.Person(change.PersonId!.Value), "person_not_found", "The person is not visible in the current workforce snapshot.");
                    var center = Require(snapshot.WorkCenters.FirstOrDefault(w => w.Code == change.WorkCenterCode && w.Active), "work_center_not_found", "The work center is not active or visible.");
                    if (!snapshot.WorkCenterActivities.Any(a => a.WorkCenterCode == center.Code && a.ActivityCode == change.ActivityCode))
                        throw new CommandRejectedException("activity_not_at_work_center", "That activity is not configured at the selected work center.");
                    var assignment = new WorkAssignment(center.SafetyAreaCode, center.Code, null, change.ActivityCode!, false);
                    return (snapshot.WithAssignment(person.PersonId, assignment), orders, material,
                        $"Simulate {person.Name} assigned to {center.Code}/{change.ActivityCode} as {(change.Kind == ScenarioChangeKind.AddTrainer ? "a trainer" : "an operator")}; qualifications, presence and safety rules still apply.");
                }
            default:
                throw new CommandRejectedException("unsupported_scenario", "This scenario change is not supported.");
        }
    }

    private static IReadOnlyList<RecoveryOption> RecoveryOptions(ScenarioChange change, SiteSnapshot snapshot, IReadOnlyList<OrderInfo> orders, ForgePrincipal principal)
    {
        var hasAlternateMachine = orders.Any(o => o.Operations.Any(op => op.EligibleMachines.Count > 1));
        var canSeePeople = principal.HasPermission(Permissions.WorkforceRead);
        var hasQualifiedPeople = canSeePeople && snapshot.People.Any(p => p.Active && p.ValidQualifications.Count > 0);
        var canSchedule = principal.HasPermission(Permissions.SchedulePropose);
        var canReallocate = principal.HasPermission(Permissions.WorkforceManage) && hasQualifiedPeople;
        var result = new[]
        {
            Option("resequence", "Re-sequence the released work", canSchedule, canSchedule ? "A fresh schedule can compare due dates, priority and finite capacity." : "Requires SchedulePropose.", ["Projected completion dates can move for other orders; the approved plan remains unchanged until a planner proposes it."], "#/scheduling", Permissions.SchedulePropose),
            Option("priority", "Change the affected order priority", principal.HasPermission(Permissions.OrdersManage) && change.OrderNumber is not null, principal.HasPermission(Permissions.OrdersManage) ? "The source order is available for a controlled priority preview." : "Requires OrdersManage and the source order's current impact preview.", ["A higher priority can delay other released orders; a fresh impact preview is required before commit."], change.OrderNumber is { } pn ? $"#/orders/{pn}" : null, Permissions.OrdersManage),
            Option("alternate-machine", "Use another eligible machine", hasAlternateMachine && canSchedule, hasAlternateMachine ? canSchedule ? "The released routing lists more than one eligible machine." : "Requires SchedulePropose." : "No released operation with multiple eligible machines was identified.", ["Machine eligibility, qualification, tooling and safety gates still apply."], "#/scheduling", Permissions.SchedulePropose),
            Option("reallocate", "Reallocate a qualified operator", canReallocate, canReallocate ? "The snapshot contains active people with current qualifications; validate presence and assignment at the target time." : principal.HasPermission(Permissions.WorkforceManage) ? "No currently qualified person was identified in the visible snapshot." : "Requires WorkforceManage." , ["Moving a person can stop work at the source station; use the workforce assignment command and review its impact."], "#/workforce", Permissions.WorkforceManage),
            Option("add-operator", "Add a qualified operator to the work", canReallocate, canReallocate ? "Qualified people appear in the visible workforce snapshot; availability must be checked at the target time." : principal.HasPermission(Permissions.WorkforceManage) ? "No currently qualified person was identified." : "Requires WorkforceManage.", ["Crew coverage may improve, but safety staffing, presence and overlapping assignments remain binding."], "#/workforce", Permissions.WorkforceManage),
            Option("add-trainer", "Add a trainer for supervised work", principal.HasPermission(Permissions.WorkforceManage), principal.HasPermission(Permissions.WorkforceManage) ? "Workforce assignment is available to this principal." : "Requires WorkforceManage.", ["A qualified trainer must remain present, satisfy the applicable rule and avoid double-booking."], "#/workforce", Permissions.WorkforceManage),
            Option("overtime", "Extend a shift", principal.HasPermission(Permissions.ResourcesManage), principal.HasPermission(Permissions.ResourcesManage) ? "Shift changes are available to this principal." : "Requires ResourcesManage.", ["Overtime changes resource calendars for every affected order and must be approved as a real shift change."], "#/plant-setup", Permissions.ResourcesManage),
            Option("second-shift", "Add or assign a second shift", principal.HasPermission(Permissions.ResourcesManage), principal.HasPermission(Permissions.ResourcesManage) ? "Shift configuration is available to this principal." : "Requires ResourcesManage.", ["Additional calendar time does not create qualified labor or override equipment, material, quality or safety gates."], "#/plant-setup", Permissions.ResourcesManage),
            Option("expedite-supplier", "Ask the supplier to advance a promised date", principal.HasPermission(Permissions.PurchasingManage), principal.HasPermission(Permissions.PurchasingManage) ? "Purchasing can record a supplier-confirmed date through its source command." : "Requires PurchasingManage.", ["MRP and schedule projections change only after the supplier date is recorded on the purchase line."], "#/procurement", Permissions.PurchasingManage),
            Option("approved-alternate", "Use an approved material alternate", principal.HasPermission(Permissions.AlternatesApprove), principal.HasPermission(Permissions.AlternatesApprove) ? "Alternate governance is available; the BOM line policy must also permit use." : "Requires alternate approval authority; unapproved or disallowed substitutes remain infeasible.", ["Approved substitution preserves peg and quantity evidence; it cannot be applied as an unrecorded planning override."], "#/engineering", Permissions.AlternatesApprove),
            Option("transfer-material", "Transfer released material to point of use", principal.HasPermission(Permissions.InventoryTransact), principal.HasPermission(Permissions.InventoryTransact) ? "Inventory movement is available to this principal." : "Requires InventoryTransact.", ["A transfer changes location and custody only; quality hold, availability and net quantity remain separate."], "#/inventory", Permissions.InventoryTransact),
            Option("request-shortage", "Request the uncovered material shortage", principal.HasPermission(Permissions.RequisitionRaise), principal.HasPermission(Permissions.RequisitionRaise) ? "The requisition workflow is available to this principal." : "Requires RequisitionRaise.", ["A requisition records a request; it is not supply until a purchase order is approved."], "#/procurement", Permissions.RequisitionRaise),
            Option("maintenance-window", "Reschedule or shorten planned maintenance", principal.HasPermission(Permissions.MaintenancePlan), principal.HasPermission(Permissions.MaintenancePlan) ? "Maintenance planning is available to this principal." : "Requires MaintenancePlan.", ["Changing a planned window requires maintenance authority; unplanned outages cannot be cleared by a scenario."], "#/maintenance", Permissions.MaintenancePlan),
            Option("split-order", "Consider a partial build or split", false, "No split is applied by this preview; validate quantity states, material, routing and customer commitment before creating separate orders.", ["Separate orders create distinct commitments and may change traceability; do not assume partial completion is accepted."], null, Permissions.OrdersManage),
            Option("customer-date", "Coordinate a revised delivery date", principal.HasPermission(Permissions.ProgramsManage), principal.HasPermission(Permissions.ProgramsManage) ? "Program authority can review delivery commitments." : "Requires program authority; customer approval must be recorded at the source.", ["A revised date changes the commitment and its downstream goal projections; it does not create capacity."], "#/programs", Permissions.ProgramsManage),
        };
        return result;
    }

    private static RecoveryOption Option(string id, string title, bool feasible, string feasibility, IReadOnlyList<string> consequences, string? path, string permission) =>
        new(id, title, feasible, feasibility, consequences, path, permission);

    private static void RequireWindow(DateTimeOffset start, DateTimeOffset end, DateTimeOffset asOf)
    {
        if (end <= asOf || start >= asOf.AddDays(62))
            throw new CommandRejectedException("scenario_outside_horizon", "The simulated outage or absence must overlap the next 62 days.");
    }

    private static async Task<PlanningScenarioInfo> OwnedAsync(IPlanningScenarioStore store, Guid id, ForgePrincipal principal, CancellationToken ct)
    {
        var scenario = await store.FindAsync(id, ct);
        if (scenario is null || scenario.Owner != principal.PersonId)
            throw new CommandRejectedException("scenario_not_found", "This private scenario does not exist.");
        return scenario;
    }

    private void Demand(ForgePrincipal principal, string permission)
    {
        var decision = authorizer.Authorize(principal, permission, ResourceRef.Of(Entity));
        if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision);
    }

    private static bool Visible(ForgePrincipal principal, Guid? programId) => programId is null || principal.Programs.Contains(new ProgramId(programId.Value));

    private static T Require<T>(T? value, string code, string message) where T : class =>
        value ?? throw new CommandRejectedException(code, message);

    private static void Validate(CreatePlanningScenario command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Name) || command.Name.Trim().Length > 100)
            throw new CommandRejectedException("invalid_scenario_name", "A scenario name is required and may contain at most 100 characters.");
        var change = command.Change ?? throw new CommandRejectedException("invalid_scenario", "A scenario change is required.");
        if (change.Kind is ScenarioChangeKind.MachineUnavailable or ScenarioChangeKind.PersonUnavailable)
        {
            if (change.StartsAt is null || change.DurationHours is < 1 or > 336)
                throw new CommandRejectedException("invalid_scenario_window", "An outage or absence needs a start time and a duration from 1 to 336 hours.");
        }

        if (change.Kind == ScenarioChangeKind.PriorityChange && (change.OrderNumber is null || change.Priority is < 1 or > 9))
            throw new CommandRejectedException("invalid_scenario_priority", "A priority scenario needs a released order and priority from 1 (highest) to 9.");
        if (change.Kind == ScenarioChangeKind.OperationMachine && (change.OrderNumber is null || change.Sequence is null || string.IsNullOrWhiteSpace(change.MachineCode)))
            throw new CommandRejectedException("invalid_scenario_operation", "An operation-machine scenario needs an order, operation sequence and eligible machine.");
        if (change.Kind == ScenarioChangeKind.SupplierDelay && (change.OrderNumber is null || change.DelayDays is < 1 or > 180))
            throw new CommandRejectedException("invalid_scenario_supply", "A supplier-delay scenario needs an order and a delay from 1 to 180 days.");
        if (change.Kind == ScenarioChangeKind.Overtime && change.DurationHours is < 1 or > 4)
            throw new CommandRejectedException("invalid_scenario_overtime", "Overtime must be from 1 to 4 additional hours per shift.");
        if ((change.Kind is ScenarioChangeKind.AddOperator or ScenarioChangeKind.AddTrainer) && (change.PersonId is null || string.IsNullOrWhiteSpace(change.WorkCenterCode) || string.IsNullOrWhiteSpace(change.ActivityCode)))
            throw new CommandRejectedException("invalid_scenario_staffing", "A staffing scenario needs a person, work center and activity.");
    }
}
