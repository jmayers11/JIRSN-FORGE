namespace Forge.Planning.Tests;

public sealed class OperationMaterialFeasibilityCalculationsTests
{
    [Fact]
    public void Operation_buildable_quantity_uses_only_material_consumed_at_that_operation_and_nets_all_hard_limits()
    {
        var input = Input(materials: [new OperationComponentAvailability("MOTOR", 60m, 1m, 0m, 1m)],
            limits: Limits(operation: 100m, labor: 90m, machine: 80m, storage: 40m, downstream: 60m));

        var result = PartialBuildFeasibilityCalculator.Calculate(input);

        Assert.Equal(60m, result.MaterialFeasibleUnits);
        Assert.Equal(40m, result.CapacityFeasibleUnits);
        Assert.Equal(40m, result.RecommendedQuantity);
        Assert.Contains("Safe WIP storage", result.LimitingConstraints);
        Assert.True(result.IsRecommendationFeasible);
    }

    [Fact]
    public void Component_setup_consumption_yield_pack_multiple_and_minimum_batch_are_applied_explicitly()
    {
        var input = Input(materials: [new OperationComponentAvailability("BRACKET", 30m, 2m, 2m, 0.9m)],
            limits: Limits(100m, 100m, 100m, 100m), minimumBatch: 15m, packMultiple: 5m);

        var result = PartialBuildFeasibilityCalculator.Calculate(input);

        Assert.Equal(12m, Assert.Single(result.ComponentLimits).BuildableGoodUnits);
        Assert.Equal(0m, result.RecommendedQuantity);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("minimum batch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Partial_policy_and_unknown_hard_capacity_evidence_block_recommendation()
    {
        var partialForbidden = PartialBuildFeasibilityCalculator.Calculate(Input(
            materials: [new OperationComponentAvailability("FRAME", 60m, 1m, 0m, 1m)],
            limits: Limits(100m, 100m, 100m, 100m), allowPartial: false));
        var unknownCapacity = PartialBuildFeasibilityCalculator.Calculate(Input(
            materials: [new OperationComponentAvailability("FRAME", 100m, 1m, 0m, 1m)],
            limits: [new(PartialBuildCapacityKind.Operation, "Operation capacity", 100m),
                     new(PartialBuildCapacityKind.QualifiedLabor, "Qualified labor", 100m),
                     new(PartialBuildCapacityKind.Machine, "Machine capacity", null, false),
                     new(PartialBuildCapacityKind.SafeStorage, "Safe WIP storage", 100m)]));

        Assert.Equal(0m, partialForbidden.RecommendedQuantity);
        Assert.Contains(partialForbidden.Blockers, blocker => blocker.Contains("prohibits", StringComparison.Ordinal));
        Assert.Equal(0m, unknownCapacity.RecommendedQuantity);
        Assert.Contains(unknownCapacity.Blockers, blocker => blocker.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Wip_storage_uses_approved_slot_capacity_and_operational_utilization_without_double_subtracting_quarantine()
    {
        var slots = new[]
        {
            new WipStorageSlotCapacity("RACK-A", "FRAME-BOX", true, true, true, 80m, 45m, 10m, 0m, false, 0.90m),
            new WipStorageSlotCapacity("RACK-B", "FRAME-BOX", true, true, true, 100m, 40m, 0m, 20m, true, null),
        };

        var result = WipStorageCapacityCalculator.Calculate("FRAME-BOX", slots, requestedQuantity: 40m);

        Assert.Equal(180m, result.ApprovedCapacityUnits);
        Assert.Equal(85m, result.PhysicallyFreeUnits);
        Assert.Equal(77m, result.OperationalFreeUnits);
        Assert.Equal(40m, result.AllocatableQuantity);
        Assert.Equal(2, result.ProposedSlotAllocations.Count);
        Assert.Equal(17m, result.ProposedSlotAllocations.Single(slot => slot.SlotCode == "RACK-A").Quantity);
        Assert.Equal(23m, result.ProposedSlotAllocations.Single(slot => slot.SlotCode == "RACK-B").Quantity);
    }

    [Fact]
    public void Unknown_slot_geometry_is_unavailable_and_reported_for_review()
    {
        var result = WipStorageCapacityCalculator.Calculate("FRAME-BOX",
        [new WipStorageSlotCapacity("FLOOR-1", "FRAME-BOX", true, true, false, 100m, 0m, 0m, 0m, false, null)],
            requestedQuantity: 20m);

        Assert.Equal(0m, result.AllocatableQuantity);
        Assert.Equal(1, result.ExcludedUnknownSlotCount);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("authorized review", StringComparison.Ordinal));
    }

    [Fact]
    public void Material_usable_and_restart_dates_apply_site_work_calendar_after_confirmed_physical_arrival()
    {
        var friday = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
        var calendar = new WeekdayCalendar();
        var forecast = MaterialUsableDateCalculator.Calculate(new MaterialUsableDateInput("MOTOR", 40m, 40m, 40m, 40m,
            friday, friday, friday, friday.AddDays(3), TimeSpan.FromHours(8), TimeSpan.FromHours(8), TimeSpan.Zero,
            "Supplier-confirmed with transit range", "PO-88 line 2"), calendar);

        Assert.Equal(friday, forecast.SupplierPromisedArrival);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 17, 0, 0, TimeSpan.Zero), forecast.ExpectedUsableAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 17, 0, 0, TimeSpan.Zero), forecast.EarliestPlausibleUsableAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 17, 0, 0, TimeSpan.Zero), forecast.RiskAdjustedUsableAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 17, 0, 0, TimeSpan.Zero),
            MaterialUsableDateCalculator.EarliestOperationRestart([forecast], MaterialDateScenario.Expected,
                friday, TimeSpan.FromHours(8), TimeSpan.Zero, calendar));
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 17, 0, 0, TimeSpan.Zero),
            MaterialUsableDateCalculator.EarliestOperationRestart([forecast], MaterialDateScenario.RiskAdjusted,
                friday, TimeSpan.FromHours(8), TimeSpan.Zero, calendar));
    }

    [Fact]
    public void Promise_without_reliable_physical_arrival_or_sufficient_quantity_never_becomes_a_ready_date()
    {
        var promiseOnly = MaterialUsableDateCalculator.Calculate(new MaterialUsableDateInput("CONNECTOR", 10m, null, null, null,
            new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero), null, null, null,
            TimeSpan.FromHours(8), TimeSpan.Zero, TimeSpan.Zero, "Low", "PO-91"), new WeekdayCalendar());
        var shortReceipt = MaterialUsableDateCalculator.Calculate(new MaterialUsableDateInput("CONNECTOR", 10m, 5m, 5m, 5m,
            new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero),
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, "Medium", "PO-92"), new WeekdayCalendar());

        Assert.Null(promiseOnly.ExpectedUsableAt);
        Assert.Null(shortReceipt.ExpectedUsableAt);
        Assert.Null(MaterialUsableDateCalculator.EarliestOperationRestart([shortReceipt], MaterialDateScenario.Expected,
            new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero), TimeSpan.Zero, TimeSpan.Zero, new WeekdayCalendar()));
    }

    private static PartialBuildInput Input(IReadOnlyCollection<OperationComponentAvailability> materials,
        IReadOnlyCollection<PartialBuildLimit> limits, decimal minimumBatch = 0m, decimal packMultiple = 1m,
        bool allowPartial = true) => new(100m, 100m, materials, limits, minimumBatch, packMultiple, true, true, true, allowPartial);

    private static IReadOnlyCollection<PartialBuildLimit> Limits(decimal operation, decimal labor, decimal machine,
        decimal storage, decimal? downstream = null)
    {
        var limits = new List<PartialBuildLimit>
        {
            new(PartialBuildCapacityKind.Operation, "Operation capacity", operation),
            new(PartialBuildCapacityKind.QualifiedLabor, "Qualified labor", labor),
            new(PartialBuildCapacityKind.Machine, "Machine capacity", machine),
            new(PartialBuildCapacityKind.SafeStorage, "Safe WIP storage", storage),
        };
        if (downstream is { } quantity)
            limits.Add(new PartialBuildLimit(PartialBuildCapacityKind.DownstreamAcceptance, "Downstream acceptance", quantity, false));
        return limits;
    }

    private sealed class WeekdayCalendar : IWorkTimeCalendar
    {
        public DateTimeOffset AddWorkingTime(DateTimeOffset start, TimeSpan workingDuration)
        {
            if (workingDuration < TimeSpan.Zero) throw new ArgumentException("Working duration cannot be negative.", nameof(workingDuration));
            var at = start;
            var remaining = workingDuration;
            while (remaining > TimeSpan.Zero)
            {
                if (at.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    at = AtHour(at, 9).AddDays(at.DayOfWeek == DayOfWeek.Saturday ? 2 : 1);
                else if (at.TimeOfDay < TimeSpan.FromHours(9)) at = AtHour(at, 9);
                else if (at.TimeOfDay >= TimeSpan.FromHours(17))
                {
                    at = AtHour(at.AddDays(1), 9);
                    continue;
                }

                var available = TimeSpan.FromHours(17) - at.TimeOfDay;
                var worked = remaining < available ? remaining : available;
                at = at.Add(worked);
                remaining -= worked;
            }
            return at;
        }

        private static DateTimeOffset AtHour(DateTimeOffset at, int hour) =>
            new(at.Year, at.Month, at.Day, hour, 0, 0, at.Offset);
    }
}
