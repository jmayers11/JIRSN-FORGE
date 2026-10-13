using System.Globalization;
using Forge.Competency.Contracts;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Engines;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Programs.Contracts;
using Forge.Quality.Contracts;
using Forge.Procurement.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Planning.Reporting;

/// <summary>
/// The standard report catalog (spec §54). Each report reads one snapshot through other contexts'
/// contracts, applies the reader's program access to every row and records where each row came from.
/// </summary>
internal static class StandardReports
{
    private static readonly ReportFilter FromFilter = new("from", "From", "date");
    private static readonly ReportFilter ToFilter = new("to", "To", "date");
    private static readonly ReportFilter ProgramFilter = new("programId", "Program", "program");
    private static readonly ReportFilter WorkCenterFilter = new("workCenter", "Work center", "text");
    private static readonly ReportFilter MachineFilter = new("machine", "Machine", "text");
    private static readonly ReportFilter PartFilter = new("partNumber", "Part", "text");
    private static readonly ReportFilter SearchFilter = new("search", "Contains", "text");
    private static readonly ReportFilter ShiftFilter = new("shift", "Shift", "shift");

    private static ReportFilter StatusFilter(params string[] values) => new("status", "Status", "choice:" + string.Join('|', values));

    public static IReadOnlyList<ReportSpec> All { get; } =
    [
        new(
            new("production-output", "Plan vs actual output", "Production",
                "Per order operation: the quantity the approved schedule planned to finish in the period against good and scrap quantities the shop floor reported in it.",
                [FromFilter, ToFilter, ProgramFilter, WorkCenterFilter, PartFilter, ShiftFilter, StatusFilter("On plan", "Short", "Not reported", "Unplanned", "Cancelled"), SearchFilter],
                [C("date", "Date", ReportColumnKind.Date), C("order", "Order", ReportColumnKind.Text), C("op", "Op", ReportColumnKind.Text), C("workCenter", "Work center", ReportColumnKind.Text), C("part", "Part", ReportColumnKind.Text),
                 C("planned", "Planned qty", ReportColumnKind.Number), C("achieved", "Achieved", ReportColumnKind.Number), C("good", "Good", ReportColumnKind.Number), C("scrap", "Scrap", ReportColumnKind.Number), C("variance", "Variance", ReportColumnKind.Number), C("status", "Status", ReportColumnKind.Text), C("shift", "Shift", ReportColumnKind.Text)],
                ["date", "workCenter", "part", "shift", "status"], "workCenter", 186,
                "Plan: the schedule approved and in force at the start of the period; an operation counts when its planned end falls in the period, at the order quantity, in the shift it was planned to end. Actual: production reports recorded in the period, in the shift they were reported. Achieved is good output up to the planned quantity."),
            Permissions.ScheduleRead, new("Output vs plan", "achieved", "planned"), ProductionOutputAsync),

        new(
            new("schedule-attainment", "Schedule attainment", "Production",
                "Operations the approved schedule planned to finish in the period, and whether the shop floor completed them by the period's end, weighted by planned minutes.",
                [FromFilter, ToFilter, ProgramFilter, WorkCenterFilter, StatusFilter("Completed", "CompletedLate", "Open", "Cancelled"), SearchFilter],
                [C("plannedEnd", "Planned end", ReportColumnKind.DateTime), C("order", "Order", ReportColumnKind.Text), C("op", "Op", ReportColumnKind.Text), C("workCenter", "Work center", ReportColumnKind.Text),
                 C("plannedMinutes", "Planned min", ReportColumnKind.Number), C("completedMinutes", "Completed min", ReportColumnKind.Number), C("completedAt", "Completed", ReportColumnKind.DateTime), C("status", "Status", ReportColumnKind.Text)],
                ["plannedEnd", "workCenter", "status"], "workCenter", 62,
                "The schedule-attainment engine: the schedule in force at the period's start, operation completion records, cancelled orders listed but not counted."),
            Permissions.ScheduleRead, new("Attainment", "completedMinutes", "plannedMinutes"), AttainmentAsync),

        new(
            new("constraint-history", "Constraint history", "Planning",
                "Constraints found by schedule runs that were open at any time in the period: when first seen, when resolved, and how long they took.",
                [FromFilter, ToFilter, ProgramFilter, StatusFilter("Open", "Investigating", "Resolved", "Verified"), SearchFilter],
                [C("firstSeen", "First seen", ReportColumnKind.DateTime), C("constraint", "Constraint", ReportColumnKind.Text), C("category", "Category", ReportColumnKind.Text), C("subject", "Subject", ReportColumnKind.Text), C("status", "Status", ReportColumnKind.Text),
                 C("resolvedAt", "Resolved", ReportColumnKind.DateTime), C("hoursToResolve", "Hours to resolve", ReportColumnKind.Hours), C("hoursOpen", "Hours open", ReportColumnKind.Hours), C("reopened", "Reopened", ReportColumnKind.Number),
                 C("orders", "Orders", ReportColumnKind.Number), C("owner", "Owner", ReportColumnKind.Text), C("finding", "Finding", ReportColumnKind.Text)],
                ["category", "status", "subject", "firstSeen"], "category", 366,
                "Constraint findings and their lifecycle as recorded. Time to resolve runs from first seen to resolved (by a person or by a later run that no longer found it)."),
            Permissions.ScheduleRead, null, ConstraintsAsync),

        new(
            new("downtime", "Machine downtime", "Maintenance",
                "Unplanned outages, planned maintenance windows and overdue-PM lockouts in the period, with the hours each took out of it.",
                [FromFilter, ToFilter, MachineFilter, StatusFilter("Unplanned", "Planned", "MaintenanceOverdue", "AssetOutOfService"), SearchFilter],
                [C("startsAt", "Starts", ReportColumnKind.DateTime), C("machine", "Machine", ReportColumnKind.Text), C("kind", "Kind", ReportColumnKind.Text), C("endsAt", "Ends", ReportColumnKind.DateTime), C("hours", "Hours in period", ReportColumnKind.Hours), C("reason", "Reason", ReportColumnKind.Text)],
                ["machine", "kind", "startsAt"], "machine", 366,
                "Downtime windows recorded by maintenance and lockouts derived from overdue PM work. Open outages run to now. Hours are clipped to the period."),
            Permissions.MaintenanceRead, null, DowntimeAsync),

        new(
            new("stoppages", "Stoppages and changeovers", "Production",
                "Every period a machine did not run, as reported on the floor: its category (planned, unplanned, setup, changeover, maintenance, waiting, starved, blocked), reason code, minutes, the minutes the routing expected for setups and changeovers, and whether a root cause is proven or only proposed.",
                [FromFilter, ToFilter, MachineFilter, StatusFilter(Enum.GetNames<StoppageCategory>()), SearchFilter],
                [C("startedAt", "Started", ReportColumnKind.DateTime), C("machine", "Machine", ReportColumnKind.Text), C("workCenter", "Work center", ReportColumnKind.Text), C("status", "Category", ReportColumnKind.Text),
                 C("reason", "Reason code", ReportColumnKind.Text), C("operation", "Order operation", ReportColumnKind.Text), C("minutes", "Minutes", ReportColumnKind.Number), C("expected", "Expected min", ReportColumnKind.Number),
                 C("variance", "Over expected", ReportColumnKind.Number), C("rootCause", "Root cause", ReportColumnKind.Text)],
                ["status", "machine", "workCenter", "reason"], "status", 92,
                "Stoppages overlapping the period; open ones run to now. A reason code is what was reported; a root cause is proven only once a second person confirms it."),
            Permissions.ShopFloorRead, null, StoppagesAsync),

        new(
            new("quantity-states", "Order quantity states", "Production",
                "Released orders, and orders released in the period, with their quantity through every state: started, produced, accepted, received and shipped so far; in process, in rework, in repair, pending inspection, released, on hold and staged now; and scrapped.",
                [FromFilter, ToFilter, ProgramFilter, PartFilter, StatusFilter("Released", "Held", "Completed"), SearchFilter],
                [C("order", "Order", ReportColumnKind.Text), C("part", "Part", ReportColumnKind.Text), C("status", "Status", ReportColumnKind.Text), C("ordered", "Ordered", ReportColumnKind.Number),
                 C("started", "Started", ReportColumnKind.Number), C("produced", "Produced", ReportColumnKind.Number), C("accepted", "Accepted", ReportColumnKind.Number), C("inProcess", "In process", ReportColumnKind.Number),
                 C("rework", "Rework", ReportColumnKind.Number), C("repair", "Repair", ReportColumnKind.Number), C("pending", "Pending inspection", ReportColumnKind.Number), C("released", "Released", ReportColumnKind.Number),
                 C("hold", "Hold", ReportColumnKind.Number), C("staged", "Staged", ReportColumnKind.Number), C("shipped", "Shipped", ReportColumnKind.Number), C("scrapped", "Scrapped", ReportColumnKind.Number)],
                ["status", "part"], "status", 92,
                "Derived from production reports, rework records, inspections, NCRs and the inventory ledger; nothing is rewritten. Packed is recorded from V3.14."),
            Permissions.OrdersRead, null, QuantityStatesAsync),

        new(
            new("delivery-performance", "Delivery performance", "Delivery",
                "Demand due in the period: how much shipped by the need date, how much shipped at all, and how late the rest is.",
                [FromFilter, ToFilter, ProgramFilter, PartFilter, StatusFilter("On time", "Late", "Overdue", "Open", "Closed", "Cancelled"), SearchFilter],
                [C("needDate", "Need date", ReportColumnKind.Date), C("demand", "Demand", ReportColumnKind.Text), C("kind", "Kind", ReportColumnKind.Text), C("part", "Part", ReportColumnKind.Text), C("customer", "Customer", ReportColumnKind.Text), C("reference", "Reference", ReportColumnKind.Text),
                 C("quantity", "Quantity", ReportColumnKind.Number), C("onTime", "On time qty", ReportColumnKind.Number), C("shipped", "Shipped", ReportColumnKind.Number), C("lastShipped", "Last shipped", ReportColumnKind.Date), C("status", "Status", ReportColumnKind.Text), C("daysLate", "Days late", ReportColumnKind.Number)],
                ["kind", "part", "customer", "status", "needDate"], "status", 366,
                "Independent demand and the shipments made against it; a shipment counts on time when it shipped on or before the need date (site time). Cancelled demand is left out unless asked for."),
            Permissions.MaterialPlanRead, new("On-time quantity", "onTime", "quantity"), DeliveryAsync),

        new(
            new("program-deliveries", "Contract deliveries", "Delivery",
                "Contract deliveries due in the period for the programs you can see: quantity, shipped against them, and value at CLIN prices.",
                [FromFilter, ToFilter, ProgramFilter, PartFilter, StatusFilter("Delivered", "Partly delivered", "Planned", "Overdue", "Not planned", "Cancelled"), SearchFilter],
                [C("dueDate", "Due", ReportColumnKind.Date), C("program", "Program", ReportColumnKind.Text), C("contract", "Contract", ReportColumnKind.Text), C("clin", "CLIN", ReportColumnKind.Text), C("part", "Part", ReportColumnKind.Text),
                 C("quantity", "Quantity", ReportColumnKind.Number), C("shipped", "Shipped", ReportColumnKind.Number), C("state", "State", ReportColumnKind.Text), C("value", "Value", ReportColumnKind.Number), C("deliveredValue", "Delivered value", ReportColumnKind.Number)],
                ["program", "contract", "state", "part", "dueDate"], "contract", 1100,
                "Contract deliveries, the demand planned from them and shipments against that demand. Value is quantity times the CLIN unit price; unpriced CLINs count as zero."),
            Permissions.ProgramsRead, new("Delivered value", "deliveredValue", "value"), ProgramDeliveriesAsync),

        new(
            new("ncr-summary", "Nonconformances", "Quality",
                "NCRs raised in the period: what was found, how it was dispositioned and how long each stayed open.",
                [FromFilter, ToFilter, ProgramFilter, PartFilter, StatusFilter("Open", "Dispositioned", "Approved", "Closed"), SearchFilter],
                [C("raisedAt", "Raised", ReportColumnKind.DateTime), C("ncr", "NCR", ReportColumnKind.Text), C("part", "Part", ReportColumnKind.Text), C("order", "Order", ReportColumnKind.Text), C("op", "Op", ReportColumnKind.Text), C("defect", "Defect", ReportColumnKind.Text),
                 C("quantity", "Quantity", ReportColumnKind.Number), C("status", "Status", ReportColumnKind.Text), C("disposition", "Disposition", ReportColumnKind.Text), C("closedAt", "Closed", ReportColumnKind.DateTime), C("daysOpen", "Days open", ReportColumnKind.Number)],
                ["part", "defect", "status", "disposition"], "defect", 366,
                "NCR records. Days open runs from raised to closed, or to now for NCRs not yet closed."),
            Permissions.QualityRead, null, NcrsAsync),

        new(
            new("inspection-results", "Inspection results", "Quality",
                "Inspections recorded in the period and the share of inspected quantity accepted first time.",
                [FromFilter, ToFilter, ProgramFilter, PartFilter, StatusFilter("Pass", "Fail"), SearchFilter],
                [C("at", "Inspected", ReportColumnKind.DateTime), C("inspection", "Inspection", ReportColumnKind.Text), C("order", "Order", ReportColumnKind.Text), C("op", "Op", ReportColumnKind.Text), C("part", "Part", ReportColumnKind.Text),
                 C("inspected", "Inspected qty", ReportColumnKind.Number), C("accepted", "Accepted qty", ReportColumnKind.Number), C("result", "Result", ReportColumnKind.Text), C("inspector", "Inspector", ReportColumnKind.Text), C("ncr", "NCR", ReportColumnKind.Text)],
                ["part", "result", "inspector", "at"], "part", 366,
                "Inspection records. Accepted quantity is the inspected quantity of passing inspections."),
            Permissions.QualityRead, new("First-pass acceptance", "accepted", "inspected"), InspectionsAsync),

        new(
            new("pm-compliance", "Maintenance work and PM compliance", "Maintenance",
                "Preventive work due in the period and whether it was done by its due date, with corrective work raised or completed in the period.",
                [FromFilter, ToFilter, MachineFilter, StatusFilter("Open", "Scheduled", "InProgress", "Completed", "Closed", "Cancelled"), SearchFilter],
                [C("dueDate", "Due", ReportColumnKind.Date), C("workOrder", "Work order", ReportColumnKind.Text), C("machine", "Machine", ReportColumnKind.Text), C("kind", "Kind", ReportColumnKind.Text), C("title", "Title", ReportColumnKind.Text), C("status", "Status", ReportColumnKind.Text),
                 C("completedAt", "Completed", ReportColumnKind.DateTime), C("due", "Due in period", ReportColumnKind.Number), C("onTime", "Done on time", ReportColumnKind.Number), C("daysLate", "Days late", ReportColumnKind.Number), C("repairHours", "Repair hours", ReportColumnKind.Hours)],
                ["machine", "kind", "status", "dueDate"], "machine", 366,
                "Maintenance work orders. A preventive order counts as due when its due date falls in the period (run-hour-only orders have no date and are not counted); it is on time when completed on or before that date (site time)."),
            Permissions.MaintenanceRead, new("PM compliance", "onTime", "due"), MaintenanceAsync),

        new(
            new("inventory-transactions", "Inventory transactions", "Inventory",
                "Every stock movement recorded in the period, with who made it and what it was for.",
                [FromFilter, ToFilter, ProgramFilter, PartFilter, StatusFilter(Enum.GetNames<InventoryTransactionKind>()), SearchFilter],
                [C("at", "At", ReportColumnKind.DateTime), C("transaction", "Transaction", ReportColumnKind.Text), C("kind", "Kind", ReportColumnKind.Text), C("part", "Part", ReportColumnKind.Text), C("quantity", "Quantity", ReportColumnKind.Number),
                 C("from", "From", ReportColumnKind.Text), C("to", "To", ReportColumnKind.Text), C("lot", "Lot", ReportColumnKind.Text), C("reference", "For", ReportColumnKind.Text), C("actor", "By", ReportColumnKind.Text), C("approvedBy", "Approved by", ReportColumnKind.Text), C("reason", "Reason", ReportColumnKind.Text)],
                ["kind", "part", "actor", "at"], "kind", 366,
                "The inventory transaction ledger. Transactions for production orders of programs you cannot see are left out."),
            Permissions.InventoryRead, null, TransactionsAsync),

        new(
            new("qualifications-expiring", "Qualifications expiring", "Workforce",
                "Certified qualifications that expire in the period (by default the next 90 days), with each person's home work center and shift, so renewals can be planned before capacity is lost.",
                [FromFilter, ToFilter, WorkCenterFilter, ShiftFilter, SearchFilter],
                [C("expires", "Expires", ReportColumnKind.DateTime), C("person", "Person", ReportColumnKind.Text), C("qualification", "Qualification", ReportColumnKind.Text), C("name", "Name", ReportColumnKind.Text),
                 C("daysLeft", "Days left", ReportColumnKind.Number), C("workCenter", "Work center", ReportColumnKind.Text), C("shift", "Shift", ReportColumnKind.Text)],
                ["qualification", "workCenter", "shift", "expires"], "qualification", 366,
                "Qualification records with status Qualified and an expiry date in the period; people who are not active workers are left out. Days left counts from today (site time).",
                Forward: true),
            Permissions.CompetencyRead, null, QualificationsAsync),

        new(
            new("item-classification", "Item classification", "Engineering",
                "Every item with its class, make or buy, and approved manufacturer parts, so unclassified items and COTS items that cannot be bought yet are visible.",
                [PartFilter, StatusFilter(["Unclassified", "Cots", "BuildToPrint", "RawMaterial", "StandardHardware", "Consumable", "Mro", "Tooling", "Service", "OtherControlled", "Make"]), SearchFilter],
                [C("part", "Part", ReportColumnKind.Text), C("description", "Description", ReportColumnKind.Text), C("makeBuy", "Make / buy", ReportColumnKind.Text), C("class", "Class", ReportColumnKind.Text),
                 C("approvedMpns", "Approved MPNs", ReportColumnKind.Number), C("manufacturers", "Approved manufacturer parts", ReportColumnKind.Text), C("lifecycle", "Worst lifecycle", ReportColumnKind.Text), C("attention", "Needs", ReportColumnKind.Text)],
                ["class", "makeBuy", "attention"], "class", 3660,
                "The item master and manufacturer parts as they stand now; the period does not apply. A COTS item with no approved manufacturer part cannot be bought."),
            Permissions.EngineeringRead, null, ItemClassificationAsync),
        new(
            new("document-register", "Document register", "Engineering",
                "Every document you may see with its authority, the revision in force, open revisions, review dates and where it is attached.",
                [StatusFilter(["Internal", "External", "Reference"]), SearchFilter],
                [C("document", "Document", ReportColumnKind.Text), C("title", "Title", ReportColumnKind.Text), C("type", "Type", ReportColumnKind.Text), C("authority", "Authority", ReportColumnKind.Text),
                 C("current", "In force", ReportColumnKind.Text), C("effectiveFrom", "Effective from", ReportColumnKind.Date), C("open", "Open revision", ReportColumnKind.Text),
                 C("reviewBy", "Review by", ReportColumnKind.Date), C("attachments", "Attached to", ReportColumnKind.Number), C("attention", "Needs", ReportColumnKind.Text)],
                ["authority", "type", "attention"], "authority", 3660,
                "Documents as they stand now; the period does not apply. Documents in programs you do not hold, or with markings you do not hold, are not shown. Reference documents are never production authority."),
            Permissions.EngineeringRead, null, DocumentRegisterAsync),
        new(
            new("sourcing", "RFQ and source decisions", "Procurement",
                "Sourcing needs, factual supplier responses and recorded human decisions. RFQs create no projected supply.",
                [ProgramFilter, PartFilter, StatusFilter(Enum.GetNames<RfqStatus>()), SearchFilter],
                [C("rfq", "RFQ", ReportColumnKind.Text), C("need", "Requisition", ReportColumnKind.Text), C("part", "Part", ReportColumnKind.Text), C("status", "RFQ state", ReportColumnKind.Text),
                 C("quote", "Response", ReportColumnKind.Text), C("supplier", "Supplier", ReportColumnKind.Text), C("quantity", "Offered qty", ReportColumnKind.Number), C("price", "Unit price", ReportColumnKind.Number),
                 C("currency", "Currency", ReportColumnKind.Text), C("promised", "Promised", ReportColumnKind.Date), C("valid", "Valid through", ReportColumnKind.Date), C("alternate", "Proposed alternate", ReportColumnKind.Text),
                 C("selected", "Selected", ReportColumnKind.Text), C("reason", "Decision reason", ReportColumnKind.Text)],
                ["status", "part", "supplier", "currency", "selected"], "status", 3660,
                "Immutable supplier responses. Program and original-file access are enforced. Prices in different currencies are not converted or ranked."),
            Permissions.PurchasingRead, null, SourcingAsync),
        new(
            new("business-evidence", "Business-record evidence", "Quality",
                "Reference files on receipts, inspections, shipments and FAI, with source-signature verification kept separate from structured inspection results.",
                [StatusFilter(["Attached", "SignatureVerified"]), SearchFilter],
                [C("targetType", "Record kind", ReportColumnKind.Text), C("target", "Record", ReportColumnKind.Text), C("document", "Evidence", ReportColumnKind.Text),
                 C("type", "Kind", ReportColumnKind.Text), C("file", "Original file", ReportColumnKind.Text), C("state", "Verification", ReportColumnKind.Text),
                 C("signer", "Source signer", ReportColumnKind.Text), C("verifiedBy", "Verified by", ReportColumnKind.Text), C("attachedAt", "Attached at", ReportColumnKind.DateTime)],
                ["targetType", "type", "state"], "targetType", 3660,
                "Current reference attachments visible through both record permissions and document markings/program access. An attachment or signature verification does not complete inspection."),
            Permissions.EngineeringRead, null, BusinessEvidenceAsync),
        new(
            new("documentation-status", "Order documentation", "Engineering",
                "Open orders and the documents they must follow: the revision each was released with, the revision that applies now, and mismatches, missing or unreleased documents.",
                [PartFilter, StatusFilter(["Current", "Pinned", "LegitimatelySuperseded", "Mismatch", "Missing", "NotReleased", "Obsolete", "RequiresReview"]), SearchFilter],
                [C("order", "Order", ReportColumnKind.Text), C("part", "Part", ReportColumnKind.Text), C("revision", "Rev", ReportColumnKind.Text), C("class", "Class", ReportColumnKind.Text), C("enforced", "Blocks readiness", ReportColumnKind.Text),
                 C("document", "Document", ReportColumnKind.Text), C("source", "Required by", ReportColumnKind.Text), C("atRelease", "Released with", ReportColumnKind.Text), C("applies", "Applies now", ReportColumnKind.Text),
                 C("state", "State", ReportColumnKind.Text), C("why", "Why", ReportColumnKind.Text)],
                ["state", "document", "part", "order"], "state", 3660,
                "Open orders as they stand now; the period does not apply. Released orders are compared with the revision that applied when they were released. Documents you may not see are shown as restricted."),
            Permissions.OrdersRead, null, DocumentationStatusAsync),
        new(
            new("stock-by-revision", "Stock by revision", "Inventory",
                "Stock on hand by part revision, through the lot it was received into. Stock received before revisions were recorded, or not lot-tracked, shows as revision not recorded: never assumed current.",
                [PartFilter, SearchFilter],
                [C("part", "Part", ReportColumnKind.Text), C("revision", "Revision", ReportColumnKind.Text), C("lot", "Lot", ReportColumnKind.Text), C("location", "Location", ReportColumnKind.Text),
                 C("status", "Status", ReportColumnKind.Text), C("quantity", "Quantity", ReportColumnKind.Number)],
                ["revision", "part", "location", "status"], "revision", 3660,
                "Stock as it stands now; the period does not apply."),
            Permissions.InventoryRead, null, StockByRevisionAsync),
        new(
            new("stock-positions", "Stock positions", "Inventory",
                "Where every lot is now, with its dimensions kept apart: quality status, location and the locations it sits in, custody (the company, an outside processor, in transit), whether it counts for planning, and the work center it is at the point of use of.",
                [PartFilter, StatusFilter(Enum.GetNames<StockStatus>()), SearchFilter],
                [C("part", "Part", ReportColumnKind.Text), C("lot", "Lot / serial", ReportColumnKind.Text), C("location", "Location", ReportColumnKind.Text), C("path", "Within", ReportColumnKind.Text),
                 C("kind", "Kind", ReportColumnKind.Text), C("status", "Quality status", ReportColumnKind.Text), C("custody", "Custody", ReportColumnKind.Text),
                 C("planning", "Counts for planning", ReportColumnKind.Text), C("pointOfUse", "Point of use", ReportColumnKind.Text), C("quantity", "Quantity", ReportColumnKind.Number)],
                ["custody", "status", "kind", "location", "part", "pointOfUse"], "custody", 3660,
                "Stock as it stands now; the period does not apply. Counts for planning = released by quality and in a nettable location."),
            Permissions.InventoryRead, null, StockPositionsAsync),
        new(
            new("tool-status", "Tools and calibration", "Resources",
                "Every tool and gauge: where it is and who has it, its condition, calibration due, uses against its limits and since its last service, and whether it may be used now, with why not.",
                [StatusFilter(Enum.GetNames<Forge.Resources.Contracts.ToolStatus>()), SearchFilter],
                [C("tool", "Tool", ReportColumnKind.Text), C("type", "Type", ReportColumnKind.Text), C("kind", "Kind", ReportColumnKind.Text), C("where", "Where / with", ReportColumnKind.Text),
                 C("condition", "Condition", ReportColumnKind.Text), C("status", "Status", ReportColumnKind.Text), C("calibrationDue", "Calibration due", ReportColumnKind.Date),
                 C("daysLeft", "Days to calibration", ReportColumnKind.Number), C("uses", "Uses", ReportColumnKind.Number), C("usesLeft", "Uses left", ReportColumnKind.Number),
                 C("sinceService", "Since service", ReportColumnKind.Number), C("valid", "Usable now", ReportColumnKind.Text), C("why", "Why not", ReportColumnKind.Text)],
                ["status", "kind", "type", "valid"], "valid", 3660,
                "Tools as they stand now; the period does not apply. Usable now means valid at this moment: calibration in force, not damaged, not away, within its limits."),
            Permissions.ResourcesRead, null, ToolStatusAsync),
        new(
            new("tool-use-history", "Tool validity at time of use", "Resources",
                "Every recorded tool use in the selected period, evaluated against the tool event history at that instant: calibration in force, service status, condition and usage limits. This is historical evidence, not a current validity check.",
                [FromFilter, ToFilter, SearchFilter],
                [C("at", "Used at", ReportColumnKind.DateTime), C("tool", "Tool", ReportColumnKind.Text), C("calibratedThrough", "Calibrated through", ReportColumnKind.Date),
                 C("valid", "Valid at use", ReportColumnKind.Text), C("usesLeft", "Uses left then", ReportColumnKind.Number), C("whyNot", "Why not", ReportColumnKind.Text), C("reference", "Recorded reference", ReportColumnKind.Text)],
                ["tool", "valid"], "tool", 3660,
                "Each Used event is evaluated using the tool's recorded history through that event. Machine and activity are not stored on the use event, so eligibility to a specific operation cannot be reconstructed."),
            Permissions.ResourcesRead, null, ToolUseHistoryAsync),
        new(
            new("goal-attainment", "Goal attainment", "Planning",
                "Every open production and delivery goal: its required-by date (its own, or cascaded from the goal above it), what is done against its target, the reforecast completion of the remaining work, and whether it is on track, behind plan but recoverable, at risk or late.",
                [ProgramFilter, StatusFilter(Enum.GetNames<GoalState>()), SearchFilter],
                [C("goal", "Goal", ReportColumnKind.Text), C("kind", "Kind", ReportColumnKind.Text), C("title", "Title", ReportColumnKind.Text), C("parent", "Part of", ReportColumnKind.Text),
                 C("requiredBy", "Required by", ReportColumnKind.Date), C("projected", "Reforecast", ReportColumnKind.DateTime), C("done", "Done", ReportColumnKind.Number), C("target", "Target", ReportColumnKind.Number),
                 C("attainment", "Attainment %", ReportColumnKind.Number), C("status", "State", ReportColumnKind.Text), C("behindPlan", "Behind plan", ReportColumnKind.Text),
                 C("remaining", "Remaining min", ReportColumnKind.Number), C("why", "Why", ReportColumnKind.Text)],
                ["status", "kind", "parent"], "status", 3660,
                "Goals as they stand now; the period does not apply. Behind plan is measured against the approved schedule; at risk and late against the goal's required-by date and a fresh finite-capacity reforecast."),
            Permissions.ScheduleRead, null, GoalAttainmentAsync),
        new(
            new("executable-operations", "Executable operations", "Production",
                "Every open operation of released and held orders, answered now: can it happen, and if not, the primary constraint and why. Machine, equipment, tooling (calibration in force, not away or damaged), material, material at the point of use, holds, quality, documentation, qualified and trained people present, safety staffing.",
                [StatusFilter(["Executable", "Order", "Holds", "Engineering", "Documentation", "Predecessor", "Quality", "Material", "PointOfUse", "RequirementProfile", "Equipment", "Tooling", "Safety", "Workforce", "Training"]), SearchFilter],
                [C("order", "Order", ReportColumnKind.Text), C("part", "Part", ReportColumnKind.Text), C("op", "Op", ReportColumnKind.Number), C("description", "Operation", ReportColumnKind.Text),
                 C("workCenter", "Work center", ReportColumnKind.Text), C("machine", "Best machine", ReportColumnKind.Text), C("executable", "Can run now", ReportColumnKind.Text),
                 C("status", "Constraint", ReportColumnKind.Text), C("why", "Why", ReportColumnKind.Text), C("due", "Due", ReportColumnKind.Date)],
                ["status", "workCenter", "executable"], "status", 3660,
                "Operations as they stand now; the period does not apply. Presence counts only clocked-in or supervisor-confirmed people: a schedule is never presence."),
            Permissions.ReadinessRead, null, ExecutableOperationsAsync),
        new(
            new("holds", "Holds", "Production",
                "Every hold in the plant: kernel holds of every kind (quality, material, production, engineering, customer, shipping, procurement, configuration, safety, administrative) and the holds Quality and Inventory keep (undecided NCRs, inspections not passed, stock on quality hold). What each is on, where it came from, what it stops, who may release it and what must happen first.",
                [StatusFilter(Enum.GetNames<Forge.Kernel.Holds.HoldKind>()), SearchFilter],
                [C("hold", "Hold", ReportColumnKind.Text), C("status", "Kind", ReportColumnKind.Text), C("target", "On", ReportColumnKind.Text), C("source", "Source", ReportColumnKind.Text),
                 C("reason", "Reason", ReportColumnKind.Text), C("blocks", "Stops", ReportColumnKind.Text), C("authority", "Release authority", ReportColumnKind.Text),
                 C("resolution", "To release", ReportColumnKind.Text), C("placed", "Placed", ReportColumnKind.DateTime), C("active", "Active", ReportColumnKind.Text),
                 C("downstream", "Recorded downstream impact", ReportColumnKind.Text), C("assurance", "Release assurance", ReportColumnKind.Text), C("signature", "Release signature", ReportColumnKind.Text)],
                ["status", "active", "source"], "status", 3660,
                "Holds as they stand now; the period does not apply. Released holds stay in the register (filter Active = No)."),
            Permissions.HoldsRead, null, HoldsAsync),
        new(
            new("receiving-inspection", "Receiving inspection", "Inventory",
                "Every receipt into inspection: supplier and PO, what was received, accepted and rejected so far, what is still to inspect, and first article inspection (FAI) status. FAI pending determination is never shown as complete; material is not accepted until FAI is complete, not required or not applicable.",
                [StatusFilter(Enum.GetNames<FaiStatus>()), SearchFilter],
                [C("receipt", "Receipt", ReportColumnKind.Text), C("part", "Part", ReportColumnKind.Text), C("lot", "Lot", ReportColumnKind.Text), C("supplier", "Supplier", ReportColumnKind.Text),
                 C("po", "PO", ReportColumnKind.Text), C("received", "Received", ReportColumnKind.Number), C("accepted", "Accepted", ReportColumnKind.Number), C("rejected", "Rejected", ReportColumnKind.Number),
                 C("remaining", "To inspect", ReportColumnKind.Number), C("status", "FAI", ReportColumnKind.Text), C("at", "Received at", ReportColumnKind.DateTime)],
                ["status", "supplier", "part"], "status", 366,
                "Receipts into inspection, newest first (up to 500); the period does not apply."),
            Permissions.InventoryRead, null, ReceivingInspectionAsync),
        new(
            new("presence", "Presence", "Workforce",
                "Every worker's presence now, as far as the evidence goes: not present, scheduled (on shift but not clocked in: never counted), clocked in or confirmed by a supervisor, assigned to a work center, or active on an operation.",
                [StatusFilter(Enum.GetNames<PresenceLevel>()), SearchFilter],
                [C("person", "Person", ReportColumnKind.Text), C("status", "Presence", ReportColumnKind.Text), C("counts", "Counts as present", ReportColumnKind.Text), C("source", "Evidence", ReportColumnKind.Text),
                 C("home", "Home work center", ReportColumnKind.Text), C("workCenter", "Assigned to", ReportColumnKind.Text), C("machine", "Machine", ReportColumnKind.Text),
                 C("operation", "Operation", ReportColumnKind.Text), C("onShift", "On shift", ReportColumnKind.Text), C("detail", "Detail", ReportColumnKind.Text)],
                ["status", "home", "workCenter"], "status", 3660,
                "Presence as it stands now; the period does not apply. No reason for an absence is ever shown."),
            Permissions.ReadinessRead, null, PresenceAsync),
        new(
            new("shelf-life", "Shelf life and aging", "Inventory",
                "Every lot on hand that has a shelf life, retest, open life or storage limits: its age, the expiry it was given and the one in force after any approved extension, days left, retest due, open life, and whether it may be used.",
                [PartFilter, StatusFilter(Enum.GetNames<ShelfLifeStatus>()), SearchFilter],
                [C("part", "Part", ReportColumnKind.Text), C("lot", "Lot", ReportColumnKind.Text), C("onHand", "On hand", ReportColumnKind.Number), C("age", "Age (days)", ReportColumnKind.Number),
                 C("manufactured", "Manufactured", ReportColumnKind.Date), C("originalExpires", "Expiry given", ReportColumnKind.Date), C("expires", "Expires", ReportColumnKind.Date),
                 C("daysLeft", "Days left", ReportColumnKind.Number), C("retestDue", "Retest due", ReportColumnKind.Date), C("openExpires", "Open life ends", ReportColumnKind.DateTime),
                 C("status", "Status", ReportColumnKind.Text), C("why", "Why", ReportColumnKind.Text)],
                ["status", "part"], "status", 3660,
                "Lots as they stand now; the period does not apply. Expired, open-life-expired, retest-overdue or failed lots, and lots through a storage excursion without a retest since, are never issued or shipped."),
            Permissions.InventoryRead, null, ShelfLifeAsync),
        new(
            new("lot-lineage", "Lot splits and merges", "Inventory",
                "Every split and merge of a lot, roll, batch or other instance in the period, and every replacement a supplier sent for returned material (V3.11): what it came from, what it became, how much, who and why. Each is a recorded link, never changed.",
                [FromFilter, ToFilter, PartFilter, StatusFilter(Enum.GetNames<InstanceRelation>()), SearchFilter],
                [C("at", "When", ReportColumnKind.DateTime), C("status", "Relation", ReportColumnKind.Text), C("part", "Part", ReportColumnKind.Text), C("from", "From", ReportColumnKind.Text),
                 C("to", "Into", ReportColumnKind.Text), C("quantity", "Quantity", ReportColumnKind.Number), C("actor", "By", ReportColumnKind.Text), C("reason", "Why", ReportColumnKind.Text)],
                ["status", "part"], "at", 366,
                "Splits and merges recorded in the period."),
            Permissions.InventoryRead, null, LotLineageAsync),
        new(
            new("material-reconciliation", "Material reconciliation", "Production",
                "What became of the material issued to each production order, per part and lot: issued = consumed + returned + scrap + loss + remnant + remaining, within the part's yield tolerance. Unexplained material stays flagged until a later record explains it; nothing is scrapped to make it add up.",
                [PartFilter, StatusFilter([.. Enum.GetNames<ReconciliationStatus>(), "Remnants"]), SearchFilter],
                [C("order", "Order", ReportColumnKind.Text), C("part", "Part", ReportColumnKind.Text), C("lot", "Lot / serial", ReportColumnKind.Text), C("issued", "Issued", ReportColumnKind.Number),
                 C("returned", "Returned", ReportColumnKind.Number), C("consumed", "Consumed", ReportColumnKind.Number), C("scrap", "Scrap", ReportColumnKind.Number), C("loss", "Loss", ReportColumnKind.Number),
                 C("remnant", "Remnant", ReportColumnKind.Number), C("remaining", "Remaining", ReportColumnKind.Number), C("unexplained", "Unexplained", ReportColumnKind.Number), C("status", "Status", ReportColumnKind.Text)],
                ["status", "part", "order"], "status", 3660,
                "Every order with material issued to it, as it stands now; the period does not apply."),
            Permissions.InventoryRead, null, MaterialReconciliationAsync),
        new(
            new("genealogy-trace", "Genealogy trace", "Inventory",
                "Where a lot, roll, batch or serial came from and what it became: receipts, splits, merges, issues, returns, consumption, scrap, loss, remnants, production, byproducts, shipments and installs, each as recorded. Moves are not genealogy. Name the lot or serial in Contains.",
                [PartFilter, StatusFilter("Came from", "Became"), SearchFilter],
                [C("direction", "Direction", ReportColumnKind.Text), C("relation", "Relation", ReportColumnKind.Text), C("from", "From", ReportColumnKind.Text), C("to", "To", ReportColumnKind.Text),
                 C("quantity", "Quantity", ReportColumnKind.Number), C("at", "When", ReportColumnKind.DateTime), C("note", "Note", ReportColumnKind.Text)],
                ["direction", "relation"], "direction", 3660,
                "Everything recorded, whatever the period."),
            Permissions.InventoryRead, null, GenealogyTraceAsync),
        new(
            new("material-flow", "Material flow to the machine", "Production",
                "Every component of every released order, through each separate state: required, requested, allocated, picked, delivered, issued and consumed. In the warehouse is a material-flow constraint (the stock exists but is not at the machine), kept apart from a shortage.",
                [PartFilter, StatusFilter(Enum.GetNames<MaterialFlowState>()), SearchFilter],
                [C("order", "Order", ReportColumnKind.Text), C("part", "Part", ReportColumnKind.Text), C("workCenter", "Work center", ReportColumnKind.Text), C("required", "Required", ReportColumnKind.Number),
                 C("requested", "Requested", ReportColumnKind.Number), C("allocated", "Allocated", ReportColumnKind.Number), C("picked", "Picked", ReportColumnKind.Number), C("delivered", "Delivered", ReportColumnKind.Number),
                 C("issued", "Issued", ReportColumnKind.Number), C("consumed", "Consumed", ReportColumnKind.Number), C("atPointOfUse", "At the machine", ReportColumnKind.Number), C("elsewhere", "Elsewhere", ReportColumnKind.Number),
                 C("status", "State", ReportColumnKind.Text), C("why", "Why", ReportColumnKind.Text)],
                ["status", "workCenter", "part", "order"], "status", 3660,
                "Released orders as they stand now; the period does not apply."),
            Permissions.InventoryRead, null, MaterialFlowAsync),
        new(
            new("counts-due", "Cycle counts due", "Inventory",
                "Balances a cycle-count program says are due: never counted, or last counted longer ago than the program's frequency. Stock at an outside processor or in transit is not counted here.",
                [PartFilter, SearchFilter],
                [C("dueOn", "Due", ReportColumnKind.Date), C("program", "Program", ReportColumnKind.Text), C("location", "Location", ReportColumnKind.Text), C("part", "Part", ReportColumnKind.Text),
                 C("lot", "Lot / serial", ReportColumnKind.Text), C("status", "Quality status", ReportColumnKind.Text), C("quantity", "On record", ReportColumnKind.Number), C("lastCounted", "Last counted", ReportColumnKind.DateTime)],
                ["program", "location", "part"], "program", 3660,
                "As it stands today; the period does not apply."),
            Permissions.InventoryRead, null, CountsDueAsync),
        new(
            new("engineering-changes", "Engineering changes", "Engineering",
                "Every engineering change you may see: what it changes, which reviews it needs and has, who approved and released it, and when it takes effect.",
                [StatusFilter(Enum.GetNames<ChangeStatus>()), SearchFilter],
                [C("change", "Change", ReportColumnKind.Text), C("title", "Title", ReportColumnKind.Text), C("class", "Class", ReportColumnKind.Text), C("priority", "Priority", ReportColumnKind.Text),
                 C("status", "Status", ReportColumnKind.Text), C("changes", "Changes", ReportColumnKind.Text), C("reviews", "Reviews", ReportColumnKind.Text), C("originator", "Originator", ReportColumnKind.Text),
                 C("approvedBy", "Approved by", ReportColumnKind.Text), C("effective", "Effective", ReportColumnKind.Date), C("age", "Days open", ReportColumnKind.Number)],
                ["status", "class", "priority", "originator"], "status", 3660,
                "Changes as they stand now; the period does not apply. Changes for programs you cannot see are left out."),
            Permissions.EngineeringRead, null, EngineeringChangesAsync),
        new(
            new("change-actions", "Change actions", "Engineering",
                "What released engineering changes ask people to decide: dispositions for work in progress, buyer reviews of open purchase lines and training acknowledgments, with who decided what.",
                [StatusFilter(Enum.GetNames<ChangeActionStatus>()), SearchFilter],
                [C("action", "Action", ReportColumnKind.Text), C("change", "Change", ReportColumnKind.Text), C("kind", "Kind", ReportColumnKind.Text), C("target", "For", ReportColumnKind.Text),
                 C("status", "Status", ReportColumnKind.Text), C("resolution", "Decision", ReportColumnKind.Text), C("resolvedBy", "Decided by", ReportColumnKind.Text), C("raised", "Raised", ReportColumnKind.DateTime),
                 C("age", "Days open", ReportColumnKind.Number)],
                ["kind", "status", "change"], "status", 3660,
                "Actions as they stand now; the period does not apply. Actions for programs you cannot see are left out."),
            Permissions.EngineeringRead, null, ChangeActionsAsync),
        new(
            new("alternates", "Alternate parts", "Engineering",
                "Every alternate relationship you may see: which part stands in for which, at what conversion, where it applies, and who proposed and decided it. Proposed alternates are never usable.",
                [StatusFilter(Enum.GetNames<AlternateStatus>()), SearchFilter],
                [C("alternate", "#", ReportColumnKind.Text), C("primary", "Primary", ReportColumnKind.Text), C("alternatePart", "Alternate", ReportColumnKind.Text), C("kind", "Kind", ReportColumnKind.Text),
                 C("factor", "Per 1", ReportColumnKind.Text), C("where", "Where", ReportColumnKind.Text), C("status", "Status", ReportColumnKind.Text), C("proposedBy", "Proposed by", ReportColumnKind.Text),
                 C("decidedBy", "Decided by", ReportColumnKind.Text), C("validTo", "Valid to", ReportColumnKind.Date)],
                ["status", "kind", "primary"], "status", 3660,
                "Relationships as they stand now; the period does not apply. Program-specific alternates for programs you cannot see are left out."),
            Permissions.EngineeringRead, null, AlternatesAsync),
        new(
            new("assets", "Assets", "Maintenance",
                "Every asset with its lifecycle status, machine, criticality and what is installed in it, and how many of those components are flagged obsolete without an approved replacement.",
                [StatusFilter(Enum.GetNames<AssetStatus>()), SearchFilter],
                [C("asset", "Asset", ReportColumnKind.Text), C("name", "Name", ReportColumnKind.Text), C("type", "Type", ReportColumnKind.Text), C("machine", "Machine", ReportColumnKind.Text),
                 C("criticality", "Criticality", ReportColumnKind.Text), C("status", "Status", ReportColumnKind.Text), C("commissioned", "Commissioned", ReportColumnKind.Date),
                 C("components", "Components installed", ReportColumnKind.Number), C("obsolete", "Obsolete, no replacement", ReportColumnKind.Number)],
                ["status", "type", "criticality"], "status", 3660,
                "Assets as they stand now; the period does not apply."),
            Permissions.MaintenanceRead, null, AssetsAsync),
        new(
            new("obsolescence", "Obsolescence", "Engineering",
                "The current obsolescence record of each item: status, dates, evidence and the replacement, if anyone named and approved one. Items without an approved replacement stay flagged.",
                [StatusFilter(Enum.GetNames<ObsolescenceStatus>()), SearchFilter],
                [C("record", "Record", ReportColumnKind.Text), C("part", "Item", ReportColumnKind.Text), C("status", "Status", ReportColumnKind.Text), C("lastTimeBuy", "Last-time buy", ReportColumnKind.Date),
                 C("endOfSupport", "End of support", ReportColumnKind.Date), C("replacement", "Replacement", ReportColumnKind.Text), C("installed", "Installed in assets", ReportColumnKind.Number),
                 C("evidence", "Evidence", ReportColumnKind.Text)],
                ["status", "replacement"], "status", 3660,
                "Records as they stand now; the period does not apply."),
            Permissions.EngineeringRead, null, ObsolescenceAsync),
        new(
            new("maintenance-readiness", "Maintenance readiness", "Maintenance",
                "Every open maintenance work order and whether it can be done: asset, material (with approved alternates, maintenance-only ones included), procedure and technician, each checked on its own. Blocked work is not planned as capacity loss.",
                [StatusFilter(["Ready", "Blocked"]), SearchFilter],
                [C("workOrder", "Work order", ReportColumnKind.Text), C("kind", "Type", ReportColumnKind.Text), C("target", "Machine / asset", ReportColumnKind.Text), C("title", "Work", ReportColumnKind.Text),
                 C("due", "Due", ReportColumnKind.Date), C("status", "Readiness", ReportColumnKind.Text), C("blocking", "Blocked by", ReportColumnKind.Text), C("detail", "Why", ReportColumnKind.Text)],
                ["status", "blocking", "kind"], "status", 3660,
                "Work orders as they stand now; the period does not apply. Stock on hand is not reserved."),
            Permissions.MaintenanceRead, null, MaintenanceReadinessAsync),
        new(
            new("outages", "Planned outages", "Maintenance",
                "Outages proposed in the period: who proposed them, who decided, and the schedule impact the approver saw. A proposed outage takes no capacity until someone else approves it.",
                [StatusFilter(Enum.GetNames<OutageStatus>()), SearchFilter],
                [C("outage", "Outage", ReportColumnKind.Text), C("machine", "Machine", ReportColumnKind.Text), C("starts", "Starts", ReportColumnKind.DateTime), C("ends", "Ends", ReportColumnKind.DateTime),
                 C("workOrder", "Work order", ReportColumnKind.Text), C("status", "Status", ReportColumnKind.Text), C("proposedBy", "Proposed by", ReportColumnKind.Text),
                 C("decidedBy", "Decided by", ReportColumnKind.Text), C("impact", "Impact seen", ReportColumnKind.Text)],
                ["status", "machine"], "starts", 92,
                "Outages proposed in the period."),
            Permissions.MaintenanceRead, null, OutagesAsync),
        new(
            new("project-material", "Project material", "Projects",
                "Every material line of every facility and capital project: what it needs, what is issued, what stock, purchases and requisitions cover, and what is short.",
                [StatusFilter("Issued", "In stock", "On order", "Requested", "Short", "Cancelled"), SearchFilter],
                [C("project", "Project", ReportColumnKind.Text), C("type", "Type", ReportColumnKind.Text), C("funding", "Funding", ReportColumnKind.Text), C("reference", "Financial reference", ReportColumnKind.Text),
                 C("projectStatus", "Project status", ReportColumnKind.Text), C("line", "Line", ReportColumnKind.Number), C("part", "Part", ReportColumnKind.Text), C("needDate", "Need date", ReportColumnKind.Date),
                 C("required", "Required", ReportColumnKind.Number), C("issued", "Issued", ReportColumnKind.Number), C("onOrder", "On order", ReportColumnKind.Number),
                 C("requested", "Requested", ReportColumnKind.Number), C("short", "Short", ReportColumnKind.Number), C("status", "Status", ReportColumnKind.Text)],
                ["status", "project"], "needDate", 92,
                "Material lines of every project."),
            Permissions.ProjectsRead, null, ProjectMaterialAsync),
        new(
            new("as-built", "As-built record", "Production",
                "Every component issued to (or returned from) a production order in the period, as recorded at that moment: revision, lot or serial, approved alternate and its approval, supplier and purchase order.",
                [FromFilter, ToFilter, PartFilter, StatusFilter("Issued", "Returned"), SearchFilter],
                [C("recorded", "Recorded", ReportColumnKind.DateTime), C("order", "Order", ReportColumnKind.Text), C("component", "Component", ReportColumnKind.Text), C("revision", "Revision", ReportColumnKind.Text),
                 C("lot", "Lot / serial", ReportColumnKind.Text), C("quantity", "Quantity", ReportColumnKind.Number), C("inPlaceOf", "In place of", ReportColumnKind.Text), C("alternate", "Alternate approval", ReportColumnKind.Text),
                 C("supplier", "Supplier", ReportColumnKind.Text), C("po", "PO", ReportColumnKind.Text), C("status", "Kind", ReportColumnKind.Text)],
                ["status", "component"], "recorded", 92,
                "As-built lines recorded in the period."),
            Permissions.OrdersRead, null, AsBuiltAsync),
        new(
            new("dated-events", "Dated events ahead", "Planning",
                "What the records say will happen in the period (by default the next 90 days): item and document revisions taking effect, document reviews, engineering changes, alternates starting or ending, obsolescence dates, outages, work-order due dates and project tasks.",
                [FromFilter, ToFilter, StatusFilter("Item revision", "Document revision", "Document review", "Engineering change", "Alternate", "Obsolescence", "Outage", "Planned downtime", "Work order due", "Project", "Project task", "Milestone"), SearchFilter],
                [C("at", "Date", ReportColumnKind.DateTime), C("status", "What", ReportColumnKind.Text), C("title", "Detail", ReportColumnKind.Text), C("source", "Record", ReportColumnKind.Text)],
                ["status", "source"], "status", 366,
                "Only dates stored on records you may read: each kind needs its read permission (engineering, maintenance, projects) and program access. Nothing is projected or estimated.",
                Forward: true),
            Permissions.AnalystUse, null, DatedEventsAsync),
    ];

