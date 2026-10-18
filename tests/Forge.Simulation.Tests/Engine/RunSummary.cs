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
                    e.GetProperty("Passed").GetBoolean(),
                    Text(e, "Expected") ?? "",
                    Text(e, "Actual") ?? "")).ToList(),
                r.TryGetProperty("covers", out var cv) ? cv.EnumerateArray().Select(c => c.GetString()!).ToList() : [],
                r.TryGetProperty("findings", out var fd) ? fd.EnumerateArray().Select(x => x.GetString()!).ToList() : [],
                r.TryGetProperty("impacts", out var im) ? im.EnumerateArray().Select(i => (Event: Text(i, "Event")!, Module: Text(i, "Module")!, Changed: i.GetProperty("Changed").GetInt32())).ToList() : []))
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

        // Output 4: the cross-module impact matrix, from observed KPI changes only.
        var impacts = real.SelectMany(r => r.Impacts.Select(i => (Event: r.Scenario + ": " + i.Event, i.Module, i.Changed))).ToList();
        var modules = impacts.Select(i => i.Module).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        md.Append("\n## Cross-module impact matrix\n\nNumber of KPIs each event moved, by module, observed through Forge's own engines and reports (all seeds).\n\n");
        if (impacts.Count == 0)
        {
            md.Append("No KPI readings in this run.\n");
        }
        else
        {
            md.Append("| Event | ").Append(string.Join(" | ", modules)).Append(" |\n|---|").Append(string.Concat(modules.Select(_ => "---|"))).Append('\n');
            foreach (var e in impacts.GroupBy(i => i.Event, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                md.Append("| ").Append(SimReport.Cell(e.Key)).Append(" | ")
                  .Append(string.Join(" | ", modules.Select(m => { var n = e.Where(i => i.Module == m).Sum(i => i.Changed); return n == 0 ? "·" : I(n); }))).Append(" |\n");
            }
        }

        // Output 8: the defect register. Every failed expectation of a real run, with where its evidence is.
        var defects = real.SelectMany(r => r.Checks.Where(c => !c.Passed).Select(c => (Run: r, Check: c))).ToList();
        md.Append("\n## Defect register\n\n");
        if (defects.Count == 0)
        {
            md.Append("No failed expectations in real runs.\n");
        }
        else
        {
            md.Append("Each row is a candidate defect, in Forge or in the expectation, until triaged. Evidence: `<scenario>/seed-<n>/` (results.md, events.jsonl, calls.jsonl).\n\n")
              .Append("| Scenario | Seed | Check | Basis | Source | Expected | Actual |\n|---|---|---|---|---|---|---|\n");
            foreach (var (run, c) in defects)
            {
                md.Append("| ").Append(run.Scenario).Append(" | ").Append(I(run.Seed)).Append(" | ").Append(SimReport.Cell(c.Name)).Append(" | ").Append(c.Basis)
                  .Append(" | ").Append(SimReport.Cell(c.Source)).Append(" | ").Append(SimReport.Cell(c.Expected)).Append(" | ").Append(SimReport.Cell(c.Actual)).Append(" |\n");
            }
        }

        // Findings: what the runs showed about Forge that is not a failed expectation (gaps, missing commands, limits).
        var found = real.SelectMany(r => r.Findings.Select(text => (Text: text, r.Scenario))).GroupBy(x => x.Text, StringComparer.Ordinal).ToList();
        md.Append("\n## Findings\n\nGaps and limits the runs showed. They are not counted as passing.\n\n");
        if (found.Count == 0)
        {
            md.Append("None.\n");
        }

        foreach (var g in found)
        {
            md.Append("- ").Append(g.Key).Append(" (").Append(string.Join(", ", g.Select(x => x.Scenario).Distinct(StringComparer.Ordinal))).Append(")\n");
        }

        // Output 10: the readiness scorecard. An area passes only if a scenario covering it ran and passed.
        // I: the area is the scenario's subject. X: the area is checked as a consequence of another module's event.
        md.Append("\n## Scorecard\n\nI = the area is the scenario's subject; X = the area is checked as a consequence of another module's event.\n\n")
          .Append("| Area | Status | I scenarios | X scenarios | I and X |\n|---|---|---|---|---|\n");
        var bothCount = 0;
        foreach (var area in Coverage.Areas)
        {
            var covering = real.Where(r => r.Covers.Contains(area)).ToList();
            var scenarios = covering.Select(r => r.Scenario).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var individual = scenarios.Where(s => Coverage.Subject.TryGetValue(s, out var subject) && subject == area).ToList();
            var cross = scenarios.Except(individual, StringComparer.Ordinal).ToList();
            var status = covering.Count == 0 ? "**Not covered yet**" : covering.All(r => r.Failed == 0) ? "Covered, passing" : "**Covered, failing**";
            var both = individual.Count > 0 && cross.Count > 0;
            bothCount += both ? 1 : 0;
            md.Append("| ").Append(area).Append(" | ").Append(status).Append(" | ").Append(individual.Count == 0 ? "—" : string.Join(", ", individual))
              .Append(" | ").Append(cross.Count == 0 ? "—" : string.Join(", ", cross)).Append(" | ").Append(both ? "yes" : "**no**").Append(" |\n");
        }

        md.Append('\n').Append(I(bothCount)).Append(" of ").Append(I(Coverage.Areas.Length)).Append(" areas have both an I and an X scenario.\n");

        md.Append("\n**Not implemented in Forge** (reported, never counted as passing):\n\n");
        foreach (var (area, what) in Coverage.NotImplemented)
        {
            md.Append("- ").Append(area).Append(": ").Append(what).Append('\n');
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

    private sealed record Check(string Basis, string Source, string Category, string Name, bool Passed, string Expected, string Actual);

    private sealed record Run(string Scenario, int Seed, int Passed, int Failed, string? Hash, List<Check> Checks, List<string> Covers, List<string> Findings, List<(string Event, string Module, int Changed)> Impacts)
    {
        public bool IsCanary => Scenario.Contains(CanaryMarker, StringComparison.Ordinal);
    }
}
