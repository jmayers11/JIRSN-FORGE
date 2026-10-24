using Forge.Competency.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Engines;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;

namespace Forge.Planning;

/// <summary>
/// One person's verified work on one operation (Sprint 10 §21.2): setup and run minutes from their own labor intervals (overlaps counted once),
/// less Maintenance downtime on that machine, with the good quantity they reported. A badge scan or an assignment alone is never an observation.
/// </summary>
public sealed record SkillObservation(
    Guid PersonId,
    long OrderNumber,
    int Sequence,
    string? MachineCode,
    string ActivityCode,
    string PartNumber,
    DateTimeOffset LastWorkedAt,
    decimal SetupMinutes,
    decimal RunMinutes,
    decimal ExcludedDowntimeMinutes,
    decimal GoodQuantity)
{
    public decimal? RunMinutesPerGoodUnit => GoodQuantity > 0 && RunMinutes > 0 ? RunMinutes / GoodQuantity : null;
}

/// <summary>The routing standard of the work a forecast is for: setup minutes and run minutes per unit.</summary>
public sealed record OperationStandard(string PartNumber, decimal SetupMinutes, decimal RunMinutesPerUnit, long? OrderNumber, int? Sequence);

/// <summary>
/// A capacity forecast with its evidence (§21.5): qualified, last operated, expected setup, good-unit range, confidence, refresher, and basis.
/// A planning estimate only; it is never a grade of the person.
/// </summary>
public sealed record SkillCapacityView(
    Guid PersonId,
    string PersonName,
    string? MachineCode,
    string ActivityCode,
    SkillCapacityForecast Forecast,
    DateTimeOffset? LastOperatedAt,
    decimal? ExpectedSetupMinutes,
    string? RequiredRefresher,
    string RuleVersion,
    IReadOnlyList<string> Notes);

/// <summary>A ranked recovery candidate with what they could produce at the gap and what their original work loses (§21.4).</summary>
public sealed record CandidateCapacity(int Rank, Guid PersonId, SkillCapacityView AtTarget, SkillCapacityView? AtOrigin, string Explanation);

public sealed record WorkforceRecoveryCapacity(EngineRun<WorkforceRecoveryResult> Recovery, IReadOnlyList<CandidateCapacity> Candidates, int ScheduledMinutes, string RuleVersion);

/// <summary>Builds verified observations and transparent, configurable capacity estimates (§21.2–21.3).</summary>
public static class SkillEvidence
{
    public const string RuleVersion = "skill-capacity-heuristic-v1";

    /// <summary>Benchmark multipliers on the routing standard when personal history is insufficient: conservative, expected, optimistic.</summary>
    public static readonly (decimal Conservative, decimal Expected, decimal Optimistic) BenchmarkFactors = (1.5m, 1.25m, 1.0m);

    public const int MinimumObservations = 3;

