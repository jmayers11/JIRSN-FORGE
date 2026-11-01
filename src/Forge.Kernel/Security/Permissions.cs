namespace Forge.Kernel.Security;

/// <summary>Actions that can be authorized. Named "context.area.verb".</summary>
public static class Permissions
{
    public const string AccessManage = "kernel.access.manage";
    public const string DelegationPropose = "kernel.delegations.propose";
    public const string PeopleRead = "kernel.people.read";
    public const string AuditRead = "kernel.audit.read";
    public const string NotificationsRead = "notifications.read";
    public const string NotificationsAcknowledge = "notifications.acknowledge";
    public const string NotificationRulesManage = "notifications.rules.manage";
    public const string WorkAssignmentsManage = "work.assignments.manage";

    public const string ResourcesManage = "resources.manage";
    public const string ResourcesFloorMapManage = "resources.floor-map.manage";
    public const string ResourcesRead = "resources.read";
    public const string ToolsManage = "resources.tools.manage";
    public const string ToolsCalibrate = "resources.tools.calibrate";
    public const string ToolsHandle = "resources.tools.handle";

    public const string WorkforceManage = "workforce.manage";
    public const string WorkforcePhotosManage = "workforce.photos.manage";
    public const string WorkforceSupervise = "workforce.supervise";
    public const string WorkforceRead = "workforce.read";
    public const string WorkforceDevelopmentNominate = "workforce.development.nominate";
    public const string WorkforceNominationReview = "workforce.development.review";
    public const string WorkforceNominationRead = "workforce.development.read";
    public const string WorkforceCompensationRead = "workforce.compensation.read";

    public const string CompetencyManage = "competency.manage";
    public const string CompetencyCertify = "competency.certify";
    public const string CompetencyRead = "competency.read";
    public const string CompetencyMachineAuthorize = "competency.machine.authorize";

    public const string SafetyRulesDraft = "safety.rules.draft";
    public const string SafetyRulesApprove = "safety.rules.approve";
    public const string SafetyRulesRead = "safety.rules.read";

    public const string ReadinessRead = "planning.readiness.read";

    public const string EngineeringManage = "engineering.manage";
    public const string EngineeringRelease = "engineering.release";
    public const string EngineeringRead = "engineering.read";

    /// <summary>Record a manufacturer part for an item, or what its manufacturer says about its lifecycle.</summary>
    public const string CotsPropose = "engineering.cots.propose";

    /// <summary>Approve or withdraw manufacturer parts (V2 §41: modify COTS approved equivalents).</summary>
    public const string CotsManage = "engineering.cots.manage";

    /// <summary>Create controlled documents, draft revisions and submit them for review (V2 §41).</summary>
    public const string DocumentsManage = "engineering.documents.manage";

    /// <summary>Approve or return a document revision someone else wrote.</summary>
    public const string DocumentsApprove = "engineering.documents.approve";

    /// <summary>Release an approved document revision, record which external revision applies, or make one obsolete.</summary>
    public const string DocumentsRelease = "engineering.documents.release";

    /// <summary>Attach a file or a document to a record (order, purchase order, work order, NCR...) and detach it.</summary>
    public const string DocumentsAttach = "engineering.documents.attach";

    /// <summary>Draft engineering changes, list their revisions and submit them for review (V2.4).</summary>
    public const string ChangesManage = "engineering.changes.manage";

    /// <summary>Decide a required review of an engineering change (the review kind decides which roles).</summary>
    public const string ChangesReview = "engineering.changes.review";

    /// <summary>Change board approval of an engineering change.</summary>
    public const string ChangesApprove = "engineering.changes.approve";

    /// <summary>Propose an alternate relationship (V2.6). Never usable until approved by someone else.</summary>
    public const string AlternatesPropose = "engineering.alternates.propose";

    /// <summary>Approve, reject or expire alternates, and set BOM line substitution policies (V2.6).</summary>
    public const string AlternatesApprove = "engineering.alternates.approve";

    /// <summary>Approve, reject or expire maintenance-only alternates (V2.6).</summary>
    public const string MaintenanceAlternatesApprove = "maintenance.alternates.approve";

    public const string OrdersManage = "execution.orders.manage";
    public const string OrdersRelease = "execution.orders.release";
    public const string OrdersRead = "execution.orders.read";

    public const string DowntimeReport = "maintenance.downtime.report";
    public const string MaintenancePlan = "maintenance.plan";
    public const string MaintenanceRead = "maintenance.read";
    public const string MaintenanceWork = "maintenance.work";
    public const string MaintenanceProgram = "maintenance.pm.manage";
    public const string MaintenanceApprove = "maintenance.approve";

    /// <summary>Approve or reject a proposed outage after its schedule preview (V2.9); never the proposer.</summary>
    public const string OutageApprove = "maintenance.outage.approve";

    /// <summary>Create and change asset records, their lifecycle and their as-delivered components (V2.8).</summary>
    public const string AssetsManage = "maintenance.assets.manage";

    /// <summary>Set or clear an item's MRO stocking levels (V2.8).</summary>
    public const string MroManage = "inventory.mro.manage";

    /// <summary>Record obsolescence with evidence and propose a replacement (V2.8). Approval of a replacement needs engineering.alternates.approve.</summary>
    public const string ObsolescenceRecord = "engineering.obsolescence.record";

    public const string ScheduleRead = "planning.schedule.read";
    public const string SchedulePropose = "planning.schedule.propose";
    public const string ScheduleApprove = "planning.schedule.approve";
    public const string ConstraintsManage = "planning.constraints.manage";

