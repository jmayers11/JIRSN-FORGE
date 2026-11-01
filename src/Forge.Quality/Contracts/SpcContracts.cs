namespace Forge.Quality.Contracts;

// Statistical process control (owner decisions 2026-11-01): an individuals and moving-range chart of every measured characteristic,
// the standard Western Electric rules, capability against the drawing limits, and a notice to quality engineers when a new measurement
// signals. A signal never holds work or material by itself; a person decides.

/// <param name="MinimumCpk">The capability every characteristic should reach (1.33 by default).</param>
/// <param name="CriticalMinimumCpk">The capability a characteristic marked critical should reach (1.67 by default).</param>
/// <param name="MinimumPoints">Measurements needed before control limits and capability are calculated.</param>
public sealed record SpcPolicy(decimal MinimumCpk, decimal CriticalMinimumCpk, int MinimumPoints, bool NotifyOnSignal, DateTimeOffset EffectiveFrom, string SetBy, string Reason)
{
    public static SpcPolicy Default { get; } = new(1.33m, 1.67m, 20, true, DateTimeOffset.MinValue, "system:default", "Default policy");
}

/// <summary>A characteristic of a part's inspection point, as charted.</summary>
public sealed record SpcCharacteristicKey(string PartNumber, int Sequence, int Number);

/// <summary>One measurement on the chart, in time order.</summary>
public sealed record SpcPoint(long InspectionId, long OrderNumber, DateTimeOffset At, decimal Value);

/// <summary>Which Western Electric rule a point broke.</summary>
public enum SpcRule
{
    /// <summary>One point beyond three sigma.</summary>
    BeyondThreeSigma,

    /// <summary>Two of three points beyond two sigma, same side.</summary>
    TwoOfThreeBeyondTwoSigma,

    /// <summary>Four of five points beyond one sigma, same side.</summary>
    FourOfFiveBeyondOneSigma,

    /// <summary>Eight points in a row on one side of the centre line.</summary>
    EightOnOneSide,
}

/// <summary>A recorded measurement of a variable characteristic, with the limits it was judged against.</summary>
public sealed record SpcMeasurement(SpcCharacteristicKey Key, string Name, decimal? Lower, decimal? Upper, SpcPoint Point);

public sealed record SpcViolation(int PointIndex, long InspectionId, SpcRule Rule, string Description);

/// <summary>
/// An individuals (I) and moving-range (MR) chart with capability. Limits are calculated from the points themselves once there are
/// enough; sigma is estimated from the average moving range (MR-bar / 1.128). Cp and Cpk compare that spread with the drawing limits.
/// </summary>
public sealed record SpcChart(
    SpcCharacteristicKey Key,
    string Name,
    string? Unit,
    decimal? Nominal,
    decimal? LowerSpec,
    decimal? UpperSpec,
    bool Critical,
    IReadOnlyList<SpcPoint> Points,
    decimal? Mean,
    decimal? AverageMovingRange,
    decimal? Sigma,
    decimal? UpperControlLimit,
    decimal? LowerControlLimit,
    decimal? MovingRangeUpperLimit,
    decimal? Cp,
    decimal? Cpk,
    decimal RequiredCpk,
    bool? Capable,
    IReadOnlyList<SpcViolation> Violations,
    IReadOnlyList<string> Notes);

public enum SpcSignalStatus { Open, Acknowledged }

/// <summary>A rule a new measurement broke, recorded once and sent to quality engineers.</summary>
public sealed record SpcSignal(long SignalId, SpcCharacteristicKey Key, string CharacteristicName, long InspectionId, long OrderNumber, SpcRule Rule, string Description,
    decimal Value, DateTimeOffset DetectedAt, SpcSignalStatus Status, string? AcknowledgedBy, DateTimeOffset? AcknowledgedAt, string? Note);

public interface ISpcLookup
{
    /// <summary>The policy in force now (the default when none was recorded).</summary>
    Task<SpcPolicy> PolicyAsync(CancellationToken ct);

    Task<IReadOnlyList<SpcCharacteristicKey>> ListCriticalAsync(CancellationToken ct);

    /// <summary>Measurements of variable characteristics recorded since <paramref name="from"/>, oldest first, for one part or every part.</summary>
    Task<IReadOnlyList<SpcMeasurement>> ListMeasurementsAsync(string? partNumber, DateTimeOffset from, CancellationToken ct);

    Task<IReadOnlyList<SpcSignal>> ListSignalsAsync(bool openOnly, CancellationToken ct);

    /// <summary>Whether a signal was already recorded for this inspection, characteristic and rule.</summary>
    Task<bool> SignalExistsAsync(SpcCharacteristicKey key, long inspectionId, SpcRule rule, CancellationToken ct);
}

/// <summary>Pure SPC arithmetic: control limits, capability and the Western Electric rules.</summary>
public static class SpcEngine
{
    /// <summary>d2 for moving ranges of two: sigma = MR-bar / 1.128.</summary>
    public const decimal D2 = 1.128m;

