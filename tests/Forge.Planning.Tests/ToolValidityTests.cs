using Forge.Resources.Contracts;

namespace Forge.Planning.Tests;

/// <summary>V3.8: a tool's validity is judged from its history as of a moment.</summary>
public class ToolValidityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private static readonly ToolTypeInfo Knife = new("KNIFE", "Knife", ToolKind.Cutting, null, 10, 3);
    private static readonly ToolTypeInfo Caliper = new("CAL", "Caliper", ToolKind.Gauge, 180, null, null);

    private static ToolInfo Tool(string code, string type, params string[] machines) =>
        new(code, type, "x", null, null, ToolCustody.InHouse, null, ToolCondition.Good, machines, [], 0, 0, null, null, ToolStatus.Available, T0, "person:1");

    private static ToolEvent E(long id, ToolEventKind kind, int hours, int? uses = null, DateOnly? due = null, ToolCondition? condition = null) =>
        new(id, "T", kind, T0.AddHours(hours), "person:1", Condition: condition, DueOn: due, Uses: uses);

    [Fact]
    public void Service_cycles_and_life_limits_are_counted_from_the_history()
    {
        var history = new[] { E(1, ToolEventKind.Registered, 0), E(2, ToolEventKind.Used, 1, 3), E(3, ToolEventKind.SentForService, 2), E(4, ToolEventKind.Returned, 3, condition: ToolCondition.Good), E(5, ToolEventKind.Used, 4, 7) };
        var tool = Tool("T", "KNIFE", "SLITTER-A");
        Assert.True(ToolValidity.At(tool, Knife, history, T0.AddMinutes(30), "SLITTER-A").Valid);
        Assert.Contains(ToolValidity.At(tool, Knife, history, T0.AddHours(1.5)).Problems, p => p.Contains("due for service", StringComparison.Ordinal));
        Assert.Contains(ToolValidity.At(tool, Knife, history, T0.AddHours(2.5)).Problems, p => p.Contains("out for service", StringComparison.Ordinal));
        Assert.True(ToolValidity.At(tool, Knife, history, T0.AddHours(3.5)).Valid);
        var spent = ToolValidity.At(tool, Knife, history, T0.AddHours(5));
        Assert.Equal((0, false), (spent.UsesLeft!.Value, spent.Valid));
        Assert.Contains(ToolValidity.At(tool, Knife, history, T0.AddMinutes(30), "SLITTER-B").Problems, p => p.Contains("not for SLITTER-B", StringComparison.Ordinal));
        Assert.False(ToolValidity.At(tool, Knife, history, T0.AddMinutes(-1)).Valid); // not registered yet
    }

    [Fact]
    public void A_gauge_is_valid_only_while_a_passing_calibration_is_in_force()
    {
        var day = DateOnly.FromDateTime(T0.UtcDateTime);
        var history = new[]
        {
            E(1, ToolEventKind.Registered, 0, due: day.AddDays(10)),
            E(2, ToolEventKind.CalibrationFailed, 24 * 5),
            E(3, ToolEventKind.Calibrated, 24 * 6, due: day.AddDays(200)),
            E(4, ToolEventKind.ConditionChanged, 24 * 300, condition: ToolCondition.Damaged),
        };
        var tool = Tool("T", "CAL");
        Assert.True(ToolValidity.At(tool, Caliper, history, T0.AddDays(2)).Valid);
        Assert.False(ToolValidity.At(tool, Caliper, history, T0.AddDays(5.5)).Valid);
        Assert.Equal(day.AddDays(200), ToolValidity.At(tool, Caliper, history, T0.AddDays(7)).CalibrationDue);
        Assert.Contains(ToolValidity.At(tool, Caliper, history, T0.AddDays(250)).Problems, p => p.Contains("was due for calibration", StringComparison.Ordinal));
        Assert.Contains(ToolValidity.At(tool, Caliper, history, T0.AddDays(301)).Problems, p => p.Contains("damaged", StringComparison.Ordinal));
    }
}
