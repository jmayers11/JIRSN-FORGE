using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;

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
            var relevant = await uow.Store<IInventoryLookup>().ListTransactionsBetweenAsync(from, until, ct);
            var visible = relevant.Where(t => t.OrderNumber is { } number && allowedOrders.ContainsKey(number)).ToList();
            foreach (var transaction in visible)
            {
                facts.Add(new ShiftOperationalFact("Material", $"transaction:{transaction.TransactionId}", transaction.Kind.ToString(),
                    $"Order {transaction.OrderNumber}: {transaction.Kind} {transaction.Quantity:0.###} {transaction.PartNumber}"));
            }
        }
        else unknowns.Add("Order-linked material movements were not included; inventory access is required.");

        return new ShiftCloseoutFacts(workCenter, shiftCode, from, until, time.GetUtcNow(), production,
            facts.OrderBy(f => f.Kind, StringComparer.Ordinal).ThenBy(f => f.Reference, StringComparer.Ordinal).ToList(), unknowns);
    }
}
