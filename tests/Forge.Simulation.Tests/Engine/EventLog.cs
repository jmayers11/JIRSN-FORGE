using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Forge.Simulation.Tests.Engine;

/// <summary>One planned simulation event. Data values are plain strings, numbers or booleans so the log is stable.</summary>
public sealed record SimEvent(int Index, TimeSpan At, string Type, IReadOnlyDictionary<string, object> Data)
{
    public string Text(string key) => Convert.ToString(Data[key], CultureInfo.InvariantCulture)!;

    public decimal Number(string key) => Convert.ToDecimal(Data[key], CultureInfo.InvariantCulture);
}

/// <summary>
/// A reproducible event log. Events are generated from a seed before anything executes, written as JSON lines and
/// replayable from the file alone. The same seed always produces byte-identical text.
/// </summary>
public sealed class EventLog
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly List<SimEvent> events = [];

    public EventLog(string scenario, int seed)
    {
        Scenario = scenario;
        Seed = seed;
        Random = new Random(seed);
    }

    public string Scenario { get; }

    public int Seed { get; }

    /// <summary>The only randomness a scenario may use. Seeded, so every draw is reproducible.</summary>
    public Random Random { get; }

    public IReadOnlyList<SimEvent> Events => events;

    public SimEvent Add(TimeSpan at, string type, params (string Key, object Value)[] data)
    {
        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in data)
        {
            map[key] = value;
        }

        var e = new SimEvent(events.Count, at, type, map);
        events.Add(e);
        return e;
    }

    /// <summary>A whole number in [min, max], drawn from the seeded generator.</summary>
    public int Between(int min, int max) => Random.Next(min, max + 1);

    public string ToJsonLines()
    {
        var text = new StringBuilder();
        text.Append(JsonSerializer.Serialize(new Dictionary<string, object> { ["scenario"] = Scenario, ["seed"] = Seed }, Json)).Append('\n');
        foreach (var e in events)
        {
            var line = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["i"] = e.Index,
                ["t"] = "+" + ((long)e.At.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m",
                ["type"] = e.Type,
                ["data"] = e.Data,
            };
            text.Append(JsonSerializer.Serialize(line, Json)).Append('\n');
        }

        return text.ToString();
    }
}
