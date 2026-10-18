using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Forge.Simulation.Tests.Engine;

/// <summary>
/// Where an expected outcome comes from. Only <see cref="Spec"/> is written down in an ADR or the README;
/// <see cref="Inferred"/> and <see cref="CodeOnly"/> are questions for the owner to rule on.
/// </summary>
public static class Basis
{
    /// <summary>Stated in an ADR or the README.</summary>
    public const string Spec = "Spec";

    /// <summary>A system-wide invariant the simulation amendment requires (conservation, non-negativity, audit, reproducibility).</summary>
    public const string Invariant = "Invariant";

    /// <summary>Arithmetic of the seeded plan, or a setup step that must succeed for the scenario to mean anything.</summary>
    public const string Plan = "Plan";

    /// <summary>A reasonable reading of the specification that is not written down. Needs an owner ruling.</summary>
    public const string Inferred = "Inferred";

    /// <summary>Stated only in a code comment, not in an ADR or the README. Needs an owner ruling.</summary>
    public const string CodeOnly = "Code only";

    public static bool NeedsRuling(string basis) => basis is Inferred or CodeOnly;
}

/// <summary>One KPI an event moved: the event, the module (first key segment), the key, and its values before and after.</summary>
public sealed record Impact(string Event, string Module, string Key, decimal Before, decimal After);

/// <summary>One expected-versus-actual comparison. It passes only when the actual text equals the expected text.</summary>
public sealed record Expectation(int Number, string Name, string Expected, string Actual, string Category, string Basis, string Source)
{
    public bool Passed => string.Equals(Expected, Actual, StringComparison.Ordinal);
}

/// <summary>
/// Collects every expectation a scenario checks, writes the results next to the event log, and fails the test if
/// any expectation failed. A scenario only counts as passing if its expectations actually executed.
/// Every expectation records the rule it checks: set it with <see cref="Rule"/> before the checks it covers.
/// </summary>
public sealed class SimReport(string scenario, int seed)
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Line = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly List<Expectation> expectations = [];
    private readonly List<string> notes = [];
    private readonly List<string> findings = [];
    private readonly List<KpiSnapshot> kpis = [];
    private readonly List<Impact> impacts = [];
    private readonly SortedSet<string> covers = new(StringComparer.Ordinal);
    private readonly List<SimApi> evidence = [];
    private string basis = Engine.Basis.Plan;
    private string source = "Seeded plan";

    public string Scenario { get; } = scenario;

    public int Seed { get; } = seed;

    public IReadOnlyList<Expectation> Expectations => expectations;

    public IEnumerable<Expectation> Failures => expectations.Where(e => !e.Passed);

    /// <summary>Sets the rule that the following expectations check, until the next call.</summary>
    public void Rule(string ruleBasis, string ruleSource)
    {
        basis = ruleBasis;
        source = ruleSource;
    }

    /// <summary>Records one comparison under the current rule. Category: Outcome, Inventory, Authorization, Invariant, Idempotency, Scope.</summary>
    public void Expect(string name, string expected, string actual, string category = "Outcome") =>
        Add(name, expected, actual, category, basis, source);

    public void Expect(string name, decimal expected, decimal actual, string category = "Inventory") =>
        Expect(name, Invariants.Q(expected), Invariants.Q(actual), category);

    /// <summary>Records one invariant comparison, whatever rule is current.</summary>
    public void ExpectInvariant(string name, string expected, string actual, string source) =>
        Add(name, expected, actual, "Invariant", Engine.Basis.Invariant, source);

    public void Note(string text) => notes.Add(text);

    /// <summary>
    /// Something the run showed about Forge that is not a failed expectation: a gap, a missing command, a limit. Findings
    /// are listed for the owner in the summary; they are never counted as passing coverage.
    /// </summary>
    public void Finding(string text) => findings.Add(text);

    /// <summary>The coverage areas (see <see cref="Coverage"/>) this scenario exercises through the real API.</summary>
    public void Covers(params string[] areas) => covers.UnionWith(areas);

    /// <summary>Keeps an API driver whose command log is written to calls.jsonl, the evidence for any defect (several drivers append in order).</summary>
    public void Evidence(SimApi api) => evidence.Add(api);

    public IReadOnlyCollection<string> Covered => covers;

    /// <summary>Keeps a KPI reading for kpis.json.</summary>
    public KpiSnapshot Kpi(KpiSnapshot snapshot)
    {
        kpis.Add(snapshot);
        return snapshot;
    }

    /// <summary>Records which KPIs an event moved, by module, for the cross-module impact matrix.</summary>
    public void ImpactOf(string eventName, KpiSnapshot before, KpiSnapshot after)
    {
        foreach (var (k, b, a) in Kpis.Changed(before, after))
        {
            impacts.Add(new Impact(eventName, k[..k.IndexOf('.', StringComparison.Ordinal)], k, b, a));
        }
    }

    /// <summary>Writes the event log, the expectations and the notes, then refreshes the run summary; returns the output folder.</summary>
    public string Write(EventLog log, string? stateHash = null, SimApi? api = null)
    {
        var folder = Path.Combine(OutputRoot(), Scenario, "seed-" + Seed.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "events.jsonl"), log.ToJsonLines());
        File.WriteAllText(Path.Combine(folder, "kpis.json"), JsonSerializer.Serialize(kpis.Select(k => new { k.Label, at = "+" + ((long)k.At.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m", k.Values }), Json));
        File.WriteAllText(Path.Combine(folder, "impact.json"), JsonSerializer.Serialize(impacts, Json));
        IReadOnlyList<SimApi> drivers = api is null ? evidence : [api];
        if (drivers.Count > 0)
        {
            File.WriteAllLines(Path.Combine(folder, "calls.jsonl"), drivers.SelectMany(d => d.Calls).Select(c => JsonSerializer.Serialize(c, Line)));
        }
        File.WriteAllText(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(new
        {
            scenario = Scenario,
            seed = Seed,
            stateHash,
            passed = expectations.Count(e => e.Passed),
            failed = expectations.Count(e => !e.Passed),
            expectations = expectations.Select(e => new { e.Number, e.Category, e.Basis, e.Source, e.Name, e.Expected, e.Actual, e.Passed }),
            covers,
            findings,
            impacts = impacts.GroupBy(i => (i.Event, i.Module)).Select(g => new { g.Key.Event, g.Key.Module, Changed = g.Count() }),
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

        md.Append("\n\n| # | Category | Basis | Source | Check | Expected | Actual | Result |\n|---|---|---|---|---|---|---|---|\n");
        foreach (var e in expectations)
        {
            md.Append("| ").Append(e.Number.ToString(CultureInfo.InvariantCulture)).Append(" | ").Append(e.Category).Append(" | ").Append(e.Basis)
              .Append(" | ").Append(Cell(e.Source)).Append(" | ").Append(Cell(e.Name))
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
        RunSummary.Refresh(OutputRoot());
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

    internal static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private void Add(string name, string expected, string actual, string category, string ruleBasis, string ruleSource) =>
        expectations.Add(new Expectation(expectations.Count + 1, name, expected, actual, category, ruleBasis, ruleSource));
}