    public static IReadOnlyList<SkillObservation> Observations(
        IReadOnlyList<LaborEntry> labor,
        IReadOnlyList<ProductionReport> reports,
        IReadOnlyList<DowntimeWindow> downtime,
        IReadOnlyDictionary<(long Order, int Sequence), (string Activity, string Part)> operations)
    {
        ArgumentNullException.ThrowIfNull(labor);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(downtime);
        ArgumentNullException.ThrowIfNull(operations);
        var result = new List<SkillObservation>();
        foreach (var group in labor.Where(l => l.EndedAt is { } end && end > l.StartedAt && l.Phase is LaborPhase.Setup or LaborPhase.Run)
                     .GroupBy(l => (l.PersonId, l.OrderNumber, l.Sequence)))
        {
            if (!operations.TryGetValue((group.Key.OrderNumber, group.Key.Sequence), out var operation)) continue;
            var machine = group.Select(l => l.MachineCode).FirstOrDefault(m => m is not null);
            var down = downtime.Where(d => !d.IsHypothetical && d.MachineCode == machine && d.EndsAt is { } e && e > d.StartsAt)
                .Select(d => new PerformanceInterval(d.StartsAt, d.EndsAt!.Value)).ToList();
            decimal Minutes(LaborPhase phase, out decimal excluded)
            {
                var intervals = group.Where(l => l.Phase == phase).Select(l => new PerformanceInterval(l.StartedAt, l.EndedAt!.Value)).ToList();
                var total = PerformanceIntervalMath.UnionMinutes(intervals);
                excluded = PerformanceIntervalMath.UnionMinutes(intervals.SelectMany(i => down.Select(d => PerformanceIntervalMath.Intersect(i, d))).OfType<PerformanceInterval>());
                return Math.Max(0m, total - excluded);
            }

            var setup = Minutes(LaborPhase.Setup, out var setupExcluded);
            var run = Minutes(LaborPhase.Run, out var runExcluded);
            var good = reports.Where(r => r.PersonId == group.Key.PersonId && r.OrderNumber == group.Key.OrderNumber && r.Sequence == group.Key.Sequence).Sum(r => r.GoodQuantity);
            if (setup <= 0 && (run <= 0 || good <= 0)) continue;
            result.Add(new SkillObservation(group.Key.PersonId, group.Key.OrderNumber, group.Key.Sequence, machine, operation.Activity, operation.Part,
                group.Max(l => l.EndedAt!.Value), setup, run, setupExcluded + runExcluded, good));
        }

        return result;
    }

    /// <summary>
    /// Turns observations into a forecast input. Three or more comparable verified runs give a history-based range (median, 25th and 75th
    /// percentile); fewer fall back to a labeled, conservative benchmark on the routing standard; neither gives no estimate at all.
    /// Reacclimation is added to setup time, never as a second penalty on the run rate.
    /// </summary>
    public static (SkillCapacityForecastInput Input, DateTimeOffset? LastOperated, IReadOnlyList<string> Notes) Estimate(
        Guid personId, string? machineCode, string activityCode, OperationStandard? standard, IReadOnlyList<SkillObservation> observations,
        DateTimeOffset asOf, int scheduledMinutes, int breakMinutes, bool qualified, bool refresherSatisfied, bool authorized, bool safety, bool twoPerson,
        int? reacclimationMinutes, int? refresherAfterDays)
    {
        ArgumentNullException.ThrowIfNull(activityCode);
        ArgumentNullException.ThrowIfNull(observations);
        var notes = new List<string>();
        var mine = observations.Where(o => o.PersonId == personId && o.ActivityCode == activityCode).ToList();
        var sameMachine = mine.Where(o => machineCode is null || o.MachineCode == machineCode).ToList();
        var pool = sameMachine.Count > 0 ? sameMachine : mine;
        if (sameMachine.Count == 0 && mine.Count > 0) notes.Add("No history on this machine; history from the same activity on other machines is used.");
        var samePart = standard is null ? pool : pool.Where(o => o.PartNumber == standard.PartNumber).ToList();
        var comparable = samePart.Count >= MinimumObservations ? samePart : pool;
        if (standard is not null && comparable != samePart && pool.Count > 0)
            notes.Add($"Fewer than {MinimumObservations} runs of {standard.PartNumber}; other parts of the same activity are used, so confidence is lower.");
        var lastOperated = mine.Where(o => o.GoodQuantity > 0).Select(o => (DateTimeOffset?)o.LastWorkedAt).Max();
        var daysSince = lastOperated is { } last ? (int)Math.Floor((asOf - last).TotalDays) : (int?)null;
        var runs = comparable.Select(o => o.RunMinutesPerGoodUnit).OfType<decimal>().Order().ToList();
        var setups = comparable.Where(o => o.SetupMinutes > 0).Select(o => o.SetupMinutes).Order().ToList();
        var reacclimate = reacclimationMinutes is > 0 && refresherAfterDays is { } window && (daysSince is null || daysSince > window) ? reacclimationMinutes.Value : 0;
        if (reacclimate > 0) notes.Add($"{reacclimate} minutes of reacclimation are added to setup after {(daysSince is null ? "no verified operation" : $"{daysSince} days away")}.");
        if (comparable.Sum(o => o.ExcludedDowntimeMinutes) > 0) notes.Add("Machine downtime during the history was excluded from the run rate.");

        CapacityEstimateEvidence evidence;
        CapacityEstimateConfidence confidence;
        decimal?[] setup;
        decimal?[] run;
        string basis;
        var count = runs.Count;
        if (count >= MinimumObservations)
        {
            evidence = CapacityEstimateEvidence.VerifiedContextualHistory;
            run = [Percentile(runs, 0.75m), Percentile(runs, 0.5m), Percentile(runs, 0.25m)];
            setup = setups.Count >= MinimumObservations
                ? [Percentile(setups, 0.75m), Percentile(setups, 0.5m), Percentile(setups, 0.25m)]
                : standard is not null ? Benchmark(standard.SetupMinutes) : [0m, 0m, 0m];
            confidence = count >= 5 && comparable == samePart && daysSince is <= 90 ? CapacityEstimateConfidence.High : CapacityEstimateConfidence.Medium;
            basis = $"{count} verified runs of {activityCode}{(sameMachine.Count > 0 && machineCode is not null ? $" on {machineCode}" : string.Empty)}{(comparable == samePart && standard is not null ? $" ({standard.PartNumber})" : string.Empty)}; median and quartiles of run minutes per good unit.";
        }
        else if (standard is not null && standard.RunMinutesPerUnit > 0)
        {
            evidence = CapacityEstimateEvidence.CalibratedRoleBenchmark;
            run = Benchmark(standard.RunMinutesPerUnit);
            setup = Benchmark(standard.SetupMinutes);
            confidence = CapacityEstimateConfidence.Low;
            basis = $"Routing standard for {standard.PartNumber} × {BenchmarkFactors.Optimistic}–{BenchmarkFactors.Conservative}; only {count} verified personal run(s), so the range is conservative.";
        }
        else
        {
            evidence = CapacityEstimateEvidence.None;
            run = [null, null, null];
            setup = [null, null, null];
            confidence = CapacityEstimateConfidence.Low;
            basis = "No verified history and no routing standard for this work.";
        }

        if (evidence != CapacityEstimateEvidence.None && reacclimate > 0)
            setup = [.. setup.Select(s => s + reacclimate)];
        var input = new SkillCapacityForecastInput(qualified, refresherSatisfied, authorized, safety, twoPerson, Math.Max(0, scheduledMinutes), Math.Max(0, breakMinutes), 0,
            evidence, basis, evidence == CapacityEstimateEvidence.VerifiedContextualHistory ? count : 0, asOf, lastOperated,
            setup[0], setup[1], setup[2], run[0], run[1], run[2], confidence);
        return (input, lastOperated, notes);
    }

