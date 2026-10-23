using Forge.Execution.Contracts;

namespace Forge.Execution;

/// <summary>Operation-level comparison built from the released routing snapshot and recorded labor intervals.</summary>
public sealed record OperationLaborMetric(
    long OrderNumber,
    int Sequence,
    string WorkCenterCode,
    decimal? PlannedSetupLaborHours,
    decimal? PlannedRunLaborHours,
    decimal? PlannedTotalLaborHours,
    int CrewSize,
    decimal RecordedOperatorHours,
    decimal RecordedTrainingHours,
    decimal ReportedGoodQuantity,
    decimal ReportedScrapQuantity,
    decimal? EarnedStandardHours,
    decimal? LaborEfficiencyPercent,
    decimal? CompletedScheduleVarianceHours,
    string EarnedStandardStatus,
    string VarianceContext);

/// <summary>
/// Shows approved routing targets beside the person-hours Forge actually recorded. Reported good
/// quantity is not treated as quality-accepted output; the calculator therefore leaves earned hours
/// and efficiency unavailable until an operation-scoped acceptance source is supplied.
/// </summary>
public static class ProductionLaborMetrics
{
    public static IReadOnlyList<OperationLaborMetric> Calculate(OrderInfo order, IReadOnlyCollection<LaborEntry> labor,
        IReadOnlyCollection<ProductionReport> reports, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(labor);
        ArgumentNullException.ThrowIfNull(reports);

        return order.Operations.OrderBy(operation => operation.Sequence).Select(operation =>
        {
            var crew = Math.Max(1, operation.CrewSize);
            var setupMinutes = operation.SetupMinutes;
            var plannedMinutes = operation.PlannedMinutes;
            decimal? setupHours = setupMinutes is { } setup
                ? decimal.Round(setup * crew / 60m, 3, MidpointRounding.AwayFromZero)
                : null;
            decimal? runHours = setupMinutes is { } knownSetup
                ? decimal.Round(Math.Max(0, plannedMinutes - knownSetup) * crew / 60m, 3, MidpointRounding.AwayFromZero)
                : null;
            decimal? totalHours = setupHours is { } sh && runHours is { } rh ? sh + rh : null;
            decimal Hours(LaborEntry entry) => decimal.Round(Math.Max(0, (decimal)((entry.EndedAt ?? asOf) - entry.StartedAt).TotalHours), 3, MidpointRounding.AwayFromZero);
            var entries = labor.Where(entry => entry.OrderNumber == order.OrderNumber && entry.Sequence == operation.Sequence
                && (entry.EndedAt is null || entry.EndedAt.Value >= entry.StartedAt)).ToArray();
            var operatorHours = entries.Where(entry => entry.Role == LaborRole.Operator).Sum(Hours);
            var trainingHours = entries.Where(entry => entry.Role is LaborRole.Trainee or LaborRole.Trainer).Sum(Hours);
            var opReports = reports.Where(report => report.OrderNumber == order.OrderNumber && report.Sequence == operation.Sequence).ToArray();
            var good = opReports.Sum(report => report.GoodQuantity);
            var scrap = opReports.Sum(report => report.ScrapQuantity);
            decimal? variance = operation.Status == OperationStatus.Complete && totalHours is { } planned
                ? decimal.Round(operatorHours - planned, 3, MidpointRounding.AwayFromZero)
                : null;

            return new OperationLaborMetric(order.OrderNumber, operation.Sequence, operation.WorkCenterCode,
                setupHours, runHours, totalHours, crew,
                decimal.Round(operatorHours, 3, MidpointRounding.AwayFromZero), decimal.Round(trainingHours, 3, MidpointRounding.AwayFromZero),
                good, scrap, null, null, variance,
                "N/A: operation-scoped quality-accepted quantity is not available; reported good quantity is not assumed accepted.",
                variance is null
                    ? "Variance is shown after completion; recorded waits are not yet classified by labor phase."
                    : "Recorded operator interval minus released routing target; reported downtime/material waits are not yet classified by labor phase.");
        }).ToArray();
    }
}