    public const string BadgesManage = "workforce.badges.manage";
    public const string AttendanceRecord = "workforce.attendance.record";
    public const string AttendanceIntelligenceRead = "workforce.attendance-intelligence.read";

    /// <summary>Reads training requests and new-employee training checklists.</summary>
    public const string TrainingRequestsRead = "workforce.training-requests.read";

    /// <summary>Asks for training to be scheduled for a person (supervisors, HR, trainers).</summary>
    public const string TrainingRequestsRequest = "workforce.training-requests.request";

    /// <summary>Schedules, completes or cancels training requests (technical trainers, training coordinators).</summary>
    public const string TrainingRequestsManage = "workforce.training-requests.manage";

    /// <summary>Defines the training every new employee (or every new employee in a work center) must complete.</summary>
    public const string OnboardingManage = "workforce.onboarding.manage";

    /// <summary>Read the costing policy, standard costs and cost reconciliations (Sprint 10 WW-S10-24).</summary>
    public const string CostingRead = "costing.read";

    /// <summary>Set the costing policy, standard costs and labor and overhead rates.</summary>
    public const string CostingManage = "costing.manage";

    /// <summary>Read customers, quotes, sales orders and invoices (owner decisions 2026-10-31).</summary>
    public const string SalesRead = "sales.read";

    /// <summary>Keep customers and draft quotes and sales orders.</summary>
    public const string SalesManage = "sales.manage";

    /// <summary>Approve quotes and sales orders written by someone else, and put customers on hold.</summary>
    public const string SalesApprove = "sales.approve";

    /// <summary>Issue and void invoices of shipped quantities.</summary>
    public const string BillingManage = "sales.billing.manage";

    /// <summary>Export invoices and journal entries for the accounting system.</summary>
    public const string AccountingExport = "accounting.export";

    /// <summary>Record, correct and sign one's own timesheet (owner decision 2026-10-31: DCAA-style timekeeping).</summary>
    public const string TimekeepingRecord = "timekeeping.record";

    /// <summary>Approve or return other people's signed timesheets.</summary>
    public const string TimekeepingApprove = "timekeeping.approve";

    /// <summary>Read everyone's timesheets.</summary>
    public const string TimekeepingRead = "timekeeping.read";

    /// <summary>Keep the charge codes time is recorded against.</summary>
    public const string TimekeepingCodesManage = "timekeeping.codes.manage";

    /// <summary>Propose an amendment to a released production order (owner decision 2026-10-31: manufacturing engineers).</summary>
    public const string OrderAmendPropose = "execution.orders.amend.propose";

    /// <summary>Approve, and so apply, an amendment someone else proposed (planners).</summary>
    public const string OrderAmendApprove = "execution.orders.amend.approve";

    /// <summary>Reserve stock for released orders and approved sales orders, and bring reservations in step.</summary>
    public const string InventoryReserve = "inventory.reserve";
    public const string AttendanceScheduleManage = "workforce.attendance-schedule.manage";
    public const string AttendancePolicyManage = "workforce.attendance-policy.manage";
    public const string AttendanceClassificationRecord = "workforce.attendance-classification.record";
    public const string AttendanceClassificationReview = "workforce.attendance-classification.review";
    public const string AttendanceCorrectionRequest = "workforce.attendance-correction.request";
    public const string AttendanceImport = "workforce.attendance.import";
    public const string ShopFloorRecord = "execution.shopfloor.record";
    public const string ShopFloorRead = "execution.shopfloor.read";
    public const string ProductionReasonsManage = "execution.reasons.manage";
    public const string RootCauseConfirm = "execution.rootcause.confirm";

    /// <summary>Place a hold of any kind (V3.10). Releasing needs the kind's release authority.</summary>
    public const string HoldsPlace = "holds.place";

    /// <summary>Release authority for production holds (V3.10).</summary>
    public const string HoldsRelease = "holds.production.release";

    /// <summary>See the hold register (V3.10).</summary>
    public const string HoldsRead = "holds.read";
    public const string GoalsManage = "planning.goals.manage";

    public const string ItemPlanningManage = "engineering.planning.manage";

    public const string InventoryTransact = "inventory.transact";
    public const string InventoryMaterialRequest = "inventory.material-request";

    /// <summary>Ask for inventory to be scrapped (Sprint 10 §20). Approval, Quality disposition and execution are separate rights.</summary>
    public const string InventoryScrapRequest = "inventory.scrap.request";
    public const string InventoryCustodyReceive = "inventory.custody.receive";
    public const string InventoryApprove = "inventory.approve";
    public const string InventoryManage = "inventory.manage";
    public const string InventoryRead = "inventory.read";

    public const string PurchasingManage = "procurement.manage";
    public const string PurchasingApprove = "procurement.approve";
    public const string PurchasingRead = "procurement.read";

    /// <summary>Raise a requisition (V2.7): ask purchasing to buy something, saying what it is for.</summary>
    public const string RequisitionRaise = "procurement.requisition.raise";

    public const string QualityPlan = "quality.plan";
    public const string QualityInspect = "quality.inspect";
    public const string QualityDisposition = "quality.disposition";
    public const string QualityApprove = "quality.approve";
    public const string QualityCommunicationsApprove = "quality.communications.approve";
    public const string QualityRead = "quality.read";
    public const string QualityReportSuspect = "quality.suspect.report";
    public const string AuditManage = "quality.audit.manage";
    public const string AuditConduct = "quality.audit.conduct";
    public const string AuditApprove = "quality.audit.approve";
    /// <summary>Decide the exact product quantity/identity scope released by Quality, separately from physical shipment.</summary>
    public const string QualityProductRelease = "quality.product.release";

