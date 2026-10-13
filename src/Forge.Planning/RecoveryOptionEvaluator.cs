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
        ScheduleResult current, ScheduleResult proposed, ForgePrincipal principal, IForgeAuthorizer authorizer, RecoverySourceFacts? sources = null)
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

        RecoveryOption Inputs(RecoveryDefinition definition)
        {
            var selected = change.RecoverySelections?.SingleOrDefault(s => s.OptionId == definition.Id);
            var order = orders.FirstOrDefault(o => o.OrderNumber == selected?.OrderNumber) ?? (affected.Length == 1 ? affected[0] : null);
            var resourceId = definition.ResourceType switch
            {
                "ProductionOrder" => order?.OrderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Stock" => selected?.PartNumber,
                "Shift" => selected?.ProposedShiftCode ?? selected?.ShiftCode,
                "WorkCenter" => selected?.WorkCenterCode,
                "OutsideProcess" => selected is null ? null : $"{selected.PartNumber}:{selected.LotNumber}:PO{selected.PurchaseOrder}-L{selected.PurchaseOrderLine}",
                "Worker" => selected?.PersonId?.ToString("D"),
                "PurchaseOrder" => selected?.PurchaseOrder?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "WorkOrder" => selected?.WorkOrderId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Shipment" => selected?.ShipmentId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };
            var resource = new ResourceRef(definition.ResourceType, resourceId,
                definition.ResourceType == "ProductionOrder" && order?.ProgramId is { } program ? new ProgramId(program) : null);
            var permitted = authorizer.Authorize(principal, definition.Permission, resource).IsPermitted;
            var missing = definition.Inputs.ToList();
            if (affected.Length == 0 && selected?.OrderNumber is null) missing.Insert(0, "Select an affected released order visible to this actor.");
            if (!permitted) missing.Add($"Obtain {definition.Permission} for the source record; its command rechecks scope and assurance.");
            var option = new RecoveryOption(definition.Id, definition.Title, false,
                "Required source selections must be supplied before an executable recovery can be asserted.",
                [definition.Consequence], definition.Path, definition.Permission, permitted ? "RequiresInputs" : "NotAuthorized",
                numbers.Order().ToArray(), projections, constraints, missing, evidence, [])
            {
                SourceAuthority = [new(definition.Context, definition.Command, definition.Permission, definition.ResourceType, resourceId, permitted,
                    definition.CommitRequirement)],
            };
            return selected is null || sources is null ? option : RecoveryTrialEvaluator.Evaluate(option, selected,
                snapshot, orders, material, quality, proposed, sources, principal, authorizer);
        }

        return RecoveryCatalog.Options.Select(definition => definition.Id == "alternate-machine"
            ? AlternateMachines(change, snapshot, orders, material, quality, proposed, affected, principal, authorizer, projections, constraints, evidence, sources)
            : Inputs(definition)).ToArray();
    }

    private static IEnumerable<OrderInfo> Affected(ScenarioChange change, SiteSnapshot snapshot, IReadOnlyList<OrderInfo> orders)
    {
        if (change.OrderNumber is { } number) return orders.Where(o => o.OrderNumber == number);
        if (change.MachineCode is { } machine) return orders.Where(o => o.Operations.Any(op => op.Status != OperationStatus.Complete && op.EligibleMachines.Contains(machine, StringComparer.Ordinal)));
        var workCenter = change.WorkCenterCode ?? (change.PersonId is { } person ? snapshot.Person(person)?.Assignment?.WorkCenterCode : null);
        if (workCenter is not null) return orders.Where(o => o.Operations.Any(op => op.Status != OperationStatus.Complete && op.WorkCenterCode == workCenter));
        return change.Kind == ScenarioChangeKind.Overtime ? orders : [];
    }

    /// <summary>Only these orders can reach a recovery trial's shared execution checks; scheduling still uses the full queue.</summary>
    internal static IReadOnlyList<OrderInfo> SourceOrders(ScenarioChange change, SiteSnapshot snapshot, IReadOnlyList<OrderInfo> orders)
    {
        var selection = change.RecoverySelections?.SingleOrDefault(s => s.OptionId == "alternate-machine");
        var machineOrders = selection?.OrderNumber is { } number
            ? orders.Where(o => o.OrderNumber == number) : Affected(change, snapshot, orders);
        var numbers = machineOrders.Where(order => order.Operations.Any(op => MachineTarget(change, selection, op)))
            .Select(o => o.OrderNumber).ToHashSet();
        numbers.UnionWith((change.RecoverySelections ?? []).Where(s => s.OrderNumber is not null).Select(s => s.OrderNumber!.Value));
        return orders.Where(o => numbers.Contains(o.OrderNumber)).ToArray();
    }

    private static bool MachineTarget(ScenarioChange change, RecoverySelection? selection, OrderOperationInfo operation) =>
        operation.Status != OperationStatus.Complete &&
        (selection?.Sequence is { } chosenSequence ? operation.Sequence == chosenSequence : change.Sequence is { } sequence ? operation.Sequence == sequence
            : change.MachineCode is { } machine ? operation.EligibleMachines.Contains(machine, StringComparer.Ordinal) : operation.EligibleMachines.Count > 0);

    private static RecoveryOption AlternateMachines(ScenarioChange change, SiteSnapshot snapshot, IReadOnlyList<OrderInfo> orders,
        IReadOnlyList<OrderMaterialStatus> material, IReadOnlyDictionary<long, OrderQuality> quality, ScheduleResult proposed,
        IReadOnlyList<OrderInfo> affected, ForgePrincipal principal, IForgeAuthorizer authorizer,
        IReadOnlyList<ScenarioOrderImpact> projections, IReadOnlyList<string> constraints, IReadOnlyList<string> evidence, RecoverySourceFacts? sources)
    {
        var selection = change.RecoverySelections?.SingleOrDefault(s => s.OptionId == "alternate-machine");
        var targetOrders = selection?.OrderNumber is { } selectedNumber ? orders.Where(o => o.OrderNumber == selectedNumber).ToArray() : affected;
        var targets = targetOrders.SelectMany(order => order.Operations.Where(op => MachineTarget(change, selection, op))
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
                (selection?.MachineCode is null || m == selection.MachineCode) &&
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
                blocks.AddRange(RecoveryTrialEvaluator.PlanConstraints(snapshot, schedule, new HashSet<long> { order.OrderNumber }, sources, material, facts));
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
        return new RecoveryOption("alternate-machine", "Alternate Machine", permitted && feasible,
            feasible ? "Each affected operation has an eligible machine with a finite-capacity completion projection under the evaluated facts. Review the candidate dates and consequences before committing a source schedule."
                : "No executable alternate is asserted. Inspect the candidate blockers or supply the missing inputs.",
            ["Candidate dates are planning projections. The source schedule requires a human proposal and approval; current execution gates and assurance are checked again at commit/use."],
            "#/scheduling", Permissions.SchedulePropose, status, affected.Select(o => o.OrderNumber).Order().ToArray(), projections,
            allConstraints, required, evidence, alternatives)
        {
            SourceAuthority = [new("Planning", "ProposeSchedule / ApproveSchedule", Permissions.SchedulePropose, "Schedule", null, permitted,
                "An authorized human proposes a fresh source schedule and a different person approves it; execution guards apply again.")],
        };
    }

    private static IReadOnlyList<string> Blockers(ScheduleResult schedule, IReadOnlySet<long> numbers) =>
        schedule.Operations.Where(o => numbers.Contains(o.OrderNumber) && !o.Scheduled)
            .Select(o => $"Order {o.OrderNumber} operation {o.Sequence}: {o.BindingConstraint}. {o.BindingDetail}")
            .Distinct(StringComparer.Ordinal).ToArray();
}
