using Forge.Execution.Contracts;

namespace Forge.Planning.Tests;

/// <summary>
/// Risk Radar rules (owner decision 2026-10-27): High = impact today up to 6 days out, Medium = 7 to 29, Low = 30 or more. The impact
/// time comes from the source record: now for conditions in force, the recorded start for upcoming windows, the approved schedule start
/// of the affected operation, or the order's due date when the schedule does not place it.
/// </summary>
public sealed class RiskRadarTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 15, 0, 0, TimeSpan.Zero);

    private static OperationalEvidenceSignal Signal(string category, OperationalEvidenceState state, string? order = null, string? operation = null,
        DateTimeOffset? observed = null, DateTimeOffset? lifecycle = null)
    {
        var links = new List<OperationalEvidenceLink>();
        if (order is not null) links.Add(new("Order", order, OperationalEvidenceRelation.ExplicitOrderLink, true));
        if (operation is not null) links.Add(new("Operation", operation, OperationalEvidenceRelation.ExplicitOperationLink, true));
        return new OperationalEvidenceSignal($"{category}:{order}:{operation}", category, state, null, null, OperationalCauseState.Unattributed,
            observed, null, lifecycle, null, links);
    }

    private static readonly Dictionary<long, OrderInfo> Orders = new()
    {
        [7] = new OrderInfo(7, "P", "A", 1m, new DateOnly(2026, 12, 15), 3, OrderStatus.Released, null, null, Now, [], []),
    };

    [Theory]
    [InlineData(0, RiskBand.High)]
    [InlineData(6, RiskBand.High)]
    [InlineData(7, RiskBand.Medium)]
    [InlineData(29, RiskBand.Medium)]
    [InlineData(30, RiskBand.Low)]
    [InlineData(90, RiskBand.Low)]
    public void Bands_follow_the_owners_day_limits(int days, RiskBand band) =>
        Assert.Equal(band, RiskRadarService.Band(days, new RiskRadarOptions()));

    [Fact]
    public void Impact_comes_from_the_source_record_not_a_score()
    {
        var starts = new Dictionary<(long, int), DateTimeOffset> { [(7, 20)] = Now.AddDays(9) };
        (DateTimeOffset At, string Basis)? Impact(OperationalEvidenceSignal signal) => OperationalEvidenceService.Impact(signal, Now, Orders, starts, "UTC");

        // A blocker on a scheduled operation bites when the approved schedule starts it.
        var blocked = Impact(Signal("ReadinessBlocker", OperationalEvidenceState.Blocked, operation: "7/20"))!.Value;
        Assert.Equal((Now.AddDays(9), "Approved schedule start of the affected operation"), (blocked.At, blocked.Basis));
        // Not placed by the schedule: the order's due date, stated as the basis.
        var unscheduled = Impact(Signal("ReadinessBlocker", OperationalEvidenceState.Blocked, operation: "7/30"))!.Value;
        Assert.StartsWith("Approved schedule start of the order's first open operation", unscheduled.Basis, StringComparison.Ordinal);
        var dueOnly = OperationalEvidenceService.Impact(Signal("MaterialRisk", OperationalEvidenceState.Shortfall, order: "7"), Now, Orders, new Dictionary<(long, int), DateTimeOffset>(), "UTC")!.Value;
        Assert.Equal((new DateTimeOffset(2026, 12, 15, 0, 0, 0, TimeSpan.Zero), "Order due date (not placed by the approved schedule)"), (dueOnly.At, dueOnly.Basis));

        // In force now, upcoming at its recorded start, and nothing for ended or released records or root-cause assessments.
        Assert.Equal(Now, Impact(Signal("MaterialRisk", OperationalEvidenceState.OverdueSupply, order: "7"))!.Value.At);
        Assert.Equal(Now, Impact(Signal("QualityNcr", OperationalEvidenceState.Open, order: "7"))!.Value.At);
        Assert.Null(Impact(Signal("QualityNcr", OperationalEvidenceState.Open, order: "7", lifecycle: Now.AddHours(-1))));
        Assert.Equal(Now.AddDays(3), Impact(Signal("MachineDowntime", OperationalEvidenceState.Upcoming, observed: Now.AddDays(3)))!.Value.At);
        Assert.Equal(Now, Impact(Signal("MachineDowntime", OperationalEvidenceState.Active, observed: Now.AddDays(-1)))!.Value.At);
        Assert.Null(Impact(Signal("ProductionStoppage", OperationalEvidenceState.Ended, observed: Now.AddDays(-1))));
        Assert.Null(Impact(Signal("RootCauseAssessment", OperationalEvidenceState.Confirmed)));
        Assert.Equal(Now, Impact(Signal("QualityHold", OperationalEvidenceState.Active, order: "7"))!.Value.At);
        Assert.Null(Impact(Signal("QualityHold", OperationalEvidenceState.Active, order: "7", lifecycle: Now)));
    }
}
