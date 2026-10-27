using Forge.Kernel.Security;

namespace Forge.Kernel.Tests;

/// <summary>
/// Owner decision 2026-10-27: supervisors and managers see every area of Forge, but seeing is all it adds. Actions, approvals, pay,
/// the audit trail and access administration stay with their own roles, and people views stay with supervisors and the plant manager.
/// </summary>
public sealed class OverviewRoleTests
{
    [Theory]
    [InlineData(Roles.Supervisor)]
    [InlineData(Roles.PlantManager)]
    [InlineData(Roles.QualityManager)]
    [InlineData(Roles.PurchasingManager)]
    [InlineData(Roles.ProgramManager)]
    [InlineData(Roles.FacilitiesManager)]
    public void Supervisors_and_managers_see_every_area(string role)
    {
        var granted = Roles.PermissionsOf(role).ToHashSet(StringComparer.Ordinal);
        Assert.All(Roles.SeeEveryArea, permission => Assert.Contains(permission, granted));
        Assert.DoesNotContain(Permissions.WorkforceCompensationRead, granted);
        Assert.DoesNotContain(Permissions.AccessManage, granted);
    }

    [Fact]
    public void The_plant_manager_sees_everything_but_changes_nothing_by_itself()
    {
        var granted = Roles.PermissionsOf(Roles.PlantManager).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(Permissions.AttendanceIntelligenceRead, granted);
        Assert.Contains(Permissions.WorkforceNominationRead, granted);
        var allowedNonReads = new HashSet<string>(StringComparer.Ordinal)
            { Permissions.AnalystUse, Permissions.ReportsSave, Permissions.NotificationsRead, Permissions.NotificationsAcknowledge, Permissions.DelegationPropose };
        Assert.All(granted.Where(p => !Roles.SeeEveryArea.Contains(p) && !Roles.SeePeople.Contains(p)), p => Assert.Contains(p, allowedNonReads));
    }

    [Fact]
    public void People_views_stay_with_supervisors_and_the_plant_manager()
    {
        foreach (var role in new[] { Roles.QualityManager, Roles.PurchasingManager, Roles.ProgramManager, Roles.FacilitiesManager })
            Assert.DoesNotContain(Permissions.AttendanceIntelligenceRead, Roles.PermissionsOf(role));
        Assert.DoesNotContain(Permissions.CostingManage, Roles.PermissionsOf(Roles.Supervisor));
    }
}
