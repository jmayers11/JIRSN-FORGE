namespace Forge.Kernel.Security;

/// <summary>Actions that can be authorized. Named "context.area.verb".</summary>
public static class Permissions
{
    public const string AccessManage = "kernel.access.manage";
    public const string PeopleRead = "kernel.people.read";
    public const string AuditRead = "kernel.audit.read";

    public const string ResourcesManage = "resources.manage";
    public const string ResourcesRead = "resources.read";

    public const string WorkforceManage = "workforce.manage";
    public const string WorkforceSupervise = "workforce.supervise";
    public const string WorkforceRead = "workforce.read";

    public const string CompetencyManage = "competency.manage";
    public const string CompetencyCertify = "competency.certify";
    public const string CompetencyRead = "competency.read";

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
    public const string ShopFloorRecord = "execution.shopfloor.record";
    public const string ShopFloorRead = "execution.shopfloor.read";

    public const string ItemPlanningManage = "engineering.planning.manage";

    public const string InventoryTransact = "inventory.transact";
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
    public const string QualityRead = "quality.read";

    public const string ShippingRelease = "inventory.shipping.ship";

    /// <summary>Issue and return spare parts on maintenance work orders.</summary>
    public const string SparesIssue = "inventory.spares.issue";

    public const string DemandManage = "planning.demand.manage";
    public const string MaterialPlanRead = "planning.mrp.read";

    /// <summary>Open the Reports page and keep saved report definitions. Each report also needs the read permission of what it reports on.</summary>
    public const string ReportsSave = "planning.reports.save";

    /// <summary>Ask Forge Analyst. Every answer still needs the read permission of what it is about.</summary>
    public const string AnalystUse = "planning.analyst.use";

    public const string ProgramsManage = "programs.manage";
    public const string ProgramsRead = "programs.read";

    /// <summary>Program codes and names only, to grant access. Grants no program data.</summary>
    public const string ProgramsDirectory = "programs.directory";
}

/// <summary>
/// Functional roles and the permissions they carry. In v1 the mapping is code, reviewed like code.
/// Roles are deliberately narrow so duties can be separated (author vs approver, trainer vs certifier).
/// </summary>
public static class Roles
{
    public const string SecurityAdministrator = "SecurityAdministrator";
    public const string Auditor = "Auditor";
    public const string ManufacturingEngineer = "ManufacturingEngineer";
    public const string TrainingCoordinator = "TrainingCoordinator";
    public const string Certifier = "Certifier";
    public const string SafetyEngineer = "SafetyEngineer";
    public const string SafetyApprover = "SafetyApprover";
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

