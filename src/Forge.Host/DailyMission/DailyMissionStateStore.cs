using System.Security.Cryptography;
using System.Text;
using Forge.Kernel.Security;
using Npgsql;

namespace Forge.Host.DailyMission;

public sealed record DailyMissionBriefingState(bool ShouldShow, DateTimeOffset? ViewedAt, DateTimeOffset? DismissedAt, bool ScopeChanged = false);
public sealed record DailyMissionWorkspaceOption(string Route, string Label);
public sealed record DailyMissionWorkspacePreferences(string LandingRoute, string HawkAccents, IReadOnlyList<DailyMissionWorkspaceOption> Options,
    string ProgramScope, IReadOnlyList<DailyMissionWorkspaceOption> ProgramOptions);
public sealed record DailyMissionSession(DailyMissionResponse? Mission, DailyMissionBriefingState Briefing);
public sealed record SaveDailyMissionWorkspaceRequest(string LandingRoute, string HawkAccents, string? ProgramScope = null);

/// <summary>
/// Stores only per-user presentation state. It never persists mission cards or operational decisions.
/// </summary>
public sealed class DailyMissionStateStore(NpgsqlDataSource dataSource, TimeProvider time)
{
    private static readonly IReadOnlyDictionary<string, string[]> RoutePermissions = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["home"] = ["planning.readiness.read", "planning.schedule.read", "execution.orders.read", "resources.read", "quality.read", "inventory.read"],
        ["supervisor"] = ["workforce.supervise", "workforce.manage"],
        ["workforce-kanban"] = ["workforce.supervise", "workforce.manage", "resources.floor-map.manage"],
        ["machine-kanban"] = ["planning.readiness.read", "maintenance.read", "resources.read"],
        ["performance"] = ["workforce.read"],
        ["horizon"] = ["planning.schedule.read", "planning.readiness.read"],
        ["scenarios"] = ["planning.schedule.read", "planning.readiness.read"],
        ["programs"] = ["programs.read", "programs.manage"],
        ["projects"] = ["projects.read", "projects.manage"],
        ["orders"] = ["execution.orders.read", "execution.orders.manage"],
        ["engineering"] = ["engineering.read", "engineering.manage"],
        ["scheduling"] = ["planning.schedule.read", "planning.schedule.propose", "planning.schedule.approve"],
        ["material-plan"] = ["planning.mrp.read", "planning.demand.manage"],
        ["work-centers"] = ["resources.read", "planning.readiness.read", "execution.shopfloor.read"],
        ["shop-floor"] = ["execution.shopfloor.read"],
        ["plant-setup"] = ["resources.manage", "resources.read"],
        ["workforce"] = ["workforce.read", "workforce.manage", "workforce.attendance-correction.request"],
        ["safety-rules"] = ["safety.rules.read", "safety.rules.draft", "safety.rules.approve"],
        ["shift-continuity"] = ["execution.shopfloor.read", "workforce.read"],
        ["notifications"] = ["notifications.read"],
        ["integrity"] = ["kernel.audit.read"],
        ["inventory"] = ["inventory.read", "inventory.transact"],
        ["receiving"] = ["inventory.read", "inventory.transact"],
        ["procurement"] = ["procurement.read", "procurement.manage"],
        ["quality"] = ["quality.read", "quality.inspect"],
        ["acceptance"] = ["quality.read", "quality.inspect", "quality.product.release"],
        ["investigations"] = ["quality.read", "quality.plan", "quality.disposition"],
        ["qms"] = ["quality.qms.read"],
        ["qms-planning"] = ["quality.qms.read", "quality.qms.manage"],
        ["qms-audit"] = ["quality.audit.conduct", "quality.audit.manage", "quality.qms.read"],
        ["qms-review"] = ["quality.qms.read", "quality.qms.review"],
        ["qms-metrics"] = ["quality.qms.read"],
        ["commercial-quality"] = ["quality.read", "quality.qms.read"],
        ["maintenance"] = ["maintenance.read", "maintenance.work", "maintenance.plan"],
        ["shipping"] = ["inventory.read", "inventory.shipping.ship"],
        ["reports"] = ["planning.reports.save", "planning.schedule.read", "quality.read"],
        ["analyst"] = ["planning.analyst.use"],
        ["access"] = ["kernel.access.manage"],
        ["my-work"] = ["notifications.read"],
        ["settings"] = [],
    };

    private static readonly IReadOnlyDictionary<string, string> RouteLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["daily-mission"] = "Daily Mission",
        ["home"] = "Classic Workspace",
        ["supervisor"] = "Plant Overview",
        ["workforce-kanban"] = "Workforce Kanban",
        ["machine-kanban"] = "Machine Readiness",
        ["performance"] = "Training & Performance",
        ["horizon"] = "Horizon",
        ["scenarios"] = "Scenarios",
        ["programs"] = "Programs",
        ["projects"] = "Projects",
        ["orders"] = "Production Orders",
        ["engineering"] = "Engineering",
        ["scheduling"] = "Scheduling",
        ["material-plan"] = "Material Plan",
        ["work-centers"] = "Work Centers",
        ["shop-floor"] = "Shop Floor",
        ["plant-setup"] = "Plant Setup",
        ["workforce"] = "Workforce Records",
        ["safety-rules"] = "Safety Rules",
        ["shift-continuity"] = "Shift Continuity",
        ["notifications"] = "Notifications",
        ["integrity"] = "Data Integrity",
        ["inventory"] = "Inventory",
        ["receiving"] = "Receiving",
        ["procurement"] = "Procurement",
        ["quality"] = "Quality",
        ["acceptance"] = "Product Acceptance",
        ["investigations"] = "Investigations",
        ["qms"] = "Quality Management",
        ["qms-planning"] = "QMS Planning",
        ["qms-audit"] = "Audits & CAPA",
        ["qms-review"] = "QMS Reviews",
        ["qms-metrics"] = "Quality Metrics",
        ["commercial-quality"] = "Customer & Provider Quality",
        ["maintenance"] = "Maintenance",
        ["shipping"] = "Shipping",
        ["reports"] = "Reports",
        ["analyst"] = "Forge Analyst",
        ["access"] = "Access Control",
        ["my-work"] = "My Work",
        ["settings"] = "Settings",
    };

    public static string ScopeKey(ForgePrincipal principal)
    {
        var roles = string.Join(',', principal.Roles.Order(StringComparer.Ordinal));
        var programs = string.Join(',', principal.Programs.Select(program => program.Value.ToString("D")).Order(StringComparer.Ordinal));
        var permissions = string.Join(',', (principal.EffectivePermissions ?? new HashSet<string>()).Order(StringComparer.Ordinal));
        var scopedPermissions = string.Join(',', (principal.ScopedPermissions ?? [])
            .Select(permission => $"{permission.Permission}:{permission.ScopeType}:{permission.ScopeId}").Order(StringComparer.Ordinal));
        var approvalThresholds = string.Join(',', (principal.ApprovalThresholds ?? [])
            .Select(threshold => $"{threshold.Permission}:{threshold.Currency}:{threshold.MaximumAmount}").Order(StringComparer.Ordinal));
        var delegations = string.Join(',', (principal.DelegatedPermissions ?? [])
            .Select(delegation => $"{delegation.Permission}:{delegation.DelegationId:D}:{delegation.StartsAt:O}:{delegation.ExpiresAt:O}:{string.Join('|', delegation.Scopes.Select(scope => $"{scope.ScopeType}:{scope.ScopeId}").Order(StringComparer.Ordinal))}:{delegation.SourceThreshold?.MaximumAmount}:{delegation.SourceThreshold?.Currency}")
            .Order(StringComparer.Ordinal));
        var authorizationScope = $"{roles}\n{programs}\n{permissions}\n{scopedPermissions}\n{approvalThresholds}\n{delegations}\n{principal.Markings}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(authorizationScope)));
    }

    public static IReadOnlyList<DailyMissionWorkspaceOption> OptionsFor(ForgePrincipal principal) =>
        [new DailyMissionWorkspaceOption("daily-mission", RouteLabels["daily-mission"]), .. RoutePermissions
            .Where(route => route.Value.Length == 0 || route.Value.Any(permission => principal.HasPermission(permission)))
            .Select(route => new DailyMissionWorkspaceOption(route.Key, RouteLabels[route.Key]))
            .OrderBy(option => option.Label, StringComparer.Ordinal)];

    public static string DefaultLandingRoute(ForgePrincipal principal)
    {
        var candidates = new (string Role, string Route)[]
        {
            ("Supervisor", "workforce-kanban"), ("PlantManager", "supervisor"), ("Planner", "scheduling"),
            ("ProductionControl", "scheduling"), ("Operator", "shop-floor"), ("MaterialHandler", "inventory"),
            ("Stockroom", "inventory"), ("Quality", "quality"), ("QualityManager", "quality"),
            ("Maintenance", "maintenance"), ("MaintenancePlanner", "maintenance"),
            ("ProgramManager", "programs"), ("ProgramLead", "programs"), ("ProjectManager", "projects"),
        };
        var available = OptionsFor(principal).Select(option => option.Route).ToHashSet(StringComparer.Ordinal);
        return candidates.FirstOrDefault(candidate => principal.Roles.Contains(candidate.Role) && available.Contains(candidate.Route)).Route
            ?? "daily-mission";
    }

    public static IReadOnlyList<DailyMissionWorkspaceOption> ProgramOptionsFor(ForgePrincipal principal) =>
        [new DailyMissionWorkspaceOption("all", "All authorized programs"), .. principal.Programs
            .OrderBy(program => program.Value)
            .Select(program => new DailyMissionWorkspaceOption($"program:{program.Value:D}", $"Program {program.Value.ToString("D")[..8]}"))];

    public static bool IsAllowedProgramScope(ForgePrincipal principal, string? scope) =>
        scope == "all" || scope is not null && principal.Programs.Any(program => $"program:{program.Value:D}" == scope);

    /// <summary>Restricts source projections to one already-authorized program; it cannot widen access.</summary>
    public static ForgePrincipal ApplyProgramScope(ForgePrincipal principal, string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope == "all") return principal;
        var selected = principal.Programs.FirstOrDefault(program => $"program:{program.Value:D}" == scope);
        if (selected == default) throw new ArgumentException("Choose a program that is already authorized for this account.", nameof(scope));
        return principal with { Programs = new HashSet<Forge.Kernel.Identity.ProgramId> { selected } };
    }

    public bool IsAllowedRoute(ForgePrincipal principal, string route) =>
        route == "daily-mission" || RoutePermissions.TryGetValue(route, out var permissions)
        && (permissions.Length == 0 || permissions.Any(permission => principal.HasPermission(permission)));

    public async Task<DailyMissionBriefingState> OpenBriefingAsync(Guid personId, DateOnly operationalDate, string operationalDayId, string scopeKey, DateTimeOffset viewedAt, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO kernel.daily_mission_briefing_state(person_id, operational_date, operational_day_id, scope_key, viewed_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $5)
            ON CONFLICT (person_id, operational_date) DO NOTHING
            RETURNING true
            """, connection, transaction);
        insert.Parameters.AddWithValue(personId);
        insert.Parameters.AddWithValue(operationalDate);
        insert.Parameters.AddWithValue(operationalDayId);
        insert.Parameters.AddWithValue(scopeKey);
        insert.Parameters.AddWithValue(viewedAt);
        var shouldShow = await insert.ExecuteScalarAsync(ct) is true;

        await using var read = new NpgsqlCommand("SELECT operational_day_id, scope_key, viewed_at, dismissed_at FROM kernel.daily_mission_briefing_state WHERE person_id=$1 AND operational_date=$2 FOR UPDATE", connection, transaction);
        read.Parameters.AddWithValue(personId);
        read.Parameters.AddWithValue(operationalDate);
        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            await transaction.RollbackAsync(ct);
            return new(false, null, null);
        }
        var previousOperationalDayId = reader.GetString(0);
        var previousScope = reader.GetString(1);
        var viewed = reader.GetFieldValue<DateTimeOffset>(2);
        DateTimeOffset? dismissed = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3);
        await reader.DisposeAsync();
        var scopeChanged = !shouldShow && (!string.Equals(previousScope, scopeKey, StringComparison.Ordinal)
            || !string.Equals(previousOperationalDayId, operationalDayId, StringComparison.Ordinal));
        if (scopeChanged)
        {
            await using var update = new NpgsqlCommand("UPDATE kernel.daily_mission_briefing_state SET operational_day_id=$3, scope_key=$4, updated_at=$5 WHERE person_id=$1 AND operational_date=$2", connection, transaction);
            update.Parameters.AddWithValue(personId);
            update.Parameters.AddWithValue(operationalDate);
            update.Parameters.AddWithValue(operationalDayId);
            update.Parameters.AddWithValue(scopeKey);
            update.Parameters.AddWithValue(viewedAt);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new(shouldShow, viewed, dismissed, scopeChanged);
    }

    public async Task<DailyMissionBriefingState> DismissBriefingAsync(Guid personId, DateOnly operationalDate, string operationalDayId, string scopeKey, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO kernel.daily_mission_briefing_state AS briefing(person_id, operational_date, operational_day_id, scope_key, viewed_at, dismissed_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $5, $5)
            ON CONFLICT (person_id, operational_date) DO UPDATE
              SET operational_day_id=EXCLUDED.operational_day_id, scope_key=EXCLUDED.scope_key,
                  dismissed_at=COALESCE(briefing.dismissed_at, EXCLUDED.dismissed_at), updated_at=EXCLUDED.updated_at
            RETURNING viewed_at, dismissed_at
            """, connection);
        command.Parameters.AddWithValue(personId);
        command.Parameters.AddWithValue(operationalDate);
        command.Parameters.AddWithValue(operationalDayId);
        command.Parameters.AddWithValue(scopeKey);
        command.Parameters.AddWithValue(now);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new(false, reader.GetFieldValue<DateTimeOffset>(0), reader.GetFieldValue<DateTimeOffset>(1));
    }

    public async Task<DailyMissionWorkspacePreferences> GetPreferencesAsync(ForgePrincipal principal, CancellationToken ct)
    {
        var scope = ScopeKey(principal);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT landing_route, hawk_accents, program_scope FROM kernel.daily_mission_workspace_preference WHERE person_id=$1 AND scope_key=$2", connection);
        command.Parameters.AddWithValue(principal.PersonId.Value);
        command.Parameters.AddWithValue(scope);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var route = DefaultLandingRoute(principal);
        var accents = "subtle";
        var programScope = "all";
        if (await reader.ReadAsync(ct))
        {
            var savedRoute = reader.GetString(0);
            if (IsAllowedRoute(principal, savedRoute)) route = savedRoute;
            accents = reader.GetString(1);
            if (IsAllowedProgramScope(principal, reader.GetString(2))) programScope = reader.GetString(2);
        }
        return new(route, accents, OptionsFor(principal), programScope, ProgramOptionsFor(principal));
    }

    public async Task<DailyMissionWorkspacePreferences> SavePreferencesAsync(ForgePrincipal principal, SaveDailyMissionWorkspaceRequest request, CancellationToken ct)
    {
        if (!IsAllowedRoute(principal, request.LandingRoute)) throw new ArgumentException("Choose a workspace available to your current permissions.");
        if (request.HawkAccents is not ("off" or "subtle" or "expressive")) throw new ArgumentException("Hawk accents must be off, subtle, or expressive.");
        if (!IsAllowedProgramScope(principal, request.ProgramScope ?? "all")) throw new ArgumentException("Choose a program that is already authorized for this account.");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO kernel.daily_mission_workspace_preference(person_id, scope_key, landing_route, hawk_accents, program_scope, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (person_id, scope_key) DO UPDATE
              SET landing_route=EXCLUDED.landing_route, hawk_accents=EXCLUDED.hawk_accents, program_scope=EXCLUDED.program_scope, updated_at=EXCLUDED.updated_at
            """, connection);
        command.Parameters.AddWithValue(principal.PersonId.Value);
        command.Parameters.AddWithValue(ScopeKey(principal));
        command.Parameters.AddWithValue(request.LandingRoute);
        command.Parameters.AddWithValue(request.HawkAccents);
        var programScope = request.ProgramScope ?? "all";
        command.Parameters.AddWithValue(programScope);
        command.Parameters.AddWithValue(time.GetUtcNow());
        await command.ExecuteNonQueryAsync(ct);
        return new(request.LandingRoute, request.HawkAccents, OptionsFor(principal), programScope, ProgramOptionsFor(principal));
    }
}
