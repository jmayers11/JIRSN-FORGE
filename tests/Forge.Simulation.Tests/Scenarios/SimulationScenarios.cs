using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Phase A scenarios. Each runs the real host and real services on its own disposable database, writes its event log
/// and expected-versus-actual results to .forge-logs/simulation, and fails if any expectation failed.
/// Negative controls run the same scenarios with a deliberate break and must fail.
/// Run with simulate.cmd; verify.cmd excludes Category=Simulation.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Simulation")]
public sealed class SimulationScenarios(PostgresFixture postgres)
{
    public static TheoryData<int> Seeds => [7, 42, 1009];

    public static TheoryData<string> ScenarioNames => [HoldScopeScenario.Name, MrbDispositionScenario.Name, SplitDispositionScenario.Name, NetworkInterruptionScenario.Name];

    /// <summary>Each break, the scenario it applies to, and the check categories that must catch it.</summary>
    public static TheoryData<Mutation, string, string> NegativeControls => new()
    {
        { Mutation.HoldWrongLot, HoldScopeScenario.Name, "Scope" },
        { Mutation.OracleOffByOne, MrbDispositionScenario.Name, "Inventory" },
        { Mutation.SelfApproveUseAsIs, MrbDispositionScenario.Name, "Outcome" },
        { Mutation.RetryWithFreshKey, NetworkInterruptionScenario.Name, "Idempotency" },
        { Mutation.ReleaseReducedPart, SplitDispositionScenario.Name, "Inventory" },
    };

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task S09_quality_hold_stops_only_the_held_lot(int seed) => await RunAndAssertAsync(HoldScopeScenario.Name, seed);

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task S10_supplier_mrb_dispositions_move_exactly_the_rejected_quantity(int seed) => await RunAndAssertAsync(MrbDispositionScenario.Name, seed);

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task S11_split_dispositions_account_for_every_rejected_unit(int seed) => await RunAndAssertAsync(SplitDispositionScenario.Name, seed);

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task S15_lost_responses_and_concurrent_retries_apply_once(int seed) => await RunAndAssertAsync(NetworkInterruptionScenario.Name, seed);

    /// <summary>A deliberate break must be caught by the checks it targets. If this passes, a green run means something.</summary>
    [Theory]
    [MemberData(nameof(NegativeControls))]
    public async Task Negative_control_is_caught(Mutation mutation, string scenario, string category)
    {
        const int seed = 42;
        var (report, log, hash) = await RunAsync(scenario, seed, mutation);
        report.Write(log, hash);
        var caught = report.Failures.Where(f => string.Equals(f.Category, category, StringComparison.Ordinal)).ToList();
        Assert.True(caught.Count > 0,
            $"{mutation} on {scenario} was not caught by any {category} check ({report.Failures.Count()} failures in other categories). The checks cannot be trusted.");
    }

    /// <summary>The same seed on two fresh databases gives the same event log and the same business state.</summary>
    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Same_seed_reproduces_the_same_event_log_and_state(string scenario)
    {
        const int seed = 42;
        var first = await RunAsync(scenario, seed);
        var second = await RunAsync(scenario, seed);
        var report = new SimReport("reproducibility-" + scenario, seed);
        const string Rule = "Amendment: reproducibility (same seed, same events, same state)";
        report.ExpectInvariant("Event log text is identical", first.Log.ToJsonLines(), second.Log.ToJsonLines(), Rule);
        report.ExpectInvariant("Business state hash is identical", first.Hash, second.Hash, Rule);
        report.ExpectInvariant("Both runs passed their own expectations", "0 / 0",
            $"{first.Report.Failures.Count()} / {second.Report.Failures.Count()}", Rule);
        report.ExpectInvariant("A different seed gives a different plan", "different",
            Plan(scenario, seed).ToJsonLines() == Plan(scenario, seed + 1).ToJsonLines() ? "same" : "different", Rule);
        report.Write(first.Log, first.Hash);
        report.AssertAllPassed();
    }

    private async Task RunAndAssertAsync(string scenario, int seed)
    {
        var (report, log, hash) = await RunAsync(scenario, seed);
        report.Write(log, hash);
        report.AssertAllPassed();
    }

    private Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(string scenario, int seed, Mutation mutation = Mutation.None) => scenario switch
    {
        HoldScopeScenario.Name => HoldScopeScenario.RunAsync(postgres, seed, mutation),
        MrbDispositionScenario.Name => MrbDispositionScenario.RunAsync(postgres, seed, mutation),
        SplitDispositionScenario.Name => SplitDispositionScenario.RunAsync(postgres, seed, mutation),
        NetworkInterruptionScenario.Name => NetworkInterruptionScenario.RunAsync(postgres, seed, mutation),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario."),
    };

    private static EventLog Plan(string scenario, int seed) => scenario switch
    {
        HoldScopeScenario.Name => HoldScopeScenario.Plan(seed),
        MrbDispositionScenario.Name => MrbDispositionScenario.Plan(seed),
        SplitDispositionScenario.Name => SplitDispositionScenario.Plan(seed),
        NetworkInterruptionScenario.Name => NetworkInterruptionScenario.Plan(seed),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario."),
    };
}