    private static decimal?[] Benchmark(decimal standard) =>
        [decimal.Round(standard * BenchmarkFactors.Conservative, 4), decimal.Round(standard * BenchmarkFactors.Expected, 4), decimal.Round(standard * BenchmarkFactors.Optimistic, 4)];

    /// <summary>Linear-interpolated percentile of an ascending list.</summary>
    public static decimal Percentile(IReadOnlyList<decimal> ascending, decimal p)
    {
        ArgumentNullException.ThrowIfNull(ascending);
        if (ascending.Count == 0) throw new ArgumentException("No values.", nameof(ascending));
        var rank = p * (ascending.Count - 1);
        var lower = (int)decimal.Floor(rank);
        var upper = Math.Min(lower + 1, ascending.Count - 1);
        return decimal.Round(ascending[lower] + (ascending[upper] - ascending[lower]) * (rank - lower), 4);
    }
}

/// <summary>
/// Loads the evidence for capacity forecasts from the authoritative records (labor, production reports, Maintenance downtime, Competency)
/// and attaches them to the existing workforce-recovery ranking. It writes nothing and adds no scheduler.
/// </summary>
public sealed class SkillCapacityService(IKernelUnitOfWorkFactory unitOfWork, ReadinessService readiness, TimeProvider time)
{
    public const int HistoryDays = 365;

