using Forge.Competency.Contracts;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
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
        var tools = uow.Store<IToolLookup>();
        var workforce = uow.Store<IWorkforceLookup>();
        var workers = await workforce.GetWorkerStatesAsync(at, ct);
        var qualifications = await uow.Store<ICompetencyLookup>().GetQualificationsAsync(workers.Select(w => new PersonId(w.PersonId)).ToList(), ct);
        var machineAuthorizations = await uow.Store<ICompetencyLookup>().GetMachineAuthorizationsAsync(workers.Select(w => new PersonId(w.PersonId)).ToList(), ct);
        var valid = qualifications
            .Where(q => q.IsValidAt(at))
            .GroupBy(q => q.PersonId)
            .ToDictionary(g => g.Key, g => g.OrderBy(q => q.Code, StringComparer.Ordinal).ToList());
        var validMachineAuthorizations = machineAuthorizations.Where(record => record.IsValidAt(at))
            .GroupBy(record => record.PersonId)
            .ToDictionary(group => group.Key, group => group.GroupBy(record => $"{record.MachineCode}|{record.ActivityCode}|{record.QualificationCode}", StringComparer.Ordinal)
                .ToDictionary(records => records.Key, records => records.Select(record => record.ExpiresAt).OrderByDescending(expiry => expiry ?? DateTimeOffset.MaxValue).First(), StringComparer.Ordinal));

        // V3.9: training on controlled documents, and who has open direct labor (active on an operation).
        var trained = (await uow.Store<IDocumentTrainingLookup>().ListTrainingAsync(workers.Select(w => w.PersonId).ToList(), ct))
            .GroupBy(t => t.PersonId)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.Select(t => t.Key).ToHashSet(StringComparer.Ordinal));
        var labor = (await uow.Store<IExecutionLookup>().ListOpenLaborAsync(ct))
            .GroupBy(l => l.PersonId)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.StartedAt).Last());

        var periods = (await uow.Store<IWorkforceLookup>().ListWorkCenterPeriodsAsync(ct)).ToLookup(p => p.PersonId);
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
                    WorkCenterPeriods = periods[w.PersonId].ToList(),
                    ShiftCode = w.ShiftCode,
                    QualificationExpiry = held.ToDictionary(q => q.Code, q => q.ExpiresAt, StringComparer.Ordinal),
                    MachineAuthorizationExpiry = validMachineAuthorizations.TryGetValue(w.PersonId, out var authorizations)
                        ? authorizations
                        : new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal),
                    TrainedDocuments = trained.TryGetValue(w.PersonId, out var docs) ? docs : new HashSet<string>(StringComparer.Ordinal),
                    ActiveOperation = labor.TryGetValue(w.PersonId, out var l) ? $"{l.OrderNumber}/{l.Sequence}" : null,
                };
            })
            .OrderBy(p => p.PersonId)
            .ToList();

        var from = at.AddDays(-1);
        var to = at + Reach;
        // Production stoppages and maintenance outages remain their own source records. Both
        // remove executable machine time; ending one never closes the other. The source category,
        // observed reason code and note are evidence, without a claim about a proven root cause.
        var downtime = await uow.Store<IMaintenanceLookup>().ListDowntimeAsync(from, to, ct);
        var stoppages = await uow.Store<IProductionActualsLookup>().ListStoppagesAsync(from, to, false, ct);
        var productionDowntime = stoppages.Select(stoppage => new DowntimeWindow(
            -2000000000000 - stoppage.StoppageId, stoppage.MachineCode,
            Enum.Parse<DowntimeKind>(stoppage.Category.ToString()), stoppage.StartedAt, stoppage.EndedAt,
            $"Production stoppage {stoppage.StoppageId}: {stoppage.Category}; observed reason {stoppage.ReasonCode}{(stoppage.Note is { Length: > 0 } note ? $": {note}" : string.Empty)}",
            SourceContext: "Execution", SourceReference: $"Stoppage/{stoppage.StoppageId}"));

        // Retired records stay in Resources for history; planning only sees what can still be used.
        var workCenters = (await resources.ListWorkCentersAsync(ct)).Where(w => w.Active).ToList();
        var machines = (await resources.ListMachinesAsync(ct)).Where(m => m.Active).ToList();
        var machineCodes = machines.Select(m => m.Code).ToHashSet(StringComparer.Ordinal);
        var workCenterCodes = workCenters.Select(w => w.Code).ToHashSet(StringComparer.Ordinal);
        var holds = await uow.Store<Forge.Kernel.Holds.IHoldLookup>().ListHoldsAsync(false, ct);
        var holdPolicies = await uow.Store<Forge.Kernel.Holds.IHoldLookup>().ListKindPoliciesAsync(ct);
        var propagation = new Dictionary<long, Forge.Kernel.Holds.HoldPropagationScope>();
        foreach (var hold in holds.Where(h => h.TargetType == Forge.Kernel.Holds.HoldTargetType.Lot && Forge.Kernel.Holds.HoldService.PolicyOf(holdPolicies, h.Kind).PropagateDownstream))
            propagation[hold.HoldId] = await uow.Store<Forge.Kernel.Holds.IHoldPropagationLookup>().ScopeAsync(hold, ct);
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
            HomeTimeZone = await resources.SiteTimeZoneAsync(null, ct),
            Shifts = await resources.ListShiftsAsync(ct),
            WorkCenterShifts = await resources.ListWorkCenterShiftsAsync(ct),
            Closures = await resources.ListSiteClosuresAsync(ct),
            Downtime = downtime.Concat(productionDowntime).OrderBy(window => window.MachineCode, StringComparer.Ordinal).ThenBy(window => window.StartsAt).ThenBy(window => window.Id).ToArray(),
            Unavailability = await workforce.ListUnavailabilityAsync(from, to, ct),
            ToolTypes = await tools.ListToolTypesAsync(ct),
            Tools = await tools.ListToolsAsync(ct),
            ToolEvents = await tools.ListToolEventsAsync(null, ct),
            ToolRequirements = await tools.ListToolRequirementsAsync(ct),
            Holds = holds,
            HoldPolicies = holdPolicies,
            HoldPropagation = propagation,
        };
    }
}
