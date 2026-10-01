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

    private static readonly Dictionary<string, string[]> Map = new(StringComparer.Ordinal)
    {
        [SecurityAdministrator] = [Permissions.AccessManage, Permissions.PeopleRead, Permissions.AuditRead],
        [Auditor] = [Permissions.AuditRead, Permissions.PeopleRead],
        [ManufacturingEngineer] = [Permissions.ResourcesManage, Permissions.ResourcesRead, Permissions.WorkforceManage, Permissions.WorkforceRead, Permissions.SafetyRulesRead, Permissions.CompetencyRead, Permissions.ReadinessRead, Permissions.EngineeringManage, Permissions.EngineeringRead],
        [EngineeringApprover] = [Permissions.EngineeringRelease, Permissions.EngineeringRead, Permissions.ResourcesRead],
        [TrainingCoordinator] = [Permissions.CompetencyManage, Permissions.CompetencyRead, Permissions.WorkforceRead, Permissions.ResourcesRead],
        [Certifier] = [Permissions.CompetencyCertify, Permissions.CompetencyRead],
        [SafetyEngineer] = [Permissions.SafetyRulesDraft, Permissions.SafetyRulesRead, Permissions.ResourcesRead, Permissions.CompetencyRead],
        [SafetyApprover] = [Permissions.SafetyRulesApprove, Permissions.SafetyRulesRead, Permissions.ResourcesRead, Permissions.CompetencyRead],
        [Supervisor] = [Permissions.WorkforceSupervise, Permissions.WorkforceRead, Permissions.ReadinessRead, Permissions.ResourcesRead, Permissions.CompetencyRead, Permissions.SafetyRulesRead, Permissions.OrdersRead, Permissions.EngineeringRead],
        [Planner] = [Permissions.ReadinessRead, Permissions.ResourcesRead, Permissions.WorkforceRead, Permissions.SafetyRulesRead, Permissions.OrdersManage, Permissions.OrdersRelease, Permissions.OrdersRead, Permissions.EngineeringRead],
    };

    public static IReadOnlyCollection<string> All => Map.Keys;

    public static bool IsKnown(string role) => Map.ContainsKey(role);

    public static IEnumerable<string> PermissionsOf(string role) =>
        Map.TryGetValue(role, out var permissions) ? permissions : [];
}
