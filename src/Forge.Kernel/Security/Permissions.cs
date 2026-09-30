namespace Forge.Kernel.Security;

/// <summary>Actions that can be authorized. Named "context.area.verb".</summary>
public static class Permissions
{
    public const string AccessManage = "kernel.access.manage";
    public const string PeopleRead = "kernel.people.read";
    public const string AuditRead = "kernel.audit.read";
}

/// <summary>
/// Functional roles and the permissions they carry. In v1 the mapping is code, reviewed like code.
/// Later contexts add their own roles here or through their own catalogues.
/// </summary>
public static class Roles
{
    public const string SecurityAdministrator = "SecurityAdministrator";
    public const string Auditor = "Auditor";

    private static readonly Dictionary<string, string[]> Map = new(StringComparer.Ordinal)
    {
        [SecurityAdministrator] = [Permissions.AccessManage, Permissions.PeopleRead, Permissions.AuditRead],
        [Auditor] = [Permissions.AuditRead, Permissions.PeopleRead],
    };

    public static IReadOnlyCollection<string> All => Map.Keys;

    public static bool IsKnown(string role) => Map.ContainsKey(role);

    public static IEnumerable<string> PermissionsOf(string role) =>
        Map.TryGetValue(role, out var permissions) ? permissions : [];
}
