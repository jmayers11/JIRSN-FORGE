using Forge.Host.DailyMission;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;

namespace Forge.Integration.Tests;

public sealed class DailyMissionWorkspacePolicyTests
{
    [Fact]
    public void Workspace_options_and_defaults_follow_current_permissions()
    {
        var supervisor = Principal("Supervisor", "workforce.supervise", "workforce.read");
        var options = DailyMissionStateStore.OptionsFor(supervisor);

        Assert.Contains(options, option => option.Route == "daily-mission");
        Assert.Contains(options, option => option.Route == "workforce-kanban");
        Assert.DoesNotContain(options, option => option.Route == "maintenance");
        Assert.Equal("workforce-kanban", DailyMissionStateStore.DefaultLandingRoute(supervisor));

        var noWorkspace = Principal("Operator", "unrelated.permission");
        Assert.Equal("daily-mission", DailyMissionStateStore.DefaultLandingRoute(noWorkspace));
    }

    [Fact]
    public void Scope_key_changes_when_roles_or_program_grants_change()
    {
        var first = Principal("Supervisor", "workforce.supervise");
        var sameScope = Principal("Supervisor", "workforce.supervise");
        var changedRole = Principal("PlantManager", "workforce.supervise");
        var changedProgram = new ForgePrincipal(first.PersonId, first.DisplayName, true, first.Roles,
            new HashSet<ProgramId> { new(Guid.NewGuid()) }, DataMarking.None, first.EffectivePermissions);

        Assert.Equal(DailyMissionStateStore.ScopeKey(first), DailyMissionStateStore.ScopeKey(sameScope));
        Assert.NotEqual(DailyMissionStateStore.ScopeKey(first), DailyMissionStateStore.ScopeKey(changedRole));
        Assert.NotEqual(DailyMissionStateStore.ScopeKey(first), DailyMissionStateStore.ScopeKey(changedProgram));
    }

    [Fact]
    public void Program_preference_can_only_reduce_the_authorized_program_set()
    {
        var first = new ProgramId(Guid.NewGuid());
        var second = new ProgramId(Guid.NewGuid());
        var principal = new ForgePrincipal(new PersonId(Guid.NewGuid()), "Multi-program user", true,
            new HashSet<string> { "Planner" }, new HashSet<ProgramId> { first, second }, DataMarking.None,
            new HashSet<string> { "planning.readiness.read" });
        var selected = $"program:{first.Value:D}";

        Assert.True(DailyMissionStateStore.IsAllowedProgramScope(principal, "all"));
        Assert.True(DailyMissionStateStore.IsAllowedProgramScope(principal, selected));
        Assert.False(DailyMissionStateStore.IsAllowedProgramScope(principal, $"program:{Guid.NewGuid():D}"));
        Assert.Single(DailyMissionStateStore.ApplyProgramScope(principal, selected).Programs);
        Assert.Equal(principal.Programs, DailyMissionStateStore.ApplyProgramScope(principal, "all").Programs);
        Assert.Throws<ArgumentException>(() => DailyMissionStateStore.ApplyProgramScope(principal, $"program:{Guid.NewGuid():D}"));
    }

    private static ForgePrincipal Principal(string role, params string[] permissions) =>
        new(new PersonId(Guid.NewGuid()), "Workspace policy test", true,
            new HashSet<string>(StringComparer.Ordinal) { role },
            new HashSet<ProgramId>(), DataMarking.None,
            new HashSet<string>(permissions, StringComparer.Ordinal));
}
