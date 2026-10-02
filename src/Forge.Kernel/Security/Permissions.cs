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

    public const string OrdersManage = "execution.orders.manage";
    public const string OrdersRelease = "execution.orders.release";
    public const string OrdersRead = "execution.orders.read";

    public const string DowntimeReport = "maintenance.downtime.report";
    public const string MaintenancePlan = "maintenance.plan";
    public const string MaintenanceRead = "maintenance.read";

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

    public const string DemandManage = "planning.demand.manage";
    public const string MaterialPlanRead = "planning.mrp.read";
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

    /// <summary>
    /// A shop-floor terminal (kiosk, scanner station). It records what badged people do; the badge
    /// identifies the person, the terminal's own identity is kept in the audit as the actor.
    /// </summary>
    public const string ShopFloorTerminal = "ShopFloorTerminal";

    private static readonly Dictionary<string, string[]> Map = new(StringComparer.Ordinal)
    {
        [SecurityAdministrator] = [Permissions.AccessManage, Permissions.PeopleRead, Permissions.AuditRead, Permissions.BadgesManage],
        [Auditor] = [Permissions.AuditRead, Permissions.PeopleRead],
        [ManufacturingEngineer] = [Permissions.ResourcesManage, Permissions.ResourcesRead, Permissions.WorkforceManage, Permissions.WorkforceRead, Permissions.SafetyRulesRead, Permissions.CompetencyRead, Permissions.ReadinessRead, Permissions.EngineeringManage, Permissions.EngineeringRead, Permissions.OrdersManage, Permissions.OrdersRead, Permissions.MaintenanceRead, Permissions.ScheduleRead, Permissions.ShopFloorRead, Permissions.ItemPlanningManage, Permissions.InventoryRead, Permissions.MaterialPlanRead, Permissions.PurchasingRead],
        [MaintenanceTechnician] = [Permissions.DowntimeReport, Permissions.MaintenancePlan, Permissions.MaintenanceRead, Permissions.ResourcesRead],
        [EngineeringApprover] = [Permissions.EngineeringRelease, Permissions.EngineeringRead, Permissions.ResourcesRead],
        [TrainingCoordinator] = [Permissions.CompetencyManage, Permissions.CompetencyRead, Permissions.WorkforceRead, Permissions.ResourcesRead],
        [Certifier] = [Permissions.CompetencyCertify, Permissions.CompetencyRead],
        [SafetyEngineer] = [Permissions.SafetyRulesDraft, Permissions.SafetyRulesRead, Permissions.ResourcesRead, Permissions.CompetencyRead],
        [SafetyApprover] = [Permissions.SafetyRulesApprove, Permissions.SafetyRulesRead, Permissions.ResourcesRead, Permissions.CompetencyRead],
        [Supervisor] = [Permissions.WorkforceSupervise, Permissions.WorkforceRead, Permissions.ReadinessRead, Permissions.ResourcesRead, Permissions.CompetencyRead, Permissions.SafetyRulesRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.DowntimeReport, Permissions.MaintenanceRead, Permissions.ScheduleRead, Permissions.ConstraintsManage, Permissions.ShopFloorRecord, Permissions.ShopFloorRead, Permissions.AttendanceRecord, Permissions.InventoryRead],
        [Planner] = [Permissions.ReadinessRead, Permissions.ResourcesRead, Permissions.WorkforceRead, Permissions.SafetyRulesRead, Permissions.OrdersManage, Permissions.OrdersRelease, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.MaintenanceRead, Permissions.ScheduleRead, Permissions.SchedulePropose, Permissions.ScheduleApprove, Permissions.ConstraintsManage, Permissions.ShopFloorRead, Permissions.ItemPlanningManage, Permissions.DemandManage, Permissions.MaterialPlanRead, Permissions.InventoryRead, Permissions.PurchasingRead],
        [MaterialHandler] = [Permissions.InventoryTransact, Permissions.InventoryRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.PurchasingRead, Permissions.ResourcesRead],
        [InventoryController] = [Permissions.InventoryApprove, Permissions.InventoryManage, Permissions.InventoryRead, Permissions.OrdersRead, Permissions.EngineeringRead, Permissions.PurchasingRead, Permissions.ResourcesRead, Permissions.MaterialPlanRead],
        [Buyer] = [Permissions.PurchasingManage, Permissions.PurchasingRead, Permissions.InventoryRead, Permissions.EngineeringRead, Permissions.MaterialPlanRead, Permissions.OrdersRead],
        [PurchasingManager] = [Permissions.PurchasingApprove, Permissions.PurchasingRead, Permissions.InventoryRead, Permissions.EngineeringRead, Permissions.MaterialPlanRead],
        [ShopFloorTerminal] = [Permissions.ShopFloorRecord, Permissions.ShopFloorRead, Permissions.AttendanceRecord, Permissions.ReadinessRead, Permissions.OrdersRead, Permissions.ResourcesRead, Permissions.ScheduleRead],
    };

    public static IReadOnlyCollection<string> All => Map.Keys;

    public static bool IsKnown(string role) => Map.ContainsKey(role);

    public static IEnumerable<string> PermissionsOf(string role) =>
        Map.TryGetValue(role, out var permissions) ? permissions : [];
}
