using Forge.Execution.Contracts;
using Forge.Kernel.Engines;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Engines;
using Forge.Quality.Contracts;

namespace Forge.Planning;

/// <summary>Recovery advice is evidence from the same visible snapshot, never a source command.</summary>
internal static class RecoveryOptionEvaluator
{
    private const int MaximumMachineEvaluations = 40;

    public static IReadOnlyList<RecoveryOption> Evaluate(
        ScenarioChange change, SiteSnapshot snapshot, IReadOnlyList<OrderInfo> orders,
        IReadOnlyList<OrderMaterialStatus> material, IReadOnlyDictionary<long, OrderQuality> quality,
        ScheduleResult current, ScheduleResult proposed, ForgePrincipal principal, IForgeAuthorizer authorizer)
    {
        var affected = Affected(change, snapshot, orders).ToArray();
        var numbers = affected.Select(o => o.OrderNumber).ToHashSet();
        var projections = proposed.Orders.Where(o => numbers.Contains(o.OrderNumber)).Select(after =>
        {
            var before = current.Orders.First(o => o.OrderNumber == after.OrderNumber);
            var shift = before.ProjectedCompletion is { } a && after.ProjectedCompletion is { } b
                ? (decimal?)(decimal)(b - a).TotalHours : null;
            return new ScenarioOrderImpact(after.OrderNumber, after.PartNumber,
                before.ProjectedCompletion == after.ProjectedCompletion && before.Status == after.Status ? "Unchanged" : "Changed",
                before.ProjectedCompletion, after.ProjectedCompletion, shift, before.Status.ToString(), after.Status.ToString(),
                [$"Current: {before.Explanation}", $"Scenario: {after.Explanation}"]);
        }).ToArray();
        var constraints = Blockers(proposed, numbers);
        var evidence = new List<string> { $"Evaluated at {snapshot.AsOf:u} against the visible released orders and the same finite-capacity, material, quality, holds, tooling, qualification and safety inputs as the scenario." };
        evidence.AddRange(material.Where(m => numbers.Contains(m.OrderNumber)).Select(m => $"Order {m.OrderNumber} material: {m.State}. {m.Summary}"));
        var machines = affected.SelectMany(o => o.Operations).SelectMany(o => o.EligibleMachines).ToHashSet(StringComparer.Ordinal);
        evidence.AddRange(snapshot.Downtime.Where(d => machines.Contains(d.MachineCode)).Select(d =>
            $"{(d.IsHypothetical ? "Scenario" : "Recorded")} downtime {d.Id} on {d.MachineCode} from {d.SourceContext}{(d.SourceReference is { } reference ? "/" + reference : string.Empty)}: {d.StartsAt:u} to {(d.EndsAt is { } end ? end.ToString("u") : "open")}. {d.Reason}"));
        evidence.AddRange(projections.Select(p => $"Order {p.OrderNumber}: {p.ProposedStatus}; projected completion {(p.ProposedCompletion is { } end ? end.ToString("u") : "unavailable")}."));

        RecoveryOption Inputs(string id, string title, string permission, string resourceType, string path, string consequence, params string[] required)
        {
            // Missing source selections cannot establish scoped authority or executable feasibility.
            var resource = resourceType == "ProductionOrder" && affected.Length == 1
                ? new ResourceRef(resourceType, affected[0].OrderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    affected[0].ProgramId is { } p ? new ProgramId(p) : null)
                : ResourceRef.Of(resourceType);
            var permitted = authorizer.Authorize(principal, permission, resource).IsPermitted;
            var missing = required.ToList();
            if (affected.Length == 0) missing.Insert(0, "Select an affected released order visible to this actor.");
            if (!permitted) missing.Add($"Obtain {permission} for the selected source record; its command rechecks scope and assurance.");
            return new(id, title, false, "Required inputs and source approvals must be supplied before this response can be evaluated as executable.",
                [consequence], path, permission, permitted ? "RequiresInputs" : "NotAuthorized", numbers.Order().ToArray(), projections,
                constraints, missing, evidence, []);
        }

        var result = new List<RecoveryOption>
        {
            Inputs("resequence", "Re-sequence the released work", Permissions.SchedulePropose, "Schedule", "#/scheduling",
                "A new dispatch order can displace other commitments; the current and scenario projections above do not approve a schedule.", "Specify dispatch order and review a fresh source schedule proposal before another person approves it."),
            Inputs("priority", "Change the affected order priority", Permissions.OrdersManage, "ProductionOrder", change.OrderNumber is { } pn ? $"#/orders/{pn}" : "#/orders",
                "Priority can move other commitments and cannot bypass execution gates.", "Select one order and new priority; obtain its current source impact fingerprint, reason and any required approval."),
        };
        result.Add(AlternateMachines(change, snapshot, orders, material, quality, proposed, affected, principal, authorizer, projections, constraints, evidence));
        result.AddRange(new[]
        {
            Inputs("reallocate", "Reallocate a qualified operator", Permissions.WorkforceSupervise, "Worker", "#/workforce",
                "Moving an operator may stop the source station; current qualification alone does not prove availability.", "Select operator, target assignment and interval; evaluate presence, qualifications, safety coverage and impact on the source station."),
            Inputs("add-operator", "Add a qualified operator to the work", Permissions.WorkforceSupervise, "Worker", "#/workforce",
                "Added calendar coverage does not create qualification or permit overlapping assignments.", "Select operator, target assignment and interval; verify current qualification, presence, calendar and crew coverage."),
            Inputs("add-trainer", "Add a trainer for supervised work", Permissions.WorkforceSupervise, "Worker", "#/workforce",
                "A trainer must satisfy the applicable safety rule and remain available throughout supervised work.", "Select trainer, trainee, assignment and interval; establish trainer authority and that the rule permits supervised work."),
            Inputs("overtime", "Extend a shift", Permissions.ResourcesManage, "Shift", "#/plant-setup",
                "A shift extension affects all orders on that calendar; it cannot remove material, quality or equipment blockers.", "Select the source shift and proposed duration; confirm qualified staffing and approval for the additional interval."),
            Inputs("second-shift", "Add or assign a second shift", Permissions.ResourcesManage, "Shift", "#/plant-setup",
                "A second shift creates calendar time only after staffing, resource and safety coverage are established.", "Specify shift, days, work centers and worker assignments; evaluate their capacity and coverage."),
            Inputs("expedite-supplier", "Ask the supplier to advance a promised date", Permissions.PurchasingManage, "PurchaseOrder", "#/procurement",
                "A request is not confirmed supply; forecasts move only after a supplier-confirmed date is recorded.", "Select the pegged purchase line and supplier-confirmed proposed date with evidence."),
            Inputs("approved-alternate", "Use an approved material alternate", Permissions.AlternatesApprove, "Alternate", "#/engineering",
                "An approved substitute remains subject to BOM policy, quantity, configuration and quality gates.", "Select BOM line and released alternate, applicable approval and available quantity; run source alternate readiness."),
            Inputs("transfer-material", "Transfer released material to point of use", Permissions.InventoryTransact, "Stock", "#/inventory",
                "Movement changes custody and location; it cannot release a hold or create missing quantity.", "Select released stock identity, available quantity, source and destination; verify source holds and point-of-use requirements."),
            Inputs("request-shortage", "Request the uncovered material shortage", Permissions.RequisitionRaise, "Requisition", "#/procurement",
                "A requisition is a request until approved procurement creates supply.", "Select an evidenced uncovered demand, component, quantity, need date and procurement route."),
            Inputs("maintenance-window", "Reschedule or shorten planned maintenance", Permissions.MaintenancePlan, "Machine", "#/maintenance",
                "An unplanned machine failure remains open until an authorized maintenance restoration command records the repair.", "Select a planned maintenance window and proposed dates; validate readiness, impact and required independent approval."),
            Inputs("split-order", "Consider a partial build or split", Permissions.OrdersManage, "ProductionOrder", "#/orders",
                "Split commitments need accepted quantities and traceability; partial delivery is not assumed acceptable.", "Select order, split quantities, available material and routing, customer acceptance and traceability policy."),
            Inputs("customer-date", "Coordinate a revised delivery date", Permissions.ProgramsManage, "ProductionOrder", "#/programs",
                "A revised date changes the commitment and dependent goals; it does not create executable capacity.", "Select the delivery commitment and accepted new date; record customer approval and source impact evidence."),
        });
        return result;
    }

