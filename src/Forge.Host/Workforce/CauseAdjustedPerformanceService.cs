using Forge.Execution;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Quality.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Host.Workforce;

/// <summary>Minutes of one kind inside a shift; overlapping intervals of the same kind count once.</summary>
public sealed record TimeCategory(string Code, string Label, decimal Minutes, string Source, bool ExternalDelay);

/// <summary>A machine event that overlapped the worker's assigned labor (§16.1). Its full length is shown, but only the overlap is charged.</summary>
public sealed record MachineEventImpact(string MachineCode, string Source, string Kind, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, decimal FullEventMinutes,
    decimal EmployeeImpactMinutes, bool Verified);

public sealed record ShiftAttribution(
    long AttendanceId,
    DateTimeOffset ClockInAt,
    DateTimeOffset? ClockOutAt,
    bool OpenShift,
    PaidTimeReconciliation Reconciliation,
    IReadOnlyList<TimeCategory> Categories,
    decimal DirectLaborMinutes,
    decimal VerifiedExternalDelayMinutes,
    decimal ReportedUnverifiedDelayMinutes,
    IReadOnlyList<MachineEventImpact> MachineEvents,
    IReadOnlyList<string> PendingReviewNotes);

public sealed record EfficiencyScope(long OrderNumber, int Sequence, decimal GoodQuantity, decimal EarnedStandardHours, decimal DirectLaborHours, decimal ExcludedDelayHours);

/// <param name="Cohort">One first-pass inspection's good or failed units, with the verified cause that decides whether the employee metric may use it.</param>
public sealed record FirstPassCohortView(long InspectionId, long OrderNumber, int Sequence, decimal Quantity, bool Passed, string Cause, bool Verified, string AttributionState, long? AttributionId);

public sealed record CauseAdjustedPerformanceReport(
    Guid PersonId,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<ShiftAttribution> Shifts,
    decimal PaidMinutes,
    decimal AccountedMinutes,
    decimal GapMinutes,
    decimal OverlapMinutes,
    ControllableLaborEfficiency Efficiency,
    IReadOnlyList<EfficiencyScope> EfficiencyScope,
    int OperationsOutsideEfficiencyScope,
    FirstPassYieldMetrics? FirstPass,
    IReadOnlyList<FirstPassCohortView> FirstPassCohorts,
    bool QualityRestricted,
    string Basis);

