using Forge.Competency.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Workforce;
using Forge.Workforce.Contracts;

namespace Forge.Host.Workforce;

/// <summary>Controllable labor efficiency for the period, from <see cref="CauseAdjustedPerformanceService"/>.</summary>
public sealed record CardEfficiency(decimal? ControllablePercent, decimal? UnadjustedPercent, decimal DirectLaborHours, decimal EligibleLaborHours,
    decimal VerifiedExternalDelayHours, int OperationsInScope, int OperationsOutsideScope, string Status);

/// <summary>Official process FPY keeps every unit; the employee figure uses Quality-verified attributions only and states its coverage.</summary>
public sealed record CardFirstPass(decimal? OfficialProcessPercent, decimal? EmployeeAttributablePercent, decimal InspectedQuantity, decimal CoveragePercent,
    decimal MinimumCoveragePercent, decimal ExternallyCausedQuantity, decimal UnassignedQuantity, string Status);

public sealed record CardAttendance(decimal? OnTimePercent, int Scheduled, int Eligible, int OnTime, int VerifiedLate, int VerifiedCallOuts, int VerifiedNoShows,
    int Excluded, int PendingReview, int MissingPolicy, string Explanation);

public sealed record CardCoverage(decimal? GoalSuccessPercent, decimal? AverageAttainmentPercent, int Completed, int GoalEligible, int GoalSuccesses,
    decimal VerifiedHours, int DistinctOperations, int Excluded, string Status);

public sealed record CardQualification(string Code, string Name, string Status, DateTimeOffset? CertifiedAt, DateTimeOffset? ExpiresAt);

public sealed record CardMachineAuthorization(string MachineCode, string ActivityCode, string QualificationCode, string Status, DateTimeOffset? ExpiresAt,
    bool Valid, string? Restrictions);

public sealed record CardTraining(string CourseCode, string CourseVersion, DateTimeOffset CompletedAt, DateTimeOffset? ExpiresAt, string? TrainerName);

public sealed record CardCompetency(int Current, int ExpiringSoon, int Expired, int InTraining, int MachineAuthorizationsValid, int MachineAuthorizationsInvalid,
    int CoursesCompletedInPeriod, int PracticalsPassedInPeriod, int PracticalsFailedInPeriod, int PeopleTrainedInPeriod,
    IReadOnlyList<CardQualification> Qualifications, IReadOnlyList<CardMachineAuthorization> MachineAuthorizations, IReadOnlyList<CardTraining> CoursesInPeriod);

/// <summary>One employee's review card. A null section means the viewer may not read that evidence, which the restriction list names.</summary>
public sealed record EmployeePerformanceCard(Guid PersonId, string DisplayName, string? JobTitle, string? Department, string HomeWorkCenterCode, string? ShiftCode,
    bool IsActive, CardEfficiency? Efficiency, CardFirstPass? FirstPass, CardAttendance? Attendance, CardCoverage? Coverage, CardCompetency? Competency,
    decimal PaidHours, decimal UnaccountedHours, int ShiftsRecorded, IReadOnlyList<string> Contributions);

public sealed record EmployeePerformanceCards(DateTimeOffset From, DateTimeOffset To, DateTimeOffset AsOf, int ExpiringWithinDays,
    IReadOnlyList<EmployeePerformanceCard> Cards, IReadOnlyList<string> Restrictions, IReadOnlyList<string> Basis);