    private static IEnumerable<OrderInfo> Affected(ScenarioChange change, SiteSnapshot snapshot, IReadOnlyList<OrderInfo> orders)
    {
        if (change.OrderNumber is { } number) return orders.Where(o => o.OrderNumber == number);
        if (change.MachineCode is { } machine) return orders.Where(o => o.Operations.Any(op => op.Status != OperationStatus.Complete && op.EligibleMachines.Contains(machine, StringComparer.Ordinal)));
        var workCenter = change.WorkCenterCode ?? (change.PersonId is { } person ? snapshot.Person(person)?.Assignment?.WorkCenterCode : null);
        if (workCenter is not null) return orders.Where(o => o.Operations.Any(op => op.Status != OperationStatus.Complete && op.WorkCenterCode == workCenter));
        return change.Kind == ScenarioChangeKind.Overtime ? orders : [];
    }

    private static RecoveryOption AlternateMachines(ScenarioChange change, SiteSnapshot snapshot, IReadOnlyList<OrderInfo> orders,
        IReadOnlyList<OrderMaterialStatus> material, IReadOnlyDictionary<long, OrderQuality> quality, ScheduleResult proposed,
        IReadOnlyList<OrderInfo> affected, ForgePrincipal principal, IForgeAuthorizer authorizer,
        IReadOnlyList<ScenarioOrderImpact> projections, IReadOnlyList<string> constraints, IReadOnlyList<string> evidence)
    {
        var targets = affected.SelectMany(order => order.Operations.Where(op => op.Status != OperationStatus.Complete &&
                (change.Sequence is { } sequence ? op.Sequence == sequence : change.MachineCode is { } machine ? op.EligibleMachines.Contains(machine, StringComparer.Ordinal) : op.EligibleMachines.Count > 0))
            .Select(op => (Order: order, Operation: op))).ToArray();
        var permitted = authorizer.Authorize(principal, Permissions.SchedulePropose, ResourceRef.Of("Schedule")).IsPermitted;
        var alternatives = new List<RecoveryMachineAlternative>();
        var required = new List<string>();
        var examined = 0;
        foreach (var (order, operation) in targets)
        {
            var excluded = change.Kind == ScenarioChangeKind.OperationMachine ? null : change.MachineCode ??
                proposed.Operations.FirstOrDefault(o => o.OrderNumber == order.OrderNumber && o.Sequence == operation.Sequence)?.MachineCode;
            var candidates = operation.EligibleMachines.Where(m => m != excluded &&
                (change.Kind != ScenarioChangeKind.OperationMachine || m == change.MachineCode)).Order(StringComparer.Ordinal).ToArray();
            foreach (var machine in candidates)
            {
                if (examined++ >= MaximumMachineEvaluations) { required.Add("Narrow the order/operation scope to evaluate all candidate machines."); break; }
                var candidateOrders = orders.Select(o => o.OrderNumber != order.OrderNumber ? o : o with
                {
                    Operations = o.Operations.Select(op => op.Sequence == operation.Sequence ? op with { EligibleMachines = [machine] } : op).ToArray(),
                }).ToArray();
                var schedule = EngineRunner.Run(new SchedulingEngine(), snapshot,
                    new ScheduleRequest(proposed.From, proposed.To, candidateOrders, material, quality), snapshot.AsOf).Output;
                var candidate = schedule.Orders.First(o => o.OrderNumber == order.OrderNumber);
                var blocks = Blockers(schedule, new HashSet<long> { order.OrderNumber }).ToList();
                var facts = new List<string> { $"Released order {order.OrderNumber} operation {operation.Sequence} lists {machine} as eligible.", candidate.Explanation,
                    "The candidate was evaluated in the full visible queue; all remaining operations, crew calendars, downtime, holds, quality, material and tooling still apply." };
                foreach (var planned in schedule.Operations.Where(o => o.OrderNumber == order.OrderNumber && o.Scheduled))
                {
                    if (!snapshot.WorkCenters.Any(w => w.Code == planned.WorkCenterCode && w.Active))
                        blocks.Add($"Operation {planned.Sequence}: work center {planned.WorkCenterCode} is inactive or missing.");
                    if (planned.MachineCode is { } plannedMachine && !snapshot.Machines.Any(m => m.Code == plannedMachine && m.Active))
                        blocks.Add($"Operation {planned.Sequence}: machine {plannedMachine} is inactive or missing.");
                    foreach (var at in planned.Segments.SelectMany(s => s.End > s.Start ? new[] { s.Start, s.End.AddTicks(-1) } : [s.Start]).Distinct())
                    {
                        var tooling = ExecutableChecks.Tooling(snapshot, planned.MachineCode, planned.ActivityCode, at);
                        if (tooling.Outcome is EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate)
                            blocks.Add($"Operation {planned.Sequence} tooling at {at:u}: {tooling.Detail}");
                        else
                            facts.Add($"Operation {planned.Sequence} tooling at {at:u}: {tooling.Detail}");
                    }
                }
                facts.AddRange(schedule.Operations.Where(o => o.OrderNumber == order.OrderNumber).Select(o => $"Operation {o.Sequence} on {o.MachineCode ?? o.WorkCenterCode}: {o.BindingConstraint}. {o.BindingDetail}"));
                facts.AddRange(schedule.Orders.Where(o => o.OrderNumber != order.OrderNumber).Select(o =>
                    (Before: proposed.Orders.First(x => x.OrderNumber == o.OrderNumber), After: o))
                    .Where(x => x.Before.ProjectedCompletion != x.After.ProjectedCompletion || x.Before.Status != x.After.Status)
                    .Select(x => $"Other commitment {x.After.OrderNumber}: {x.Before.Status} at {x.Before.ProjectedCompletion:u} → {x.After.Status} at {x.After.ProjectedCompletion:u}."));
                var executable = candidate.ProjectedCompletion is not null && blocks.Count == 0;
                alternatives.Add(new(order.OrderNumber, operation.Sequence, machine, executable,
                    executable ? candidate.ProjectedCompletion : null, blocks, facts));
            }
        }
        if (targets.Length == 0) required.Add("Select an affected released order and operation with machine eligibility.");
        if (targets.Length > 1) required.Add("Select one affected order and operation, or specify a combined machine assignment to evaluate as one plan; independent candidate plans do not establish a combined recovery.");
        var feasible = targets.Length > 0 && required.Count == 0 && targets.All(t => alternatives.Any(a => a.OrderNumber == t.Order.OrderNumber && a.Sequence == t.Operation.Sequence && a.Feasible));
        var status = !permitted ? "NotAuthorized" : required.Count > 0 ? "RequiresInputs" : feasible ? "Feasible" : "Infeasible";
        if (!permitted) required.Add($"Obtain {Permissions.SchedulePropose}; the source schedule command checks authority again.");
        var allConstraints = constraints.Concat(alternatives.SelectMany(a => a.Constraints)).Distinct(StringComparer.Ordinal).ToList();
        if (targets.Any(t => !alternatives.Any(a => a.OrderNumber == t.Order.OrderNumber && a.Sequence == t.Operation.Sequence)))
            allConstraints.Add("An affected operation has no other released eligible machine; unrelated orders' machine eligibility cannot provide a recovery.");
        return new("alternate-machine", "Use another eligible machine", permitted && feasible,
            feasible ? "Each affected operation has an eligible machine with a finite-capacity completion projection under the evaluated facts. Review the candidate dates and consequences before committing a source schedule."
                : "No executable alternate is asserted. Inspect the candidate blockers or supply the missing inputs.",
            ["Candidate dates are planning projections. The source schedule requires a human proposal and approval; current execution gates and assurance are checked again at commit/use."],
            "#/scheduling", Permissions.SchedulePropose, status, affected.Select(o => o.OrderNumber).Order().ToArray(), projections,
            allConstraints, required, evidence, alternatives);
    }

    private static IReadOnlyList<string> Blockers(ScheduleResult schedule, IReadOnlySet<long> numbers) =>
        schedule.Operations.Where(o => numbers.Contains(o.OrderNumber) && !o.Scheduled)
            .Select(o => $"Order {o.OrderNumber} operation {o.Sequence}: {o.BindingConstraint}. {o.BindingDetail}")
            .Distinct(StringComparer.Ordinal).ToArray();
}