    private static readonly Dictionary<string, string[]> Map = new(StringComparer.Ordinal)
    {
        [SecurityAdministrator] = [Permissions.AccessManage, Permissions.PeopleRead, Permissions.AuditRead, Permissions.BadgesManage, Permissions.ProgramsDirectory],
        [Auditor] = [Permissions.AuditRead, Permissions.PeopleRead],
        [ManufacturingEngineer] = [Permissions.AssetsManage, Permissions.ObsolescenceRecord, Permissions.ResourcesManage, Permissions.ResourcesRead, Permissions.WorkforceManage, Permissions.WorkforceRead, Permissions.SafetyRulesRead, Permissions.CompetencyRead, Permissions.ReadinessRead, Permissions.EngineeringManage, Permissions.EngineeringRead, Permissions.OrdersManage, Permissions.OrdersRead, Permissions.MaintenanceRead, Permissions.ScheduleRead, Permissions.ShopFloorRead, Permissions.ItemPlanningManage, Permissions.InventoryRead, Permissions.MaterialPlanRead, Permissions.PurchasingRead, Permissions.QualityRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.CotsPropose, Permissions.DocumentsManage, Permissions.DocumentsAttach, Permissions.ChangesManage, Permissions.ChangesReview, Permissions.AlternatesPropose, Permissions.RequisitionRaise],
        [MaintenanceTechnician] = [Permissions.DowntimeReport, Permissions.MaintenancePlan, Permissions.MaintenanceRead, Permissions.MaintenanceWork, Permissions.ResourcesRead, Permissions.SparesIssue, Permissions.InventoryRead, Permissions.EngineeringRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.DocumentsAttach],
        [MaintenancePlanner] = [Permissions.OutageApprove, Permissions.AssetsManage, Permissions.MroManage, Permissions.ObsolescenceRecord, Permissions.MaintenanceProgram, Permissions.InventoryRead, Permissions.MaintenanceApprove, Permissions.MaintenancePlan, Permissions.MaintenanceRead, Permissions.DowntimeReport, Permissions.ResourcesRead, Permissions.ScheduleRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.CotsPropose, Permissions.EngineeringRead, Permissions.DocumentsAttach, Permissions.ChangesReview, Permissions.AlternatesPropose, Permissions.MaintenanceAlternatesApprove, Permissions.RequisitionRaise, Permissions.DemandManage],
        [ProgramManager] = [Permissions.ProgramsManage, Permissions.ProgramsRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.MaterialPlanRead, Permissions.ScheduleRead, Permissions.InventoryRead, Permissions.QualityRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.DocumentsAttach, Permissions.ChangesReview, Permissions.RequisitionRaise],
        [EngineeringApprover] = [Permissions.EngineeringRelease, Permissions.EngineeringRead, Permissions.ResourcesRead, Permissions.CotsManage, Permissions.DocumentsApprove, Permissions.DocumentsRelease, Permissions.OrdersRead, Permissions.ProgramsRead, Permissions.ChangesReview, Permissions.ChangesApprove, Permissions.AlternatesApprove],
        [TrainingCoordinator] = [Permissions.CompetencyManage, Permissions.CompetencyRead, Permissions.WorkforceRead, Permissions.ResourcesRead, Permissions.EngineeringRead],
        [Certifier] = [Permissions.CompetencyCertify, Permissions.CompetencyRead],
        [SafetyEngineer] = [Permissions.SafetyRulesDraft, Permissions.SafetyRulesRead, Permissions.ResourcesRead, Permissions.CompetencyRead],
        [SafetyApprover] = [Permissions.SafetyRulesApprove, Permissions.SafetyRulesRead, Permissions.ResourcesRead, Permissions.CompetencyRead],
        [Supervisor] = [Permissions.WorkforceSupervise, Permissions.WorkforceRead, Permissions.ReadinessRead, Permissions.ResourcesRead, Permissions.CompetencyRead, Permissions.SafetyRulesRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.DowntimeReport, Permissions.MaintenanceRead, Permissions.ScheduleRead, Permissions.ConstraintsManage, Permissions.ShopFloorRecord, Permissions.ShopFloorRead, Permissions.AttendanceRecord, Permissions.InventoryRead, Permissions.QualityRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.DocumentsAttach, Permissions.ChangesReview],
        [Planner] = [Permissions.OutageApprove, Permissions.MroManage, Permissions.ReadinessRead, Permissions.ResourcesRead, Permissions.WorkforceRead, Permissions.SafetyRulesRead, Permissions.OrdersManage, Permissions.OrdersRelease, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.MaintenanceRead, Permissions.ScheduleRead, Permissions.SchedulePropose, Permissions.ScheduleApprove, Permissions.ConstraintsManage, Permissions.ShopFloorRead, Permissions.ItemPlanningManage, Permissions.DemandManage, Permissions.MaterialPlanRead, Permissions.InventoryRead, Permissions.PurchasingRead, Permissions.QualityRead, Permissions.ProgramsRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.DocumentsAttach, Permissions.ChangesReview, Permissions.RequisitionRaise],
        [MaterialHandler] = [Permissions.InventoryTransact, Permissions.SparesIssue, Permissions.MaintenanceRead, Permissions.InventoryRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.PurchasingRead, Permissions.ResourcesRead, Permissions.QualityRead, Permissions.MaterialPlanRead, Permissions.ReportsSave, Permissions.AnalystUse],
        [InventoryController] = [Permissions.MroManage, Permissions.InventoryApprove, Permissions.InventoryManage, Permissions.InventoryRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.PurchasingRead, Permissions.ResourcesRead, Permissions.MaterialPlanRead, Permissions.ReportsSave, Permissions.AnalystUse],
        [Buyer] = [Permissions.ObsolescenceRecord, Permissions.PurchasingManage, Permissions.PurchasingRead, Permissions.InventoryRead, Permissions.EngineeringRead, Permissions.MaterialPlanRead, Permissions.OrdersRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.CotsPropose, Permissions.DocumentsAttach, Permissions.ChangesReview, Permissions.AlternatesPropose, Permissions.RequisitionRaise],
        [Inspector] = [Permissions.QualityInspect, Permissions.QualityRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.InventoryRead, Permissions.ResourcesRead, Permissions.ShopFloorRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.DocumentsAttach],
        [QualityEngineer] = [Permissions.QualityPlan, Permissions.QualityDisposition, Permissions.QualityInspect, Permissions.QualityRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.InventoryRead, Permissions.ResourcesRead, Permissions.ShopFloorRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.DocumentsManage, Permissions.DocumentsAttach, Permissions.ChangesReview, Permissions.AlternatesPropose, Permissions.RequisitionRaise, Permissions.DemandManage],
        [QualityManager] = [Permissions.QualityApprove, Permissions.QualityRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.InventoryRead, Permissions.ResourcesRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.ChangesReview],
        [Shipper] = [Permissions.ShippingRelease, Permissions.InventoryRead, Permissions.MaterialPlanRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.QualityRead, Permissions.ReportsSave, Permissions.AnalystUse],
        [PurchasingManager] = [Permissions.PurchasingApprove, Permissions.PurchasingRead, Permissions.InventoryRead, Permissions.EngineeringRead, Permissions.MaterialPlanRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.DocumentsAttach, Permissions.ChangesReview, Permissions.AlternatesPropose, Permissions.RequisitionRaise],
        [DocumentController] = [Permissions.DocumentsManage, Permissions.DocumentsApprove, Permissions.DocumentsRelease, Permissions.DocumentsAttach, Permissions.EngineeringRead, Permissions.OrdersRead, Permissions.PurchasingRead, Permissions.QualityRead, Permissions.MaintenanceRead, Permissions.ProgramsRead, Permissions.ResourcesRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.ChangesManage, Permissions.ChangesReview, Permissions.EngineeringRelease],
        [ChangeBoard] = [Permissions.ChangesApprove, Permissions.ChangesReview, Permissions.EngineeringRead, Permissions.OrdersRead, Permissions.PurchasingRead, Permissions.InventoryRead, Permissions.QualityRead, Permissions.ProgramsRead, Permissions.ResourcesRead, Permissions.ReportsSave, Permissions.AnalystUse, Permissions.AlternatesApprove],
        [ShopFloorTerminal] = [Permissions.ShopFloorRecord, Permissions.ShopFloorRead, Permissions.AttendanceRecord, Permissions.ReadinessRead, Permissions.OrdersRead, Permissions.ResourcesRead, Permissions.ScheduleRead],
    };

