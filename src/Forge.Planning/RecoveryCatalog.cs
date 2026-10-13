using Forge.Kernel.Security;

namespace Forge.Planning;

internal sealed record RecoveryDefinition(string Id, string Title, string Context, string Command, string Permission,
    string ResourceType, string Path, string Consequence, string CommitRequirement, params string[] Inputs);

/// <summary>The fifteen options in the recovered V3 brief, section BG, in their original order.</summary>
internal static class RecoveryCatalog
{
    private const string Human = "An authorized human invokes the source command with fresh source evidence; this trial does not invoke it.";
    public static IReadOnlyList<RecoveryDefinition> Options { get; } =
    [
        new("overtime", "Overtime", "Resources", "DefineShift / AssignWorkCenterShift / SetWorkerShift", Permissions.ResourcesManage, "Shift", "#/plant-setup",
            "Extra calendar time requires qualified staffing; displaced worker calendars and all existing execution gates still apply.", Human,
            "Select order, source shift, new shift code, additional minutes, work center and the workers covering the extended shift."),
        new("additional-shift", "Additional Shift", "Resources", "AssignWorkCenterShift / SetWorkerShift", Permissions.ResourcesManage, "WorkCenter", "#/plant-setup",
            "Another shift changes calendar coverage and worker availability; it does not create material, qualification or safe staffing.", Human,
            "Select order, a defined additional shift, target work center and workers assigned to cover it."),
        new("alternate-personnel", "Alternate Qualified Personnel", "Workforce", "AssignWork", Permissions.WorkforceSupervise, "Worker", "#/workforce",
            "The shared reallocation preview identifies source work that would stop or become unstaffed; the queue shows displaced commitments.", Human,
            "Select order, operation and qualified worker; review presence, safety, qualification expiry and source assignment impact."),
        new("alternate-machine", "Alternate Machine", "Planning", "ProposeSchedule / ApproveSchedule", Permissions.SchedulePropose, "Schedule", "#/scheduling",
            "An eligible machine is still subject to finite capacity, downtime, holds, tooling, crew and material. A different person approves the source schedule.", Human,
            "Select order and operation, and a released eligible machine or review the evaluated eligible candidates."),
        new("alternate-work-center", "Alternate Work Center", "Execution", "ApplyChangeRevision", Permissions.OrdersRelease, "ProductionOrder", "#/engineering",
            "Moving work changes its controlled routing, machine profiles, documentation and staffing. A configured activity alone is not an approved routing alternate.", Human,
            "Select order, operation, alternate work center and a released engineering change authorizing the replacement routing."),
        new("approved-alternate", "Approved Alternate Material", "Inventory", "IssueToOrder", Permissions.InventoryTransact, "Stock", "#/inventory",
            "The approved directional conversion consumes real stock once, retains genealogy and remains subject to relationship conditions, holds and shelf life.", Human,
            "Select order, primary component, approved alternate relationship, released stock part/lot/location and quantity."),
        new("existing-wip", "Existing WIP", "Inventory", "IssueToOrder", Permissions.InventoryTransact, "Stock", "#/inventory",
            "Only identified released usable WIP can satisfy matching material; its existing allocation and configuration cannot be assumed away.", Human,
            "Select order, matching WIP part, lot, source location and quantity."),
        new("remnant-transformation", "Remnant Transformation", "Inventory", "IssueToOrder / RecordTransformation", Permissions.InventoryTransact, "Stock", "#/inventory",
            "A recorded remnant keeps its source genealogy and inherited age. The released transform order must establish what it can become; no unrecorded yield or output is invented.", Human,
            "Select a recorded remnant part/lot/location/quantity and the visible released transform order whose routing uses it."),
        new("split-production", "Split Production", "Execution", "CreateProductionOrder / ReleaseProductionOrder", Permissions.OrdersManage, "ProductionOrder", "#/orders",
            "Split lots require controlled routing quantities, material accounting and commitment traceability. Independently possible sub-builds do not establish an accepted split plan.", Human,
            "Select the order, split quantities and source split plan with approved routing and commitment traceability."),
        new("resequence", "Resequencing", "Execution", "ChangeOrderPriority", Permissions.OrdersManage, "ProductionOrder", "#/orders",
            "The full visible queue shows other commitments moved by the priority change. The source priority command requires a reason, fresh impact fingerprint and any required approval.", Human,
            "Select order and new priority from 1 (highest) to 9."),
        new("expedited-procurement", "Expedited Procurement", "Procurement", "ChangePromisedDate", Permissions.PurchasingManage, "PurchaseOrder", "#/procurement",
            "A proposed date remains hypothetical until a supplier-confirmed promise is recorded. Full material allocation prevents the same supply being counted twice.", Human,
            "Select order, approved open purchase line pegged to its material and proposed supplier date; confirm the supplier's evidence at commit."),
        new("expedited-material-movement", "Expedited Material Movement", "Inventory", "TransferStock", Permissions.InventoryTransact, "Stock", "#/inventory",
            "Moving a released stock identity changes location and custody without creating stock or clearing its holds; point-of-use readiness is evaluated separately from MRP quantity.", Human,
            "Select order, released part/lot/quantity, company source location and target point-of-use location."),
        new("outside-processing", "Outside Processing", "Inventory", "DispatchOutsideProcess / ReturnFromOutsideProcess", Permissions.InventoryTransact, "OutsideProcess", "#/inventory",
            "Dispatch retains ownership and identity. A promised return does not become usable supply before certificate and incoming-inspection release; local operations are not silently removed.", Human,
            "Select order, part/lot/quantity, company source and processor locations, approved service PO/line, expected return and applicable process specification."),
        new("maintenance-timing", "Maintenance Timing Change", "Maintenance", "ScheduleWorkOrder / DecideOutage", Permissions.MaintenancePlan, "WorkOrder", "#/maintenance",
            "A new maintenance window needs independent outage approval. Existing approved downtime, PM lockouts and observed failures remain binding until their real source commands change them.", Human,
            "Select open maintenance work order, proposed start and end; existing approved or observed downtime cannot be deleted by this trial."),
        new("partial-shipment", "Partial Shipment", "Inventory", "ShipShipment", Permissions.ShippingRelease, "Shipment", "#/shipping",
            "The selected packed shipment leaves the remaining delivery demand outstanding. Source shipping checks stock, quality, shelf life, packaging, holds and separation of duties again.", Human,
            "Select a packed partial shipment linked to a visible open delivery demand; customer acceptance remains a human commitment decision."),
    ];
}