    /// <summary>Read governed QMS records in their actual organization/facility/program scope.</summary>
    public const string QmsRead = "quality.qms.read";
    public const string IdentificationRead = "identification.read";
    public const string IdentificationLabelsManage = "identification.labels.manage";
    public const string IdentificationLabelsApprove = "identification.labels.approve";
    public const string IdentificationLabelsPrint = "identification.labels.print";
    public const string IdentificationAliasesManage = "identification.aliases.manage";
    public const string IdentificationScanStage = "identification.scan.stage";
    public const string QmsManage = "quality.qms.manage";
    public const string QmsReview = "quality.qms.review";
    public const string QmsApprove = "quality.qms.approve";
    public const string QmsPublish = "quality.qms.publish";

    public const string ShippingRelease = "inventory.shipping.ship";

    /// <summary>Issue and return spare parts on maintenance work orders.</summary>
    public const string SparesIssue = "inventory.spares.issue";

    public const string DemandManage = "planning.demand.manage";
    public const string MaterialPlanRead = "planning.mrp.read";

    /// <summary>Open the Reports page and keep saved report definitions. Each report also needs the read permission of what it reports on.</summary>
    public const string ReportsSave = "planning.reports.save";

    /// <summary>Ask Forge Analyst. Every answer still needs the read permission of what it is about.</summary>
    public const string AnalystUse = "planning.analyst.use";

    /// <summary>Sprint 9 demo tenant: advance the simulated clock and capture or compare a baseline. Only on a designated demo database.</summary>
    public const string DemoControl = "demo.control";

    /// <summary>Sprint 9 demo tenant: designate a demo-only database and reset it to its saved baseline (the current state is archived).</summary>
    public const string DemoReset = "demo.reset";

    public const string ProgramsManage = "programs.manage";
    public const string ProgramsRead = "programs.read";

    /// <summary>Program codes and names only, to grant access. Grants no program data.</summary>
    public const string ProgramsDirectory = "programs.directory";

    /// <summary>Facility and capital projects (V2.10): read, prepare and request changes, approve.</summary>
    public const string ProjectsRead = "projects.read";

    public const string ProjectsManage = "projects.manage";

    public const string ProjectsApprove = "projects.approve";
}

/// <summary>
/// Functional roles and the permissions they carry. In v1 the mapping is code, reviewed like code.
/// Roles are deliberately narrow so duties can be separated (author vs approver, trainer vs certifier).
/// </summary>
public static class Roles
{
    /// <summary>Can view and request correction of the holder's own attendance records.</summary>
    public const string Employee = "Employee";
    public const string SecurityAdministrator = "SecurityAdministrator";
    public const string Auditor = "Auditor";
    public const string ManufacturingEngineer = "ManufacturingEngineer";
    public const string TrainingCoordinator = "TrainingCoordinator";

    /// <summary>Schedules and delivers training and is notified when someone needs it; certification stays with Competency roles.</summary>
    public const string TechnicalTrainer = "TechnicalTrainer";

    /// <summary>Keeps the costing policy, standard costs and labor and overhead rates (owner decision 2026-10-25).</summary>
    public const string CostAccountant = "CostAccountant";
    public const string Certifier = "Certifier";
    public const string SafetyEngineer = "SafetyEngineer";
    public const string SafetyApprover = "SafetyApprover";
    public const string HumanResources = "HumanResources";
    public const string AttendanceImporter = "AttendanceImporter";
    public const string Supervisor = "Supervisor";
    public const string Planner = "Planner";
    public const string EngineeringApprover = "EngineeringApprover";
    public const string MaintenanceTechnician = "MaintenanceTechnician";

    /// <summary>Moves material: receipts, issues, returns, transfers, scrap, counts and holds.</summary>
    public const string MaterialHandler = "MaterialHandler";

    /// <summary>Approves adjustments and count variances, releases held stock and sets up locations. Never their own requests.</summary>
    public const string InventoryController = "InventoryController";

    /// <summary>Raises purchase orders and keeps supplier dates current.</summary>
    public const string Buyer = "Buyer";

    /// <summary>Approves purchase orders raised by someone else.</summary>
    public const string PurchasingManager = "PurchasingManager";

    /// <summary>Inspects work they did not do, with calibrated gauges, and raises nonconformances.</summary>
    public const string Inspector = "Inspector";

    /// <summary>Writes inspection plans, keeps gauges calibrated, dispositions NCRs and owns corrective actions.</summary>
    public const string QualityEngineer = "QualityEngineer";

    /// <summary>Approves use-as-is dispositions and verifies corrective actions owned by someone else.</summary>
    public const string QualityManager = "QualityManager";
    public const string CustomerCommunicationsApprover = "CustomerCommunicationsApprover";

    public const string QmsAuthor = "QmsAuthor";
    public const string AuditPlanner = "AuditPlanner";
    public const string InternalAuditor = "InternalAuditor";
    public const string AuditApprover = "AuditApprover";
    public const string QmsReviewer = "QmsReviewer";
    public const string QmsApprover = "QmsApprover";
    public const string QmsPolicyConsumer = "QmsPolicyConsumer";
    public const string LabelAuthor = "LabelAuthor";
    public const string LabelApprover = "LabelApprover";
    public const string LabelPrinter = "LabelPrinter";
    public const string IdentificationAdministrator = "IdentificationAdministrator";
    public const string ScanOperator = "ScanOperator";

