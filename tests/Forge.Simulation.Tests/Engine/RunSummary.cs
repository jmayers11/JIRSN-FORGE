using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Forge.Simulation.Tests.Engine;

/// <summary>
/// One page for the whole run: every scenario and seed, the negative controls, the expectations by basis, and the
/// rules that are not written in the specification and need an owner ruling. Rebuilt after every scenario writes, from
/// the result files on disk, so the last scenario to finish leaves the complete page.
/// </summary>
internal static class RunSummary
{
    public const string CanaryMarker = "-canary-";

    public static void Refresh(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var runs = Directory.GetFiles(root, "results.json", SearchOption.AllDirectories)
            .Select(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement)
            .Select(r => new Run(
                r.GetProperty("scenario").GetString()!,
                r.GetProperty("seed").GetInt32(),
                r.GetProperty("passed").GetInt32(),
                r.GetProperty("failed").GetInt32(),
                r.TryGetProperty("stateHash", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null,
                r.GetProperty("expectations").EnumerateArray().Select(e => new Check(
                    Text(e, "Basis") ?? "-",
                    Text(e, "Source") ?? "-",
                    Text(e, "Category") ?? "-",
                    Text(e, "Name") ?? "-",
                    e.GetProperty("Passed").GetBoolean())).ToList()))
            .OrderBy(r => r.IsCanary).ThenBy(r => r.Scenario, StringComparer.Ordinal).ThenBy(r => r.Seed)
            .ToList();

        var real = runs.Where(r => !r.IsCanary).ToList();
        var canaries = runs.Where(r => r.IsCanary).ToList();
        var md = new StringBuilder();
        md.Append("# Simulation run summary\n\n");
        md.Append(I(real.Count)).Append(" scenario runs: ").Append(I(real.Sum(r => r.Passed))).Append(" expectations passed, ")
          .Append(I(real.Sum(r => r.Failed))).Append(" failed. ")
          .Append(I(canaries.Count(c => c.Failed > 0))).Append(" of ").Append(I(canaries.Count)).Append(" negative controls were caught.\n\n");

        md.Append("## Scenario runs\n\n| Scenario | Seed | Passed | Failed | State hash |\n|---|---|---|---|---|\n");
        foreach (var r in real)
        {
            md.Append("| ").Append(r.Scenario).Append(" | ").Append(I(r.Seed)).Append(" | ").Append(I(r.Passed)).Append(" | ")
              .Append(r.Failed == 0 ? "0" : "**" + I(r.Failed) + "**").Append(" | ").Append(r.Hash is null ? "-" : "`" + r.Hash[..Math.Min(12, r.Hash.Length)] + "`").Append(" |\n");
        }

        md.Append("\n## Negative controls\n\nEach run deliberately breaks something. It must produce failures, or the checks cannot be trusted.\n\n")
          .Append("| Control | Seed | Failures produced | Result | First failed checks |\n|---|---|---|---|---|\n");
        foreach (var c in canaries)
        {
            var first = string.Join("; ", c.Checks.Where(x => !x.Passed).Take(3).Select(x => SimReport.Cell(x.Name)));
            md.Append("| ").Append(c.Scenario).Append(" | ").Append(I(c.Seed)).Append(" | ").Append(I(c.Failed)).Append(" | ")
              .Append(c.Failed > 0 ? "caught" : "**NOT CAUGHT**").Append(" | ").Append(first).Append(" |\n");
        }

        md.Append("\n## Expectations by basis\n\n| Basis | Checks | Distinct rules |\n|---|---|---|\n");
        foreach (var g in real.SelectMany(r => r.Checks).GroupBy(c => c.Basis, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            md.Append("| ").Append(g.Key).Append(" | ").Append(I(g.Count())).Append(" | ")
              .Append(I(g.Select(c => c.Source).Distinct(StringComparer.Ordinal).Count())).Append(" |\n");
        }

        md.Append("\n## Rules that need an owner ruling\n\nThese expectations are not written in an ADR or the README. ")
          .Append("Each passed against Forge's current behavior, but nothing in the specification says that behavior is required.\n\n")
          .Append("| Basis | Rule | Example check | Checks |\n|---|---|---|---|\n");
        foreach (var g in real.SelectMany(r => r.Checks).Where(c => Basis.NeedsRuling(c.Basis))
                     .GroupBy(c => (c.Basis, c.Source)).OrderBy(g => g.Key.Basis, StringComparer.Ordinal).ThenBy(g => g.Key.Source, StringComparer.Ordinal))
        {
            md.Append("| ").Append(g.Key.Basis).Append(" | ").Append(SimReport.Cell(g.Key.Source)).Append(" | ")
              .Append(SimReport.Cell(StripStep(g.First().Name))).Append(" | ").Append(I(g.Count())).Append(" |\n");
        }

        File.WriteAllText(Path.Combine(root, "summary.md"), md.ToString());
        File.WriteAllText(Path.Combine(root, "summary.json"), JsonSerializer.Serialize(runs.Select(r => new
        {
            r.Scenario,
            r.Seed,
            r.Passed,
            r.Failed,
            r.Hash,
            r.IsCanary,
        }), SimReport.Json));
    }

    private static string StripStep(string name)
    {
        var colon = name.IndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? name : name[(colon + 2)..];
    }

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed record Check(string Basis, string Source, string Category, string Name, bool Passed);

    private sealed record Run(string Scenario, int Seed, int Passed, int Failed, string? Hash, List<Check> Checks)
    {
        public bool IsCanary => Scenario.Contains(CanaryMarker, StringComparison.Ordinal);
    }
}
