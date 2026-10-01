using Forge.Execution.Contracts;
using Forge.Kernel.Engines;

namespace Forge.Planning.Engines;

/// <summary>The plan that was in force (an approved schedule) and what the shop floor has since recorded.</summary>
public sealed record AttainmentSnapshot(long? ScheduleVersionId, DateTimeOffset? ApprovedAt, ScheduleResult? Plan, IReadOnlyList<OrderInfo> Orders);

public sealed record AttainmentRequest(DateTimeOffset From, DateTimeOffset To);

public enum AttainmentLineStatus
{
    /// <summary>Complete by the end of the window.</summary>
    Completed,

    /// <summary>Completed, but after the window ended.</summary>
    CompletedLate,

    /// <summary>Not complete.</summary>
    Open,

    /// <summary>The order was cancelled; not counted against the shop floor.</summary>
    Cancelled,
}

public sealed record AttainmentLine(long OrderNumber, int Sequence, string WorkCenterCode, DateTimeOffset PlannedEnd, decimal PlannedMinutes, DateTimeOffset? CompletedAt, AttainmentLineStatus Status, Guid? ProgramId);

public sealed record WorkCenterAttainment(string WorkCenterCode, int Planned, int Completed, decimal PlannedMinutes, decimal CompletedMinutes, decimal? Percent);

/// <param name="Percent">Planned minutes completed ÷ planned minutes due in the window. Null when nothing was due.</param>
public sealed record AttainmentResult(
    DateTimeOffset From,
    DateTimeOffset To,
    long? ScheduleVersionId,
    DateTimeOffset? ScheduleApprovedAt,
    int PlannedOperations,
    int CompletedOperations,
    decimal PlannedMinutes,
    decimal CompletedMinutes,
    decimal? Percent,
    IReadOnlyList<WorkCenterAttainment> WorkCenters,
    IReadOnlyList<AttainmentLine> Lines,
    string Explanation);

/// <summary>
/// Schedule attainment: of the operations the approved schedule in force at the window's start
/// planned to finish inside the window, how much (weighted by planned minutes) the shop floor
/// reported complete by the window's end. Cancelled orders are listed but not counted.
/// Everything is traceable to a schedule version and to operation completion records.
/// </summary>
public sealed class AttainmentEngine : IEngine<AttainmentSnapshot, AttainmentRequest, AttainmentResult>
{
    public string Name => "schedule-attainment";

    public string Version => "1";

    public EngineEvaluation<AttainmentResult> Evaluate(AttainmentSnapshot snapshot, AttainmentRequest parameters, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(parameters);
        var (from, to) = (parameters.From, parameters.To);
        if (snapshot.Plan is null)
        {
            var none = new AttainmentResult(from, to, null, null, 0, 0, 0, 0, null, [], [], "No schedule was approved before this period, so there is no plan to measure against.");
            return new(none, new EvidenceNode("Attainment", EvidenceOutcome.Indeterminate, none.Explanation));
        }

        var orders = snapshot.Orders.ToDictionary(o => o.OrderNumber);
        var lines = snapshot.Plan.Operations
            .Where(o => o.Scheduled && o.End is { } end && end >= from && end <= to)
            .OrderBy(o => o.End).ThenBy(o => o.OrderNumber).ThenBy(o => o.Sequence)
            .Select(o =>
            {
                orders.TryGetValue(o.OrderNumber, out var order);
                var actual = order?.Operations.FirstOrDefault(a => a.Sequence == o.Sequence);
                var status = order?.Status == OrderStatus.Cancelled ? AttainmentLineStatus.Cancelled
                    : actual is { Status: OperationStatus.Complete, CompletedAt: { } done } ? (done <= to ? AttainmentLineStatus.Completed : AttainmentLineStatus.CompletedLate)
                    : AttainmentLineStatus.Open;
                return new AttainmentLine(o.OrderNumber, o.Sequence, o.WorkCenterCode, o.End!.Value, o.PlannedMinutes, actual?.CompletedAt, status, o.ProgramId);
            })
            .ToList();

        var counted = lines.Where(l => l.Status != AttainmentLineStatus.Cancelled).ToList();
        var done = counted.Where(l => l.Status == AttainmentLineStatus.Completed).ToList();
        decimal? Percent(decimal planned, decimal completed) => planned > 0 ? decimal.Round(100m * completed / planned, 1) : null;
        var centers = counted.GroupBy(l => l.WorkCenterCode).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var planned = g.Sum(l => l.PlannedMinutes);
                var completed = g.Where(l => l.Status == AttainmentLineStatus.Completed).Sum(l => l.PlannedMinutes);
                return new WorkCenterAttainment(g.Key, g.Count(), g.Count(l => l.Status == AttainmentLineStatus.Completed), planned, completed, Percent(planned, completed));
            })
            .ToList();
        var plannedMinutes = counted.Sum(l => l.PlannedMinutes);
        var completedMinutes = done.Sum(l => l.PlannedMinutes);
        var percent = Percent(plannedMinutes, completedMinutes);
        var explanation = counted.Count == 0
            ? $"Schedule {snapshot.ScheduleVersionId} planned no operations to finish in this period."
            : $"{done.Count} of {counted.Count} operation(s) schedule {snapshot.ScheduleVersionId} planned to finish in this period were complete by its end: {percent:0.#}% of {plannedMinutes:0.#} planned minutes.";
        var result = new AttainmentResult(from, to, snapshot.ScheduleVersionId, snapshot.ApprovedAt, counted.Count, done.Count, plannedMinutes, completedMinutes, percent, centers, lines, explanation);
        var evidence = new EvidenceNode("Attainment", counted.Count == 0 ? EvidenceOutcome.Indeterminate : EvidenceOutcome.Info, explanation)
        {
            Children = lines.Select(l => new EvidenceNode(
                $"{l.OrderNumber}/{l.Sequence}",
                l.Status == AttainmentLineStatus.Completed ? EvidenceOutcome.Pass : l.Status == AttainmentLineStatus.Cancelled ? EvidenceOutcome.Info : EvidenceOutcome.Fail,
                $"Planned to finish {l.PlannedEnd:u}; {l.Status}{(l.CompletedAt is { } c ? $" at {c:u}" : string.Empty)}.")).ToList(),
        };
        return new(result, evidence);
    }
}