    /// <summary>D4 for moving ranges of two: the MR chart's upper limit is 3.267 × MR-bar.</summary>
    public const decimal D4 = 3.267m;

    public static SpcChart Chart(SpcCharacteristicKey key, CharacteristicInfo characteristic, IReadOnlyList<SpcPoint> points, bool critical, SpcPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(characteristic);
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(policy);
        var ordered = points.OrderBy(p => p.At).ThenBy(p => p.InspectionId).ToList();
        var required = critical ? policy.CriticalMinimumCpk : policy.MinimumCpk;
        var notes = new List<string>();
        if (ordered.Count < Math.Max(2, policy.MinimumPoints))
        {
            notes.Add($"{ordered.Count} measurement(s); control limits and capability need at least {Math.Max(2, policy.MinimumPoints)}.");
            return new SpcChart(key, characteristic.Name, characteristic.Unit, characteristic.Nominal, characteristic.Lower, characteristic.Upper, critical, ordered,
                ordered.Count == 0 ? null : Round(ordered.Average(p => p.Value)), null, null, null, null, null, null, null, required, null, [], notes);
        }

        var values = ordered.Select(p => p.Value).ToList();
        var mean = values.Average();
        var ranges = values.Zip(values.Skip(1), (a, b) => Math.Abs(b - a)).ToList();
        var mrBar = ranges.Average();
        var sigma = mrBar / D2;
        decimal? cp = null, cpk = null;
        if (sigma > 0)
        {
            var lowerSide = characteristic.Lower is { } l ? (mean - l) / (3 * sigma) : (decimal?)null;
            var upperSide = characteristic.Upper is { } u ? (u - mean) / (3 * sigma) : (decimal?)null;
            if (characteristic.Lower is { } lo && characteristic.Upper is { } hi) cp = (hi - lo) / (6 * sigma);
            cpk = (lowerSide, upperSide) switch
            {
                ({ } a, { } b) => Math.Min(a, b),
                ({ } a, null) => a,
                (null, { } b) => b,
                _ => null,
            };
            if (characteristic.Lower is null || characteristic.Upper is null) notes.Add("One-sided limit: Cpk uses the side that has a limit; Cp needs both.");
        }
        else
        {
            notes.Add("Every measurement is the same; sigma is zero, so capability cannot be calculated. Check the gauge resolution.");
        }

        var violations = sigma > 0 ? Rules(ordered, mean, sigma) : [];
        var capable = cpk is { } c ? c >= required : (bool?)null;
        if (capable == false) notes.Add($"Cpk {Round(cpk!.Value, 2)} is below the required {required}{(critical ? " for a critical characteristic" : string.Empty)}.");
        return new SpcChart(key, characteristic.Name, characteristic.Unit, characteristic.Nominal, characteristic.Lower, characteristic.Upper, critical, ordered,
            Round(mean), Round(mrBar), Round(sigma), Round(mean + (3 * sigma)), Round(mean - (3 * sigma)), Round(D4 * mrBar),
            cp is { } p ? Round(p, 3) : null, cpk is { } k ? Round(k, 3) : null, required, capable, violations, notes);
    }

    /// <summary>The Western Electric rules, each reported at the point that completes the pattern.</summary>
    public static IReadOnlyList<SpcViolation> Rules(IReadOnlyList<SpcPoint> points, decimal mean, decimal sigma)
    {
        ArgumentNullException.ThrowIfNull(points);
        var found = new List<SpcViolation>();
        var z = points.Select(p => (p.Value - mean) / sigma).ToList();
        for (var i = 0; i < z.Count; i++)
        {
            var id = points[i].InspectionId;
            if (Math.Abs(z[i]) > 3) found.Add(new(i, id, SpcRule.BeyondThreeSigma, "One point beyond 3 sigma."));
            if (i >= 2 && SameSide(z, i, 3, 2, 2)) found.Add(new(i, id, SpcRule.TwoOfThreeBeyondTwoSigma, "Two of three points beyond 2 sigma on the same side."));
            if (i >= 4 && SameSide(z, i, 5, 4, 1)) found.Add(new(i, id, SpcRule.FourOfFiveBeyondOneSigma, "Four of five points beyond 1 sigma on the same side."));
            if (i >= 7 && (z.Skip(i - 7).Take(8).All(v => v > 0) || z.Skip(i - 7).Take(8).All(v => v < 0)))
                found.Add(new(i, id, SpcRule.EightOnOneSide, "Eight points in a row on one side of the centre line."));
        }

        return found;
    }

    private static bool SameSide(IReadOnlyList<decimal> z, int end, int window, int needed, decimal beyond)
    {
        var slice = z.Skip(end - window + 1).Take(window).ToList();
        // The completing point itself must be beyond the limit, so a pattern is reported once, where it happens.
        return (z[end] > beyond && slice.Count(v => v > beyond) >= needed) || (z[end] < -beyond && slice.Count(v => v < -beyond) >= needed);
    }

    private static decimal Round(decimal value, int digits = 6) => Math.Round(value, digits, MidpointRounding.AwayFromZero);
}