    /// <summary>
    /// Roles held only by system actors, never by people: they cannot be granted (not in <see cref="All"/>).
    /// </summary>
    private static readonly Dictionary<string, string[]> SystemMap = new(StringComparer.Ordinal)
    {
        [MaintenanceInstallation] = [Permissions.MaintenanceWork, Permissions.MaintenanceRead, Permissions.EngineeringRead, Permissions.InventoryRead],
        [ChangePropagation] = [Permissions.EngineeringRead, Permissions.OrdersRead, Permissions.OrdersManage, Permissions.OrdersRelease, Permissions.PurchasingRead, Permissions.WorkforceRead, Permissions.ChangesManage],
    };

    /// <summary>The system role the propagation of a released engineering change acts with (V2.5).</summary>
    public const string ChangePropagation = "system.ChangePropagation";

    /// <summary>The system role that records a part issued to a work order as installed on its asset (V2.9).</summary>
    public const string MaintenanceInstallation = "system.MaintenanceInstallation";

    public static IReadOnlyCollection<string> All => Map.Keys;

    public static bool IsKnown(string role) => Map.ContainsKey(role);

    public static IEnumerable<string> PermissionsOf(string role) =>
        Map.TryGetValue(role, out var permissions) ? permissions : SystemMap.TryGetValue(role, out var system) ? system : [];
}