    /// <summary>Confirms shipments leave the building. Never the person who packed them.</summary>
    public const string Shipper = "Shipper";

    /// <summary>Owns programs and contracts: CLINs, delivery schedules, requirements and milestones.</summary>
    public const string ProgramManager = "ProgramManager";

    /// <summary>Owns preventive-maintenance plans, schedules maintenance work and signs off work done by technicians.</summary>
    public const string MaintenancePlanner = "MaintenancePlanner";

    /// <summary>
    /// A shop-floor terminal (kiosk, scanner station). It records what badged people do; the badge
    /// identifies the person, the terminal's own identity is kept in the audit as the actor.
    /// </summary>
    public const string ShopFloorTerminal = "ShopFloorTerminal";

    /// <summary>Keeps controlled documents: creates them, approves and releases revisions written by others (V2.2).</summary>
    public const string DocumentController = "DocumentController";

    /// <summary>Change control board: approves engineering changes reviewed by others and sets review policies (V2.4).</summary>
    public const string ChangeBoard = "ChangeBoard";

    /// <summary>Approves facility and capital projects and changes to them (V2.10).</summary>
    public const string FacilitiesManager = "FacilitiesManager";

    /// <summary>
    /// Answers for the whole plant (owner decision 2026-10-27): sees every area of Forge, including the people views supervisors use.
    /// It changes nothing by itself; each change stays with that area's own role, so separation of duties is unchanged.
    /// </summary>
    public const string PlantManager = "PlantManager";

    /// <summary>Keeps customers and drafts quotes and sales orders (owner decision 2026-10-31).</summary>
    public const string SalesRepresentative = "SalesRepresentative";

    /// <summary>Approves quotes and sales orders written by someone else and puts customers on hold.</summary>
    public const string SalesManager = "SalesManager";

    /// <summary>Invoices shipped quantities and exports them for the accounting system.</summary>
    public const string BillingClerk = "BillingClerk";