    public async Task<WorkforceRecoveryCapacity> RecoveryWithCapacityAsync(ForgePrincipal principal, Forge.Workforce.AssignWork target, int scheduledMinutes,
        CancellationToken ct, DateOnly? shiftDate = null, string? shiftCode = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(target);
        if (scheduledMinutes is < 1 or > 1440)
            throw new Forge.Kernel.Commands.CommandRejectedException("invalid_minutes", "Forecast between 1 and 1440 scheduled minutes.");
        var recovery = await readiness.RecommendWorkforceRecoveryAsync(principal, target, ct, shiftDate, shiftCode);
        var result = recovery.Output;
        var asOf = time.GetUtcNow();
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var snapshot = await readiness.LoadSnapshotAsync(ct);
        var evidence = await LoadAsync(uow, asOf, ct);
        var people = result.Candidates.SelectMany(c => c.Moves.Select(m => m.PersonId)).Append(Guid.Empty).Distinct().Where(id => id != Guid.Empty).ToArray();
        var assessments = await uow.Store<ICompetencyLookup>().GetPracticalAssessmentsAsync([.. people.Select(p => new PersonId(p))], ct);
        var candidates = new List<CandidateCapacity>();
        foreach (var candidate in result.Candidates)
        {
            var person = snapshot.Person(candidate.PersonId);
            var name = person?.Name ?? candidate.PersonName;
            var atTarget = Forecast(snapshot, evidence, assessments, candidate.PersonId, name, result.Target.MachineCode, result.Target.ActivityCode, scheduledMinutes, asOf, true);
            var origin = person?.Assignment;
            var atOrigin = origin is null || origin.AsTrainee
                ? null
                : Forecast(snapshot, evidence, assessments, candidate.PersonId, name, origin.MachineCode, origin.ActivityCode, scheduledMinutes, asOf, false);
            candidates.Add(new CandidateCapacity(candidate.Rank, candidate.PersonId, atTarget, atOrigin, Explain(name, atTarget, atOrigin, candidate.RequiresSourceBackfill)));
        }

        return new WorkforceRecoveryCapacity(recovery, candidates, scheduledMinutes, SkillEvidence.RuleVersion);
    }

    private sealed record Evidence(IReadOnlyList<SkillObservation> Observations, IReadOnlyList<RefresherPolicy> Policies,
        IReadOnlyDictionary<string, OperationStandard> Standards);

    private static async Task<Evidence> LoadAsync(IKernelUnitOfWork uow, DateTimeOffset asOf, CancellationToken ct)
    {
        var execution = uow.Store<IExecutionLookup>();
        var from = asOf.AddDays(-HistoryDays);
        var labor = await execution.ListLaborAsync(from, asOf, null, ct);
        var reports = await execution.ListReportsAsync(from, asOf, null, ct);
        var downtime = await uow.Store<IMaintenanceLookup>().ListDowntimeAsync(from, asOf, ct);
        var operations = new Dictionary<(long, int), (string, string)>();
        var standards = new Dictionary<string, OperationStandard>(StringComparer.Ordinal);
        var headers = await execution.ListOrdersAsync(ct);
        foreach (var number in labor.Select(l => l.OrderNumber).Concat(headers.Where(h => h.Status == OrderStatus.Released).Select(h => h.OrderNumber)).Distinct())
        {
            if (await execution.FindOrderAsync(number, ct) is not { } order) continue;
            foreach (var op in order.Operations)
            {
                operations[(order.OrderNumber, op.Sequence)] = (op.ActivityCode, order.PartNumber);
                // The released, open operation of an activity on a machine is the work a forecast is for.
                if (order.Status == OrderStatus.Released && op.Status != OperationStatus.Complete && order.Quantity > 0)
                {
                    var setup = op.SetupMinutes ?? 0m;
                    var standard = new OperationStandard(order.PartNumber, setup, Math.Max(0m, op.PlannedMinutes - setup) / order.Quantity, order.OrderNumber, op.Sequence);
                    foreach (var machine in op.EligibleMachines.DefaultIfEmpty(string.Empty))
                        standards.TryAdd($"{machine}|{op.ActivityCode}", standard);
                }
            }
        }

        return new Evidence(SkillEvidence.Observations(labor, reports, downtime, operations),
            await uow.Store<IRefresherPolicyLookup>().ListRefresherPoliciesAsync(ct), standards);
    }

