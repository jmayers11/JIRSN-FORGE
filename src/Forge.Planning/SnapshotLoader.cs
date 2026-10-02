using Forge.Competency.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Resources.Contracts;
using Forge.SafetyRules.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Planning;

/// <summary>Reads everything the engines need in one consistent transaction, sorted by key.</summary>
internal static class SnapshotLoader
{
    /// <summary>How far ahead downtime and absences are read. Covers the longest planning horizon.</summary>
    public static readonly TimeSpan Reach = TimeSpan.FromDays(63);

    public static async Task<SiteSnapshot> LoadAsync(IKernelUnitOfWork uow, TimeProvider time, CancellationToken ct)
    {
        var at = time.GetUtcNow();
        at = new DateTimeOffset(at.UtcDateTime.Ticks / 10 * 10, TimeSpan.Zero); // database precision

        var resources = uow.Store<IResourcesLookup>();
        var workforce = uow.Store<IWorkforceLookup>();
        var workers = await workforce.GetWorkerStatesAsync(at, ct);
        var qualifications = await uow.Store<ICompetencyLookup>().GetQualificationsAsync(workers.Select(w => new PersonId(w.PersonId)).ToList(), ct);
        var valid = qualifications
            .Where(q => q.IsValidAt(at))
            .GroupBy(q => q.PersonId)
            .ToDictionary(g => g.Key, g => g.OrderBy(q => q.Code, StringComparer.Ordinal).ToList());

        var people = workers
            .Select(w =>
            {
                var held = valid.TryGetValue(w.PersonId, out var list) ? list : [];
                return new PersonSnapshot(
                    w.PersonId,
                    w.DisplayName,
                    w.IsActive,
                    w.PresenceKind,
                    w.PresentUntil,
                    w.Unavailability,
                    w.Assignment,
                    held.Select(q => q.Code).ToList())
                {
                    HomeWorkCenterCode = w.HomeWorkCenterCode,
                    ShiftCode = w.ShiftCode,
                    QualificationExpiry = held.ToDictionary(q => q.Code, q => q.ExpiresAt, StringComparer.Ordinal),
                };
            })
            .OrderBy(p => p.PersonId)
            .ToList();

        var from = at.AddDays(-1);
        var to = at + Reach;

        // Retired records stay in Resources for history; planning only sees what can still be used.
        var workCenters = (await resources.ListWorkCentersAsync(ct)).Where(w => w.Active).ToList();
        var machines = (await resources.ListMachinesAsync(ct)).Where(m => m.Active).ToList();
        var machineCodes = machines.Select(m => m.Code).ToHashSet(StringComparer.Ordinal);
        var workCenterCodes = workCenters.Select(w => w.Code).ToHashSet(StringComparer.Ordinal);
        return new SiteSnapshot(
            at,
            (await resources.ListSafetyAreasAsync(ct)).Where(a => a.Active).ToList(),
            workCenters,
            machines,
            (await resources.ListMachineActivitiesAsync(ct)).Where(a => machineCodes.Contains(a.MachineCode)).ToList(),
            (await resources.ListWorkCenterActivitiesAsync(ct)).Where(a => workCenterCodes.Contains(a.WorkCenterCode)).ToList(),
            (await resources.ListActivityTypesAsync(ct)).Where(a => a.Active).ToList(),
            people,
            await uow.Store<ISafetyRuleLookup>().GetEffectiveRulesAsync(at, ct))
        {
            Sites = await resources.ListSitesAsync(ct),
            Shifts = await resources.ListShiftsAsync(ct),
            WorkCenterShifts = await resources.ListWorkCenterShiftsAsync(ct),
            Closures = await resources.ListSiteClosuresAsync(ct),
            Downtime = await uow.Store<IMaintenanceLookup>().ListDowntimeAsync(from, to, ct),
            Unavailability = await workforce.ListUnavailabilityAsync(from, to, ct),
        };
    }
}