    private static readonly Dictionary<string, string[]> Map = new(StringComparer.Ordinal)
    {
        [Employee] = [Permissions.AttendanceCorrectionRequest],
        [SecurityAdministrator] = [Permissions.HoldsPlace, Permissions.HoldsRead, Permissions.AccessManage, Permissions.PeopleRead, Permissions.AuditRead, Permissions.BadgesManage, Permissions.ProgramsDirectory, Permissions.ResourcesManage, Permissions.ResourcesFloorMapManage, Permissions.ResourcesRead, Permissions.WorkforceManage, Permissions.WorkforcePhotosManage, Permissions.WorkforceRead, Permissions.AttendanceIntelligenceRead, Permissions.AttendanceScheduleManage, Permissions.AttendancePolicyManage, Permissions.AttendanceClassificationRecord, Permissions.AttendanceClassificationReview, Permissions.AttendanceCorrectionRequest, Permissions.AttendanceImport, Permissions.WorkforceDevelopmentNominate, Permissions.WorkforceNominationReview, Permissions.WorkforceNominationRead, Permissions.WorkforceCompensationRead, Permissions.CompetencyMachineAuthorize],
        [HumanResources] = [Permissions.PeopleRead, Permissions.WorkforceRead, Permissions.AttendanceIntelligenceRead, Permissions.AttendanceScheduleManage, Permissions.AttendancePolicyManage, Permissions.AttendanceClassificationRecord, Permissions.AttendanceClassificationReview, Permissions.AttendanceCorrectionRequest, Permissions.AttendanceImport, Permissions.WorkforceDevelopmentNominate, Permissions.WorkforceNominationReview, Permissions.WorkforceNominationRead, Permissions.WorkforceCompensationRead],
        [AttendanceImporter] = [Permissions.AttendanceImport],
        [Auditor] = [Permissions.HoldsRead, Permissions.AuditRead, Permissions.PeopleRead, Permissions.ProjectsRead],
        [QmsAuthor] = [Permissions.QmsRead, Permissions.QmsManage, Permissions.EngineeringRead],
        [AuditPlanner] = [Permissions.AuditManage, Permissions.QualityRead, Permissions.QmsRead, Permissions.EngineeringRead, Permissions.CompetencyRead],
        [InternalAuditor] = [Permissions.AuditConduct, Permissions.QualityRead, Permissions.QmsRead, Permissions.EngineeringRead, Permissions.CompetencyRead],
        [AuditApprover] = [Permissions.AuditApprove, Permissions.QualityRead, Permissions.QmsRead, Permissions.EngineeringRead, Permissions.CompetencyRead],
        [QmsReviewer] = [Permissions.QmsRead, Permissions.QmsReview, Permissions.EngineeringRead],
        [QmsApprover] = [Permissions.QmsRead, Permissions.QmsApprove, Permissions.QmsPublish, Permissions.EngineeringRead],
        [QmsPolicyConsumer] = [Permissions.QmsRead, Permissions.EngineeringRead],
        [LabelAuthor] = [Permissions.IdentificationRead, Permissions.IdentificationLabelsManage],
        [LabelApprover] = [Permissions.IdentificationRead, Permissions.IdentificationLabelsApprove],
        [LabelPrinter] = [Permissions.IdentificationRead, Permissions.IdentificationLabelsPrint],
        [IdentificationAdministrator] = [Permissions.IdentificationRead, Permissions.IdentificationAliasesManage],
        [ScanOperator] = [Permissions.IdentificationRead, Permissions.IdentificationScanStage],
        [ManufacturingEngineer] = [Permissions.HoldsPlace, Permissions.HoldsRead, Permissions.ToolsManage, Permissions.ProductionReasonsManage, Permissions.RootCauseConfirm, Permissions.AssetsManage, Permissions.ObsolescenceRecord, Permissions.ResourcesManage, Permissions.ResourcesFloorMapManage, Permissions.ResourcesRead, Permissions.WorkforceManage, Permissions.WorkforcePhotosManage, Permissions.WorkforceRead, Permissions.SafetyRulesRead, Permissions.CompetencyRead, Permissions.ReadinessRead, Permissions.EngineeringManage, Permissions.EngineeringRead, Permissions.OrdersManage, Permissions.OrdersRead, Permissions.MaintenanceRead, Permissions.ScheduleRead, Permissions.ShopFloorRead, Permissions.ItemPlanningManage, Permissions.InventoryRead, Permissions.MaterialPlanRead, Permissions.PurchasingRead, Permissions.QualityRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.CotsPropose, Permissions.DocumentsManage, Permissions.DocumentsAttach, Permissions.ChangesManage, Permissions.ChangesReview, Permissions.AlternatesPropose, Permissions.RequisitionRaise, Permissions.ProjectsRead, Permissions.ProjectsManage],
        [MaintenanceTechnician] = [Permissions.ToolsCalibrate, Permissions.ToolsHandle, Permissions.DowntimeReport, Permissions.MaintenancePlan, Permissions.MaintenanceRead, Permissions.MaintenanceWork, Permissions.ResourcesRead, Permissions.SparesIssue, Permissions.InventoryRead, Permissions.EngineeringRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.DocumentsAttach],
        [MaintenancePlanner] = [Permissions.HoldsRead, Permissions.ToolsManage, Permissions.ToolsHandle, Permissions.RootCauseConfirm, Permissions.OutageApprove, Permissions.AssetsManage, Permissions.MroManage, Permissions.ObsolescenceRecord, Permissions.MaintenanceProgram, Permissions.InventoryRead, Permissions.MaintenanceApprove, Permissions.MaintenancePlan, Permissions.MaintenanceRead, Permissions.DowntimeReport, Permissions.ResourcesRead, Permissions.ScheduleRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.CotsPropose, Permissions.EngineeringRead, Permissions.DocumentsAttach, Permissions.ChangesReview, Permissions.AlternatesPropose, Permissions.MaintenanceAlternatesApprove, Permissions.RequisitionRaise, Permissions.DemandManage, Permissions.ProjectsRead, Permissions.ProjectsManage],
        [ProgramManager] = [Permissions.HoldsPlace, Permissions.HoldsRead, Permissions.GoalsManage, Permissions.ProgramsManage, Permissions.ProgramsRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.MaterialPlanRead, Permissions.ScheduleRead, Permissions.InventoryRead, Permissions.QualityRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.DocumentsAttach, Permissions.ChangesReview, Permissions.RequisitionRaise, Permissions.ProjectsRead],
        [EngineeringApprover] = [Permissions.HoldsPlace, Permissions.HoldsRead, Permissions.EngineeringRelease, Permissions.EngineeringRead, Permissions.ResourcesRead, Permissions.CotsManage, Permissions.DocumentsApprove, Permissions.DocumentsRelease, Permissions.OrdersRead, Permissions.ProgramsRead, Permissions.ChangesReview, Permissions.ChangesApprove, Permissions.AlternatesApprove],
        [TechnicalTrainer] = [Permissions.WorkforceRead, Permissions.CompetencyRead, Permissions.ResourcesRead, Permissions.TrainingRequestsRead, Permissions.TrainingRequestsRequest, Permissions.TrainingRequestsManage, Permissions.OnboardingManage],
        [CostAccountant] = [Permissions.CostingRead, Permissions.CostingManage, Permissions.EngineeringRead, Permissions.InventoryRead, Permissions.OrdersRead, Permissions.QualityRead, Permissions.ResourcesRead],
        [TrainingCoordinator] = [Permissions.CompetencyManage, Permissions.CompetencyRead, Permissions.CompetencyMachineAuthorize, Permissions.WorkforceRead, Permissions.WorkforceDevelopmentNominate, Permissions.WorkforceNominationReview, Permissions.WorkforceNominationRead, Permissions.ResourcesRead, Permissions.EngineeringRead],
        [Certifier] = [Permissions.CompetencyCertify, Permissions.CompetencyRead, Permissions.CompetencyMachineAuthorize],
        [SafetyEngineer] = [Permissions.HoldsPlace, Permissions.HoldsRead, Permissions.SafetyRulesDraft, Permissions.SafetyRulesRead, Permissions.ResourcesRead, Permissions.CompetencyRead],
        [SafetyApprover] = [Permissions.HoldsPlace, Permissions.HoldsRead, Permissions.SafetyRulesApprove, Permissions.SafetyRulesRead, Permissions.ResourcesRead, Permissions.CompetencyRead],
        [Supervisor] = [Permissions.HoldsPlace, Permissions.HoldsRead, Permissions.HoldsRelease, Permissions.ToolsHandle, Permissions.WorkforceSupervise, Permissions.WorkforceRead, Permissions.WorkforceDevelopmentNominate, Permissions.WorkforceNominationRead, Permissions.WorkforceNominationReview, Permissions.ReadinessRead, Permissions.ResourcesRead, Permissions.CompetencyRead, Permissions.CompetencyMachineAuthorize, Permissions.SafetyRulesRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.DowntimeReport, Permissions.MaintenanceRead, Permissions.ScheduleRead, Permissions.ConstraintsManage, Permissions.ShopFloorRecord, Permissions.ShopFloorRead, Permissions.AttendanceRecord, Permissions.AttendanceScheduleManage, Permissions.AttendanceClassificationRecord, Permissions.AttendanceClassificationReview, Permissions.AttendanceCorrectionRequest, Permissions.InventoryRead, Permissions.InventoryMaterialRequest, Permissions.InventoryCustodyReceive, Permissions.QualityRead, Permissions.QualityReportSuspect, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.DocumentsAttach, Permissions.ChangesReview],
        [Planner] = [Permissions.HoldsPlace, Permissions.HoldsRead, Permissions.HoldsRelease, Permissions.GoalsManage, Permissions.OutageApprove, Permissions.MroManage, Permissions.ReadinessRead, Permissions.ResourcesRead, Permissions.WorkforceRead, Permissions.SafetyRulesRead, Permissions.OrdersManage, Permissions.OrdersRelease, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.MaintenanceRead, Permissions.ScheduleRead, Permissions.SchedulePropose, Permissions.ScheduleApprove, Permissions.ConstraintsManage, Permissions.ShopFloorRead, Permissions.ItemPlanningManage, Permissions.DemandManage, Permissions.MaterialPlanRead, Permissions.InventoryRead, Permissions.PurchasingRead, Permissions.QualityRead, Permissions.ProgramsRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.DocumentsAttach, Permissions.ChangesReview, Permissions.RequisitionRaise, Permissions.ProjectsRead, Permissions.InventoryScrapRequest],
        [MaterialHandler] = [Permissions.HoldsRead, Permissions.ToolsHandle, Permissions.InventoryTransact, Permissions.InventoryMaterialRequest, Permissions.InventoryCustodyReceive, Permissions.SparesIssue, Permissions.MaintenanceRead, Permissions.InventoryRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.PurchasingRead, Permissions.ResourcesRead, Permissions.QualityRead, Permissions.MaterialPlanRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.ProjectsRead],
        [InventoryController] = [Permissions.HoldsPlace, Permissions.HoldsRead, Permissions.MroManage, Permissions.InventoryApprove, Permissions.InventoryManage, Permissions.InventoryTransact, Permissions.InventoryCustodyReceive, Permissions.InventoryRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.PurchasingRead, Permissions.ResourcesRead, Permissions.MaterialPlanRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.ProjectsRead, Permissions.InventoryScrapRequest],
        [Buyer] = [Permissions.HoldsPlace, Permissions.HoldsRead, Permissions.ObsolescenceRecord, Permissions.PurchasingManage, Permissions.PurchasingRead, Permissions.InventoryRead, Permissions.EngineeringRead, Permissions.MaterialPlanRead, Permissions.OrdersRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.CotsPropose, Permissions.DocumentsAttach, Permissions.ChangesReview, Permissions.AlternatesPropose, Permissions.RequisitionRaise, Permissions.ProjectsRead],
        [Inspector] = [Permissions.HoldsPlace, Permissions.HoldsRead, Permissions.QualityInspect, Permissions.QualityRead, Permissions.QualityReportSuspect, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.InventoryRead, Permissions.ResourcesRead, Permissions.ShopFloorRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.DocumentsAttach],
        [QualityEngineer] = [Permissions.HoldsPlace, Permissions.HoldsRead, Permissions.ToolsManage, Permissions.ToolsCalibrate, Permissions.ToolsHandle, Permissions.RootCauseConfirm, Permissions.QualityPlan, Permissions.QualityDisposition, Permissions.QualityInspect, Permissions.QualityRead, Permissions.QualityReportSuspect, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.InventoryRead, Permissions.ResourcesRead, Permissions.ShopFloorRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.DocumentsManage, Permissions.DocumentsAttach, Permissions.ChangesReview, Permissions.AlternatesPropose, Permissions.RequisitionRaise, Permissions.DemandManage],
        [QualityManager] = [Permissions.HoldsPlace, Permissions.HoldsRead, Permissions.QualityApprove, Permissions.QualityRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.InventoryRead, Permissions.ResourcesRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.ChangesReview],
        [CustomerCommunicationsApprover] = [Permissions.QualityCommunicationsApprove, Permissions.QualityRead, Permissions.OrdersRead, Permissions.PurchasingRead, Permissions.EngineeringRead, Permissions.InventoryRead],
        [Shipper] = [Permissions.HoldsPlace, Permissions.HoldsRead, Permissions.ShippingRelease, Permissions.InventoryRead, Permissions.MaterialPlanRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.QualityRead, Permissions.ReportsSave, Permissions.AnalystUse],
        [PurchasingManager] = [Permissions.HoldsPlace, Permissions.HoldsRead, Permissions.PurchasingApprove, Permissions.PurchasingRead, Permissions.InventoryRead, Permissions.EngineeringRead, Permissions.MaterialPlanRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.DocumentsAttach, Permissions.ChangesReview, Permissions.AlternatesPropose, Permissions.RequisitionRaise, Permissions.ProjectsRead],
        [DocumentController] = [Permissions.DocumentsManage, Permissions.DocumentsApprove, Permissions.DocumentsRelease, Permissions.DocumentsAttach, Permissions.EngineeringRead, Permissions.OrdersRead, Permissions.PurchasingRead, Permissions.QualityRead, Permissions.MaintenanceRead, Permissions.ProgramsRead, Permissions.ResourcesRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.ChangesManage, Permissions.ChangesReview, Permissions.EngineeringRelease],
        [ChangeBoard] = [Permissions.HoldsPlace, Permissions.HoldsRead, Permissions.ChangesApprove, Permissions.ChangesReview, Permissions.EngineeringRead, Permissions.OrdersRead, Permissions.PurchasingRead, Permissions.InventoryRead, Permissions.QualityRead, Permissions.ProgramsRead, Permissions.ResourcesRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.AlternatesApprove],
        [FacilitiesManager] = [Permissions.ProjectsRead, Permissions.ProjectsManage, Permissions.ProjectsApprove, Permissions.RequisitionRaise, Permissions.DemandManage, Permissions.InventoryRead, Permissions.MaintenanceRead, Permissions.EngineeringRead, Permissions.PurchasingRead, Permissions.MaterialPlanRead, Permissions.ResourcesRead, Permissions.ResourcesFloorMapManage, Permissions.WorkforcePhotosManage, Permissions.WorkforceRead, Permissions.WorkforceSupervise, Permissions.CompetencyRead, Permissions.ScheduleRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.DocumentsAttach],
        [PlantManager] = [Permissions.AnalystUse, Permissions.ReportsSave],
        [SalesRepresentative] = [Permissions.SalesRead, Permissions.SalesManage, Permissions.EngineeringRead, Permissions.InventoryRead, Permissions.MaterialPlanRead],
        [SalesManager] = [Permissions.SalesRead, Permissions.SalesApprove, Permissions.EngineeringRead, Permissions.InventoryRead, Permissions.MaterialPlanRead, Permissions.ReportsSave, Permissions.AnalystUse],
        [BillingClerk] = [Permissions.SalesRead, Permissions.BillingManage, Permissions.AccountingExport, Permissions.InventoryRead],
        [ShopFloorTerminal] = [Permissions.ShopFloorRecord, Permissions.ShopFloorRead, Permissions.AttendanceRecord, Permissions.InventoryMaterialRequest, Permissions.QualityReportSuspect, Permissions.ReadinessRead, Permissions.OrdersRead, Permissions.ResourcesRead, Permissions.ScheduleRead],
    };