/// <summary>
/// Cause-adjusted performance (Sprint 10 §16) from the records Forge already keeps: attendance is paid time, labor is operation time,
/// Maintenance downtime is verified machine loss, stoppages are reported (unverified) loss, approved coded time covers the rest, and
/// Quality's verified attributions decide the employee-attributable FPY. Nothing here ranks, scores or recommends anything about a person.
/// </summary>
public sealed class CauseAdjustedPerformanceService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    public const string BasisText = "Paid time is the worker's recorded attendance. Operation labor, verified Maintenance downtime and approved coded time account for it; overlaps count once and gaps stay visible. Stoppages reported on the floor are shown but are not excluded until verified. Earned standard hours use the released routing standards, only for operations whose latest inspection passed; operations without accepted output are outside the efficiency scope rather than counted as zero. Official process FPY keeps every first-pass unit; the employee-attributable diagnostic uses only Quality-verified attributions and reports its coverage.";

    private static readonly LaborPhase[] DirectPhases = [LaborPhase.Setup, LaborPhase.Run, LaborPhase.Rework];

    public async Task<CauseAdjustedPerformanceReport> BuildAsync(ForgePrincipal principal, Guid personId, DateTimeOffset from, DateTimeOffset to, decimal minimumCoverage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (to <= from || to - from > TimeSpan.FromDays(31))
            throw new CommandRejectedException("invalid_date_range", "Choose a period of up to 31 days.");
        return (await BuildManyAsync(principal, [personId], from, to, minimumCoverage, ct))[0];
    }

    /// <summary>The same report for several people over up to a year, reading each source once (quarterly and annual reviews).</summary>
    public async Task<IReadOnlyList<CauseAdjustedPerformanceReport>> BuildManyAsync(ForgePrincipal principal, IReadOnlyList<Guid> personIds, DateTimeOffset from, DateTimeOffset to,
        decimal minimumCoverage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(personIds);
        Demand(principal, Permissions.WorkforceRead, "Workforce");
        (from, to) = (from.ToUniversalTime(), to.ToUniversalTime());
        if (to <= from || to - from > TimeSpan.FromDays(370))
            throw new CommandRejectedException("invalid_date_range", "Choose a period of up to a year.");
        if (minimumCoverage is < 0 or > 100)
            throw new CommandRejectedException("invalid_coverage", "The coverage threshold is a percentage.");
        var now = time.GetUtcNow();

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var workforce = uow.Store<IWorkforceLookup>();
        var execution = uow.Store<IExecutionLookup>();
        var allAttendance = await workforce.ListAttendanceAsync(from, to, ct);
        var allLabor = await execution.ListLaborAsync(from, to, null, ct);
        var allReports = await execution.ListReportsAsync(from, to, null, ct);
        var downtime = (await uow.Store<IMaintenanceLookup>().ListDowntimeAsync(from, to, ct)).Where(d => !d.IsHypothetical).ToList();
        var stoppages = await uow.Store<IProductionActualsLookup>().ListStoppagesAsync(from, to, false, ct);
        var qualityRestricted = !authorizer.Authorize(principal, Permissions.QualityRead, ResourceRef.Of("Quality")).IsPermitted;

        var result = new List<CauseAdjustedPerformanceReport>();
        foreach (var personId in personIds)
        {
            var attendance = allAttendance.Where(a => a.PersonId == personId).OrderBy(a => a.ClockInAt).ToList();
            var labor = allLabor.Where(l => l.PersonId == personId).ToList();
            var reports = allReports.Where(r => r.PersonId == personId).ToList();
            var coded = await uow.Store<ICodedTimeLookup>().ListCodedTimeAsync(personId, from, to, ct);

            var shifts = new List<ShiftAttribution>();
            var allVerifiedExternal = new List<PerformanceInterval>();
            foreach (var record in attendance)
            {
                var end = record.ClockOutAt ?? (now < to ? now : to);
                if (end <= record.ClockInAt) continue;
                shifts.Add(Shift(personId, record, end, labor, coded, downtime, stoppages, allVerifiedExternal));
            }

            var (efficiency, scope, outside) = await EfficiencyAsync(uow, principal, labor, reports, allVerifiedExternal, ct);
            FirstPassYieldMetrics? firstPass = null;
            IReadOnlyList<FirstPassCohortView> cohorts = [];
            if (!qualityRestricted)
                (firstPass, cohorts) = await FirstPassAsync(uow, principal, labor, from, to, minimumCoverage, ct);

            result.Add(new CauseAdjustedPerformanceReport(personId, from, to, shifts,
                shifts.Sum(s => s.Reconciliation.ExpectedPaidMinutes), shifts.Sum(s => s.Reconciliation.AccountedPaidMinutes),
                shifts.Sum(s => s.Reconciliation.GapMinutes), shifts.Sum(s => s.Reconciliation.OverlapMinutes),
                efficiency, scope, outside, firstPass, cohorts, qualityRestricted, BasisText));
        }

        return result;
    }

    private static ShiftAttribution Shift(Guid personId, AttendanceRecord record, DateTimeOffset end, IReadOnlyList<LaborEntry> labor,
        IReadOnlyList<CodedTimeEntry> coded, IReadOnlyList<DowntimeWindow> downtime, IReadOnlyList<StoppageInfo> stoppages, List<PerformanceInterval> allVerifiedExternal)
    {
        var shift = new PerformanceInterval(record.ClockInAt, end);
        var key = $"attendance-{record.AttendanceId}";
        var approved = coded.Where(c => c.State == CodedTimeState.Approved).ToList();
        var unpaid = approved.Where(c => !c.Paid).Select(c => Clip(c.StartsAt, c.EndsAt, shift)).OfType<PerformanceInterval>().ToList();
        var expected = Math.Max(0m, shift.Minutes - PerformanceIntervalMath.UnionMinutes(unpaid));

        var laborIntervals = labor
            .Select(l => (Entry: l, Interval: Clip(l.StartedAt, l.EndedAt ?? end, shift)))
            .Where(x => x.Interval is not null)
            .Select(x => (x.Entry, Interval: x.Interval!))
            .ToList();
        var paidCoded = approved.Where(c => c.Paid).Select(c => (Entry: c, Interval: Clip(c.StartsAt, c.EndsAt, shift))).Where(x => x.Interval is not null)
            .Select(x => (x.Entry, Interval: x.Interval!)).ToList();
        var activities = laborIntervals.Select(x => new PaidTimeActivityInterval(personId, key, x.Interval, "Labor:" + x.Entry.Phase))
            .Concat(paidCoded.Select(x => new PaidTimeActivityInterval(personId, key, x.Interval, x.Entry.Code.ToString())))
            .ToList();
        var reconciliation = PaidTimeReconciliationCalculator.Calculate(personId, key, shift, expected, activities);

        // Machine events charged only where they overlap this worker's labor on that machine, inside the paid shift.
        var events = new List<MachineEventImpact>();
        var verifiedWindows = new List<PerformanceInterval>();
        var reportedWindows = new List<PerformanceInterval>();
        var classified = paidCoded.Select(x => x.Interval).ToList();
        foreach (var (entry, interval) in laborIntervals.Where(x => x.Entry.MachineCode is not null))
        {
            foreach (var window in downtime.Where(d => d.MachineCode == entry.MachineCode))
            {
                var machineEvent = new PerformanceInterval(window.StartsAt, window.EndsAt ?? end);
                if (machineEvent.EndsAt <= machineEvent.StartsAt) continue;
                var overlap = PerformanceIntervalMath.Intersect(machineEvent, interval);
                if (overlap is null) continue;
                var impact = MachineDowntimeImpactCalculator.Calculate(machineEvent, interval, shift, classified);
                events.Add(new MachineEventImpact(window.MachineCode, "Maintenance downtime", window.Kind.ToString(), window.StartsAt, window.EndsAt,
                    machineEvent.Minutes, impact.EmployeeMachineImpactMinutes, true));
                verifiedWindows.Add(overlap);
            }

            foreach (var stop in stoppages.Where(s => s.MachineCode == entry.MachineCode && s.Category is StoppageCategory.Maintenance or StoppageCategory.Unplanned
                         or StoppageCategory.Starved or StoppageCategory.Waiting or StoppageCategory.Blocked))
            {
                var machineEvent = new PerformanceInterval(stop.StartedAt, stop.EndedAt ?? end);
                if (machineEvent.EndsAt <= machineEvent.StartsAt) continue;
                var overlap = PerformanceIntervalMath.Intersect(machineEvent, interval);
                if (overlap is null) continue;
                var impact = MachineDowntimeImpactCalculator.Calculate(machineEvent, interval, shift, classified);
                events.Add(new MachineEventImpact(stop.MachineCode, $"Stoppage reported ({stop.ReasonCode})", stop.Category.ToString(), stop.StartedAt, stop.EndedAt,
                    machineEvent.Minutes, impact.EmployeeMachineImpactMinutes, false));
                reportedWindows.Add(overlap);
            }
        }

        var codedExternal = paidCoded.Where(x => x.Entry.ExternalDelay).Select(x => x.Interval).ToList();
        var verifiedExternal = verifiedWindows.Concat(codedExternal).ToList();
        allVerifiedExternal.AddRange(verifiedExternal);
        var verifiedMinutes = PerformanceIntervalMath.UnionMinutes(verifiedExternal);
        var reportedOnly = Math.Max(0m, PerformanceIntervalMath.UnionMinutes(verifiedExternal.Concat(reportedWindows)) - verifiedMinutes);
        var direct = PerformanceIntervalMath.UnionMinutes(laborIntervals.Where(x => DirectPhases.Contains(x.Entry.Phase)).Select(x => x.Interval));

        var categories = laborIntervals.GroupBy(x => x.Entry.Phase)
            .Select(g => new TimeCategory("Labor:" + g.Key, PhaseLabel(g.Key), PerformanceIntervalMath.UnionMinutes(g.Select(x => x.Interval)), "Operation labor", false))
            .Concat(paidCoded.GroupBy(x => x.Entry.Code)
                .Select(g => new TimeCategory(g.Key.ToString(), CodeLabel(g.Key), PerformanceIntervalMath.UnionMinutes(g.Select(x => x.Interval)), "Approved coded time", g.First().Entry.ExternalDelay)))
            .Append(new TimeCategory("MachineDowntime:Verified", "Verified machine downtime during assigned work", PerformanceIntervalMath.UnionMinutes(verifiedWindows), "Maintenance downtime", true))
            .Where(c => c.Minutes > 0)
            .ToList();
        var pending = coded.Where(c => c.State == CodedTimeState.PendingReview && Clip(c.StartsAt, c.EndsAt, shift) is not null)
            .Select(c => $"{CodeLabel(c.Code)} {c.StartsAt:HH:mm}–{c.EndsAt:HH:mm} is waiting for review and is not counted yet.")
            .ToList();
        return new ShiftAttribution(record.AttendanceId, record.ClockInAt, record.ClockOutAt, record.ClockOutAt is null, reconciliation, categories,
            direct, verifiedMinutes, reportedOnly, events, pending);
    }

    private async Task<(ControllableLaborEfficiency Efficiency, IReadOnlyList<EfficiencyScope> Scope, int Outside)> EfficiencyAsync(
        IKernelUnitOfWork uow, ForgePrincipal principal, IReadOnlyList<LaborEntry> labor, IReadOnlyList<ProductionReport> reports,
        IReadOnlyList<PerformanceInterval> verifiedExternal, CancellationToken ct)
    {
        var execution = uow.Store<IExecutionLookup>();
        var quality = authorizer.Authorize(principal, Permissions.QualityRead, ResourceRef.Of("Quality")).IsPermitted ? uow.Store<IQualityLookup>() : null;
        var scope = new List<EfficiencyScope>();
        var outside = 0;
        var directLabor = labor.Where(l => DirectPhases.Contains(l.Phase) && l.EndedAt is not null).ToList();
        var totalDirectHours = PerformanceIntervalMath.UnionMinutes(directLabor.Select(l => new PerformanceInterval(l.StartedAt, l.EndedAt!.Value))
            .Where(i => i.EndsAt > i.StartsAt)) / 60m;
        foreach (var op in directLabor.GroupBy(l => (l.OrderNumber, l.Sequence)))
        {
            var order = await execution.FindOrderAsync(op.Key.OrderNumber, ct);
            var operation = order?.Operations.SingleOrDefault(o => o.Sequence == op.Key.Sequence);
            var good = reports.Where(r => r.OrderNumber == op.Key.OrderNumber && r.Sequence == op.Key.Sequence).Sum(r => r.GoodQuantity);
            var visible = order is not null && (order.ProgramId is not { } program || principal.Programs.Contains(new ProgramId(program)));
            var accepted = quality is not null && (await quality.ListInspectionsAsync(op.Key.OrderNumber, 200, ct))
                .Where(i => i.Sequence == op.Key.Sequence).OrderBy(i => i.At).LastOrDefault() is { Result: InspectionResult.Pass };
            if (!visible || operation is null || order!.Quantity <= 0 || good <= 0 || !accepted)
            {
                outside++;
                continue;
            }

            var setup = operation.SetupMinutes ?? 0m;
            var runPerUnit = Math.Max(0m, operation.PlannedMinutes - setup) / operation.QuantityFor(order.Quantity);
            var earned = ((op.Any(l => l.Phase == LaborPhase.Setup) ? setup : 0m) + runPerUnit * good) / 60m;
            var intervals = op.Select(l => new PerformanceInterval(l.StartedAt, l.EndedAt!.Value)).Where(i => i.EndsAt > i.StartsAt).ToList();
            var hours = PerformanceIntervalMath.UnionMinutes(intervals) / 60m;
            var delays = PerformanceIntervalMath.UnionMinutes(intervals.SelectMany(i => verifiedExternal.Select(e => PerformanceIntervalMath.Intersect(i, e)))
                .OfType<PerformanceInterval>()) / 60m;
            scope.Add(new EfficiencyScope(op.Key.OrderNumber, op.Key.Sequence, good, decimal.Round(earned, 4), decimal.Round(hours, 4), decimal.Round(Math.Min(delays, hours), 4)));
        }

        var scopeHours = scope.Sum(s => s.DirectLaborHours);
        var scopeDelays = scope.Sum(s => s.ExcludedDelayHours);
        decimal? earnedHours = scope.Count > 0 ? scope.Sum(s => s.EarnedStandardHours) : null;
        var efficiency = ControllableLaborEfficiencyCalculator.Calculate(earnedHours, scopeHours, Math.Max(0m, scopeHours - scopeDelays), scopeDelays);
        if (earnedHours is null && totalDirectHours > 0)
            efficiency = efficiency with { Status = "N/A: no operation in this period has accepted output (a passed inspection) and a released routing standard." };
        return (efficiency, scope, outside);
    }

    private static async Task<(FirstPassYieldMetrics Metrics, IReadOnlyList<FirstPassCohortView> Cohorts)> FirstPassAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        IReadOnlyList<LaborEntry> labor, DateTimeOffset from, DateTimeOffset to, decimal minimumCoverage, CancellationToken ct)
    {
        var operations = labor.Where(l => l.Phase is LaborPhase.Run or LaborPhase.Setup or LaborPhase.Rework).Select(l => (l.OrderNumber, l.Sequence)).ToHashSet();
        var execution = uow.Store<IExecutionLookup>();
        var hidden = (await execution.ListOrdersAsync(ct)).Where(o => o.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p))).Select(o => o.OrderNumber).ToHashSet();
        var inspections = (await uow.Store<IQualityLookup>().ListInspectionsBetweenAsync(from.AddDays(-31), to, ct))
            .Where(i => operations.Contains((i.OrderNumber, i.Sequence)) && !hidden.Contains(i.OrderNumber))
            .GroupBy(i => (i.OrderNumber, i.Sequence))
            .Select(g => g.OrderBy(i => i.At).ThenBy(i => i.InspectionId).First())
            .Where(i => i.At >= from && i.At < to)
            .ToList();
        var attributions = await uow.Store<IFirstPassAttributionLookup>().ListAttributionsAsync([.. inspections.Select(i => i.InspectionId)], ct);
        var cohorts = new List<FirstPassCohortView>();
        var results = new List<FirstPassInspectionResult>();
        foreach (var inspection in inspections)
        {
            var current = attributions.Where(a => a.InspectionId == inspection.InspectionId && a.State is AttributionState.Verified or AttributionState.Proposed)
                .OrderByDescending(a => a.State == AttributionState.Verified).ThenByDescending(a => a.AttributionId).FirstOrDefault();
            var verified = current?.State == AttributionState.Verified;
            var failed = inspection.Result == InspectionResult.Fail ? Math.Min(inspection.QuantityInspected, current?.FailedQuantity ?? inspection.QuantityInspected) : 0m;
            var good = inspection.QuantityInspected - failed;
            var opportunity = verified && current!.EmployeeOpportunity;
            if (good > 0)
            {
                var cause = opportunity ? FirstPassCause.EmployeeControlled : FirstPassCause.Unknown;
                results.Add(new FirstPassInspectionResult($"inspection:{inspection.InspectionId}:good", good, true, cause, verified, opportunity));
                cohorts.Add(new FirstPassCohortView(inspection.InspectionId, inspection.OrderNumber, inspection.Sequence, good, true, cause.ToString(), verified,
                    current?.State.ToString() ?? "None", current?.AttributionId));
            }

            if (failed > 0)
            {
                var cause = current is null ? FirstPassCause.Unknown : Map(current.Cause);
                results.Add(new FirstPassInspectionResult($"inspection:{inspection.InspectionId}:failed", failed, false, cause, verified, opportunity));
                cohorts.Add(new FirstPassCohortView(inspection.InspectionId, inspection.OrderNumber, inspection.Sequence, failed, false, cause.ToString(), verified,
                    current?.State.ToString() ?? "None", current?.AttributionId));
            }
        }

        return (FirstPassYieldCalculator.Calculate(results, minimumCoverage), cohorts);
    }

    private static FirstPassCause Map(DefectCause cause) => cause switch
    {
        DefectCause.EmployeeControlled => FirstPassCause.EmployeeControlled,
        DefectCause.Machine => FirstPassCause.Machine,
        DefectCause.IncomingMaterial => FirstPassCause.IncomingMaterial,
        DefectCause.UpstreamProcess => FirstPassCause.UpstreamProcess,
        DefectCause.Mixed => FirstPassCause.Mixed,
        _ => FirstPassCause.Unknown,
    };

    private static PerformanceInterval? Clip(DateTimeOffset start, DateTimeOffset end, PerformanceInterval shift) =>
        end > start ? PerformanceIntervalMath.Intersect(new PerformanceInterval(start, end), shift) : null;

    private static string PhaseLabel(LaborPhase phase) => phase switch
    {
        LaborPhase.Setup => "Setup",
        LaborPhase.Run => "Productive operation",
        LaborPhase.Inspection => "Operator inspection",
        LaborPhase.Rework => "Rework",
        _ => "Supervised training",
    };

    private static string CodeLabel(CodedTimeCode code) => code switch
    {
        CodedTimeCode.IndirectTask => "Indirect task",
        CodedTimeCode.MaterialShortage => "Material shortage",
        CodedTimeCode.WaitingForInspection => "Waiting for inspection",
        CodedTimeCode.QualityHold => "Quality hold",
        CodedTimeCode.MachineDowntime => "Machine downtime (coded)",
        _ => code.ToString(),
    };

    private void Demand(ForgePrincipal principal, string permission, string resource)
    {
        var decision = authorizer.Authorize(principal, permission, ResourceRef.Of(resource));
        if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision);
    }
}
