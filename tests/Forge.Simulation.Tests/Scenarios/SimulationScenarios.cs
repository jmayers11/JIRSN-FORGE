using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Phase A scenarios. Each runs the real host and real services on its own disposable database, writes its event log
/// and expected-versus-actual results to .forge-logs/simulation, and fails if any expectation failed.
/// Run with simulate.cmd; verify.cmd excludes Category=Simulation.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Simulation")]
public sealed class SimulationScenarios(PostgresFixture postgres)
{
    public static TheoryData<int> Seeds => [7, 42, 1009];

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task S09_quality_hold_stops_only_the_held_lot(int seed)
    {
        var (report, log, hash) = await HoldScopeScenario.RunAsync(postgres, seed);
        report.Write(log, hash);
        report.AssertAllPassed();
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task S10_supplier_mrb_dispositions_move_exactly_the_rejected_quantity(int seed)
    {
        var (report, log, hash) = await MrbDispositionScenario.RunAsync(postgres, seed);
        report.Write(log, hash);
        report.AssertAllPassed();
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task S15_lost_responses_and_concurrent_retries_apply_once(int seed)
    {
        var (report, log, hash) = await NetworkInterruptionScenario.RunAsync(postgres, seed);
        report.Write(log, hash);
        report.AssertAllPassed();
    }

    /// <summary>The same seed on two fresh databases gives the same event log and the same business state.</summary>
    [Fact]
    public async Task Same_seed_reproduces_the_same_event_log_and_state()
    {
        const int seed = 42;
        var first = await MrbDispositionScenario.RunAsync(postgres, seed);
        var second = await MrbDispositionScenario.RunAsync(postgres, seed);
        var report = new SimReport("reproducibility", seed);
        report.Expect("Event log text is identical", first.Log.ToJsonLines(), second.Log.ToJsonLines(), "Invariant");
        report.Expect("Business state hash is identical", first.Hash, second.Hash, "Invariant");
        report.Expect("Both runs passed their own expectations", "0 / 0",
            $"{first.Report.Failures.Count()} / {second.Report.Failures.Count()}", "Invariant");
        report.Note("Different seeds must give different plans: seed 42 and seed 43 event logs differ = "
            + (MrbDispositionScenario.Plan(42).ToJsonLines() != MrbDispositionScenario.Plan(43).ToJsonLines()));
        report.Write(first.Log, first.Hash);
        report.AssertAllPassed();
    }
}
