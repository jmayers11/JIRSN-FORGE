using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Engines;
using Forge.Kernel.Holds;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Procurement.Contracts;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Planning;

/// <summary>Each explicit choice is evaluated independently on copies through the real shared engines.</summary>
internal static class RecoveryTrialEvaluator
{
    public static RecoveryOption Evaluate(RecoveryOption option, RecoverySelection selection, SiteSnapshot snapshot,
        IReadOnlyList<OrderInfo> orders, IReadOnlyList<OrderMaterialStatus> material, IReadOnlyDictionary<long, OrderQuality> quality,
        ScheduleResult baseline, RecoverySourceFacts sources, ForgePrincipal principal, IForgeAuthorizer authorizer)
    {
        var required = new List<string>();
        var constraints = new List<string>();
        var evidence = option.Evidence.ToList();
        var consequences = option.Consequences.ToList();
        var authority = option.SourceAuthority.ToList();
        var target = orders.FirstOrDefault(o => o.OrderNumber == selection.OrderNumber);
        var trialSnapshot = snapshot;
        var trialOrders = orders;
        var trialMaterial = material;
        var selectedNumbers = target is null ? new HashSet<long>() : new HashSet<long> { target.OrderNumber };
        var sourceActionOnly = false;
        bool Need(bool present, string input) { if (!present) required.Add(input); return present; }
        void Command(string context, string name, string permission, ResourceRef resource)
        {
            authority.Add(new(context, name, permission, resource.Type, resource.Id,
                authorizer.Authorize(principal, permission, resource).IsPermitted,
                "The authorized human commits this real source command; its scope, assurance and current guards are checked again.")
                { ProgramId = resource.Program?.Value });
        }
        if (option.Id != "partial-shipment")
        {
            if (!Need(selection.OrderNumber is not null, "Select an affected released order.")) return Finish();
            if (target is null) { constraints.Add("The selected released order is unavailable in this actor's scope."); return Finish(); }
        }

        switch (option.Id)
        {
            case "overtime":
            case "additional-shift":
            {
                Need(selection.ShiftCode is not null, "Select a source shift.");
                Need(selection.WorkCenterCode is not null, "Select the target work center.");
                Need(selection.PersonIds is { Count: > 0 }, "Select the workers covering the proposed calendar.");
                if (option.Id == "overtime")
                {
                    Need(selection.ProposedShiftCode is not null, "Name the new extended shift to define through Resources.");
                    Need(selection.AdditionalMinutes is not null, "Specify additional minutes (1 to 240).");
                }
                if (required.Count > 0) break;
                if (!principal.HasPermission(Permissions.WorkforceRead)) { required.Add("Workforce read authority is required to evaluate the selected workers."); break; }
                var shift = snapshot.Shifts.FirstOrDefault(s => s.Code == selection.ShiftCode);
                var center = snapshot.WorkCenters.FirstOrDefault(c => c.Code == selection.WorkCenterCode && c.Active);
                if (shift is null || center is null) { constraints.Add("The selected shift or active work center does not exist."); break; }
                if (!target!.Operations.Any(o => o.Status != OperationStatus.Complete && o.WorkCenterCode == center.Code))
                    constraints.Add("The selected work center has no unfinished operation on the target order.");
                var people = selection.PersonIds!.Distinct().ToArray();
                if (people.Length != selection.PersonIds!.Count || people.Length > 100 || people.Any(id => snapshot.Person(id) is not { Active: true }))
                    constraints.Add("Worker selections must name distinct active workers in the current snapshot (at most 100).");
                var shiftCode = shift.Code;
                if (option.Id == "overtime")
                {
                    if (selection.AdditionalMinutes is < 1 or > 240 || shift.DurationMinutes + selection.AdditionalMinutes > 1440)
                        constraints.Add("The extension must add 1 to 240 minutes and keep the total shift at or below 1440 minutes.");
                    if (snapshot.Shifts.Any(s => s.Code == selection.ProposedShiftCode))
                        constraints.Add("The extended shift code already exists; DefineShift creates a new source calendar.");
                    if (constraints.Count > 0) break;
                    shiftCode = selection.ProposedShiftCode!;
                    trialSnapshot = trialSnapshot with { Shifts = [.. snapshot.Shifts, shift with { Code = shiftCode, DurationMinutes = shift.DurationMinutes + selection.AdditionalMinutes!.Value }] };
                    Command("Resources", "DefineShift", Permissions.ResourcesManage, ResourceRef.Of("Shift", shiftCode));
                }
                else if (snapshot.WorkCenterShifts.Any(s => s.WorkCenterCode == center.Code && s.ShiftCode == shiftCode))
                    constraints.Add("This shift is already assigned to the work center; it does not add a calendar window.");
                if (constraints.Count > 0) break;
                trialSnapshot = trialSnapshot with
                {
                    WorkCenterShifts = [.. trialSnapshot.WorkCenterShifts, new WorkCenterShiftInfo(center.Code, shiftCode)],
                    People = trialSnapshot.People.Select(p => people.Contains(p.PersonId) ? p with { ShiftCode = shiftCode } : p).ToArray(),
                };
                Command("Resources", "AssignWorkCenterShift", Permissions.ResourcesManage, ResourceRef.Of("WorkCenter", center.Code));
                foreach (var person in people) Command("Workforce", "SetWorkerShift", Permissions.WorkforceManage, ResourceRef.Of("Worker", person));
                evidence.Add($"Trial assigns {center.Code} to {shiftCode} and replaces the calendar of {people.Length} selected worker(s); qualifications, unavailability and safety remain unchanged.");
                break;
            }
            case "alternate-personnel":
            {
                Need(selection.PersonId is not null, "Select the alternate worker.");
                Need(selection.Sequence is not null, "Select the affected operation sequence.");
                if (required.Count > 0) break;
                if (!principal.HasPermission(Permissions.WorkforceRead)) { required.Add("Workforce read authority is required to evaluate the selected worker."); break; }
                var person = snapshot.Person(selection.PersonId!.Value);
                var operation = target!.Operations.FirstOrDefault(o => o.Sequence == selection.Sequence && o.Status != OperationStatus.Complete);
                var center = snapshot.WorkCenters.FirstOrDefault(c => c.Code == operation?.WorkCenterCode && c.Active);
                if (person is null || operation is null || center is null) { constraints.Add("The selected worker, unfinished operation or active work center is unavailable."); break; }
                if (!person.Active || !person.CountsAsPresent || person.Presence == PresenceKind.Projected)
                    constraints.Add("The alternate worker needs active, recorded presence evidence; a shift assignment alone is not presence.");
                if (person.ActiveOperation is not null) constraints.Add("The alternate worker has open direct labor; the source operation must be stopped before reassignment.");
                var assignment = new WorkAssignment(center.SafetyAreaCode, center.Code, selection.MachineCode, operation.ActivityCode, false);
                if (selection.MachineCode is { } machine && !operation.EligibleMachines.Contains(machine, StringComparer.Ordinal))
                    constraints.Add("The selected assignment machine is not released eligible on this operation.");
                var preview = EngineRunner.Run(new ReallocationImpactEngine(), snapshot, new ReallocationRequest(person.PersonId, assignment), snapshot.AsOf).Output;
                evidence.AddRange(preview.Impacts.Select(i => $"Reallocation {i.Kind}: {i.Detail}"));
                consequences.AddRange(preview.Impacts.Where(i => i.Kind is ImpactKind.WouldStop or ImpactKind.WouldBeUnstaffed).Select(i => i.Detail));
                constraints.AddRange(preview.Impacts.Where(i => i.Kind == ImpactKind.NewWork && i.Constraint is not null).Select(i => i.Detail));
                if (constraints.Count > 0) break;
                trialSnapshot = snapshot.WithAssignment(person.PersonId, assignment);
                break;
            }
            case "resequence":
                if (!Need(selection.Priority is not null, "Select a new priority from 1 (highest) to 9.")) break;
                if (selection.Priority is < 1 or > 9) { constraints.Add("Priority must be between 1 and 9."); break; }
                trialOrders = orders.Select(o => o.OrderNumber == target!.OrderNumber ? o with { Priority = selection.Priority!.Value } : o).ToArray();
                trialMaterial = Allocate(sources.Material with { Orders = sources.Material.Orders.Select(o => o.OrderNumber == target!.OrderNumber ? o with { Priority = selection.Priority!.Value } : o).ToArray() });
                evidence.Add($"Trial priority {target!.Priority} → {selection.Priority}; full material demand is reallocated before the visible queue is scheduled.");
                break;
            case "approved-alternate":
            case "existing-wip":
            case "remnant-transformation":
            {
                Need(selection.PartNumber is not null && selection.FromLocation is not null && selection.Quantity is not null,
                    "Select stock part, source location and quantity, with its recorded lot identity where applicable.");
                if (option.Id == "approved-alternate")
                    Need(selection.AlternateId is not null && selection.PrimaryPartNumber is not null, "Select the approved alternate relationship and primary component.");
                if (required.Count > 0) break;
                if (!principal.HasPermission(Permissions.InventoryRead)) { required.Add("Inventory read authority is required to evaluate this stock identity."); break; }
                if (sources.SourceChecks.TryGetValue(option.Id, out var sourceIssue))
                {
                    constraints.AddRange(sourceIssue.Constraints);
                    evidence.AddRange(sourceIssue.Evidence);
                    if (!sourceIssue.Passed) break;
                }
                else { required.Add("Run the shared IssueToOrder source preparation against this complete stock selection before feasibility can be asserted."); break; }
                var position = Stock(selection, sources, constraints);
                if (position is null) break;
                if (option.Id == "existing-wip" && position.LocationKind != LocationKind.WipStorage)
                    constraints.Add("The selected source is not identified stock in WIP storage.");
                if (option.Id == "remnant-transformation" && !sources.Transformations.SelectMany(t => t.Lines)
                    .Any(l => l.Role == TransformationRole.Remnant && l.PartNumber == position.PartNumber && l.LotNumber == position.LotNumber))
                    constraints.Add("The selected lot has no recorded remnant transformation in the visible source orders.");
                var primary = option.Id == "approved-alternate" ? selection.PrimaryPartNumber! : position.PartNumber;
                decimal factor = 1;
                if (option.Id == "approved-alternate")
                {
                    if (!principal.HasPermission(Permissions.EngineeringRead)) { required.Add("Engineering read authority is required to evaluate the alternate relationship."); break; }
                    var alternate = Alternates.For(sources.Alternates, primary).FirstOrDefault(a => a.Relationship.AlternateId == selection.AlternateId && a.OfferedPart == position.PartNumber);
                    if (alternate.Relationship is null) { constraints.Add("The selected directional relationship does not offer this part for the primary component."); break; }
                    // Use exactly the context the source IssueToOrder command can establish; unknown conditions never pass.
                    var check = Alternates.Evaluate(alternate.Relationship, new AlternateContext(snapshot.LocalDate(snapshot.AsOf), target!.PartNumber, target.ProgramId));
                    evidence.AddRange(check.Reasons);
                    if (check.Usability != AlternateUsability.Usable) constraints.AddRange(check.Reasons);
                    factor = alternate.Factor;
                }
                constraints.AddRange(snapshot.Stopping(new HoldCheck(HoldActivity.IssueMaterial, target!.OrderNumber, Parts: [position.PartNumber],
                    LotList: position.LotNumber is null ? null : [new HoldLot(position.PartNumber, position.LotNumber)], ProgramId: target.ProgramId)).Select(h => h.Describe()));
                var requiredQuantity = target.Materials.Where(m => m.ComponentPartNumber == primary).Sum(m => m.RequiredQuantity);
                var alreadyIssued = sources.Material.Issued.Where(i => i.OrderNumber == target.OrderNumber && i.PartNumber == primary).Sum(i => i.Quantity);
                var primaryQuantity = decimal.Round(selection.Quantity!.Value / factor, 6, MidpointRounding.AwayFromZero);
                if (requiredQuantity == 0 || alreadyIssued + primaryQuantity > requiredQuantity)
                    constraints.Add("The selected issue must match an order component and cannot exceed its unissued requirement.");
                if (constraints.Count > 0) break;
                // Remove the selected physical quantity from the common pool and issue its converted quantity once.
                var changed = sources.Material with
                {
                    Stock = sources.Material.Stock.Select(s => s.PartNumber == position.PartNumber ? s with { Available = Math.Max(0, s.Available - (position.AvailableForPlanning ? selection.Quantity.Value : 0)) } : s).ToArray(),
                    Issued = [.. sources.Material.Issued, new MaterialIssued(target.OrderNumber, primary, primaryQuantity)],
                };
                trialMaterial = Allocate(changed);
                evidence.Add($"Trial issues {selection.Quantity} {position.PartNumber}/{position.LotNumber ?? "untracked"} from {position.LocationCode} as {primaryQuantity} {primary}; the shared pool is reduced before full material allocation.");
                if (option.Id == "remnant-transformation")
                    consequences.Add($"Released order {target.OrderNumber} routing and reported transformation yield establish the remnant's use. The trial does not create an output identity or a production report.");
                break;
            }
            case "expedited-procurement":
            {
                Need(selection.PurchaseOrder is not null && selection.PurchaseOrderLine is not null && selection.ProposedDate is not null,
                    "Select the approved purchase order, open line and proposed supplier date.");
                if (required.Count > 0) break;
                if (sources.Visibility is null && !principal.HasPermission(Permissions.PurchasingRead)) { required.Add("Purchasing read authority is required to evaluate this source line."); break; }
                var po = sources.PurchaseOrders.FirstOrDefault(p => p.PoNumber == selection.PurchaseOrder && p.Status == PurchaseOrderStatus.Approved);
                if (sources.Visibility is { } sourceAccess)
                {
                    if (po is null || !sourceAccess.CanSeePurchaseOrder(po.PoNumber))
                    {
                        constraints.Add("The selected purchase source is unavailable in this actor's scope.");
                        authority.Clear();
                        authority.Add(new("Procurement", "ChangePromisedDate", Permissions.PurchasingManage, "PurchaseOrder", null, false,
                            "Source authority cannot be established for an unavailable purchase source."));
                        break;
                    }
                    authority.Clear();
                    foreach (var resource in sourceAccess.PurchaseResources[po.PoNumber])
                        Command("Procurement", "ChangePromisedDate", Permissions.PurchasingManage, resource);
                }
                var line = po?.Lines.FirstOrDefault(l => l.LineNumber == selection.PurchaseOrderLine && l.Status == PurchaseLineStatus.Open);
                var key = $"PO{selection.PurchaseOrder}/{selection.PurchaseOrderLine}";
                if (line is null || !sources.Material.Supplies.Any(s => s.Key == key)) { constraints.Add("The selected line is not approved open unreceived supply."); break; }
                if (!material.Any(m => m.OrderNumber == target!.OrderNumber && m.Lines.Any(l => l.Allocations.Any(a => a.Source == key))))
                    constraints.Add("The selected supply is not pegged to this order's current material allocation.");
                if (selection.ProposedDate < sources.Material.Today || selection.ProposedDate >= line.PromisedDate)
                    constraints.Add("An expedited date must be on or after today and earlier than the recorded promise.");
                if (constraints.Count > 0) break;
                trialMaterial = Allocate(sources.Material with { Supplies = sources.Material.Supplies.Select(s => s.Key == key ? s with { DueDate = selection.ProposedDate!.Value } : s).ToArray() });
                evidence.Add($"Hypothetical supplier promise on {key}: {line.PromisedDate:yyyy-MM-dd} → {selection.ProposedDate:yyyy-MM-dd}; supply quantity and technical configuration remain from the approved source line.");
                break;
            }
            case "expedited-material-movement":
            {
                Need(selection.PartNumber is not null && selection.FromLocation is not null && selection.ToLocation is not null && selection.Quantity is not null,
                    "Select the released stock part/lot/quantity and source/destination locations.");
                if (required.Count > 0) break;
                if (!principal.HasPermission(Permissions.InventoryRead)) { required.Add("Inventory read authority is required to evaluate this movement."); break; }
                if (!sources.SourceChecks.TryGetValue(option.Id, out var moveCheck))
                { required.Add("Run the shared TransferStock source preparation for this complete movement selection."); break; }
                constraints.AddRange(moveCheck.Constraints);
                evidence.AddRange(moveCheck.Evidence);
                var position = Stock(selection, sources, constraints);
                var destination = sources.Locations.FirstOrDefault(l => l.Code == selection.ToLocation && l.Active && l.InCompanyCustody && l.PointOfUse);
                if (position is null) break;
                if (destination is null || destination.Code == position.LocationCode) { constraints.Add("Select a different active company point-of-use location."); break; }
                if (!target!.Operations.Any(o => o.Status != OperationStatus.Complete && o.WorkCenterCode == destination.WorkCenterCode)
                    || target.Materials.All(m => m.ComponentPartNumber != position.PartNumber))
                    constraints.Add("The destination must serve the target's unfinished work and the stock must match its material.");
                constraints.AddRange(snapshot.Stopping(new HoldCheck(HoldActivity.IssueMaterial, target.OrderNumber, Parts: [position.PartNumber],
                    LotList: position.LotNumber is null ? null : [new HoldLot(position.PartNumber, position.LotNumber)], ProgramId: target.ProgramId)).Select(h => h.Describe()));
                if (constraints.Count > 0) break;
                var points = sources.OrderChecks[target.OrderNumber].PointOfUse;
                var updated = points.AtPointOfUse.Select(p => p.PartNumber == position.PartNumber && p.LotNumber == position.LotNumber && p.LocationCode == position.LocationCode && p.QualityStatus == StockStatus.Released
                    ? p with { Quantity = p.Quantity - selection.Quantity!.Value } : p).Where(p => p.Quantity > 0).ToList();
                updated.Add(position with { LocationCode = destination.Code, LocationPath = destination.Code, Quantity = selection.Quantity!.Value, PointOfUseWorkCenter = destination.WorkCenterCode, MachineCode = destination.MachineCode });
                sources = sources with { OrderChecks = sources.OrderChecks.ToDictionary(p => p.Key, p => p.Value with { PointOfUse = points with { AtPointOfUse = updated } }) };
                evidence.Add($"Trial moves {selection.Quantity} {position.PartNumber}/{position.LotNumber ?? "untracked"} from {position.LocationCode} to {destination.Code}; no issue or new quantity is assumed.");
                break;
            }
            case "maintenance-timing":
            {
                Need(selection.WorkOrderId is not null && selection.StartsAt is not null && selection.EndsAt is not null,
                    "Select a maintenance work order and proposed start/end.");
                if (required.Count > 0) break;
                if (!principal.HasPermission(Permissions.MaintenanceRead)) { required.Add("Maintenance read authority is required to evaluate this work order."); break; }
                var work = sources.MaintenanceOrders.FirstOrDefault(w => w.WorkOrderId == selection.WorkOrderId);
                if (work is null || work.Status is not (WorkOrderStatus.Open or WorkOrderStatus.Scheduled) || work.MachineCode is null)
                    constraints.Add("Select an open or scheduled machine maintenance work order.");
                if (selection.EndsAt <= selection.StartsAt || selection.EndsAt <= snapshot.AsOf || selection.EndsAt - selection.StartsAt > TimeSpan.FromDays(30))
                    constraints.Add("The proposed source window must end in the future after its start and last at most 30 days.");
                if (work?.DowntimeId is not null)
                    constraints.Add("This work order has recorded downtime; changing its planned repair date cannot remove approved or observed downtime. Resolve the source window through maintenance before a replacement can be asserted.");
                if (constraints.Count > 0) break;
                trialSnapshot = snapshot with { Downtime = [.. snapshot.Downtime, new DowntimeWindow(long.MinValue + 1, work!.MachineCode!, DowntimeKind.Planned,
                    selection.StartsAt!.Value, selection.EndsAt, "Recovery trial: proposed maintenance timing", true, "Planning", $"WorkOrder/{work.WorkOrderId}")] };
                evidence.Add($"Trial adds proposed maintenance work order {work.WorkOrderId} window {selection.StartsAt:u} to {selection.EndsAt:u}; existing downtime and PM lockouts remain.");
                break;
            }
            case "outside-processing":
            case "partial-shipment":
            {
                if (!principal.HasPermission(Permissions.InventoryRead)) required.Add("Inventory read authority is required for the selected source action.");
                if (option.Id == "outside-processing" && !principal.HasPermission(Permissions.PurchasingRead)) required.Add("Purchasing read authority is required for the approved service purchase line.");
                if (option.Id == "partial-shipment" && !principal.HasPermission(Permissions.QualityRead)) required.Add("Quality read authority is required for shipping release readiness.");
                if (!sources.SourceChecks.TryGetValue(option.Id, out var check))
                { required.AddRange(RecoveryCatalog.Options.Single(d => d.Id == option.Id).Inputs); break; }
                constraints.AddRange(check.Constraints);
                evidence.AddRange(check.Evidence);
                sourceActionOnly = true;
                consequences.Add(option.Id == "outside-processing"
                    ? "Feasibility describes the dispatch command only. Completion of the outsourced process and its effect on delivery need a controlled routing and released returned material; no order completion benefit is asserted."
                    : "Feasibility describes release of this already packed partial shipment. Remaining quantities and delivery dates are not rewritten by the trial.");
                break;
            }
            case "alternate-work-center":
                Need(selection.Sequence is not null && selection.WorkCenterCode is not null, "Select the operation and proposed alternate work center.");
                if (required.Count == 0)
                    constraints.Add("The proposed center has no source-authorized replacement routing in this scenario. The released operation has one controlled work center; an authorized human must release an engineering change and apply its routing through ApplyChangeRevision. Configured capacity cannot establish this source authority.");
                break;
            case "split-production":
                Need(selection.SplitQuantities is { Count: >= 2 }, "Specify at least two split production quantities.");
                if (required.Count > 0) break;
                if (selection.SplitQuantities!.Any(q => q <= 0) || selection.SplitQuantities!.Sum() != target!.Quantity)
                    constraints.Add("Every split quantity must be positive and their sum must equal the released order quantity.");
                constraints.Add("No controlled split-production source plan with separately released routing quantities and traceable delivery commitments is present. Existing CreateProductionOrder cannot resize this released order. An authorized human must review cancel/create/release source steps and reconcile issued material and recorded production before such a split can be executable.");
                break;
            default:
                constraints.Add("This selected recovery option has no supported trial.");
                break;
        }
        if (required.Count > 0 || constraints.Count > 0 || sourceActionOnly) return Finish();
        var candidate = EngineRunner.Run(new SchedulingEngine(), trialSnapshot,
            new ScheduleRequest(baseline.From, baseline.To, trialOrders, trialMaterial, quality), snapshot.AsOf).Output;
        constraints.AddRange(PlanConstraints(trialSnapshot, candidate, selectedNumbers, sources, trialMaterial, evidence));
        var projections = Impacts(baseline, candidate, selectedNumbers);
        consequences.AddRange(projections.Select(p => $"Order {p.OrderNumber}: {p.BaselineStatus} at {p.BaselineCompletion:u} → {p.ProposedStatus} at {p.ProposedCompletion:u}; shift {p.ShiftHours?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"} hour(s)."));
        evidence.Add("The independent trial uses full material allocation and the full visible finite-capacity queue. Documentation, training, tooling, point of use, active equipment, holds, qualification and safety checks remain binding.");
        return Finish(projections);

        IReadOnlyList<OrderMaterialStatus> Allocate(MaterialSnapshot input)
        {
            var allocated = EngineRunner.Run(new MaterialEngine(), input, new MaterialRequest(), snapshot.AsOf).Output;
            return sources.Visibility?.ForEvaluation(allocated) ?? allocated;
        }
        RecoveryOption Finish(IReadOnlyList<ScenarioOrderImpact>? projections = null)
        {
            var permitted = authority.All(a => a.Permitted);
            var feasible = permitted && required.Count == 0 && constraints.Count == 0;
            var status = !permitted ? "NotAuthorized" : required.Count > 0 ? "RequiresInputs" : feasible ? "Feasible" : "Infeasible";
            if (!permitted) required.AddRange(authority.Where(a => !a.Permitted).Select(a => $"Source command {a.Command} requires {a.Permission} on {a.ResourceType}/{a.ResourceId ?? "selection"}."));
            return option with
            {
                Feasible = feasible, Status = status, RequiredInputs = required.Distinct(StringComparer.Ordinal).ToArray(),
                Feasibility = feasible ? (sourceActionOnly ? "The selected source action passes its shared read-only command gates under the current facts. Review its stated scope and consequences before a human commits."
                    : "The complete selected trial has a finite-capacity completion projection and passes the evaluated source and execution gates. Delivery risk remains explicit in its dates.")
                    : status == "RequiresInputs" ? "No executable recovery is asserted until the listed source choices are supplied."
                    : status == "NotAuthorized" ? "The actor lacks one or more source command permissions; no executable recovery is authorized."
                    : "The complete selected trial is infeasible under the recorded constraints.",
                AffectedOrders = selectedNumbers.Order().ToArray(), Projections = projections ?? option.Projections,
                Constraints = constraints.Distinct(StringComparer.Ordinal).ToArray(), Evidence = evidence, Consequences = consequences,
                SourceAuthority = authority,
            };
        }
    }

    private static StockPosition? Stock(RecoverySelection selection, RecoverySourceFacts sources, List<string> constraints)
    {
        if (selection.Quantity <= 0) { constraints.Add("Stock quantity must be positive."); return null; }
        var position = sources.Positions.FirstOrDefault(p => p.PartNumber == selection.PartNumber && p.LotNumber == selection.LotNumber
            && p.LocationCode == selection.FromLocation && p.QualityStatus == StockStatus.Released);
        if (position is null) { constraints.Add("The selected released stock identity is unavailable."); return null; }
        var location = sources.Locations.FirstOrDefault(l => l.Code == position.LocationCode);
        if (!position.Usable || position.Custody != Custody.Company || location is not { Active: true })
            constraints.Add("The selected stock must be usable by shelf life in active company custody.");
        var reserved = sources.Tasks.Where(t => t.PartNumber == position.PartNumber && t.LotNumber == position.LotNumber && t.FromLocation == position.LocationCode).Sum(t => t.Reserved);
        if (position.Quantity - reserved < selection.Quantity) constraints.Add("The selected quantity exceeds unreserved released stock at that location and lot.");
        return position;
    }

    internal static IReadOnlyList<string> PlanConstraints(SiteSnapshot snapshot, ScheduleResult schedule, IReadOnlySet<long> numbers,
        RecoverySourceFacts? sources, IReadOnlyList<OrderMaterialStatus> material, List<string> evidence)
    {
        var constraints = schedule.Operations.Where(o => numbers.Contains(o.OrderNumber) && !o.Scheduled)
            .Select(o => $"Order {o.OrderNumber} operation {o.Sequence}: {o.BindingConstraint}. {o.BindingDetail}").ToList();
        foreach (var order in schedule.Orders.Where(o => numbers.Contains(o.OrderNumber) && o.ProjectedCompletion is null))
            constraints.Add($"Order {order.OrderNumber} has no completion within the evaluated horizon.");
        foreach (var operation in schedule.Operations.Where(o => numbers.Contains(o.OrderNumber) && o.Scheduled))
        {
            if (!snapshot.WorkCenters.Any(w => w.Code == operation.WorkCenterCode && w.Active)) constraints.Add($"Operation {operation.Sequence}: work center is inactive or missing.");
            if (operation.MachineCode is { } machine && !snapshot.Machines.Any(m => m.Code == machine && m.Active)) constraints.Add($"Operation {operation.Sequence}: machine is inactive or missing.");
            if (sources?.OrderChecks.TryGetValue(operation.OrderNumber, out var checks) == true)
            {
                if (checks.Revision is null || !checks.Revision.IsEffectiveAt(operation.Start!.Value))
                {
                    if (checks.Order is { } exactOrder && checks.Continuation?.Permits(exactOrder, checks.Revision, operation.Start!.Value) == true)
                        evidence.Add($"Order {operation.OrderNumber}: recorded CompleteOld WIP disposition permits its released revision at the projected start.");
                    else constraints.Add($"Order {operation.OrderNumber}: released engineering revision is not effective at the projected start and no exact authorized CompleteOld disposition permits continuation.");
                }
                Add(checks.Documentation.Enforced ? checks.Documentation.DimensionFor(operation.Sequence) : null);
                var first = schedule.Operations.Where(o => o.OrderNumber == operation.OrderNumber).OrderBy(o => o.Sequence).First();
                if (first.Sequence == operation.Sequence) Add(ExecutableChecks.PointOfUse(checks.PointOfUse, material.FirstOrDefault(m => m.OrderNumber == operation.OrderNumber), operation.WorkCenterCode));
                foreach (var segment in operation.Segments)
                    Add(ExecutableChecks.Training(checks.Training, operation.Sequence, segment.Operators.Select(id => snapshot.Person(id)!).ToArray(), Math.Max(1, segment.Operators.Count)));
            }
            foreach (var at in operation.Segments.SelectMany(s => s.End > s.Start ? new[] { s.Start, s.End.AddTicks(-1) } : [s.Start]).Distinct())
                Add(ExecutableChecks.Tooling(snapshot, operation.MachineCode, operation.ActivityCode, at));
            void Add(ReadinessDimension? check)
            {
                if (check is null) return;
                if (check.Outcome is EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate) constraints.Add($"Order {operation.OrderNumber} operation {operation.Sequence} {check.Name}: {check.Detail}");
                else evidence.Add($"Order {operation.OrderNumber} operation {operation.Sequence} {check.Name}: {check.Detail}");
            }
        }
        return constraints.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<ScenarioOrderImpact> Impacts(ScheduleResult before, ScheduleResult after, IReadOnlySet<long> targets) =>
        after.Orders.Select(b => (Before: before.Orders.First(a => a.OrderNumber == b.OrderNumber), After: b))
            .Where(x => targets.Contains(x.After.OrderNumber) || x.Before.ProjectedCompletion != x.After.ProjectedCompletion || x.Before.Status != x.After.Status)
            .Select(x => new ScenarioOrderImpact(x.After.OrderNumber, x.After.PartNumber,
                x.Before.ProjectedCompletion == x.After.ProjectedCompletion && x.Before.Status == x.After.Status ? "Unchanged" : "Changed",
                x.Before.ProjectedCompletion, x.After.ProjectedCompletion,
                x.Before.ProjectedCompletion is { } a && x.After.ProjectedCompletion is { } b ? (decimal?)(decimal)(b - a).TotalHours : null,
                x.Before.Status.ToString(), x.After.Status.ToString(), [x.Before.Explanation, x.After.Explanation])).ToArray();
}
