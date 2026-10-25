using Forge.Maintenance.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Host.DailyMission;

/// <summary>Site-boundary helpers for Daily Mission projections.</summary>
public static class DailyMissionSiteScope
{
    public static HashSet<string> WorkCenterCodesForSite(string siteCode, IReadOnlyList<SafetyAreaInfo> areas,
        IReadOnlyList<WorkCenterInfo> workCenters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteCode);
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(workCenters);
        var siteAreas = areas.Where(area => string.Equals(area.SiteCode, siteCode, StringComparison.OrdinalIgnoreCase))
            .Select(area => area.Code).ToHashSet(StringComparer.Ordinal);
        return workCenters.Where(center => siteAreas.Contains(center.SafetyAreaCode))
            .Select(center => center.Code).ToHashSet(StringComparer.Ordinal);
    }

    public static HashSet<string> MachineCodesForSite(string siteCode, IReadOnlyList<SafetyAreaInfo> areas,
        IReadOnlyList<WorkCenterInfo> workCenters, IReadOnlyList<MachineInfo> machines)
    {
        ArgumentNullException.ThrowIfNull(machines);
        var siteCenters = WorkCenterCodesForSite(siteCode, areas, workCenters);
        return machines.Where(machine => siteCenters.Contains(machine.WorkCenterCode))
            .Select(machine => machine.Code).ToHashSet(StringComparer.Ordinal);
    }

    public static bool MachineIsInSite(string? machineCode, string? assetCode, IReadOnlySet<string> siteMachines,
        IReadOnlyDictionary<string, string> assetMachines)
    {
        ArgumentNullException.ThrowIfNull(siteMachines);
        ArgumentNullException.ThrowIfNull(assetMachines);
        if (machineCode is not null) return siteMachines.Contains(machineCode);
        return assetCode is not null && assetMachines.TryGetValue(assetCode, out var assetMachine) && siteMachines.Contains(assetMachine);
    }
}
