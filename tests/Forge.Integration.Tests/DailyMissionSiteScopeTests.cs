using Forge.Host.DailyMission;
using Forge.Resources.Contracts;

namespace Forge.Integration.Tests;

public sealed class DailyMissionSiteScopeTests
{
    [Fact]
    public void Machine_map_scope_uses_site_ownership_even_for_retired_maintenance_resources()
    {
        SafetyAreaInfo[] areas = [new("AREA-A", "SITE-A", "Area A"), new("AREA-B", "SITE-B", "Area B"), new("AREA-OLD", "SITE-A", "Retired", Active: false)];
        WorkCenterInfo[] centers = [new("WC-A", "AREA-A", "Center A"), new("WC-B", "AREA-B", "Center B"), new("WC-OLD", "AREA-OLD", "Retired", Active: false)];
        MachineInfo[] machines = [new("M-A", "WC-A", "AREA-A", "Machine A"), new("M-B", "WC-B", "AREA-B", "Machine B"), new("M-OLD", "WC-OLD", "AREA-OLD", "Retired machine")];

        var siteCenters = DailyMissionSiteScope.WorkCenterCodesForSite("SITE-A", areas, centers);
        var siteMachines = DailyMissionSiteScope.MachineCodesForSite("SITE-A", areas, centers, machines);

        Assert.Equal(new[] { "WC-A", "WC-OLD" }, siteCenters.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "M-A", "M-OLD" }, siteMachines.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Maintenance_work_order_scope_accepts_site_machines_and_machine_assets_only()
    {
        IReadOnlySet<string> siteMachines = new HashSet<string>(["M-A"], StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> assetMachines = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ASSET-A"] = "M-A",
            ["ASSET-B"] = "M-B",
        };

        Assert.True(DailyMissionSiteScope.MachineIsInSite("M-A", null, siteMachines, assetMachines));
        Assert.True(DailyMissionSiteScope.MachineIsInSite(null, "ASSET-A", siteMachines, assetMachines));
        Assert.False(DailyMissionSiteScope.MachineIsInSite("M-B", null, siteMachines, assetMachines));
        Assert.False(DailyMissionSiteScope.MachineIsInSite(null, "ASSET-B", siteMachines, assetMachines));
        Assert.False(DailyMissionSiteScope.MachineIsInSite(null, "FACILITY-ASSET", siteMachines, assetMachines));
        Assert.False(DailyMissionSiteScope.MachineIsInSite(null, null, siteMachines, assetMachines));
    }
}
