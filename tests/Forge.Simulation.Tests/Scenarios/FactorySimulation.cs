using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;
using Forge.Simulation.Tests.Factory;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Phase C: the full factory at the owner's sizes (300 orders, 70 operators, 90 days; FORGE_FACTORY_* override, and
/// FORGE_FACTORY_SIZE=smoke runs a small factory). Two runs on the same seed and the same organization, each on its own
/// disposable database: a baseline and a disrupted run. Both write their results; the comparison and the amendment's
/// outputs 1-10 are written to .forge-logs/simulation/factory. Long-running: simulate-factory.cmd runs it alone;
/// simulate.cmd and verify.cmd exclude Mode=Factory.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Simulation")]
[Trait("Mode", "Factory")]
public sealed class FactorySimulation(PostgresFixture postgres)
{
    [Fact]
    public async Task F01_full_factory_baseline_and_disrupted_runs_keep_every_invariant()
    {
        var spec = new FactorySpec(FactorySize.FromEnvironment());
        var baseline = await RunAsync(spec, disrupted: false);
        var disrupted = await RunAsync(spec, disrupted: true);
        var page = FactoryReport.Write(spec, baseline, disrupted, disrupted.Run.Disruptions!);
        RunSummary.Refresh(SimReport.OutputRoot());
        Assert.True(File.Exists(page));
        baseline.Report.AssertAllPassed();
        disrupted.Report.AssertAllPassed();
    }

    private async Task<FactoryResult> RunAsync(FactorySpec spec, bool disrupted)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var name = "F01-factory-" + (disrupted ? "disrupted" : "baseline");
        var report = new SimReport(name, spec.Size.Seed);
        var plant = await FactoryBuilder.BuildAsync(postgres, spec, (disrupted ? "fd-" : "fb-") + spec.Size.Seed.ToString(CultureInfo.InvariantCulture));
        await using (plant.F)
        {
            report.Evidence(plant.Api);
            var run = new FactoryRun(plant, report, disrupted);
            await run.RunAsync();
            var parts = spec.Raw.Select(r => r.Part).Concat(spec.AllMade.Select(m => m.Part)).ToList();
            var hash = await Invariants.StateHash(plant.Stores, parts);
            var log = new EventLog(name, spec.Size.Seed);
            foreach (var d in spec.Demand)
            {
                log.Add(TimeSpan.FromDays(d.ArrivalDay).Add(TimeSpan.FromHours(6)), "CustomerOrder", ("index", d.Index), ("customer", d.Customer), ("part", d.Part), ("quantity", d.Quantity), ("needDay", d.NeedDay + 1), ("priority", d.Priority));
            }

            foreach (var e in run.Disruptions?.Plan ?? Array.Empty<Disruption>())
            {
                log.Add(TimeSpan.FromDays(e.Day).Add(TimeSpan.FromHours(6)), "Disruption", ("name", e.Name), ("area", e.Area));
            }

            var folder = report.Write(log, hash);
            File.WriteAllText(Path.Combine(folder, "days.csv"), run.DaysCsv());
            return new FactoryResult(disrupted ? "Disrupted" : "Baseline", run, report, plant.Api.Timings, watch.Elapsed, hash);
        }
    }
}