    /// <summary>
    /// Roles held only by system actors, never by people: they cannot be granted (not in <see cref="All"/>).
    /// </summary>
    private static readonly Dictionary<string, string[]> SystemMap = new(StringComparer.Ordinal)
    {
        [MaintenanceInstallation] = [Permissions.MaintenanceWork, Permissions.MaintenanceRead, Permissions.EngineeringRead, Permissions.InventoryRead],
        [ChangePropagation] = [Permissions.EngineeringRead, Permissions.OrdersRead, Permissions.OrdersManage, Permissions.OrdersRelease, Permissions.PurchasingRead, Permissions.WorkforceRead, Permissions.ChangesManage],
        [QualityReplan] = [Permissions.ScheduleRead, Permissions.SchedulePropose],
        [WorkforceMoves] = [Permissions.WorkforceRead, Permissions.WorkforceSupervise],
        [TrainingRequests] = [Permissions.WorkforceRead, Permissions.CompetencyRead, Permissions.TrainingRequestsRequest],
        [MaterialRisk] = [Permissions.MaterialPlanRead, Permissions.ReadinessRead, Permissions.OrdersRead],
        [SalesDemand] = [Permissions.DemandManage, Permissions.SalesRead, Permissions.EngineeringRead],
        [StockReservations] = [Permissions.InventoryReserve, Permissions.InventoryRead, Permissions.OrdersRead],
    };

