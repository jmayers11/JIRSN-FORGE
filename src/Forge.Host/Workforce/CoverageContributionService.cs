using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Planning;
using Forge.Quality.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Host.Workforce;

/// <param name="ActualGoodUnits">Good units this person reported on the covered work inside the assignment; null when no output was reported.</param>
/// <param name="ContextFlags">Verified downtime, reported stoppages and quality results during the assignment, for the reviewer.</param>
public sealed record CoverageAssignmentView(
    CoverageAssignment Assignment,
    IReadOnlyList<CoverageGoal> Goals,
    CoverageGoal? GoalInEffect,
    decimal? ActualGoodUnits,
    bool OutputAccepted,
    IReadOnlyList<string> ContextFlags);

public sealed record CoverageContributionReport(Guid PersonId, DateTimeOffset From, DateTimeOffset To, CrossFunctionalCoverageMetrics Metrics,
    IReadOnlyList<CoverageAssignmentView> Assignments, string Basis);

/// <summary>
/// Cross-functional contributions (Sprint 10 §22.2): coverage goals against accepted good output from the production reports, with the forecast
/// beside them and every exclusion counted. Evidence-based reporting only; there is no score and no personnel recommendation.
/// </summary>
public sealed class CoverageContributionService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer)
{
    public const int MinimumDurationMinutes = 30;

    public const string BasisText = "Each assignment is judged against its own goal in effect (a reviewed time-adjusted goal when there is one), never by adding units across different operations. Actual output is the good quantity this person reported on the covered work during the assignment; output on an operation whose latest inspection failed is not accepted. Cancelled, unstarted, unfinished, excluded, zero-goal and short assignments stay out of the percentages and are listed with their reasons. Forecast error is shown separately and is not a measure of the person.";

    public async Task<CoverageContributionReport> BuildAsync(ForgePrincipal principal, Guid personId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.PersonId.Value != personId && !authorizer.Authorize(principal, Permissions.WorkforceRead, ResourceRef.Of("Workforce")).IsPermitted)
            throw new ForgeAuthorizationException(authorizer.Authorize(principal, Permissions.WorkforceRead, ResourceRef.Of("Workforce")));
        if (to <= from || to - from > TimeSpan.FromDays(400))
            throw new CommandRejectedException("invalid_date_range", "Choose a period of up to about a year.");

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var coverage = uow.Store<ICoverageLookup>();
        var assignments = await coverage.ListCoverageAsync(personId, from, to, ct);
        var goals = await coverage.ListCoverageGoalsAsync([.. assignments.Select(a => a.AssignmentId)], ct);
        var execution = uow.Store<IExecutionLookup>();
        var reports = (await execution.ListReportsAsync(from.AddDays(-1), to.AddDays(1), null, ct)).Where(r => r.PersonId == personId).ToList();
        var downtime = await uow.Store<IMaintenanceLookup>().ListDowntimeAsync(from.AddDays(-1), to.AddDays(1), ct);
        var stoppages = await uow.Store<IProductionActualsLookup>().ListStoppagesAsync(from.AddDays(-1), to.AddDays(1), false, ct);
        var quality = authorizer.Authorize(principal, Permissions.QualityRead, ResourceRef.Of("Quality")).IsPermitted || principal.PersonId.Value == personId
            ? uow.Store<IQualityLookup>() : null;

        var views = new List<CoverageAssignmentView>();
        var inputs = new List<CoverageGoalAssignment>();
        foreach (var a in assignments)
        {
            var mine = goals.Where(g => g.AssignmentId == a.AssignmentId).OrderBy(g => g.Version).ToList();
            var inEffect = mine.LastOrDefault(g => g.Kind == CoverageGoalKind.TimeAdjusted) ?? mine.LastOrDefault();
            var flags = new List<string>();
            decimal? actual = null;
            var accepted = true;
            if (a.ActualStart is { } start)
            {
                var end = a.ActualEnd ?? start;
                var covered = reports.Where(r => r.ReportedAt >= start && r.ReportedAt <= end
                    && (a.OrderNumber is { } order ? r.OrderNumber == order && r.Sequence == a.Sequence : r.WorkCenterCode == a.DestinationWorkCenter)).ToList();
                actual = covered.Count > 0 ? covered.Sum(r => r.GoodQuantity) : null;
                if (quality is not null)
                {
                    foreach (var op in covered.Select(r => (r.OrderNumber, r.Sequence)).Distinct())
                    {
                        var last = (await quality.ListInspectionsAsync(op.OrderNumber, 200, ct)).Where(i => i.Sequence == op.Sequence).OrderBy(i => i.At).LastOrDefault();
                        if (last is { Result: InspectionResult.Fail })
                        {
                            accepted = false;
                            flags.Add($"The latest inspection of WO {op.OrderNumber} op {op.Sequence} failed; its output is not accepted.");
                        }
                    }
                }

                if (a.MachineCode is { } machine)
                {
                    var down = downtime.Where(d => d.MachineCode == machine && !d.IsHypothetical && d.StartsAt < end && (d.EndsAt ?? end) > start)
                        .Sum(d => (decimal)(Min(d.EndsAt ?? end, end) - Max(d.StartsAt, start)).TotalMinutes);
                    if (down > 0) flags.Add($"{down:0} minutes of verified Maintenance downtime on {machine} during the assignment.");
                    foreach (var stop in stoppages.Where(s => s.MachineCode == machine && s.StartedAt < end && (s.EndedAt ?? end) > start))
                        flags.Add($"Stoppage reported on {machine}: {stop.Category} ({stop.ReasonCode}).");
                }
            }

            if (mine.Any(g => g.AfterStart && g.Kind == CoverageGoalKind.Revision))
                flags.Add("The goal was revised after work began; the original and every revision are shown.");
            views.Add(new CoverageAssignmentView(a, mine, inEffect, actual, accepted, flags));
            inputs.Add(new CoverageGoalAssignment(personId, a.AssignmentId.ToString("D"), $"{a.MachineCode ?? a.DestinationWorkCenter}|{a.ActivityCode}",
                a.ActualStart, a.ActualEnd, true, true, true, true, a.State == CoverageState.Cancelled, a.ActualStart is not null, a.State == CoverageState.Completed,
                actual is not null && accepted, a.Comparable, inEffect?.GoodUnits, actual, a.ForecastExpected, a.ExclusionReason));
        }

        var metrics = CrossFunctionalCoverageCalculator.Calculate(personId, inputs, MinimumDurationMinutes);
        return new CoverageContributionReport(personId, from, to, metrics, views, BasisText);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