/// <summary>
/// Quarterly and annual review cards (Sprint 10 review summary): every figure is one Forge already computes from source records — cause-adjusted
/// labor and first-pass yield, verified attendance, coverage goals and the competency register — gathered per employee for a review period.
/// The cards report evidence; they make no ranking, rating or personnel decision. Colors are drawn by the page from published thresholds.
/// </summary>
public sealed class EmployeePerformanceCardService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time,
    CauseAdjustedPerformanceService performance, CoverageContributionService coverage)
{
    public const int ExpiringWithinDays = 30;

    public async Task<EmployeePerformanceCards> BuildAsync(ForgePrincipal principal, DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<Guid>? personIds,
        bool includeInactive, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Demand(principal, Permissions.WorkforceRead, "Workforce");
        if (to <= from || to - from > TimeSpan.FromDays(370))
            throw new CommandRejectedException("invalid_date_range", "Choose a review period of up to a year.");
        var now = time.GetUtcNow();
        var restrictions = new List<string>();
        var canAttendance = Can(principal, Permissions.AttendanceIntelligenceRead, "Workforce");
        var canCompetency = Can(principal, Permissions.CompetencyRead, "Competency");
        var canQuality = Can(principal, Permissions.QualityRead, "Quality");
        if (!canAttendance) restrictions.Add("Attendance is hidden: reading attendance rates needs the attendance-intelligence permission (Human Resources).");
        if (!canCompetency) restrictions.Add("Qualifications and training are hidden: they need competency read access.");
        if (!canQuality) restrictions.Add("First-pass yield is hidden: it needs quality read access.");

        List<WorkerState> workers;
        Dictionary<Guid, AttendanceReliabilitySummary> attendance = [];
        List<PersonQualification> qualifications = [];
        List<MachineAuthorization> authorizations = [];
        List<TrainingCourseCompletion> courses = [];
        List<PracticalAssessment> practicals = [];
        Dictionary<string, string> typeNames = [];
        await using (var uow = await unitOfWork.BeginSnapshotAsync(ct))
        {
            workers = [.. (await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(now, ct))
                .Where(w => (includeInactive || w.IsActive) && (personIds is null || personIds.Count == 0 || personIds.Contains(w.PersonId)))
                .OrderBy(w => w.DisplayName, StringComparer.OrdinalIgnoreCase)];
            var ids = workers.Select(w => new PersonId(w.PersonId)).ToList();
            if (canAttendance)
            {
                var store = uow.Store<IAttendanceReliabilityStore>();
                var opportunities = await store.ListAttendanceOpportunitiesAsync(from, to, ct);
                var events = await store.ListAttendanceIntelligenceEventsAsync(from, to, ct);
                var policies = await store.ListAttendancePoliciesAsync(ct);
                foreach (var worker in workers)
                    attendance[worker.PersonId] = AttendanceReliabilityCalculator.Calculate(
                        [.. opportunities.Where(o => o.PersonId == worker.PersonId)], [.. events.Where(e => e.PersonId == worker.PersonId)], policies, from, to, now);
            }

            if (canCompetency && ids.Count > 0)
            {
                var competency = uow.Store<ICompetencyLookup>();
                qualifications = [.. await competency.GetQualificationsAsync(ids, ct)];
                authorizations = [.. await competency.GetMachineAuthorizationsAsync(ids, ct)];
                courses = [.. await competency.GetTrainingCourseCompletionsAsync(ids, ct)];
                practicals = [.. await competency.GetPracticalAssessmentsAsync(ids, ct)];
                typeNames = (await competency.ListTypesAsync(ct)).DistinctBy(t => t.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(t => t.Code, t => t.Name, StringComparer.OrdinalIgnoreCase);
            }
        }

        var reports = await performance.BuildManyAsync(principal, [.. workers.Select(w => w.PersonId)], from, to, 80m, ct);
        var names = workers.ToDictionary(w => w.PersonId, w => w.DisplayName);
        var cards = new List<EmployeePerformanceCard>();
        for (var index = 0; index < workers.Count; index++)
        {
            var worker = workers[index];
            var report = reports[index];
            var e = report.Efficiency;
            var efficiency = new CardEfficiency(e.ControllableEfficiencyPercent, e.UnadjustedEfficiencyPercent, e.TotalDirectLaborHours, e.EligibleDirectLaborHours,
                e.VerifiedExternalDelayHours, report.EfficiencyScope.Count, report.OperationsOutsideEfficiencyScope, e.Status);
            CardFirstPass? firstPass = report.FirstPass is { } f
                ? new CardFirstPass(f.OfficialProcessFpyPercent, f.EmployeeAttributableFpyPercent, f.InspectedQuantity, f.EmployeeAttributionCoveragePercent,
                    f.MinimumRequiredAttributionCoveragePercent, f.ExternallyCausedQuantity, f.UnassignedQuantity, f.EmployeeMetricStatus)
                : null;

            var cov = (await coverage.BuildAsync(principal, worker.PersonId, from, to, ct)).Metrics;
            var coverageCard = new CardCoverage(cov.CoverageGoalSuccessRatePercent, cov.AverageCoverageGoalAttainmentPercent, cov.CompletedAssignmentCount,
                cov.GoalEligibleAssignmentCount, cov.GoalSuccessCount, cov.VerifiedCoverageHours, cov.DistinctQualifiedOperationsCovered, cov.ExcludedGoalAssignmentCount, cov.Status);

            CardAttendance? attendanceCard = attendance.TryGetValue(worker.PersonId, out var a)
                ? new CardAttendance(a.OnTimeArrivalPercent, a.ScheduledOpportunities, a.EligibleArrivalOpportunities, a.OnTimeArrivals, a.VerifiedLateArrivals,
                    a.VerifiedCallOuts, a.VerifiedNoShows, a.ExcludedOpportunities, a.PendingReviewOpportunities, a.MissingPolicyOpportunities, a.Explanation)
                : null;

            var competencyCard = canCompetency ? Competency(worker.PersonId, from, to, now, qualifications, authorizations, courses, practicals, typeNames, names) : null;
            cards.Add(new EmployeePerformanceCard(worker.PersonId, worker.DisplayName, worker.JobTitle, worker.Department, worker.HomeWorkCenterCode, worker.ShiftCode,
                worker.IsActive, efficiency, canQuality ? firstPass : null, attendanceCard, coverageCard, competencyCard,
                decimal.Round(report.PaidMinutes / 60m, 2), decimal.Round(report.GapMinutes / 60m, 2), report.Shifts.Count,
                Contributions(report, cov, competencyCard)));
        }

        return new EmployeePerformanceCards(from, to, now, ExpiringWithinDays, cards, restrictions,
            [CauseAdjustedPerformanceService.BasisText, CoverageContributionService.BasisText,
             "Attendance counts only verified events against scheduled arrivals; approved PTO, protected leave and excused absences are excluded, and a missing punch never becomes a no-show.",
             "Qualifications count when certified and unexpired at the time of the report; In Training grants nothing."]);
    }

    private static CardCompetency Competency(Guid personId, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, List<PersonQualification> qualifications,
        List<MachineAuthorization> authorizations, List<TrainingCourseCompletion> courses, List<PracticalAssessment> practicals,
        Dictionary<string, string> typeNames, Dictionary<Guid, string> names)
    {
        var soon = now.AddDays(ExpiringWithinDays);
        var mine = qualifications.Where(q => q.PersonId == personId).OrderBy(q => q.Code, StringComparer.OrdinalIgnoreCase).ToList();
        var current = mine.Where(q => q.IsValidAt(now)).ToList();
        var expiring = current.Count(q => q.ExpiresAt is { } x && x <= soon);
        var expired = mine.Count(q => q.Status == QualificationStatus.Qualified && q.ExpiresAt is { } x && x <= now);
        var inTraining = mine.Count(q => q.Status == QualificationStatus.InTraining);
        var auths = authorizations.Where(m => m.PersonId == personId).OrderBy(m => m.MachineCode, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.ActivityCode).ToList();
        var coursesInPeriod = courses.Where(c => c.PersonId == personId && c.CompletedAt >= from && c.CompletedAt < to).OrderBy(c => c.CompletedAt).ToList();
        var practicalsInPeriod = practicals.Where(p => p.PersonId == personId && p.AssessedAt >= from && p.AssessedAt < to).ToList();
        var trained = courses.Where(c => c.TrainerPersonId == personId && c.PersonId != personId && c.CompletedAt >= from && c.CompletedAt < to)
            .Select(c => c.PersonId).Distinct().Count();
        return new CardCompetency(current.Count, expiring, expired, inTraining, auths.Count(m => m.IsValidAt(now)), auths.Count(m => !m.IsValidAt(now)),
            coursesInPeriod.Count, practicalsInPeriod.Count(p => p.Passed), practicalsInPeriod.Count(p => !p.Passed), trained,
            [.. mine.Select(q => new CardQualification(q.Code, typeNames.GetValueOrDefault(q.Code, q.Code),
                q.Status == QualificationStatus.Qualified && q.ExpiresAt is { } x && x <= now ? "Expired" : q.Status.ToString(), q.CertifiedAt, q.ExpiresAt))],
            [.. auths.Select(m => new CardMachineAuthorization(m.MachineCode, m.ActivityCode, m.QualificationCode, m.Status.ToString(), m.ExpiresAt, m.IsValidAt(now), m.Restrictions))],
            [.. coursesInPeriod.Select(c => new CardTraining(c.CourseCode, c.CourseVersion, c.CompletedAt, c.ExpiresAt, names.GetValueOrDefault(c.TrainerPersonId)))]);
    }

    /// <summary>Plain statements of recorded facts for the card's "documented contributions"; nothing is inferred.</summary>
    private static List<string> Contributions(CauseAdjustedPerformanceReport report, Forge.Planning.CrossFunctionalCoverageMetrics coverageMetrics, CardCompetency? competency)
    {
        var lines = new List<string>();
        if (report.EfficiencyScope.Count > 0)
            lines.Add($"Completed {report.EfficiencyScope.Count} operation(s) with accepted output: {report.EfficiencyScope.Sum(s => s.GoodQuantity):0.##} good unit(s), {report.EfficiencyScope.Sum(s => s.EarnedStandardHours):0.#} earned standard hour(s).");
        if (coverageMetrics.CompletedAssignmentCount > 0)
            lines.Add($"Covered {coverageMetrics.CompletedAssignmentCount} cross-functional assignment(s) across {coverageMetrics.DistinctQualifiedOperationsCovered} operation(s), {coverageMetrics.VerifiedCoverageHours:0.#} verified hour(s).");
        if (competency is not null)
        {
            if (competency.PeopleTrainedInPeriod > 0) lines.Add($"Trained {competency.PeopleTrainedInPeriod} co-worker(s) as the recorded trainer.");
            if (competency.CoursesCompletedInPeriod > 0) lines.Add($"Completed {competency.CoursesCompletedInPeriod} training course(s).");
            if (competency.PracticalsPassedInPeriod > 0) lines.Add($"Passed {competency.PracticalsPassedInPeriod} practical assessment(s).");
            if (competency.InTraining > 0) lines.Add($"In training for {competency.InTraining} qualification(s).");
        }

        return lines;
    }

    private bool Can(ForgePrincipal principal, string permission, string resource) => authorizer.Authorize(principal, permission, ResourceRef.Of(resource)).IsPermitted;

    private void Demand(ForgePrincipal principal, string permission, string resource)
    {
        var decision = authorizer.Authorize(principal, permission, ResourceRef.Of(resource));
        if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision);
    }
}