    static Roles()
    {
        foreach (var role in Map.Keys.ToArray())
        {
            // Integration identities should receive only their explicitly mapped capabilities.
            if (role == AttendanceImporter) continue;
            Map[role] = [.. Map[role], Permissions.NotificationsRead, Permissions.NotificationsAcknowledge, Permissions.DelegationPropose];
        }
        Map[SecurityAdministrator] = [.. Map[SecurityAdministrator], Permissions.NotificationRulesManage];
        Map[Supervisor] = [.. Map[Supervisor], Permissions.NotificationRulesManage];
        Map[SecurityAdministrator] = [.. Map[SecurityAdministrator], Permissions.WorkAssignmentsManage];
        Map[Supervisor] = [.. Map[Supervisor], Permissions.WorkAssignmentsManage];
        Map[SecurityAdministrator] = [.. Map[SecurityAdministrator], Permissions.DemoControl, Permissions.DemoReset];
        Map[Planner] = [.. Map[Planner], Permissions.DemoControl, Permissions.CompetencyRead];
        Map[Supervisor] = [.. Map[Supervisor], Permissions.DemoControl];
        // Supervisors run quarterly and annual reviews (attendance included) and ask for training; trainers and coordinators schedule it.
        Map[Supervisor] = [.. Map[Supervisor], Permissions.AttendanceIntelligenceRead, Permissions.TrainingRequestsRead, Permissions.TrainingRequestsRequest];
        Map[TrainingCoordinator] = [.. Map[TrainingCoordinator], Permissions.TrainingRequestsRead, Permissions.TrainingRequestsRequest, Permissions.TrainingRequestsManage, Permissions.OnboardingManage];
        Map[HumanResources] = [.. Map[HumanResources], Permissions.TrainingRequestsRead, Permissions.TrainingRequestsRequest];
        // Costs are read where decisions use them; only cost accountants change them.
        Map[Planner] = [.. Map[Planner], Permissions.CostingRead];
        Map[InventoryController] = [.. Map[InventoryController], Permissions.CostingRead];
        Map[QualityManager] = [.. Map[QualityManager], Permissions.CostingRead];
        // Cost accountants and billing clerks hand Forge's figures to the accounting system (owner decision 2026-10-31).
        Map[CostAccountant] = [.. Map[CostAccountant], Permissions.AccountingExport];
        // Owner decision 2026-10-31: DCAA-style timesheets. Employees record and sign their own; supervisors approve; cost accountants keep the codes.
        Map[Employee] = [.. Map[Employee], Permissions.TimekeepingRecord];
        Map[Supervisor] = [.. Map[Supervisor], Permissions.TimekeepingApprove, Permissions.TimekeepingRead];
        Map[CostAccountant] = [.. Map[CostAccountant], Permissions.TimekeepingRead, Permissions.TimekeepingCodesManage];
        foreach (var role in new[] { HumanResources, Auditor })
            Map[role] = [.. Map[role], Permissions.TimekeepingRead];
        // Owner decisions 2026-10-31: engineers propose released-order amendments and planners approve them; inventory controllers keep reservations.
        Map[ManufacturingEngineer] = [.. Map[ManufacturingEngineer], Permissions.OrderAmendPropose];
        Map[Planner] = [.. Map[Planner], Permissions.OrderAmendApprove];
        Map[InventoryController] = [.. Map[InventoryController], Permissions.InventoryReserve];
        // Owner decision 2026-10-27: supervisors and managers answer to several areas, so they can see every area of Forge. Seeing is all
        // this adds; recording, proposing and approving stay with each area's role. People views (attendance, development packets,
        // training requests) go to supervisors and the plant manager only; pay, the audit trail and access administration go to nobody new.
        foreach (var role in OperationalOverview)
            Map[role] = [.. Map[role].Union(SeeEveryArea, StringComparer.Ordinal)];
        foreach (var role in new[] { Supervisor, PlantManager })
            Map[role] = [.. Map[role].Union(SeePeople, StringComparer.Ordinal)];
    }

