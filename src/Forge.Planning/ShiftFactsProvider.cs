using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Holds;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Planning;

/// <summary>Builds an access-filtered shift snapshot from existing domain records without copying them into Execution.</summary>
public sealed class ShiftFactsProvider(IForgeAuthorizer authorizer, TimeProvider time) : IShiftFactsProvider
{
    public async Task<ShiftCloseoutFacts> BuildAsync(IKernelUnitOfWork uow, string workCenter, string shiftCode, DateTimeOffset from, DateTimeOffset until, ForgePrincipal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(principal);
        var resource = new ResourceRef("WorkCenter", workCenter, WorkCenterId: workCenter, ShiftId: shiftCode);
        bool Can(string permission) => authorizer.Authorize(principal, permission, resource).IsPermitted;
        var facts = new List<ShiftOperationalFact>();
        var unknowns = new List<string>();
        var allowedOrders = new Dictionary<long, OrderInfo>();
        var relevantLots = new HashSet<(string Part, string Lot)>();
        var orderLookup = uow.Store<IExecutionLookup>();
        if (Can(Permissions.OrdersRead))
        {
            foreach (var summary in await orderLookup.ListOrdersAsync(ct))
            {
                var order = await orderLookup.FindOrderAsync(summary.OrderNumber, ct) ?? summary;
                if (order.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program))) continue;
                var workCenterOperations = order.Operations.Where(o => o.WorkCenterCode == workCenter).ToList();
                if (workCenterOperations.Count == 0) continue;
                allowedOrders[order.OrderNumber] = order;
                if (order.Status is not (OrderStatus.Completed or OrderStatus.Cancelled))
                    facts.Add(new ShiftOperationalFact("Priority", $"order:{order.OrderNumber}:priority", "Current",
                        $"Order {order.OrderNumber} {order.PartNumber}: current priority {order.Priority}", order.ProgramId, Priority: order.Priority));
                foreach (var operation in workCenterOperations.Where(o => o.Status != OperationStatus.Complete))
                {
                    facts.Add(new ShiftOperationalFact("WorkInProgress", $"order:{order.OrderNumber}:operation:{operation.Sequence}", operation.Status.ToString(),
                        $"Order {order.OrderNumber} {order.PartNumber}, operation {operation.Sequence}: {operation.Description}", order.ProgramId));
                }
            }
        }
        else unknowns.Add("Work orders and WIP were not included; schedule access is required.");

        var production = new ShiftProductionTotals(0, 0, 0, 0, 0);
        if (Can(Permissions.OrdersRead))
        {
            var reports = (await orderLookup.ListReportsAsync(from, until, null, ct))
                .Where(r => r.WorkCenterCode == workCenter && allowedOrders.ContainsKey(r.OrderNumber)).ToList();
            foreach (var report in reports)
            {
                facts.Add(new ShiftOperationalFact("ProductionReport", $"report:{report.ReportId}", report.CompletedOperation ? "Complete" : "Reported",
                    $"Order {report.OrderNumber}, operation {report.Sequence}: {report.GoodQuantity:0.###} good and {report.ScrapQuantity:0.###} scrap", allowedOrders[report.OrderNumber].ProgramId,
                    report.GoodQuantity, report.ScrapQuantity, report.CompletedOperation));
            }

            production = new ShiftProductionTotals(reports.Count, reports.Sum(r => r.GoodQuantity), reports.Sum(r => r.ScrapQuantity),
                reports.Count(r => r.CompletedOperation), facts.Count(f => f.Kind == "WorkInProgress"));
            foreach (var group in reports.GroupBy(r => (r.OrderNumber, r.Sequence)))
            {
                if (group.Sum(r => r.GoodQuantity + r.ScrapQuantity) > allowedOrders[group.Key.OrderNumber].Quantity)
                    facts.Add(new ShiftOperationalFact("Reconciliation", $"order:{group.Key.OrderNumber}:operation:{group.Key.Sequence}", "Review",
                        "Reported good and scrap quantities exceed the order quantity; preserve the recorded values and investigate.", allowedOrders[group.Key.OrderNumber].ProgramId));
            }

            var labor = await orderLookup.ListLaborAsync(from, until, null, ct);
            foreach (var entry in labor.Where(l => l.WorkCenterCode == workCenter && allowedOrders.ContainsKey(l.OrderNumber)))
                facts.Add(new ShiftOperationalFact("DirectLabor", $"labor:{entry.LaborId}", entry.Role.ToString(),
                    $"Person {entry.PersonId:D} recorded on order {entry.OrderNumber}, operation {entry.Sequence}", allowedOrders[entry.OrderNumber].ProgramId));
        }

        var machines = (await uow.Store<IResourcesLookup>().ListMachinesAsync(ct)).Where(m => m.Active && m.WorkCenterCode == workCenter).Select(m => m.Code).ToHashSet(StringComparer.Ordinal);
        if (Can(Permissions.MaintenanceRead))
        {
            var maintenance = uow.Store<IMaintenanceLookup>();
            var stoppages = await maintenance.ListDowntimeAsync(from, until, ct);
            foreach (var window in stoppages.Where(w => machines.Contains(w.MachineCode)))
            {
                facts.Add(new ShiftOperationalFact("Downtime", $"machine:{window.MachineCode}:downtime:{window.StartsAt:O}", window.EndsAt is null ? "Open" : "Recorded",
                    $"{window.MachineCode} {window.Kind}: {window.Reason}; {Math.Round((decimal)((window.EndsAt ?? time.GetUtcNow()) - window.StartsAt).TotalMinutes, 1):0.#} minutes"));
            }

            var workOrders = await maintenance.ListWorkOrdersAsync(false, ct);
            foreach (var workOrder in workOrders.Where(w => w.MachineCode is { } machine && machines.Contains(machine)))
                facts.Add(new ShiftOperationalFact("Maintenance", $"work-order:{workOrder.WorkOrderId}", workOrder.Status.ToString(), $"{workOrder.MachineCode}: {workOrder.Title}"));
        }
        else unknowns.Add("Downtime and maintenance details were not included; maintenance access is required.");

        if (Can(Permissions.WorkforceRead))
        {
            var resources = uow.Store<IResourcesLookup>();
            var center = await resources.FindWorkCenterAsync(workCenter, ct);
            var area = center is null ? null : await resources.FindSafetyAreaAsync(center.SafetyAreaCode, ct);
            if (area is null)
            {
                unknowns.Add("Worker availability could not be associated with the work center's site and date.");
            }
            else
            {
                var shiftDate = SiteTime.DateAt(from, await resources.SiteTimeZoneAsync(area.SiteCode, ct));
                var owners = (await uow.Store<IWorkforceLookup>().ListWorkCenterPeriodsAsync(ct))
                    .Where(period => period.WorkCenterCode == workCenter && period.Covers(shiftDate))
                    .Select(period => period.PersonId).ToHashSet();
                var windows = await uow.Store<IWorkforceLookup>().ListUnavailabilityAsync(from, until, ct);
                foreach (var window in windows.Where(window => owners.Contains(window.PersonId)
                    && window.StartsAt < until && window.EndsAt > from))
                {
                    facts.Add(new ShiftOperationalFact("WorkerAvailability", $"worker:{window.PersonId:D}:unavailability:{window.StartsAt:O}", window.Kind.ToString(),
                        $"Worker {window.PersonId:D} has recorded {window.Kind} unavailability overlapping this shift."));
                }
            }
        }
        else unknowns.Add("Recorded worker unavailability was not included; workforce access is required.");

        if (Can(Permissions.ToolsHandle) || Can(Permissions.ToolsManage))
        {
            var relevantTools = (await uow.Store<IToolLookup>().ListToolsAsync(ct))
                .Where(t => t.Status != ToolStatus.Retired && (t.Location == workCenter || t.EligibleMachines.Any(machines.Contains)))
                .OrderBy(t => t.Code, StringComparer.Ordinal);
            foreach (var tool in relevantTools)
                facts.Add(new ShiftOperationalFact("Tooling", $"tool:{tool.Code}", tool.Status.ToString(),
                    $"{tool.Code} ({tool.TypeCode}) at {tool.Location ?? "unlocated"}: {tool.Condition}, {tool.Custody}; calibration due {tool.CalibrationDue?.ToString("yyyy-MM-dd") ?? "not recorded"}"));
        }
        else unknowns.Add("Tool custody and service state were not included; tool access is required.");

        if (Can(Permissions.QualityRead))
        {
            foreach (var ncr in await uow.Store<IQualityLookup>().ListNcrsAsync(false, ct))
            {
                if (ncr.OrderNumber is { } orderNumber && allowedOrders.TryGetValue(orderNumber, out var order))
                    facts.Add(new ShiftOperationalFact("QualityIssue", $"ncr:{ncr.NcrId}", ncr.Status.ToString(),
                        $"NCR {ncr.NcrId} on order {orderNumber}: {ncr.Defect} ({ncr.Status})", order.ProgramId));
            }
        }
        else unknowns.Add("Open quality issues were not included; quality access is required.");

        if (Can(Permissions.InventoryRead))
        {
            var inventory = uow.Store<IInventoryLookup>();
            var relevant = await inventory.ListTransactionsBetweenAsync(from, until, ct);
            var visible = relevant.Where(t => t.OrderNumber is { } number && allowedOrders.ContainsKey(number)).ToList();
            foreach (var transaction in visible)
            {
                facts.Add(new ShiftOperationalFact("Material", $"transaction:{transaction.TransactionId}", transaction.Kind.ToString(),
                    $"Order {transaction.OrderNumber}: {transaction.Kind} {transaction.Quantity:0.###} {transaction.PartNumber}", allowedOrders[transaction.OrderNumber!.Value].ProgramId));
            }

            var locations = (await inventory.ListLocationsAsync(ct)).Where(l => l.WorkCenterCode == workCenter || l.MachineCode is { } machine && machines.Contains(machine))
                .Select(l => l.Code).ToHashSet(StringComparer.Ordinal);
            foreach (var task in await inventory.ListMaterialTasksAsync(true, null, ct))
            {
                if (task.OrderNumber is { } number ? !allowedOrders.ContainsKey(number) : !locations.Contains(task.ToLocation)) continue;
                var program = task.OrderNumber is { } orderNumber ? allowedOrders[orderNumber].ProgramId : null;
                if (task.LotNumber is { } taskLot) relevantLots.Add((task.PartNumber, taskLot));
                facts.Add(new ShiftOperationalFact("MaterialRequest", $"material-task:{task.TaskId}", task.Status.ToString(),
                    $"Open {task.Kind} request: {task.Quantity:0.###} {task.PartNumber}{(task.LotNumber is null ? "" : $" lot {task.LotNumber}")} to {task.ToLocation}{(task.OrderNumber is null ? "" : $" for order {task.OrderNumber}")}; picked {task.PickedQuantity?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "not recorded"}", program));
            }

            // Movements within the shift and outstanding material at capture are distinct facts. The
            // cumulative equation must include earlier issues/returns and the latest remaining observation.
            var transactions = (await inventory.ListGenealogyTransactionsAsync(ct))
                .Where(t => t.OrderNumber is { } number && allowedOrders.ContainsKey(number)).ToLookup(t => t.OrderNumber!.Value);
            var transformations = (await inventory.ListTransformationsAsync(null, null, null, ct)).ToLookup(t => t.OrderNumber);
            var tolerances = (await inventory.ListYieldTolerancesAsync(ct)).ToDictionary(t => t.PartNumber, t => t.Percent, StringComparer.Ordinal);
            var lots = (await inventory.ListLotsAsync(null, ct)).ToDictionary(l => (l.PartNumber, l.LotNumber));
            foreach (var order in allowedOrders.Values)
            {
                foreach (var row in MaterialReconciliation.For(order.OrderNumber, transactions[order.OrderNumber], transformations[order.OrderNumber], part => tolerances.GetValueOrDefault(part)))
                {
                    var identity = row.LotNumber is { } lot && lots.TryGetValue((row.PartNumber, lot), out var instance) ? instance.IdentityKind.ToString() : null;
                    if (row.LotNumber is { } issuedLot) relevantLots.Add((row.PartNumber, issuedLot));
                    var recorded = row.Status != ReconciliationStatus.NotRecorded;
                    var material = new ShiftMaterialBalance(row.OrderNumber, row.PartNumber, row.LotNumber, row.Issued, row.Returned, recorded ? row.Consumed : null,
                        recorded ? row.Scrap : null, recorded ? row.Loss : null, recorded ? row.Remnant : null, recorded ? row.Remaining : null,
                        row.TolerancePercent, recorded ? row.Unexplained : null, identity);
                    var reference = $"order:{row.OrderNumber}:material:{row.PartNumber}:{row.LotNumber ?? "(no lot)"}";
                    var summary = row.Status == ReconciliationStatus.NotRecorded
                        ? $"Cumulative order {row.OrderNumber}, {row.PartNumber}{(row.LotNumber is null ? "" : $" {identity ?? "lot"} {row.LotNumber}")}: {row.Issued:0.###} issued and {row.Returned:0.###} returned; consumed, scrap, loss, remnant and remaining quantities have not been recorded."
                        : $"Cumulative order {row.OrderNumber}, {row.PartNumber}{(row.LotNumber is null ? "" : $" {identity ?? "lot"} {row.LotNumber}")}: {row.Issued:0.###} issued = {row.Consumed:0.###} consumed + {row.Returned:0.###} returned + {row.Scrap:0.###} scrap + {row.Loss:0.###} loss + {row.Remnant:0.###} remnant + {row.Remaining:0.###} remaining + {row.Unexplained:0.###} unexplained; tolerance {row.TolerancePercent:0.###}%";
                    facts.Add(new ShiftOperationalFact("MaterialReconciliation", reference, row.Status.ToString(),
                        summary,
                        order.ProgramId, MaterialBalance: material));
                    if (row.Status == ReconciliationStatus.Unexplained)
                        facts.Add(new ShiftOperationalFact("Reconciliation", reference, row.Status.ToString(),
                            $"Order {row.OrderNumber}, {row.PartNumber}{(row.LotNumber is null ? "" : $" lot {row.LotNumber}")}: {row.Unexplained:0.###} requires investigation; preserve the recorded quantities and do not infer scrap.",
                            order.ProgramId, MaterialBalance: material));
                }
            }
        }
        else unknowns.Add("Material movements, open requests and cumulative reconciliation were not included; inventory access is required.");

        if (Can(Permissions.HoldsRead))
        {
            var parts = allowedOrders.Values.SelectMany(o => o.Materials.Select(m => m.ComponentPartNumber).Prepend(o.PartNumber)).ToHashSet(StringComparer.Ordinal);
            foreach (var hold in await uow.Store<IHoldLookup>().ListHoldsAsync(false, ct))
            {
                Guid? program = null;
                var relevant = hold.TargetType switch
                {
                    HoldTargetType.Order => long.TryParse(hold.TargetId, out var number) && allowedOrders.ContainsKey(number),
                    HoldTargetType.Operation => hold.TargetId.Split('/') is [var orderText, var sequenceText]
                        && long.TryParse(orderText, out var number) && int.TryParse(sequenceText, out var sequence)
                        && allowedOrders.TryGetValue(number, out var order) && order.Operations.Any(o => o.Sequence == sequence && o.WorkCenterCode == workCenter),
                    HoldTargetType.Part => parts.Contains(hold.TargetId),
                    HoldTargetType.Lot => relevantLots.Any(l => l.Lot == hold.TargetId && (hold.TargetPart is null || l.Part == hold.TargetPart)),
                    HoldTargetType.WorkCenter => hold.TargetId == workCenter,
                    HoldTargetType.Machine => machines.Contains(hold.TargetId),
                    HoldTargetType.Program => Guid.TryParse(hold.TargetId, out var id) && allowedOrders.Values.Any(o => o.ProgramId == id),
                    _ => false,
                };
                if (!relevant) continue;
                if (hold.TargetType is HoldTargetType.Order or HoldTargetType.Operation)
                    program = allowedOrders[long.Parse(hold.TargetId.Split('/')[0], System.Globalization.CultureInfo.InvariantCulture)].ProgramId;
                else if (hold.TargetType == HoldTargetType.Program) program = Guid.Parse(hold.TargetId);
                facts.Add(new ShiftOperationalFact(hold.Kind == HoldKind.Quality ? "QualityHold" : "Hold", $"hold:{hold.HoldId}", "Open",
                    $"{hold.Describe()}; source {hold.Source}; resolution required: {hold.RequiredResolution}", program));
            }
        }
        else unknowns.Add("Generalized holds were not included; hold-register access is required.");

        return new ShiftCloseoutFacts(workCenter, shiftCode, from, until, time.GetUtcNow(), production,
            facts.OrderBy(f => f.Kind, StringComparer.Ordinal).ThenBy(f => f.Reference, StringComparer.Ordinal).ToList(), unknowns);
    }
}