    private static SkillCapacityView Forecast(SiteSnapshot snapshot, Evidence evidence, IReadOnlyList<PracticalAssessment> assessments, Guid personId, string name,
        string? machineCode, string activityCode, int minutes, DateTimeOffset asOf, bool target)
    {
        var requirement = snapshot.MachineActivities.FirstOrDefault(a => a.MachineCode == machineCode && a.ActivityCode == activityCode);
        var policies = (requirement?.OperatorQualifications ?? []).Select(q => evidence.Policies.FirstOrDefault(p => p.Code == q)).OfType<RefresherPolicy>().ToList();
        var refresherDays = policies.Select(p => p.RefresherAfterDays).OfType<int>().DefaultIfEmpty().Min() is var d && d > 0 ? d : (int?)null;
        var reacclimation = policies.Select(p => p.ReacclimationMinutes).OfType<int>().DefaultIfEmpty().Max() is var m && m > 0 ? m : (int?)null;
        evidence.Standards.TryGetValue($"{machineCode ?? string.Empty}|{activityCode}", out var standard);
        var lastOperated = evidence.Observations.Where(o => o.PersonId == personId && o.ActivityCode == activityCode && (machineCode is null || o.MachineCode == machineCode) && o.GoodQuantity > 0)
            .Select(o => (DateTimeOffset?)o.LastWorkedAt).Max();
        string? refresher = null;
        var refresherSatisfied = true;
        if (refresherDays is { } window && (lastOperated is null || (asOf - lastOperated.Value).TotalDays > window))
        {
            var refreshed = assessments.Any(a => a.PersonId == personId && a.Passed && a.ActivityCode == activityCode
                && (machineCode is null || a.MachineCode == machineCode) && (asOf - a.AssessedAt).TotalDays <= window);
            refresher = refreshed
                ? $"Refresher passed within the last {window} days."
                : $"Required: a passed practical assessment, because there is no verified operation in the last {window} days.";
            refresherSatisfied = refreshed;
        }

        // The recovery engine already proved qualification, authorization, safety and staffing for the target; at the origin the person is already working.
        var (input, last, notes) = SkillEvidence.Estimate(personId, machineCode, activityCode, standard, evidence.Observations, asOf, minutes, 0,
            true, !target || refresherSatisfied, true, true, true, reacclimation, refresherDays);
        var forecast = SkillCapacityForecastCalculator.Calculate(input);
        return new SkillCapacityView(personId, name, machineCode, activityCode, forecast, last, input.ExpectedSetupMinutes, refresher, SkillEvidence.RuleVersion, notes);
    }

    private static string Explain(string name, SkillCapacityView target, SkillCapacityView? origin, bool backfill)
    {
        var f = target.Forecast;
        var where = target.MachineCode ?? target.ActivityCode;
        if (!f.IsEligible) return $"{name}: not eligible for {where}. {target.RequiredRefresher ?? f.EligibilityStatus}";
        var last = target.LastOperatedAt is { } at ? $"last verified operation {f.DaysSinceLastVerifiedProductiveExecution} days ago" : "no verified operation on record";
        var units = f.ExpectedGoodUnits is null ? "no capacity estimate" : $"estimated {f.ConservativeGoodUnits:0}–{f.OptimisticGoodUnits:0} good units (expected {f.ExpectedGoodUnits:0})";
        var setup = target.ExpectedSetupMinutes is { } s ? $" after about {s:0} minutes of setup" : string.Empty;
        var loss = origin?.Forecast.ExpectedGoodUnits is { } lost
            ? backfill ? $" The original work loses about {lost:0} units unless the planned backfill covers it." : $" The original work loses about {lost:0} units unless backfilled."
            : string.Empty;
        return $"{name}: qualified for {where}; {last}; {units}{setup} ({f.Confidence?.ToString() ?? "no"} confidence).{loss}";
    }
}
