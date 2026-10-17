using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Forge.Simulation.Tests.Engine;

/// <summary>One expected-versus-actual comparison. It passes only when the actual text equals the expected text.</summary>
public sealed record Expectation(int Number, string Name, string Expected, string Actual, string Category)
{
    public bool Passed => string.Equals(Expected, Actual, StringComparison.Ordinal);
}

/// <summary>
/// Collects every expectation a scenario checks, writes the results next to the event log, and fails the test if
/// any expectation failed. A scenario only counts as passing if its expectations actually executed.
/// </summary>
public sealed class SimReport(string scenario, int seed)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly List<Expectation> expectations = [];
    private readonly List<string> notes = [];

    public string Scenario { get; } = scenario;

    public int Seed { get; } = seed;

    public IReadOnlyList<Expectation> Expectations => expectations;

    public IEnumerable<Expectation> Failures => expectations.Where(e => !e.Passed);

    /// <summary>Records one comparison. Category: Outcome (API result), Inventory, Authorization, Invariant, Idempotency, Scope.</summary>
    public void Expect(string name, string expected, string actual, string category = "Outcome") =>
        expectations.Add(new Expectation(expectations.Count + 1, name, expected, actual, category));

    public void Expect(string name, decimal expected, decimal actual, string category = "Inventory") =>
        Expect(name, Invariants.Q(expected), Invariants.Q(actual), category);

    public void Note(string text) => notes.Add(text);

    /// <summary>Writes the event log, the expectations and the notes; returns the output folder.</summary>
    public string Write(EventLog log, string? stateHash = null)
    {
        var folder = Path.Combine(OutputRoot(), Scenario, "seed-" + Seed.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "events.jsonl"), log.ToJsonLines());
        File.WriteAllText(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(new
        {
            scenario = Scenario,
            seed = Seed,
            stateHash,
            passed = expectations.Count(e => e.Passed),
            failed = expectations.Count(e => !e.Passed),
            expectations = expectations.Select(e => new { e.Number, e.Category, e.Name, e.Expected, e.Actual, e.Passed }),
            notes,
        }, Json));
        var md = new StringBuilder();
        md.Append("# ").Append(Scenario).Append(", seed ").Append(Seed.ToString(CultureInfo.InvariantCulture)).Append("\n\n");
        md.Append(expectations.Count(e => e.Passed).ToString(CultureInfo.InvariantCulture)).Append(" passed, ")
          .Append(expectations.Count(e => !e.Passed).ToString(CultureInfo.InvariantCulture)).Append(" failed.");
        if (stateHash is not null)
        {
            md.Append(" State hash `").Append(stateHash).Append("`.");
        }

        md.Append("\n\n| # | Category | Check | Expected | Actual | Result |\n|---|---|---|---|---|---|\n");
        foreach (var e in expectations)
        {
            md.Append("| ").Append(e.Number.ToString(CultureInfo.InvariantCulture)).Append(" | ").Append(e.Category).Append(" | ").Append(Cell(e.Name))
              .Append(" | ").Append(Cell(e.Expected)).Append(" | ").Append(Cell(e.Actual)).Append(" | ").Append(e.Passed ? "PASS" : "**FAIL**").Append(" |\n");
        }

        if (notes.Count > 0)
        {
            md.Append("\n## Notes\n\n");
            foreach (var n in notes)
            {
                md.Append("- ").Append(n).Append('\n');
            }
        }

        File.WriteAllText(Path.Combine(folder, "results.md"), md.ToString());
        return folder;
    }

    /// <summary>Fails the test with every failed expectation listed. Call after <see cref="Write"/>.</summary>
    public void AssertAllPassed()
    {
        Assert.True(expectations.Count > 0, "A scenario that checked nothing cannot pass.");
        var failures = Failures.Select(e => $"#{e.Number} [{e.Category}] {e.Name}: expected '{e.Expected}', actual '{e.Actual}'").ToList();
        Assert.True(failures.Count == 0, $"{Scenario} seed {Seed}: {failures.Count} of {expectations.Count} expectations failed:\n" + string.Join('\n', failures));
    }

    /// <summary>FORGE_SIM_OUT, or .forge-logs/simulation under the repository root (found by walking up to Forge.slnx).</summary>
    public static string OutputRoot()
    {
        var configured = Environment.GetEnvironmentVariable("FORGE_SIM_OUT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Forge.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? Path.GetTempPath(), ".forge-logs", "simulation");
    }

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