    /// <summary>Roles that see every operational area (owner decision 2026-10-27).</summary>
    public static readonly IReadOnlyList<string> OperationalOverview = [Supervisor, PlantManager, QualityManager, PurchasingManager, ProgramManager, FacilitiesManager, SalesManager];

    /// <summary>Read access to every operational area of Forge.</summary>
    public static readonly IReadOnlyList<string> SeeEveryArea =
    [
        Permissions.ResourcesRead, Permissions.WorkforceRead, Permissions.CompetencyRead, Permissions.SafetyRulesRead, Permissions.ReadinessRead,
        Permissions.EngineeringRead, Permissions.OrdersRead, Permissions.MaintenanceRead, Permissions.ScheduleRead, Permissions.ShopFloorRead,
        Permissions.HoldsRead, Permissions.InventoryRead, Permissions.PurchasingRead, Permissions.QualityRead, Permissions.QmsRead,
        Permissions.IdentificationRead, Permissions.MaterialPlanRead, Permissions.ProgramsRead, Permissions.ProjectsRead, Permissions.CostingRead,
        Permissions.AnalystUse, Permissions.ReportsSave, Permissions.SalesRead,
    ];

    /// <summary>The people views a supervisor works from: attendance intelligence, development packets and training requests.</summary>
    public static readonly IReadOnlyList<string> SeePeople =
        [Permissions.AttendanceIntelligenceRead, Permissions.WorkforceNominationRead, Permissions.TrainingRequestsRead, Permissions.TimekeepingRead];

    /// <summary>The system role the propagation of a released engineering change acts with (V2.5).</summary>
    public const string ChangePropagation = "system.ChangePropagation";

    /// <summary>The system role that re-plans the schedule after a failed inspection or a new NCR (ADR 0057 §2.4).</summary>
    public const string QualityReplan = "system.QualityReplan";

    /// <summary>The system role that reminds supervisors of temporary moves ending and returns workers when they run out (ADR 0057 §5).</summary>
    public const string WorkforceMoves = "system.WorkforceMoves";

    /// <summary>The system role that opens training requests for qualifications about to expire.</summary>
    public const string TrainingRequests = "system.TrainingRequests";

    /// <summary>The system role that tells planners when released orders become, or stop being, at risk from material (Sprint 10 WW-S10-30).</summary>
    public const string MaterialRisk = "system.MaterialRisk";

    /// <summary>The system role that records a part issued to a work order as installed on its asset (V2.9).</summary>
    public const string MaintenanceInstallation = "system.MaintenanceInstallation";

    /// <summary>The system role that keeps an approved sales order's customer-order demand in step with it (owner decision 2026-10-31).</summary>
    public const string SalesDemand = "system.SalesDemand";

    /// <summary>The system role that keeps stock reservations in step with released orders and approved sales orders (owner decision 2026-10-31).</summary>
    public const string StockReservations = "system.StockReservations";

    public static IReadOnlyCollection<string> All => Map.Keys;

    public static bool IsKnown(string role) => Map.ContainsKey(role);

    public static IEnumerable<string> PermissionsOf(string role) =>
        Map.TryGetValue(role, out var permissions) ? permissions : SystemMap.TryGetValue(role, out var system) ? system : [];
}