    private static async Task<ReportBody> DatedEventsAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var start = run.From > run.Now ? run.From : run.Now;
        var rows = (await HistoryService.CollectFutureAsync(run.Uow, run.Principal, start, run.To, ct))
            .Where(e => ReportRun.Is(e.Kind, p.Status) && run.Matches(e.Title, e.Link))
            .Select(e => new ReportRow([e.At, e.Kind, e.Title, e.Source], [e.Link]))
            .ToList();
        return new ReportBody(rows, [$"{rows.Count} dated event(s) from now to the end of the period."], rows.Select(r => (string)r.Cells[3]!).Distinct(StringComparer.Ordinal).ToList());
    }

    private static async Task<ReportBody> AsBuiltAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var orders = run.Uow.Store<Forge.Execution.Contracts.IExecutionLookup>();
        var visible = new Dictionary<long, bool>();
        var rows = new List<ReportRow>();
        foreach (var l in (await run.Uow.Store<Forge.Execution.Contracts.IAsBuiltRecord>().ListAsync(null, null, null, ct)).Where(l => run.InPeriod(l.RecordedAt)))
        {
            if (!visible.TryGetValue(l.OrderNumber, out var see))
            {
                var order = await orders.FindOrderAsync(l.OrderNumber, ct);
                see = order is not null && run.Visible(order.ProgramId);
                visible[l.OrderNumber] = see;
            }

            if (!see || (p.PartNumber is { } part && l.ComponentPart != part && l.SubstitutesFor != part) || !ReportRun.Is(l.Kind.ToString(), p.Status)
                || !run.Matches(l.ComponentPart, l.LotNumber, l.SupplierName, l.SubstitutesFor))
            {
                continue;
            }

            rows.Add(new ReportRow(
                [l.RecordedAt, l.OrderNumber.ToString(CultureInfo.InvariantCulture), l.ComponentPart, l.ComponentRevision, l.LotNumber, l.Quantity, l.SubstitutesFor,
                 l.AlternateId is { } a ? $"{l.AlternateKind} {a.ToString(CultureInfo.InvariantCulture)}, approved by {l.AlternateApprovedBy} {l.AlternateApprovedAt:yyyy-MM-dd}" : null,
                 l.SupplierName, l.PoNumber is { } po ? $"{po.ToString(CultureInfo.InvariantCulture)}/{l.PoLine?.ToString(CultureInfo.InvariantCulture)}" : null, l.Kind.ToString()],
                ["order:" + l.OrderNumber.ToString(CultureInfo.InvariantCulture), "item:" + l.ComponentPart]));
        }

        return new ReportBody(rows, [$"{rows.Count} as-built line(s); {rows.Count(r => r.Cells[6] is not null)} through an approved alternate."], ["As-built record"]);
    }

    private static async Task<ReportBody> ProjectMaterialAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var rows = new List<ReportRow>();
        foreach (var project in await run.Uow.Store<Forge.Projects.Contracts.IProjectLookup>().ListProjectsAsync(ct))
        {
            var view = await ProjectPlanningService.MaterialAsync(run.Uow, project.Code, ct);
            foreach (var l in view.Material.Lines)
            {
                if (!ReportRun.Is(l.Status, p.Status) || !run.Matches(project.Code, project.Title, l.PartNumber, l.Description, project.FinancialReference))
                {
                    continue;
                }

                rows.Add(new ReportRow(
                    [project.Code, project.Type.ToString(), project.Funding.ToString(), project.FinancialReference, project.Status.ToString(), l.LineNumber, l.PartNumber, l.NeedDate,
                     l.Required, l.Issued, l.OnOrder, l.Requested, l.Shortage, l.Status],
                    ["project:" + project.Code, "part:" + l.PartNumber]));
            }
        }

        return new ReportBody(rows, [$"{rows.Count} line(s) across {rows.Select(r => r.Cells[0]).Distinct().Count()} project(s); {rows.Count(r => r.Cells[13] is "Short")} short."], ["Projects", "Inventory", "Procurement"]);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task<ReportBody> MaintenanceReadinessAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var orders = (await run.Uow.Store<IMaintenanceLookup>().ListWorkOrdersAsync(false, ct)).ToDictionary(o => o.WorkOrderId);
        var rows = new List<ReportRow>();
        foreach (var r in await MaintenancePlanningService.ReadinessAsync(run.Uow, new FixedTime(run.Now), null, ct))
        {
            var o = orders[r.WorkOrderId];
            if (!ReportRun.Is(r.Status, p.Status) || !run.Matches(r.Target, o.Title, r.Blocking, o.Kind.ToString()))
            {
                continue;
            }

            var why = r.Dimensions.Where(d => d.Outcome != EvidenceOutcome.Pass).Select(d => d.Detail).ToList();
            List<string> trace = ["work-order:" + r.WorkOrderId.ToString(CultureInfo.InvariantCulture)];
            if (o.AssetCode is { } asset)
            {
                trace.Add("asset:" + asset);
            }

            rows.Add(new ReportRow([r.WorkOrderId.ToString(CultureInfo.InvariantCulture), o.Kind.ToString(), r.Target, o.Title, o.DueDate, r.Status, r.Blocking, why.Count == 0 ? r.Summary : string.Join(" ", why)], trace));
        }

        return new ReportBody(rows, [$"{rows.Count} open work order(s): {rows.Count(x => x.Cells[5] is "Ready")} ready, {rows.Count(x => x.Cells[5] is "Blocked")} blocked."], ["Work orders", "Required parts", "Stock", "Alternates", "Documents", "Qualifications"]);
    }

    private static async Task<ReportBody> OutagesAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var rows = new List<ReportRow>();
        foreach (var x in (await run.Uow.Store<IMaintenanceLookup>().ListOutagesAsync(true, ct)).Where(x => run.InPeriod(x.ProposedAt)).OrderBy(x => x.StartsAt))
        {
            if (!ReportRun.Is(x.Status.ToString(), p.Status) || !run.Matches(x.MachineCode, x.Reason, x.Impact))
            {
                continue;
            }

            rows.Add(new ReportRow(
                [x.OutageId.ToString(CultureInfo.InvariantCulture), x.MachineCode, x.StartsAt, x.EndsAt, x.WorkOrderId?.ToString(CultureInfo.InvariantCulture), x.Status.ToString(), x.ProposedBy, x.DecidedBy, x.Impact],
                ["machine:" + x.MachineCode]));
        }

        return new ReportBody(rows, [$"{rows.Count} outage(s); {rows.Count(x => x.Cells[5] is "Proposed")} waiting for a decision."], ["Outages"]);
    }

    private static async Task<ReportBody> AssetsAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var lookup = run.Uow.Store<IAssetLookup>();
        var components = await lookup.ListComponentsAsync(null, null, false, ct);
        var flagged = (await run.Uow.Store<IObsolescenceLookup>().ListObsolescenceAsync(true, ct)).Where(o => o.Flagged).Select(o => o.PartNumber).ToHashSet(StringComparer.Ordinal);
        var rows = new List<ReportRow>();
        foreach (var a in await lookup.ListAssetsAsync(ct))
        {
            if (!ReportRun.Is(a.Status.ToString(), p.Status) || !run.Matches(a.AssetCode, a.Name, a.MachineCode, a.Manufacturer, a.Model, a.Location))
            {
                continue;
            }

            var mine = components.Where(c => c.AssetCode == a.AssetCode).ToList();
            List<string> trace = ["asset:" + a.AssetCode];
            if (a.MachineCode is { } m)
            {
                trace.Add("machine:" + m);
            }

            rows.Add(new ReportRow(
                [a.AssetCode, a.Name, a.Type.ToString(), a.MachineCode, a.Criticality.ToString(), a.Status.ToString(), a.CommissionedOn, mine.Sum(c => c.Quantity), mine.Where(c => flagged.Contains(c.PartNumber)).Sum(c => c.Quantity)],
                trace));
        }

        return new ReportBody(rows, [$"{rows.Count} asset(s); {rows.Count(r => r.Cells[5] is "Restricted" or "Decommissioned")} out of service; {rows.Count(r => r.Cells[8] is decimal d && d > 0)} with obsolete components lacking a replacement."], ["Assets", "Asset components", "Obsolescence records"]);
    }

    private static async Task<ReportBody> ObsolescenceAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var installed = (await run.Uow.Store<IAssetLookup>().ListComponentsAsync(null, null, false, ct)).GroupBy(c => c.PartNumber, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(c => c.Quantity), StringComparer.Ordinal);
        var rows = new List<ReportRow>();
        foreach (var o in await run.Uow.Store<IObsolescenceLookup>().ListObsolescenceAsync(true, ct))
        {
            if (!ReportRun.Is(o.Status.ToString(), p.Status) || !run.Matches(o.PartNumber, o.Evidence, o.ReplacementPart))
            {
                continue;
            }

            var replacement = o.Replacement == ReplacementStatus.None ? "None recorded" : $"{o.ReplacementPart} ({o.Replacement})";
            rows.Add(new ReportRow(
                [o.RecordId.ToString(CultureInfo.InvariantCulture), o.PartNumber, o.Status.ToString(), o.LastTimeBuy, o.EndOfSupport, replacement, installed.GetValueOrDefault(o.PartNumber), o.Evidence],
                ["item:" + o.PartNumber]));
        }

        return new ReportBody(rows, [$"{rows.Count} item(s) with an obsolescence record; {rows.Count(r => r.Cells[5] is string s && !s.EndsWith("(Approved)", StringComparison.Ordinal))} still flagged."], ["Obsolescence records", "Asset components"]);
    }

    private static async Task<ReportBody> AlternatesAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var rows = new List<ReportRow>();
        foreach (var a in (await run.Uow.Store<IAlternateLookup>().ListAlternatesAsync(ct)).Where(a => run.Visible(a.ProgramId)))
        {
            if (!ReportRun.Is(a.Status.ToString(), p.Status) || !run.Matches(a.PrimaryPart, a.AlternatePart, a.Conditions, a.Evidence, a.QualityNotes))
            {
                continue;
            }

            rows.Add(new ReportRow(
                [a.AlternateId.ToString(System.Globalization.CultureInfo.InvariantCulture), a.PrimaryPart, a.AlternatePart, Alternates.Label(a.Kind), a.Factor.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                 a.Conditions, a.Status.ToString(), a.ProposedBy, a.DecidedBy, a.ValidTo],
                ["item:" + a.PrimaryPart, "item:" + a.AlternatePart]));
        }

        return new ReportBody(rows, [$"{rows.Count} relationship(s); {rows.Count(r => r.Cells[6] is "Proposed")} proposed, {rows.Count(r => r.Cells[6] is "Approved")} approved."], ["Alternate relationships"]);
    }

    private static async Task<ReportBody> ChangeActionsAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var rows = new List<ReportRow>();
        foreach (var a in (await run.Uow.Store<IPropagationLookup>().ListActionsAsync(null, false, ct)).Where(a => run.Visible(a.ProgramId)))
        {
            if (!ReportRun.Is(a.Status.ToString(), p.Status) || !run.Matches(a.ChangeNumber, a.Target, a.Detail, a.PartNumber, a.DocumentNumber))
            {
                continue;
            }

            var age = a.Status == ChangeActionStatus.Open ? Math.Round((decimal)Math.Max(0, (run.Now - a.RaisedAt).TotalDays), 1) : (decimal?)null;
            var trace = new List<string> { "change:" + a.ChangeNumber };
            if (a.Kind == ChangeActionKind.WipDisposition)
            {
                trace.Add("order:" + a.Target);
            }

            rows.Add(new ReportRow(
                [a.ActionId.ToString(System.Globalization.CultureInfo.InvariantCulture), a.ChangeNumber, a.Kind.ToString(), a.Target, a.Status.ToString(), a.Resolution, a.ResolvedBy, a.RaisedAt, age],
                trace));
        }

        var open = rows.Count(r => r.Cells[4] is "Open");
        return new ReportBody(rows, [$"{rows.Count} action(s); {open} open."], ["Change actions", "Propagation of released changes"]);
    }

    private static async Task<ReportBody> EngineeringChangesAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var rows = new List<ReportRow>();
        foreach (var c in (await run.Uow.Store<IChangeLookup>().ListChangesAsync(ct)).Where(c => run.Visible(c.ProgramId)))
        {
            var lines = string.Join("; ", c.ActiveLines.Select(l => $"{l.Subject} {l.FromRevision ?? "new"}→{l.ToRevision}"));
            var current = c.CurrentReviews.ToList();
            var reviews = current.Count == 0 ? "Not submitted" : $"{current.Count(r => r.Decision is not null)}/{current.Count} decided ({string.Join(", ", current.Select(r => r.Kind + (r.Decision is null ? "" : ": " + r.Decision)))})";
            if (!ReportRun.Is(c.Status.ToString(), p.Status) || !run.Matches(c.Number, c.Title, c.Reason, lines))
            {
                continue;
            }

            var age = c.IsOpen ? (decimal)Math.Max(0, (run.Now - c.CreatedAt).TotalDays) : (decimal?)null;
            rows.Add(new ReportRow(
                [c.Number, c.Title, c.Class.ToString(), c.Priority.ToString(), c.Status.ToString(), lines, reviews, c.Originator, c.ApprovedBy, c.EffectiveFrom, age is null ? null : Math.Round(age.Value, 1)],
                ["change:" + c.Number]));
        }

        var open = rows.Count(r => r.Cells[4] is "Draft" or "InReview" or "Approved");
        return new ReportBody(rows, [$"{rows.Count} change(s); {open} open."], ["Engineering changes", "Change reviews"]);
    }

    private static async Task<ReportBody> MaterialFlowAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var rows = new List<ReportRow>();
        foreach (var l in await MaterialFlowService.ReleasedAsync(run.Uow, run.Visible, ct))
        {
            if (!ReportRun.Is(l.PartNumber, p.PartNumber) || !ReportRun.Is(l.State.ToString(), p.Status) || !run.Matches(l.PartNumber, l.WorkCenterCode, ReportRun.Id(l.OrderNumber), l.Explanation))
            {
                continue;
            }

            rows.Add(new ReportRow(
                [ReportRun.Id(l.OrderNumber), l.PartNumber, l.WorkCenterCode, l.Required, l.Requested, l.Allocated, l.Picked, l.Delivered, l.Issued, l.Consumed, l.AtPointOfUse, l.Elsewhere, l.State.ToString(), l.Explanation],
                ["order:" + ReportRun.Id(l.OrderNumber), "item:" + l.PartNumber]));
        }

        var flow = rows.Count(r => (string)r.Cells[12]! == nameof(MaterialFlowState.InWarehouse));
        var shortRows = rows.Count(r => (string)r.Cells[12]! == nameof(MaterialFlowState.Shortage));
        return new ReportBody(rows, [$"{rows.Count} component line(s); {flow} in the warehouse but not at the machine, {shortRows} short."], ["Material tasks", "Inventory ledger", "Stock positions"]);
    }

    private static async Task<ReportBody> CountsDueAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var inventory = run.Uow.Store<IInventoryLookup>();
        var due = CountDue.For(await inventory.ListCountProgramsAsync(ct), await inventory.ListBalancesAsync(null, ct), await inventory.ListLocationsAsync(ct), await inventory.ListLastCountsAsync(ct), run.Today, run.Local);
        var rows = due.Where(d => ReportRun.Is(d.PartNumber, p.PartNumber) && run.Matches(d.PartNumber, d.LocationCode, d.LotNumber, d.ProgramCode))
            .Select(d => new ReportRow([d.DueOn, d.ProgramCode, d.LocationCode, d.PartNumber, d.LotNumber, d.Status.ToString(), d.Quantity, d.LastCountedAt], ["item:" + d.PartNumber]))
            .ToList();
        return new ReportBody(rows, [$"{rows.Count} balance(s) due for counting."], ["Cycle-count programs", "Counts", "Inventory ledger"]);
    }

    private static async Task<ReportBody> MaterialReconciliationAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var exactOrder = p.Search is { } search && search.StartsWith("order:", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(search.AsSpan(6), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var orderNumber)
            ? (long?)orderNumber : null;
        var onlyRemnants = string.Equals(p.Status, "Remnants", StringComparison.OrdinalIgnoreCase);
        var inventory = run.Uow.Store<IInventoryLookup>();
        var orders = (await run.Uow.Store<Forge.Execution.Contracts.IExecutionLookup>().ListOrdersAsync(ct)).ToDictionary(o => o.OrderNumber);
        var tolerances = (await inventory.ListYieldTolerancesAsync(ct)).ToDictionary(t => t.PartNumber, t => t.Percent, StringComparer.Ordinal);
        var transformations = (await inventory.ListTransformationsAsync(null, null, null, ct)).ToLookup(t => t.OrderNumber);
        var byOrder = (await inventory.ListGenealogyTransactionsAsync(ct)).Where(t => t.OrderNumber is not null && t.Kind is InventoryTransactionKind.Issue or InventoryTransactionKind.Return).ToLookup(t => t.OrderNumber!.Value);
        var rows = new List<ReportRow>();
        foreach (var group in byOrder.OrderBy(g => g.Key))
        {
            if ((exactOrder is { } selected && group.Key != selected) || !orders.TryGetValue(group.Key, out var order) || !run.Visible(order.ProgramId))
            {
                continue;
            }

            foreach (var r in MaterialReconciliation.For(group.Key, group, transformations[group.Key], part => tolerances.GetValueOrDefault(part)))
            {
                if (!ReportRun.Is(r.PartNumber, p.PartNumber)
                    || (onlyRemnants ? r.Remnant <= 0 : !ReportRun.Is(r.Status.ToString(), p.Status))
                    || (exactOrder is null && !run.Matches(r.PartNumber, r.LotNumber, ReportRun.Id(r.OrderNumber))))
                {
                    continue;
                }

                rows.Add(new ReportRow(
                    [ReportRun.Id(r.OrderNumber), r.PartNumber, r.LotNumber, r.Issued, r.Returned, r.Consumed, r.Scrap, r.Loss, r.Remnant, r.Remaining, r.Unexplained, r.Status.ToString()],
                    ["order:" + ReportRun.Id(r.OrderNumber), "item:" + r.PartNumber]));
            }
        }

        var flagged = rows.Count(r => (string)r.Cells[11]! == nameof(ReconciliationStatus.Unexplained));
        return new ReportBody(rows, [$"{rows.Count} issued part/lot line(s); {flagged} unexplained."], ["Inventory ledger", "Transformation records", "Yield tolerances"]);
    }

    private static async Task<ReportBody> GenealogyTraceAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        if (string.IsNullOrWhiteSpace(p.Search))
        {
            return new ReportBody([], ["Name a lot, roll, batch or serial in Contains to trace it."], ["Genealogy graph"]);
        }

        var graph = await GenealogyGraphService.BuildAsync(run.Uow, run.Principal, ct);
        var lot = p.Search.Trim().ToUpperInvariant();
        var starts = graph.Nodes.Where(n => n.Kind == GenealogyNodeKind.Instance && n.LotNumber == lot && ReportRun.Is(n.PartNumber, p.PartNumber)).ToList();
        var rows = new List<ReportRow>();
        foreach (var start in starts)
        {
            foreach (var (direction, trace) in new[] { ("Came from", graph.Trace(start.Id, TraceDirection.Back)), ("Became", graph.Trace(start.Id, TraceDirection.Forward)) })
            {
                if (!ReportRun.Is(direction, p.Status))
                {
                    continue;
                }

                var names = trace.Nodes.ToDictionary(n => n.Id, n => n.Label, StringComparer.Ordinal);
                foreach (var e in trace.Edges.OrderBy(e => e.At))
                {
                    rows.Add(new ReportRow([direction, e.Relation.ToString(), names[e.From], names[e.To], e.Quantity, e.At, e.Note], ["item:" + start.PartNumber]));
                }
            }
        }

        return new ReportBody(rows, [starts.Count == 0 ? $"Nothing is recorded for {lot}." : $"{rows.Count} link(s) from {string.Join(", ", starts.Select(s => s.Label))}."], ["Genealogy graph"]);
    }

    private static async Task<ReportBody> LotLineageAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var rows = new List<ReportRow>();
        foreach (var l in (await run.Uow.Store<IInventoryLookup>().ListInstanceLinksAsync(p.PartNumber, null, ct)).Where(l => run.InPeriod(l.At)))
        {
            if (!ReportRun.Is(l.Relation.ToString(), p.Status) || !run.Matches(l.SourcePart, l.SourceLot, l.ResultLot, l.Reason))
            {
                continue;
            }

            rows.Add(new ReportRow(
                [l.At, l.Relation.ToString(), l.SourcePart, l.SourceLot, l.ResultLot, l.Quantity, l.Actor, l.Reason],
                ["item:" + l.SourcePart]));
        }

        return new ReportBody(rows, [$"{rows.Count} link(s): {rows.Count(r => (string)r.Cells[1]! == "Split")} split, {rows.Count(r => (string)r.Cells[1]! == "Merge")} merge."], ["Instance links", "Inventory ledger"]);
    }

    private static async Task<ReportBody> ShelfLifeAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var onHand = (await run.Uow.Store<IInventoryLookup>().ListBalancesAsync(null, ct))
            .Where(b => b.LotNumber is not null).GroupBy(b => (b.PartNumber, b.LotNumber!)).ToDictionary(g => g.Key, g => g.Sum(b => b.Quantity));
        var today = DateOnly.FromDateTime(run.Now.UtcDateTime);
        var rows = new List<ReportRow>();
        foreach (var s in await Forge.Inventory.Contracts.ShelfLifeQueries.StatesAsync(run.Uow, null, run.Now, ct))
        {
            var quantity = onHand.GetValueOrDefault((s.PartNumber, s.LotNumber));
            if (quantity <= 0 || s.Status == ShelfLifeStatus.NotTracked || !ReportRun.Is(s.PartNumber, p.PartNumber) || !ReportRun.Is(s.Status.ToString(), p.Status)
                || !run.Matches(s.PartNumber, s.LotNumber, s.Explanation))
            {
                continue;
            }

            rows.Add(new ReportRow(
                [s.PartNumber, s.LotNumber, quantity, (decimal)s.AgeDays, s.ManufacturedOn, s.OriginalExpires, s.Expires, s.Expires is { } e ? (decimal)(e.DayNumber - today.DayNumber) : null,
                 s.RetestDue, s.OpenExpiresAt, s.Status.ToString(), s.Explanation],
                ["item:" + s.PartNumber, $"lot:{s.PartNumber}/{s.LotNumber}"]));
        }

        var blocked = rows.Count(r => (string)r.Cells[10]! is not (nameof(ShelfLifeStatus.Ok) or nameof(ShelfLifeStatus.Expiring)));
        var expiring = rows.Count(r => (string)r.Cells[10]! == nameof(ShelfLifeStatus.Expiring));
        return new ReportBody(rows, [rows.Count == 0 ? "No lots on hand with shelf life." : $"{rows.Count} lot(s): {blocked} not usable, {expiring} expiring within {LotShelfLife.ExpiringDays} days."],
            ["Lots", "Shelf-life rules", "Extensions", "Retests", "Storage excursions", "Inventory ledger"]);
    }

    private static async Task<ReportBody> GoalAttainmentAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var rows = new List<ReportRow>();
        foreach (var g in await GoalService.EvaluateAsync(run.Uow, new FixedTime(run.Now), false, ct, run.Principal))
        {
            var id = Id(g.GoalId);
            if (!run.Visible(g.ProgramId) || !ReportRun.Is(g.State.ToString(), p.Status) || !run.Matches(id, g.Title, g.Kind.ToString(), string.Join(" ", g.Evidence)))
            {
                continue;
            }

            List<string> trace = ["goal:" + id];
            if (g.OrderNumber is { } n)
            {
                trace.Add("order:" + Id(n));
            }

            rows.Add(new ReportRow(
                [id, g.Kind.ToString(), g.Title, g.ParentGoalId is { } parent ? Id(parent) : null, g.RequiredBy, g.Projected, g.Done, g.Target, Math.Round(g.Attainment * 100, 1), g.State.ToString(),
                 g.BehindPlan ? "Yes" : "No", g.RemainingMinutes, string.Join(" ", g.Evidence)],
                trace));
        }

        var risky = rows.Count(r => (string)r.Cells[9]! is nameof(GoalState.AtRisk) or nameof(GoalState.Late));
        var behind = rows.Count(r => (string)r.Cells[10]! == "Yes" && (string)r.Cells[9]! is not (nameof(GoalState.AtRisk) or nameof(GoalState.Late)));
        return new ReportBody(rows, [rows.Count == 0 ? "No open goals." : $"{rows.Count} goal(s): {risky} at risk or late; {behind} behind plan but still recoverable."],
            ["Goals", "Approved schedule", "Reforecast schedule", "Production orders", "Demand and shipments"]);
    }

    private static async Task<ReportBody> ExecutableOperationsAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var rows = new List<ReportRow>();
        foreach (var o in await ExecutableOperations.EvaluateAsync(run.Uow, new FixedTime(run.Now), run.Principal, ct))
        {
            var constraint = o.Executable ? "Executable" : o.PrimaryConstraint ?? "Unknown";
            if (!run.Visible(o.ProgramId) || !ReportRun.Is(constraint, p.Status) || !run.Matches(Id(o.OrderNumber), o.PartNumber, o.Description, o.WorkCenter, o.Why))
            {
                continue;
            }

            rows.Add(new ReportRow(
                [Id(o.OrderNumber), o.PartNumber, (decimal)o.Sequence, o.Description, o.WorkCenter, o.Machine, o.Executable ? "Yes" : "No", constraint, o.Why, o.DueDate],
                ["order:" + Id(o.OrderNumber)]));
        }

        var blocked = rows.Where(r => (string)r.Cells[6]! == "No").GroupBy(r => (string)r.Cells[7]!).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}").ToList();
        return new ReportBody(rows, [rows.Count == 0 ? "No open operations." : $"{rows.Count} open operation(s): {rows.Count(r => (string)r.Cells[6]! == "Yes")} can run now" + (blocked.Count == 0 ? "." : $"; blocked by {string.Join(", ", blocked)}.")],
            ["Production orders", "Order readiness", "Tools", "Holds", "Stock positions", "Presence", "Document training"]);
    }

    private static async Task<ReportBody> ReceivingInspectionAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var rows = new List<ReportRow>();
        foreach (var receipt in await run.Uow.Store<IReceivingLookup>().ListInspectionReceiptsAsync(500, ct))
        {
            var state = await ReceivingQueries.ReceiptStateAsync(run.Uow, receipt, ct);
            var (fai, _) = await ReceivingQueries.FaiStatusAsync(run.Uow, receipt, state.SupplierCode, ct);
            var po = receipt.PoNumber is { } n ? Id(n) : null;
            if (!ReportRun.Is(fai.ToString(), p.Status) || !run.Matches(Id(receipt.TransactionId), receipt.PartNumber, receipt.LotNumber ?? string.Empty, state.SupplierCode ?? string.Empty, po ?? string.Empty))
            {
                continue;
            }

            var trace = new List<string> { "part:" + receipt.PartNumber };
            if (po is not null)
            {
                trace.Add("po:" + po);
            }

            rows.Add(new ReportRow(
                [Id(receipt.TransactionId), receipt.PartNumber, receipt.LotNumber, state.SupplierCode, po, receipt.Quantity, state.Accepted, state.Rejected, state.Remaining, fai.ToString(), receipt.At],
                trace));
        }

        var waiting = rows.Count(r => r.Cells[8] is decimal d && d > 0);
        var pending = rows.Count(r => (string)r.Cells[9]! is nameof(FaiStatus.PendingDetermination) or nameof(FaiStatus.Required));
        return new ReportBody(rows, [rows.Count == 0 ? "No receipts into inspection." : $"{rows.Count} receipt(s): {waiting} still to inspect; {pending} with FAI pending determination or not yet done."],
            ["Inventory ledger", "Receiving inspections", "Determinations", "First article inspections", "Purchase orders"]);
    }

    private static async Task<ReportBody> HoldsAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var rows = new List<ReportRow>();
        foreach (var h in await HoldRegister.ListAsync(run.Uow, run.Principal, true, ct))
        {
            if (!ReportRun.Is(h.Kind.ToString(), p.Status) || !run.Matches(h.Reference, h.Target, h.Source, h.Reason, h.RequiredResolution))
            {
                continue;
            }

            rows.Add(new ReportRow(
                [h.Reference, h.Kind.ToString(), h.Target, h.Source, h.Reason, string.Join(", ", h.Blocks), h.ReleaseAuthority, h.RequiredResolution, h.PlacedAt, h.Active ? "Yes" : "No",
                 string.Join(", ", (h.DownstreamImpact ?? []).Select(i => i.RecordId)), h.RequireFreshAssurance ? "Fresh provider step-up required" : "Standard authenticated authority",
                 h.ReleaseSignature is { } signature ? $"{signature.Subject}; version {signature.RecordVersion}; {signature.Meaning}" : h.RequireSignature ? "Explicit signature required" : "Not required by kind policy"],
                (h.HoldId is { } id ? new List<string> { "hold:" + Id(id) } : h.AffectedRecords.ToList()).Concat((h.DownstreamImpact ?? []).Select(i => i.RecordId)).ToList()));
        }

        var active = rows.Count(r => (string)r.Cells[9]! == "Yes");
        var kinds = rows.Where(r => (string)r.Cells[9]! == "Yes").GroupBy(r => (string)r.Cells[1]!).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}").ToList();
        return new ReportBody(rows, [rows.Count == 0 ? "No holds." : $"{active} active hold(s)" + (kinds.Count == 0 ? "." : $": {string.Join(", ", kinds)}.")],
            ["Holds", "Hold kinds", "Nonconformances", "Inspections", "Stock status"]);
    }

    private static async Task<ReportBody> PresenceAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var rows = new List<ReportRow>();
        foreach (var x in ExecutableChecks.Presence(await SnapshotLoader.LoadAsync(run.Uow, new FixedTime(run.Now), ct)))
        {
            if (!ReportRun.Is(x.Level.ToString(), p.Status) || !run.Matches(x.Name, x.HomeWorkCenter ?? string.Empty, x.WorkCenter ?? string.Empty, x.Operation ?? string.Empty, x.Detail))
            {
                continue;
            }

            rows.Add(new ReportRow(
                [x.Name, x.Level.ToString(), x.CountsAsPresent ? "Yes" : "No", x.Source, x.HomeWorkCenter, x.WorkCenter, x.Machine, x.Operation, x.OnShift ? "Yes" : "No", x.Detail],
                ["person:" + x.PersonId]));
        }

        var present = rows.Count(r => (string)r.Cells[2]! == "Yes");
        var scheduled = rows.Count(r => (string)r.Cells[1]! == nameof(PresenceLevel.Scheduled));
        return new ReportBody(rows, [rows.Count == 0 ? "No workers." : $"{rows.Count} worker(s): {present} present; {scheduled} scheduled but not clocked in (not counted)."], ["Workers", "Attendance", "Assignments", "Open direct labor", "Shift schedules"]);
    }

    private static async Task<ReportBody> ToolStatusAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var tools = run.Uow.Store<Forge.Resources.Contracts.IToolLookup>();
        var types = (await tools.ListToolTypesAsync(ct)).ToDictionary(t => t.Code, StringComparer.Ordinal);
        var events = (await tools.ListToolEventsAsync(null, ct)).ToLookup(e => e.ToolCode);
        var today = DateOnly.FromDateTime(run.Now.UtcDateTime);
        var rows = new List<ReportRow>();
        foreach (var t in await tools.ListToolsAsync(ct))
        {
            var type = types[t.TypeCode];
            var v = Forge.Resources.Contracts.ToolValidity.At(t, type, events[t.Code], run.Now);
            var where = t.Custodian is { } who ? $"{t.Custody}: {who}" : t.Location ?? "—";
            if (!ReportRun.Is(t.Status.ToString(), p.Status) || !run.Matches(t.Code, t.TypeCode, t.Description, where, string.Join(" ", v.Problems)))
            {
                continue;
            }

            rows.Add(new ReportRow(
                [t.Code, type.Name, type.Kind.ToString(), where, t.Condition.ToString(), t.Status.ToString(), t.CalibrationDue, t.CalibrationDue is { } due ? (decimal)(due.DayNumber - today.DayNumber) : null,
                 (decimal)t.UsageCount, v.UsesLeft is { } left ? (decimal)left : null, (decimal)t.UsesSinceService, v.Valid ? "Yes" : "No", string.Join(" ", v.Problems)],
                ["tool:" + t.Code]));
        }

        var unusable = rows.Count(r => (string)r.Cells[11]! == "No");
        var dueSoon = rows.Count(r => r.Cells[7] is decimal d && d is >= 0 and <= 30);
        return new ReportBody(rows, [rows.Count == 0 ? "No tools registered." : $"{rows.Count} tool(s): {unusable} not usable now, {dueSoon} due for calibration within 30 days."], ["Tools", "Tool history"]);
    }

    private static async Task<ReportBody> ToolUseHistoryAsync(ReportRun run, CancellationToken ct)
    {
        var tools = run.Uow.Store<Forge.Resources.Contracts.IToolLookup>();
        var registered = (await tools.ListToolsAsync(ct)).ToDictionary(t => t.Code, StringComparer.Ordinal);
        var types = (await tools.ListToolTypesAsync(ct)).ToDictionary(t => t.Code, StringComparer.Ordinal);
        var events = (await tools.ListToolEventsAsync(null, ct)).GroupBy(e => e.ToolCode, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(e => e.At).ThenBy(e => e.EventId).ToList(), StringComparer.Ordinal);
        var rows = new List<ReportRow>();
        var visibleUses = await ToolUseVisibility.FilterAsync(run.Uow, run.Principal,
            events.Values.SelectMany(history => history).Where(use => use.Kind == Forge.Resources.Contracts.ToolEventKind.Used && run.InPeriod(use.At)), run.Now, ct);
        foreach (var use in visibleUses.OrderBy(e => e.At).ThenBy(e => e.EventId))
        {
            if (!registered.TryGetValue(use.ToolCode, out var tool) || !types.TryGetValue(tool.TypeCode, out var type)
                || !run.Matches(use.ToolCode, use.Reference, type.Name))
            {
                continue;
            }

            var validity = Forge.Resources.Contracts.ToolValidity.At(tool, type, events[tool.Code], use.At);
            rows.Add(new ReportRow(
                [use.At, tool.Code, validity.CalibrationDue, validity.Valid ? "Yes" : "No", validity.UsesLeft is { } left ? (decimal)left : null,
                 string.Join(" ", validity.Problems), use.Reference],
                ["tool:" + tool.Code]));
        }

        var invalid = rows.Count(r => (string)r.Cells[3]! == "No");
        return new ReportBody(rows, [$"{rows.Count} recorded tool use(s); {invalid} evaluated invalid from the history available at use time."], ["Tool use events", "Calibration and service history"]);
    }

    private static async Task<ReportBody> StockPositionsAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var inventory = run.Uow.Store<IInventoryLookup>();
        var rows = new List<ReportRow>();
        foreach (var x in await Forge.Inventory.Contracts.ShelfLifeQueries.PositionsAsync(run.Uow, null, run.Now, ct))
        {
            var custody = x.Custody switch
            {
                Custody.Supplier => $"Outside processor {x.CustodianSupplier}",
                Custody.InTransit => "In transit",
                _ => "Company",
            };
            var pointOfUse = x.PointOfUseWorkCenter is { } wc ? (x.MachineCode is { } m ? $"{wc} / {m}" : wc) : null;
            if (!ReportRun.Is(x.PartNumber, p.PartNumber) || !ReportRun.Is(x.QualityStatus.ToString(), p.Status)
                || !run.Matches(x.PartNumber, x.LotNumber, x.LocationPath, custody, pointOfUse, x.LocationKind.ToString()))
            {
                continue;
            }

            rows.Add(new ReportRow(
                [x.PartNumber, x.LotNumber, x.LocationCode, x.LocationPath, x.LocationKind.ToString(), x.QualityStatus.ToString(), custody, x.AvailableForPlanning ? "Yes" : "No", pointOfUse, x.Quantity],
                ["item:" + x.PartNumber]));
        }

        var away = rows.Where(r => (string)r.Cells[6]! != "Company").Sum(r => (decimal)r.Cells[9]!);
        return new ReportBody(rows, [$"{rows.Count} position(s); {away:0.###} not in the company's custody."], ["Inventory ledger", "Locations"]);
    }

    private static async Task<ReportBody> StockByRevisionAsync(ReportRun run, CancellationToken ct)
    {
        const string NotRecorded = "Revision not recorded";
        var p = run.Parameters;
        var inventory = run.Uow.Store<IInventoryLookup>();
        var lots = (await inventory.ListLotsAsync(null, ct)).ToDictionary(l => (l.PartNumber, l.LotNumber));
        var rows = new List<ReportRow>();
        foreach (var b in await inventory.ListBalancesAsync(null, ct))
        {
            var revision = b.LotNumber is { } lot && lots.TryGetValue((b.PartNumber, lot), out var info) ? info.PartRevision ?? NotRecorded : NotRecorded;
            if (!ReportRun.Is(b.PartNumber, p.PartNumber) || !run.Matches(b.PartNumber, revision, b.LotNumber, b.LocationCode))
            {
                continue;
            }

            rows.Add(new ReportRow([b.PartNumber, revision, b.LotNumber, b.LocationCode, b.Status.ToString(), b.Quantity], ["item:" + b.PartNumber]));
        }

        var unknown = rows.Where(r => (string?)r.Cells[1] == NotRecorded).Sum(r => (decimal)r.Cells[5]!);
        return new ReportBody(
            rows,
            [$"{rows.Count} balance(s); {Fmt(unknown)} on hand with no recorded revision."],
            ["Inventory ledger", "Lots"]);
    }

    private static async Task<ReportBody> DocumentationStatusAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var rows = new List<ReportRow>();
        var orders = (await run.Uow.Store<IExecutionLookup>().ListOrdersAsync(ct))
            .Where(o => o.Status is OrderStatus.Planned or OrderStatus.FirmPlanned or OrderStatus.Released or OrderStatus.Held && run.Visible(o.ProgramId) && ReportRun.Is(o.PartNumber, p.PartNumber))
            .OrderBy(o => o.OrderNumber);
        foreach (var order in orders)
        {
            var docs = await DocumentationService.ForOrderAsync(run.Uow, order, run.Now, run.Principal, ct);
            foreach (var d in docs.Documents)
            {
                if (!ReportRun.Is(d.State.ToString(), p.Status) || !run.Matches(Id(order.OrderNumber), order.PartNumber, d.DocumentNumber, d.Explanation))
                {
                    continue;
                }

                rows.Add(new ReportRow(
                    [Id(order.OrderNumber), order.PartNumber, order.Revision, docs.ItemClass.ToString(), docs.Enforced ? "Yes" : "No", d.DocumentNumber, d.Source, d.AtRelease, d.Applicable, DocumentationService.Describe(d.State), d.Explanation],
                    d.DocumentNumber == DocumentationService.Restricted ? ["order:" + Id(order.OrderNumber)] : ["order:" + Id(order.OrderNumber), "document:" + d.DocumentNumber]));
            }
        }

        var problems = rows.Count(r => r.Cells[9] is string state && state is not ("current" or "pinned to its revision" or "superseded after release (work in progress continues)"));
        return new ReportBody(
            rows,
            [$"{rows.Count} order document(s); {problems} need attention (mismatch, missing, not released, obsolete or waiting for review)."],
            ["Production orders", "Controlled documents", "Applicability rules"]);
    }

    private static async Task<ReportBody> DocumentRegisterAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var lookup = run.Uow.Store<IDocumentLookup>();
        var links = (await lookup.ListLinksAsync(null, null, null, false, ct)).Where(l => DocumentAccess.CanSeeLink(run.Principal, l)).ToLookup(l => l.DocumentNumber);
        var rows = new List<ReportRow>();
        foreach (var d in (await lookup.ListDocumentsAsync(ct)).Where(d => DocumentAccess.CanSee(run.Principal, d)))
        {
            var current = d.Authority == DocumentAuthority.Reference ? d.Latest : d.Current;
            var open = d.Revisions.FirstOrDefault(r => r.Status is DocumentRevisionStatus.Draft or DocumentRevisionStatus.InReview or DocumentRevisionStatus.Approved);
            var reviewBy = current?.ReviewBy;
            var needs = d.Authority == DocumentAuthority.Reference ? null
                : current is null && open is null ? "Revision"
                : reviewBy is { } due && due < run.Today ? "Review overdue"
                : open?.Status == DocumentRevisionStatus.InReview ? "Approval"
                : open?.Status == DocumentRevisionStatus.Approved ? "Release"
                : current is null ? "Released revision"
                : null;
            if (!ReportRun.Is(d.Authority.ToString(), p.Status) || !run.Matches(d.Number, d.Title, d.Type.ToString(), d.ExternalAuthority, d.ExternalNumber))
            {
                continue;
            }

            rows.Add(new ReportRow(
                [d.Number, d.Title, d.Type.ToString(), d.Authority == DocumentAuthority.External ? $"External ({d.ExternalAuthority})" : d.Authority.ToString(),
                 current is null ? null : $"{current.Revision} ({current.Status})", current?.EffectiveFrom, open is null ? null : $"{open.Revision} ({open.Status})",
                 reviewBy, (decimal)links[d.Number].Count(), needs],
                ["document:" + d.Number]));
        }

        var attention = rows.Count(r => r.Cells[9] is not null);
        return new ReportBody(
            rows,
            [$"{rows.Count} document(s); {attention} need attention (a revision, approval, release or an overdue review)."],
            ["Controlled documents", "Attachments"]);
    }

    private static async Task<ReportBody> SourcingAsync(ReportRun run, CancellationToken ct)
    {
        var sourcing = run.Uow.Store<ISourcingLookup>();
        var needs = run.Uow.Store<IRequisitionLookup>();
        var documents = run.Uow.Store<IDocumentLookup>();
        var rows = new List<ReportRow>();
        foreach (var rfq in await sourcing.ListRfqsAsync(ct))
        {
            var need = await needs.FindRequisitionAsync(rfq.RequisitionId, ct);
            if (need is null || !run.Visible(need.ProgramId) || !ReportRun.Is(need.PartNumber, run.Parameters.PartNumber)
                || !ReportRun.Is(rfq.Status.ToString(), run.Parameters.Status) || !run.Matches(rfq.Title, need.PartNumber, need.SourceLabel)) continue;
            var visible = new List<SupplierQuote>();
            foreach (var quote in await sourcing.ListQuotesAsync(rfq.RfqId, ct))
            {
                var original = await documents.FindDocumentAsync(quote.OriginalDocumentNumber, ct);
                if (original is not null && DocumentAccess.CanSee(run.Principal, original)) visible.Add(quote);
            }
            if (visible.Count == 0)
                rows.Add(new ReportRow([Id(rfq.RfqId), Id(need.RequisitionId), need.PartNumber, rfq.Status.ToString(), null, null, null, null, null, null, null, null, null, null], ["rfq:" + Id(rfq.RfqId), "requisition:" + Id(need.RequisitionId)]));
            foreach (var q in visible)
                rows.Add(new ReportRow([Id(rfq.RfqId), Id(need.RequisitionId), need.PartNumber, rfq.Status.ToString(), Id(q.QuoteId), q.SupplierCode, q.Quantity, q.UnitPrice, q.Currency,
                    q.PromisedDate, q.ValidThrough, q.ProposedAlternatePart, rfq.SelectedQuoteId == q.QuoteId ? "Yes" : "No", rfq.SelectedQuoteId == q.QuoteId ? rfq.SelectionReason : null],
                    ["rfq:" + Id(rfq.RfqId), "supplier-quote:" + Id(q.QuoteId), "document:" + q.OriginalDocumentNumber]));
        }
        return new ReportBody(rows, [$"{rows.Count} visible sourcing row(s). Selection remains a human decision; RFQs and quotes do not count as supply."], ["Requisitions", "RFQs", "Immutable supplier responses", "Source decisions"]);
    }
    private static async Task<ReportBody> BusinessEvidenceAsync(ReportRun run, CancellationToken ct)
    {
        var lookup = run.Uow.Store<IDocumentLookup>();
        var rows = new List<ReportRow>();
        foreach (var link in (await lookup.ListLinksAsync(null, null, null, false, ct)).Where(l =>
            l.TargetType is DocumentTargetType.Receipt or DocumentTargetType.ReceivingInspection or DocumentTargetType.Inspection or DocumentTargetType.Shipment or DocumentTargetType.Fai
            && DocumentAccess.CanSeeLink(run.Principal, l)))
        {
            var document = await lookup.FindDocumentAsync(link.DocumentNumber, ct);
            if (document is null || document.Authority != DocumentAuthority.Reference || !DocumentAccess.CanSee(run.Principal, document)) continue;
            var revision = link.Revision is { } pinned ? document.Revisions.FirstOrDefault(r => r.Revision == pinned) : document.Latest;
            var verification = revision is null ? null : await run.Uow.Store<IEvidenceLookup>().FindVerificationAsync(document.Number, revision.Revision, ct);
            var state = verification is null ? "Attached" : "SignatureVerified";
            if (!ReportRun.Is(state, run.Parameters.Status) || !run.Matches(link.TargetLabel, document.Number, document.Title, document.Type.ToString())) continue;
            rows.Add(new ReportRow([link.TargetType.ToString(), link.TargetLabel, document.Number, document.Type.ToString(), revision?.FileName, state,
                verification?.SourceSigner, verification?.VerifiedBy, link.LinkedAt], ["document:" + document.Number]));
        }

        return new ReportBody(rows, [$"{rows.Count} visible evidence attachment(s). Signature verification and structured inspection acceptance are separate facts."], ["Reference attachments", "Source signature verification"]);
    }

    private static async Task<ReportBody> ItemClassificationAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var lookup = run.Uow.Store<IEngineeringLookup>();
        var mpns = (await lookup.ListManufacturerPartsAsync(null, ct)).Where(m => m.Status == ManufacturerPartStatus.Approved).ToLookup(m => m.PartNumber);
        var order = new[] { ManufacturerLifecycle.EndOfLife, ManufacturerLifecycle.LastTimeBuy, ManufacturerLifecycle.NotRecommended, ManufacturerLifecycle.Unknown, ManufacturerLifecycle.Active };
        var rows = new List<ReportRow>();
        foreach (var item in await lookup.ListItemsAsync(ct))
        {
            var approved = mpns[item.PartNumber].ToList();
            var worst = approved.Count == 0 ? null : order.First(l => approved.Any(m => m.Lifecycle == l)).ToString();
            var needs = item.Class == ItemClass.Unclassified ? "Classification"
                : item.Class == ItemClass.Cots && approved.Count == 0 ? "Approved manufacturer part"
                : approved.Count > 0 && approved.All(m => m.Lifecycle is ManufacturerLifecycle.EndOfLife or ManufacturerLifecycle.LastTimeBuy) ? "Lifecycle review"
                : null;
            if (!ReportRun.Is(item.PartNumber, p.PartNumber) || !ReportRun.Is(item.Class.ToString(), p.Status)
                || !run.Matches(item.PartNumber, item.Description, item.Class.ToString(), string.Join(' ', approved.Select(m => $"{m.Manufacturer} {m.Mpn}"))))
            {
                continue;
            }

            rows.Add(new ReportRow(
                [item.PartNumber, item.Description, item.MakeBuy.ToString(), item.Class.ToString(), (decimal)approved.Count,
                 approved.Count == 0 ? null : string.Join("; ", approved.Select(m => $"{m.Manufacturer} {m.Mpn}")), worst, needs],
                ["item:" + item.PartNumber]));
        }

        var unclassified = rows.Count(r => (string)r.Cells[3]! == nameof(ItemClass.Unclassified));
        var cotsBlocked = rows.Count(r => (string?)r.Cells[7] == "Approved manufacturer part");
        return new ReportBody(
            rows,
            [$"{rows.Count} item(s); {unclassified} unclassified; {cotsBlocked} COTS item(s) with no approved manufacturer part, which cannot be bought."],
            ["Item master", "Manufacturer parts"]);
    }

    public static ReportSpec? Find(string id) => All.FirstOrDefault(s => string.Equals(s.Entry.Id, id, StringComparison.Ordinal));

    private static ReportColumn C(string key, string title, ReportColumnKind kind) => new(key, title, kind);

    private static string Id(long id) => ReportRun.Id(id);

    private static string Seq(int sequence) => sequence.ToString(CultureInfo.InvariantCulture);

    private static string Fmt(decimal d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    private static async Task<Dictionary<long, OrderInfo>> OrdersAsync(ReportRun run, CancellationToken ct) =>
        (await run.Uow.Store<IExecutionLookup>().ListOrdersAsync(ct)).ToDictionary(o => o.OrderNumber);

    // ------------------------------------------------------------------ production

    private static async Task<ReportBody> ProductionOutputAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var version = await run.Uow.Store<IPlanningStore>().ApprovedAsOfAsync(run.From, ct);
        var orders = await OrdersAsync(run, ct);
        var reports = await run.Uow.Store<IExecutionLookup>().ListReportsAsync(run.From, run.To, null, ct);

        // Rows are per order operation and shift: planned output in the shift it was planned to end, actual in the shift it was reported.
        var planned = (version?.Result?.Operations ?? [])
            .Where(o => o.Scheduled && o.End is { } end && run.InPeriod(end))
            .ToDictionary(o => (o.OrderNumber, o.Sequence, Shift: run.ShiftOf(o.End!.Value) ?? string.Empty));
        var reported = reports.ToLookup(r => (r.OrderNumber, r.Sequence, Shift: run.ShiftOf(r.ReportedAt) ?? string.Empty));

        var rows = new List<ReportRow>();
        foreach (var key in planned.Keys.Concat(reported.Select(g => g.Key)).Distinct().OrderBy(k => k.OrderNumber).ThenBy(k => k.Sequence).ThenBy(k => k.Shift, StringComparer.Ordinal))
        {
            planned.TryGetValue(key, out var op);
            orders.TryGetValue(key.OrderNumber, out var order);
            var mine = reported[key].ToList();
            var shift = key.Shift.Length == 0 ? null : key.Shift;
            if (!run.Visible(order?.ProgramId ?? op?.ProgramId) || (p.Shift is { } wanted && !string.Equals(shift, wanted, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var workCenter = op?.WorkCenterCode ?? mine[0].WorkCenterCode;
            if (!ReportRun.Is(workCenter, p.WorkCenter) || !ReportRun.Is(order?.PartNumber, p.PartNumber))
            {
                continue;
            }

            var cancelled = order?.Status == OrderStatus.Cancelled;
            var plannedQty = op is null || cancelled ? 0 : order?.Quantity ?? 0;
            var good = mine.Sum(r => r.GoodQuantity);
            var scrap = mine.Sum(r => r.ScrapQuantity);
            var status = op is null ? "Unplanned" : cancelled ? "Cancelled" : good >= plannedQty ? "On plan" : good > 0 ? "Short" : "Not reported";
            if (!ReportRun.Is(status, p.Status) || !run.Matches(Id(key.OrderNumber), order?.PartNumber, workCenter, status, shift))
            {
                continue;
            }

            var date = op?.End is { } end ? run.Local(end) : run.Local(mine.Min(r => r.ReportedAt));
            var trace = new List<string> { "order:" + Id(key.OrderNumber) };
            if (op is not null)
            {
                trace.Add("schedule:" + Id(version!.VersionId));
            }

            trace.AddRange(mine.Select(r => "report:" + Id(r.ReportId)));
            rows.Add(new ReportRow(
                [date, Id(key.OrderNumber), Seq(key.Sequence), workCenter, order?.PartNumber, plannedQty, Math.Min(good, plannedQty), good, scrap, good - plannedQty, status, shift],
                trace));
        }

        rows = rows.OrderBy(r => (DateOnly)r.Cells[0]!).ThenBy(r => (string)r.Cells[1]!, StringComparer.Ordinal).ToList();
        var summary = new List<string>
        {
            version is null
                ? "No schedule was approved before this period, so all reported output shows as unplanned."
                : $"Plan: schedule {Id(version.VersionId)}, approved {version.ApprovedAt:yyyy-MM-dd HH:mm} UTC and in force at the start of the period.",
        };
        var plannedTotal = rows.Sum(r => (decimal)r.Cells[5]!);
        var achieved = rows.Sum(r => (decimal)r.Cells[6]!);
        if (plannedTotal > 0)
        {
            summary.Add($"Output vs plan: {Fmt(decimal.Round(achieved / plannedTotal * 100m, 1))}% ({Fmt(achieved)} of {Fmt(plannedTotal)} planned units).");
        }

        var unplanned = rows.Where(r => (string)r.Cells[10]! == "Unplanned").Sum(r => (decimal)r.Cells[7]!);
        if (unplanned > 0)
        {
            summary.Add($"{Fmt(unplanned)} good units were reported on operations the plan did not have finishing in this period.");
        }

        return new ReportBody(rows, summary, version is null ? ["Production reports"] : [$"Schedule version {Id(version.VersionId)}", "Production reports", "Production orders"]);
    }

    private static async Task<ReportBody> AttainmentAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var version = await run.Uow.Store<IPlanningStore>().ApprovedAsOfAsync(run.From, ct);
        var lookup = run.Uow.Store<IExecutionLookup>();
        var executed = new List<OrderInfo>();
        foreach (var number in (version?.Result?.Operations ?? []).Where(o => o.End is { } e && run.InPeriod(e)).Select(o => o.OrderNumber).Distinct().Order())
        {
            if (await lookup.FindOrderAsync(number, ct) is { } order)
            {
                executed.Add(order);
            }
        }

        var snapshot = new AttainmentSnapshot(version?.VersionId, version?.ApprovedAt, version?.Result, executed);
        var result = EngineRunner.Run(new AttainmentEngine(), snapshot, new AttainmentRequest(run.From, run.To), run.Now).Output;
        var rows = result.Lines
            .Where(l => run.Visible(l.ProgramId) && ReportRun.Is(l.WorkCenterCode, p.WorkCenter) && ReportRun.Is(l.Status.ToString(), p.Status)
                && run.Matches(Id(l.OrderNumber), l.WorkCenterCode, l.Status.ToString()))
            .Select(l =>
            {
                var counted = l.Status == AttainmentLineStatus.Cancelled ? 0 : l.PlannedMinutes;
                var done = l.Status == AttainmentLineStatus.Completed ? l.PlannedMinutes : 0;
                return new ReportRow(
                    [l.PlannedEnd, Id(l.OrderNumber), Seq(l.Sequence), l.WorkCenterCode, counted, done, l.CompletedAt, l.Status.ToString()],
                    ["order:" + Id(l.OrderNumber), "schedule:" + Id(result.ScheduleVersionId!.Value)]);
            })
            .ToList();
        var planned = rows.Sum(r => (decimal)r.Cells[4]!);
        var completed = rows.Sum(r => (decimal)r.Cells[5]!);
        var summary = new List<string> { result.Explanation };
        if (planned > 0)
        {
            summary.Add($"Attainment for the rows shown: {Fmt(decimal.Round(completed / planned * 100m, 1))}% ({Fmt(completed)} of {Fmt(planned)} planned minutes).");
        }

        return new ReportBody(rows, summary, version is null ? [] : [$"Schedule version {Id(version.VersionId)}", "Operation completion records"]);
    }

    // ------------------------------------------------------------------ planning

    private static async Task<ReportBody> ConstraintsAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var orders = await OrdersAsync(run, ct);
        bool OrderVisible(long n) => run.Visible(orders.TryGetValue(n, out var o) ? o.ProgramId : null);
        var all = await run.Uow.Store<IPlanningStore>().ListConstraintsAsync(true, ct);
        var rows = new List<ReportRow>();
        foreach (var c in all.Where(c => c.FirstSeen < run.To && (c.ResolvedAt ?? run.Now) >= run.From).OrderBy(c => c.FirstSeen).ThenBy(c => c.ConstraintId))
        {
            var visibleOrders = c.AffectedOrders.Where(OrderVisible).ToList();
            var keep = p.ProgramId is null
                ? c.AffectedOrders.Count == 0 || visibleOrders.Count > 0
                : visibleOrders.Count > 0;
            if (!keep || !ReportRun.Is(c.Status.ToString(), p.Status) || !run.Matches(c.Category, c.Subject, c.Finding, c.Key))
            {
                continue;
            }

            decimal? toResolve = c.ResolvedAt is { } r ? ReportRun.Hours(r - c.FirstSeen) : null;
            var open = ReportRun.Hours((c.ResolvedAt ?? run.Now) - c.FirstSeen);
            rows.Add(new ReportRow(
                [c.FirstSeen, Id(c.ConstraintId), c.Category, c.Subject, c.Status.ToString(), c.ResolvedAt, toResolve, open, (decimal)c.ReopenedCount, (decimal)visibleOrders.Count, c.Owner, c.Finding],
                ["constraint:" + Id(c.ConstraintId), .. visibleOrders.Select(o => "order:" + Id(o))]));
        }

        var resolved = rows.Where(r => r.Cells[6] is decimal).Select(r => (decimal)r.Cells[6]!).Order().ToList();
        var stillOpen = rows.Where(r => r.Cells[6] is null).ToList();
        var summary = new List<string> { $"{rows.Count} constraints open at some time in the period; {resolved.Count} resolved, {stillOpen.Count} still open." };
        if (resolved.Count > 0)
        {
            var median = resolved.Count % 2 == 1 ? resolved[resolved.Count / 2] : (resolved[(resolved.Count / 2) - 1] + resolved[resolved.Count / 2]) / 2;
            summary.Add($"Time to resolve: average {Fmt(decimal.Round(resolved.Average(), 1))} h, median {Fmt(decimal.Round(median, 1))} h, longest {Fmt(resolved[^1])} h.");
        }

        if (stillOpen.Count > 0)
        {
            summary.Add($"Oldest still open: constraint {stillOpen.MaxBy(r => (decimal)r.Cells[7]!)!.Cells[1]}, open {Fmt(stillOpen.Max(r => (decimal)r.Cells[7]!))} h.");
        }

        return new ReportBody(rows, summary, ["Constraint findings"]);
    }

    // ------------------------------------------------------------------ maintenance

    private static async Task<ReportBody> StoppagesAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var orders = run.Uow.Store<IExecutionLookup>();
        var visible = new Dictionary<long, bool>();
        var rows = new List<ReportRow>();
        foreach (var v in await ProductionActualsQueries.ViewsAsync(run.Uow, false, run.From, run.To, run.Now, p.Machine, ct))
        {
            var s = v.Stoppage;
            string? operation = null;
            if (s.OrderNumber is { } n)
            {
                if (!visible.TryGetValue(n, out var see))
                {
                    see = await orders.FindOrderAsync(n, ct) is { } o && run.Visible(o.ProgramId);
                    visible[n] = see;
                }

                operation = see ? $"{Id(n)}/{s.Sequence?.ToString(CultureInfo.InvariantCulture)}" : "(another program)";
            }

            var proven = v.RootCauses.FirstOrDefault(r => r.Proven);
            var rootCause = proven is not null ? $"Proven: {proven.Cause}" : v.RootCauses.Count > 0 ? $"Proposed: {v.RootCauses[^1].Cause}" : null;
            if (!ReportRun.Is(s.Category.ToString(), p.Status) || !run.Matches(s.MachineCode, s.ReasonCode, v.ReasonDescription, s.Note, rootCause, operation))
            {
                continue;
            }

            List<string> trace = ["machine:" + s.MachineCode, "stoppage:" + Id(s.StoppageId)];
            if (operation is not null && visible[s.OrderNumber!.Value])
            {
                trace.Add("order:" + Id(s.OrderNumber.Value));
            }

            rows.Add(new ReportRow(
                [s.StartedAt, s.MachineCode, s.WorkCenterCode, s.Category.ToString(), $"{s.ReasonCode} — {v.ReasonDescription}", operation, v.Minutes, s.ExpectedMinutes, v.Variance, rootCause],
                trace));
        }

        var summary = rows.GroupBy(r => (string)r.Cells[3]!, StringComparer.Ordinal).OrderByDescending(g => g.Sum(r => (decimal)r.Cells[6]!))
            .Select(g => $"{g.Key}: {g.Count()} stoppage(s), {Fmt(g.Sum(r => (decimal)r.Cells[6]!))} min.")
            .ToList();
        var changeovers = rows.Where(r => r.Cells[8] is decimal).ToList();
        if (changeovers.Count > 0)
        {
            summary.Add($"Setups and changeovers: {Fmt(changeovers.Sum(r => (decimal)r.Cells[6]!))} min actual against {Fmt(changeovers.Sum(r => (decimal)r.Cells[7]!))} min expected.");
        }

        var unproven = rows.Count(r => r.Cells[9] is not string text || !text.StartsWith("Proven", StringComparison.Ordinal));
        summary.Add(rows.Count == 0 ? "No stoppages in the period." : $"{unproven} of {rows.Count} without a proven root cause.");
        return new ReportBody(rows, summary, ["Stoppages", "Reason codes", "Root causes", "Routing setup minutes"]);
    }

    private static async Task<ReportBody> QuantityStatesAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var execution = run.Uow.Store<IExecutionLookup>();
        var orders = new List<OrderInfo>();
        foreach (var summary in await execution.ListOrdersAsync(ct))
        {
            var current = summary.Status == OrderStatus.Released || (summary.Status is OrderStatus.Held or OrderStatus.Completed && summary.ReleasedAt is { } at && run.InPeriod(at));
            if (!current || !run.Visible(summary.ProgramId) || !ReportRun.Is(summary.PartNumber, p.PartNumber) || !ReportRun.Is(summary.Status.ToString(), p.Status))
            {
                continue;
            }

            if (await execution.FindOrderAsync(summary.OrderNumber, ct) is { } order)
            {
                orders.Add(order);
            }
        }

        var rows = new List<ReportRow>();
        foreach (var q in await ProductionActualsQueries.StatesAsync(run.Uow, orders, ct))
        {
            if (!run.Matches(Id(q.OrderNumber), q.PartNumber, q.Status))
            {
                continue;
            }

            rows.Add(new ReportRow(
                [Id(q.OrderNumber), q.PartNumber, q.Status, q.Of("Ordered"), q.Of("Started"), q.Of("Produced"), q.Of("Accepted"), q.Of("In process"), q.Of("Rework"), q.Of("Repair"),
                 q.Of("Pending inspection"), q.Of("Released"), q.Of("Hold"), q.Of("Staged"), q.Of("Shipped"), q.Of("Scrapped")],
                ["order:" + Id(q.OrderNumber), "item:" + q.PartNumber]));
        }

        decimal Sum(int cell) => rows.Sum(r => (decimal)r.Cells[cell]!);
        return new ReportBody(rows,
            [$"{rows.Count} order(s): {Fmt(Sum(5))} produced, {Fmt(Sum(7))} in process, {Fmt(Sum(8) + Sum(9))} in rework or repair, {Fmt(Sum(15))} scrapped."],
            ["Production reports", "Rework records", "Inspections", "NCRs", "Inventory ledger"]);
    }

    private static async Task<ReportBody> DowntimeAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var windows = await run.Uow.Store<IMaintenanceLookup>().ListDowntimeAsync(run.From, run.To, ct);
        var end = run.To < run.Now ? run.To : run.Now;
        var rows = windows
            .Where(w => ReportRun.Is(w.MachineCode, p.Machine) && ReportRun.Is(w.Kind.ToString(), p.Status) && run.Matches(w.MachineCode, w.Reason, w.Kind.ToString()))
            .OrderBy(w => w.StartsAt).ThenBy(w => w.MachineCode, StringComparer.Ordinal)
            .Select(w =>
            {
                var from = w.StartsAt > run.From ? w.StartsAt : run.From;
                var until = w.EndsAt is { } e && e < end ? e : end;
                var hours = until > from ? ReportRun.Hours(until - from) : 0m;
                List<string> trace = ["machine:" + w.MachineCode];
                if (w.Kind != DowntimeKind.MaintenanceOverdue && w.Id > 0)
                {
                    trace.Add("downtime:" + Id(w.Id));
                }

                return new ReportRow([w.StartsAt, w.MachineCode, w.Kind.ToString(), w.EndsAt, hours, w.Reason], trace);
            })
            .ToList();
        var summary = rows.GroupBy(r => (string)r.Cells[2]!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: {g.Count()} windows, {Fmt(g.Sum(r => (decimal)r.Cells[4]!))} h in the period.")
            .ToList();
        if (rows.Count == 0)
        {
            summary.Add("No downtime in the period.");
        }

        return new ReportBody(rows, summary, ["Downtime records", "PM work orders (overdue lockouts)"]);
    }

    private static async Task<ReportBody> MaintenanceAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var orders = await run.Uow.Store<IMaintenanceLookup>().ListWorkOrdersAsync(true, ct);
        var rows = new List<ReportRow>();
        foreach (var w in orders)
        {
            var preventive = w.Kind == WorkOrderKind.Preventive;
            var dueHere = preventive && w.DueDate is { } d && run.InPeriod(d) && w.Status != WorkOrderStatus.Cancelled;
            var relevant = dueHere
                || (w.CompletedAt is { } c && run.InPeriod(c))
                || (!preventive && run.InPeriod(w.CreatedAt));
            if (!relevant || !ReportRun.Is(w.MachineCode, p.Machine) || !ReportRun.Is(w.Status.ToString(), p.Status) || !run.Matches(w.MachineCode, w.Title, w.Kind.ToString()))
            {
                continue;
            }

            var completedDate = w.CompletedAt is { } at ? run.Local(at) : (DateOnly?)null;
            var onTime = dueHere && completedDate is { } cd && cd <= w.DueDate!.Value;
            decimal? daysLate = !dueHere ? null
                : completedDate is { } done ? Math.Max(0, done.DayNumber - w.DueDate!.Value.DayNumber)
                : w.DueDate!.Value < run.Today ? run.Today.DayNumber - w.DueDate.Value.DayNumber
                : null;
            decimal? repair = !preventive && w.CompletedAt is { } finished ? ReportRun.Hours(finished - w.CreatedAt) : null;
            rows.Add(new ReportRow(
                [w.DueDate, Id(w.WorkOrderId), w.MachineCode, w.Kind.ToString(), w.Title, w.Status.ToString(), w.CompletedAt, dueHere ? 1m : 0m, onTime ? 1m : 0m, daysLate, repair],
                ["work-order:" + Id(w.WorkOrderId), "machine:" + w.MachineCode]));
        }

        rows = rows.OrderBy(r => r.Cells[0] is DateOnly d ? d : DateOnly.MaxValue).ThenBy(r => (string)r.Cells[1]!, StringComparer.Ordinal).ToList();
        var due = rows.Sum(r => (decimal)r.Cells[7]!);
        var onTimeCount = rows.Sum(r => (decimal)r.Cells[8]!);
        List<string> summary = due == 0
            ? ["No preventive work was due in the period."]
            : [$"PM compliance: {Fmt(decimal.Round(onTimeCount / due * 100m, 1))}% ({Fmt(onTimeCount)} of {Fmt(due)} due PM work orders done by their due date)."];
        var corrective = rows.Where(r => (string)r.Cells[3]! == nameof(WorkOrderKind.Corrective)).ToList();
        if (corrective.Count > 0)
        {
            var repaired = corrective.Where(r => r.Cells[10] is decimal).Select(r => (decimal)r.Cells[10]!).ToList();
            summary.Add($"{corrective.Count} corrective work orders" + (repaired.Count > 0 ? $"; average {Fmt(decimal.Round(repaired.Average(), 1))} h from raised to repaired." : "."));
        }

        return new ReportBody(rows, summary, ["Maintenance work orders"]);
    }

    // ------------------------------------------------------------------ delivery

    private static async Task<ReportBody> DeliveryAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var demand = (await run.Uow.Store<IDemandStore>().ListAsync(true, ct))
            .Where(d => run.InPeriod(d.NeedDate) && run.Visible(d.ProgramId) && ReportRun.Is(d.PartNumber, p.PartNumber))
            .Where(d => d.Status != DemandStatus.Cancelled || string.Equals(p.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var shipments = demand.Count == 0 ? [] : await run.Uow.Store<IInventoryLookup>().ListShippedForDemandsAsync(demand.Select(d => d.DemandId).ToList(), ct);
        var byDemand = shipments.ToLookup(s => s.DemandId!.Value);
        var rows = new List<ReportRow>();
        foreach (var d in demand)
        {
            var sent = byDemand[d.DemandId]
                .Select(s => (s.ShipmentId, Date: run.Local(s.ShippedAt!.Value), Quantity: s.Lines.Where(l => l.PartNumber == d.PartNumber).Sum(l => l.Quantity)))
                .Where(s => s.Quantity > 0)
                .ToList();
            var shipped = sent.Sum(s => s.Quantity);
            var onTime = Math.Min(d.Quantity, sent.Where(s => s.Date <= d.NeedDate).Sum(s => s.Quantity));
            DateOnly? last = sent.Count > 0 ? sent.Max(s => s.Date) : null;
            DateOnly? completeOn = null;
            var running = 0m;
            foreach (var s in sent.OrderBy(s => s.Date))
            {
                running += s.Quantity;
                if (running >= d.Quantity)
                {
                    completeOn = s.Date;
                    break;
                }
            }

            var status = d.Status == DemandStatus.Cancelled ? "Cancelled"
                : onTime >= d.Quantity ? "On time"
                : completeOn is not null ? "Late"
                : d.Status == DemandStatus.Closed ? "Closed"
                : d.NeedDate < run.Today ? "Overdue"
                : "Open";
            decimal? daysLate = status switch
            {
                "Late" => completeOn!.Value.DayNumber - d.NeedDate.DayNumber,
                "Overdue" => run.Today.DayNumber - d.NeedDate.DayNumber,
                _ => null,
            };
            if (!ReportRun.Is(status, p.Status) || !run.Matches(Id(d.DemandId), d.PartNumber, d.Customer, d.Reference, d.Kind.ToString()))
            {
                continue;
            }

            List<string> trace = ["demand:" + Id(d.DemandId), .. sent.Select(s => "shipment:" + Id(s.ShipmentId))];
            if (d.SourceKey is { } key)
            {
                trace.Add(key);
            }

            rows.Add(new ReportRow(
                [d.NeedDate, Id(d.DemandId), d.Kind.ToString(), d.PartNumber, d.Customer, d.Reference, d.Status == DemandStatus.Cancelled ? 0m : d.Quantity, onTime, shipped, last, status, daysLate],
                trace));
        }

        var qty = rows.Sum(r => (decimal)r.Cells[6]!);
        var ontime = rows.Sum(r => (decimal)r.Cells[7]!);
        var lines = new List<string>
        {
            qty == 0 ? "No demand was due in the period." : $"On-time quantity: {Fmt(decimal.Round(ontime / qty * 100m, 1))}% ({Fmt(ontime)} of {Fmt(qty)} units shipped by the need date).",
        };
        var counts = rows.GroupBy(r => (string)r.Cells[10]!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}");
        if (rows.Count > 0)
        {
            lines.Add(string.Join(", ", counts) + ".");
        }

        return new ReportBody(rows, lines, ["Demand", "Shipments"]);
    }

    private static async Task<ReportBody> ProgramDeliveriesAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var lookup = run.Uow.Store<IProgramsLookup>();
        var programs = (await lookup.ListProgramsAsync(ct)).Where(x => run.Visible(x.ProgramId)).ToList();
        var demand = (await run.Uow.Store<IDemandStore>().ListAsync(true, ct)).Where(d => d.SourceKey is not null).ToDictionary(d => d.SourceKey!, StringComparer.Ordinal);
        var shipped = (await run.Uow.Store<IInventoryLookup>().ListShippedByDemandAsync(ct)).GroupBy(s => s.DemandId).ToDictionary(g => g.Key, g => g.Sum(s => s.Shipped));
        var rows = new List<ReportRow>();
        foreach (var program in programs)
        {
            foreach (var contract in await lookup.ListContractsAsync(program.ProgramId, ct))
            {
                foreach (var d in contract.Deliveries.Where(d => run.InPeriod(d.DueDate)))
                {
                    var clin = contract.Clins.FirstOrDefault(c => c.Clin == d.Clin);
                    demand.TryGetValue(DeliveryReference.SourceKeyFor(d.DeliveryId), out var planned);
                    var sent = planned is null ? 0 : shipped.GetValueOrDefault(planned.DemandId);
                    var state = d.Cancelled ? "Cancelled"
                        : sent >= d.Quantity ? "Delivered"
                        : d.DueDate < run.Today ? "Overdue"
                        : planned is null ? "Not planned"
                        : sent > 0 ? "Partly delivered"
                        : "Planned";
                    if (!ReportRun.Is(clin?.PartNumber, p.PartNumber) || !ReportRun.Is(state, p.Status) || !run.Matches(contract.Number, d.Clin, clin?.PartNumber, program.Code, state))
                    {
                        continue;
                    }

                    var price = clin?.UnitPrice ?? 0;
                    var quantity = d.Cancelled ? 0 : d.Quantity;
                    List<string> trace = ["program:" + program.ProgramId.ToString("D", CultureInfo.InvariantCulture), "contract:" + Id(contract.ContractId), "delivery:" + Id(d.DeliveryId)];
                    if (planned is not null)
                    {
                        trace.Add("demand:" + Id(planned.DemandId));
                    }

                    rows.Add(new ReportRow(
                        [d.DueDate, program.Code, contract.Number, d.Clin, clin?.PartNumber, quantity, Math.Min(sent, quantity), state, quantity * price, Math.Min(sent, quantity) * price],
                        trace));
                }
            }
        }

        rows = rows.OrderBy(r => (DateOnly)r.Cells[0]!).ThenBy(r => (string)r.Cells[2]!, StringComparer.Ordinal).ToList();
        var value = rows.Sum(r => (decimal)r.Cells[8]!);
        var delivered = rows.Sum(r => (decimal)r.Cells[9]!);
        var units = rows.Sum(r => (decimal)r.Cells[5]!);
        var sentUnits = rows.Sum(r => (decimal)r.Cells[6]!);
        return new ReportBody(
            rows,
            [$"{rows.Count} deliveries due across {programs.Count} program(s): {Fmt(sentUnits)} of {Fmt(units)} units shipped, {delivered.ToString("C0", CultureInfo.GetCultureInfo("en-US"))} of {value.ToString("C0", CultureInfo.GetCultureInfo("en-US"))} by value."],
            ["Contracts and deliveries", "Contract demand", "Shipments"]);
    }

    // ------------------------------------------------------------------ quality

    private static async Task<ReportBody> NcrsAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var orders = await OrdersAsync(run, ct);
        var ncrs = await run.Uow.Store<IQualityLookup>().ListNcrsRaisedBetweenAsync(run.From, run.To, ct);
        var rows = new List<ReportRow>();
        foreach (var n in ncrs)
        {
            var program = n.OrderNumber is { } o && orders.TryGetValue(o, out var order) ? order.ProgramId : null;
            if (!run.Visible(program) || !ReportRun.Is(n.PartNumber, p.PartNumber) || !ReportRun.Is(n.Status.ToString(), p.Status)
                || !run.Matches(Id(n.NcrId), n.PartNumber, n.Defect, n.Description, n.LotNumber))
            {
                continue;
            }

            var days = decimal.Round((decimal)((n.ClosedAt ?? run.Now) - n.RaisedAt).TotalDays, 1, MidpointRounding.AwayFromZero);
            List<string> trace = ["ncr:" + Id(n.NcrId)];
            if (n.OrderNumber is { } on)
            {
                trace.Add("order:" + Id(on));
            }

            rows.Add(new ReportRow(
                [n.RaisedAt, Id(n.NcrId), n.PartNumber, n.OrderNumber is { } x ? Id(x) : null, n.Sequence is { } s ? Seq(s) : null, n.Defect, n.Quantity, n.Status.ToString(), n.Disposition?.ToString(), n.ClosedAt, days],
                trace));
        }

        var summary = new List<string> { $"{rows.Count} NCRs raised, {Fmt(rows.Sum(r => (decimal)r.Cells[6]!))} units affected." };
        var top = rows.GroupBy(r => (string)r.Cells[5]!, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(3).ToList();
        if (top.Count > 0)
        {
            summary.Add("Most frequent defects: " + string.Join("; ", top.Select(g => $"{g.Key} ({g.Count()})")) + ".");
        }

        return new ReportBody(rows, summary, ["Nonconformance reports"]);
    }

    private static async Task<ReportBody> InspectionsAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var orders = await OrdersAsync(run, ct);
        var inspections = await run.Uow.Store<IQualityLookup>().ListInspectionsBetweenAsync(run.From, run.To, ct);
        var rows = inspections
            .Where(i => run.Visible(orders.TryGetValue(i.OrderNumber, out var o) ? o.ProgramId : null)
                && ReportRun.Is(i.PartNumber, p.PartNumber) && ReportRun.Is(i.Result.ToString(), p.Status) && run.Matches(Id(i.InspectionId), i.PartNumber, i.Notes, i.GaugeCode))
            .Select(i =>
            {
                List<string> trace = ["inspection:" + Id(i.InspectionId), "order:" + Id(i.OrderNumber)];
                if (i.NcrId is { } ncr)
                {
                    trace.Add("ncr:" + Id(ncr));
                }

                return new ReportRow(
                    [i.At, Id(i.InspectionId), Id(i.OrderNumber), Seq(i.Sequence), i.PartNumber, i.QuantityInspected, i.Result == InspectionResult.Pass ? i.QuantityInspected : 0m, i.Result.ToString(), i.Inspector, i.NcrId is { } n ? Id(n) : null],
                    trace);
            })
            .ToList();
        var inspected = rows.Sum(r => (decimal)r.Cells[5]!);
        var accepted = rows.Sum(r => (decimal)r.Cells[6]!);
        List<string> summary = inspected == 0
            ? ["No inspections in the period."]
            : [$"First-pass acceptance: {Fmt(decimal.Round(accepted / inspected * 100m, 1))}% ({Fmt(accepted)} of {Fmt(inspected)} inspected units); {rows.Count(r => (string)r.Cells[7]! == "Fail")} failed inspections."];
        return new ReportBody(rows, summary, ["Inspection records"]);
    }

    // ------------------------------------------------------------------ workforce

    private static async Task<ReportBody> QualificationsAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var workers = (await run.Uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(run.Now, ct)).Where(w => w.IsActive).ToDictionary(w => w.PersonId);
        var competency = run.Uow.Store<ICompetencyLookup>();
        var names = (await competency.ListTypesAsync(ct)).ToDictionary(t => t.Code, t => t.Name, StringComparer.Ordinal);
        var records = await competency.GetQualificationsAsync(workers.Keys.Select(id => new Kernel.Identity.PersonId(id)).ToList(), ct);
        var rows = new List<ReportRow>();
        foreach (var q in records.Where(q => q.Status == QualificationStatus.Qualified && q.ExpiresAt is { } e && run.InPeriod(e)).OrderBy(q => q.ExpiresAt).ThenBy(q => q.Code, StringComparer.Ordinal))
        {
            var w = workers[q.PersonId];
            var name = names.GetValueOrDefault(q.Code, q.Code);
            if (!ReportRun.Is(w.HomeWorkCenterCode, p.WorkCenter) || !ReportRun.Is(w.ShiftCode, p.Shift) || !run.Matches(w.DisplayName, q.Code, name))
            {
                continue;
            }

            var days = (decimal)(run.Local(q.ExpiresAt!.Value).DayNumber - run.Today.DayNumber);
            rows.Add(new ReportRow(
                [q.ExpiresAt, w.DisplayName, q.Code, name, days, w.HomeWorkCenterCode, w.ShiftCode],
                ["person:" + q.PersonId.ToString("D", CultureInfo.InvariantCulture), "qualification:" + q.Code]));
        }

        var soon = rows.Count(r => (decimal)r.Cells[4]! <= 30);
        List<string> summary = rows.Count == 0
            ? ["No qualifications expire in the period."]
            : [$"{rows.Count} qualification(s) expire in the period, {soon} within 30 days.",
               "Most affected: " + string.Join("; ", rows.GroupBy(r => (string)r.Cells[2]!, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(3).Select(g => $"{g.Key} ({g.Count()})")) + "."];
        return new ReportBody(rows, summary, ["Qualification records", "Workers"]);
    }

    // ------------------------------------------------------------------ inventory

    private static async Task<ReportBody> TransactionsAsync(ReportRun run, CancellationToken ct)
    {
        var p = run.Parameters;
        var orders = await OrdersAsync(run, ct);
        var all = await run.Uow.Store<IInventoryLookup>().ListTransactionsBetweenAsync(run.From, run.To, ct);
        var rows = new List<ReportRow>();
        foreach (var t in all)
        {
            var program = t.OrderNumber is { } o && orders.TryGetValue(o, out var order) ? order.ProgramId : null;
            if (!run.Visible(program) || !ReportRun.Is(t.PartNumber, p.PartNumber) || !ReportRun.Is(t.Kind.ToString(), p.Status))
            {
                continue;
            }

            var (reference, trace) = t switch
            {
                { OrderNumber: { } n } => ("Order " + Id(n), "order:" + Id(n)),
                { PoNumber: { } po } => ($"PO {Id(po)}" + (t.PoLine is { } l ? "/" + Seq(l) : string.Empty), "po:" + Id(po)),
                { ShipmentId: { } s } => ("Shipment " + Id(s), "shipment:" + Id(s)),
                { WorkOrderId: { } w } => ("Work order " + Id(w), "work-order:" + Id(w)),
                { RequestId: { } r } => ("Request " + Id(r), "request:" + Id(r)),
                _ => ((string?)null, (string?)null),
            };
            string? Place(string? location, StockStatus? status) => location is null ? null : status is { } s && s != StockStatus.Released ? $"{location} ({s})" : location;
            if (!run.Matches(t.PartNumber, t.LotNumber, t.Reason, reference, t.FromLocation, t.ToLocation))
            {
                continue;
            }

            rows.Add(new ReportRow(
                [t.At, Id(t.TransactionId), t.Kind.ToString(), t.PartNumber, t.Quantity, Place(t.FromLocation, t.FromStatus), Place(t.ToLocation, t.ToStatus), t.LotNumber, reference, t.Actor, t.ApprovedBy, t.Reason],
                trace is null ? ["transaction:" + Id(t.TransactionId)] : ["transaction:" + Id(t.TransactionId), trace]));
        }

        var summary = rows.GroupBy(r => (string)r.Cells[2]!, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: {g.Count()}").ToList();
        return new ReportBody(rows, summary.Count == 0 ? ["No transactions in the period."] : [$"{rows.Count} transactions — " + string.Join(", ", summary) + "."], ["Inventory transaction ledger"]);
    }
}
