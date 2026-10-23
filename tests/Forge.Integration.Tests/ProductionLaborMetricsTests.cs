using Forge.Execution;
using Forge.Execution.Contracts;

namespace Forge.Integration.Tests;

public sealed class ProductionLaborMetricsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Released_routing_setup_and_run_targets_compare_with_person_hours_without_claiming_unverified_efficiency()
    {
        var order = Order(OperationStatus.Complete);
        var labor = new[]
        {
            Entry("00000000-0000-0000-0000-000000000001", LaborRole.Operator, Start, Start.AddHours(2)),
            Entry("00000000-0000-0000-0000-000000000002", LaborRole.Trainee, Start, Start.AddHours(1)),
            Entry("00000000-0000-0000-0000-000000000003", LaborRole.Trainer, Start, Start.AddHours(1)),
        };
        var reports = new[] { new ProductionReport(1, order.OrderNumber, 10, "WC-A", Guid.Empty, 48, 2, "Setup scrap", true, true, Start) };

        var metric = Assert.Single(ProductionLaborMetrics.Calculate(order, labor, reports, Start.AddHours(3)));

        Assert.Equal(0.333m, metric.PlannedSetupLaborHours);
        Assert.Equal(4m, metric.PlannedRunLaborHours);
        Assert.Equal(4.333m, metric.PlannedTotalLaborHours);
        Assert.Equal(2m, metric.RecordedOperatorHours);
        Assert.Equal(2m, metric.RecordedTrainingHours);
        Assert.Equal(48m, metric.ReportedGoodQuantity);
        Assert.Equal(2m, metric.ReportedScrapQuantity);
        Assert.Equal(-2.333m, metric.CompletedScheduleVarianceHours);
        Assert.Null(metric.EarnedStandardHours);
        Assert.Null(metric.LaborEfficiencyPercent);
        Assert.Contains("not assumed accepted", metric.EarnedStandardStatus);
    }

    [Fact]
    public void Incomplete_operation_has_no_full_batch_schedule_variance_and_elapsed_open_labor_is_live()
    {
        var order = Order(OperationStatus.InProgress);
        var labor = new[] { Entry("00000000-0000-0000-0000-000000000001", LaborRole.Operator, Start, null) };

        var metric = Assert.Single(ProductionLaborMetrics.Calculate(order, labor, [], Start.AddHours(1.5)));

        Assert.Equal(1.5m, metric.RecordedOperatorHours);
        Assert.Null(metric.CompletedScheduleVarianceHours);
        Assert.Equal(0m, metric.ReportedGoodQuantity);
        Assert.Equal(0m, metric.ReportedScrapQuantity);
    }

    private static OrderInfo Order(OperationStatus status) => new(
        1001, "PART-A", "A", 10, new DateOnly(2026, 10, 30), 1, OrderStatus.Released, null, null, Start,
        [new OrderOperationInfo(10, "Assemble", "WC-A", "ASSEMBLE", [], [], 130, 2, status, SetupMinutes: 10)], []);

    private static LaborEntry Entry(string person, LaborRole role, DateTimeOffset started, DateTimeOffset? ended) => new(
        1, Guid.Parse(person), 1001, 10, "WC-A", "M-1", role, started, ended, null);
}
