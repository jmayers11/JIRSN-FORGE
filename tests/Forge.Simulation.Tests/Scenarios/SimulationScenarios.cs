using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Phase A and B scenarios. Each runs the real host and real services on its own disposable database, writes its event log
/// and expected-versus-actual results to .forge-logs/simulation, and fails if any expectation failed.
/// Negative controls run the same scenarios with a deliberate break and must fail.
/// Run with simulate.cmd; verify.cmd excludes Category=Simulation.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Simulation")]
public sealed class SimulationScenarios(PostgresFixture postgres)
{
    public static TheoryData<int> Seeds => [7, 42, 1009];

    /// <summary>Phase B scenarios run on two seeds by default; each is slower because it reads KPIs from the planning engines.</summary>
    public static TheoryData<int> PhaseBSeeds => [42, 7];

    public static TheoryData<string> ScenarioNames => [HoldScopeScenario.Name, MrbDispositionScenario.Name, SplitDispositionScenario.Name, NetworkInterruptionScenario.Name];

    /// <summary>Each break, the scenario it applies to, and the check categories that must catch it.</summary>
    public static TheoryData<Mutation, string, string> NegativeControls => new()
    {
        { Mutation.HoldWrongLot, HoldScopeScenario.Name, "Scope" },
        { Mutation.OracleOffByOne, MrbDispositionScenario.Name, "Inventory" },
        { Mutation.SelfApproveUseAsIs, MrbDispositionScenario.Name, "Outcome" },
        { Mutation.RetryWithFreshKey, NetworkInterruptionScenario.Name, "Idempotency" },
        { Mutation.ReleaseReducedPart, SplitDispositionScenario.Name, "Inventory" },
        { Mutation.CallOutWrongPerson, OperatorCalloutScenario.Name, "Capacity" },
        { Mutation.SelfApproveCount, InaccurateInventoryScenario.Name, "Inventory" },
    };

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task S09_quality_hold_stops_only_the_held_lot(int seed) => await RunAndAssertAsync(HoldScopeScenario.Name, seed);

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task S10_supplier_mrb_dispositions_move_exactly_the_rejected_quantity(int seed) => await RunAndAssertAsync(MrbDispositionScenario.Name, seed);

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task S10b_split_dispositions_account_for_every_rejected_unit(int seed) => await RunAndAssertAsync(SplitDispositionScenario.Name, seed);

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task S15_lost_responses_and_concurrent_retries_apply_once(int seed) => await RunAndAssertAsync(NetworkInterruptionScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S01_operator_callout_stops_slitting_and_reallocation_trades_paint_for_it(int seed) => await RunAndAssertAsync(OperatorCalloutScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S02_pto_and_a_suspended_qualification_change_capacity_ahead_of_time(int seed) => await RunAndAssertAsync(PtoAndTrainingScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S03_staffing_transfer_moves_capacity_between_departments(int seed) => await RunAndAssertAsync(StaffingTransferScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S04_breakdown_and_overdue_pm_move_work_and_recover_on_sign_off(int seed) => await RunAndAssertAsync(BreakdownAndPmScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S05_an_expired_gauge_cannot_be_used_until_recalibrated(int seed) => await RunAndAssertAsync(CalibrationExpiryScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S06_a_supplier_delay_past_the_due_date_makes_the_order_late(int seed) => await RunAndAssertAsync(SupplierDelayScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S07_an_approved_count_variance_creates_a_shortage_and_replanning(int seed) => await RunAndAssertAsync(InaccurateInventoryScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S08_fai_gates_acceptance_and_a_rejected_part_is_replaced_by_a_new_lot(int seed) => await RunAndAssertAsync(ReceivingFaiScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S11_an_engineering_revision_mid_production_keeps_released_work_and_blocks_a_mismatch(int seed) => await RunAndAssertAsync(EngineeringRevisionScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S12_an_expedited_order_moves_ahead_and_delays_only_the_orders_it_passes(int seed) => await RunAndAssertAsync(ExpeditedOrderScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S13_a_missing_or_conflicting_document_blocks_work_until_corrected(int seed) => await RunAndAssertAsync(DocumentErrorScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S14_a_wrong_scan_and_a_jammed_print_never_create_a_trusted_label(int seed) => await RunAndAssertAsync(ScanAndPrintScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S16_concurrent_transactions_on_the_same_material_apply_exactly_once(int seed) => await RunAndAssertAsync(ConcurrencyScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S17_a_restored_backup_is_the_plant_at_the_backup_and_accepts_new_work(int seed) => await RunAndAssertAsync(RecoveryScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S18_production_runs_through_training_breakdown_restart_and_racing_terminals_to_finished_stock(int seed) => await RunAndAssertAsync(ProductionExecutionScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S19_a_lone_work_rule_stops_solo_painting_and_borrowing_a_responder_stops_slitting(int seed) => await RunAndAssertAsync(LoneWorkRuleScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S20_demand_changes_drive_mrp_to_exactly_the_open_quantity(int seed) => await RunAndAssertAsync(DemandChangeScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S21_mrp_buys_exactly_what_demand_lacks_and_requisitions_keep_their_source(int seed) => await RunAndAssertAsync(MrpPurchaseScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S22_a_customer_order_ships_once_per_command_and_never_from_a_held_lot(int seed) => await RunAndAssertAsync(CustomerShipmentScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S23_failed_replaced_and_voided_labels_are_never_trusted(int seed) => await RunAndAssertAsync(LabelPrintingScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S24_standard_reports_match_the_records_they_summarize(int seed) => await RunAndAssertAsync(ReportsScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S25_corrective_action_is_verified_by_someone_other_than_the_implementer(int seed) => await RunAndAssertAsync(CorrectiveActionScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S26_approved_alternates_are_allocated_only_where_engineering_allows(int seed) => await RunAndAssertAsync(ApprovedAlternateScenario.Name, seed);

    [Theory]
    [Trait("Phase", "B")]
    [MemberData(nameof(PhaseBSeeds))]
    public async Task S27_a_customer_concession_ships_nonconforming_rolls_with_a_second_approval(int seed) => await RunAndAssertAsync(CustomerConcessionScenario.Name, seed);

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
        ProductionExecutionScenario.Name => ProductionExecutionScenario.RunAsync(postgres, seed, mutation),
        LoneWorkRuleScenario.Name => LoneWorkRuleScenario.RunAsync(postgres, seed, mutation),
        DemandChangeScenario.Name => DemandChangeScenario.RunAsync(postgres, seed, mutation),
        MrpPurchaseScenario.Name => MrpPurchaseScenario.RunAsync(postgres, seed, mutation),
        CustomerShipmentScenario.Name => CustomerShipmentScenario.RunAsync(postgres, seed, mutation),
        LabelPrintingScenario.Name => LabelPrintingScenario.RunAsync(postgres, seed, mutation),
        ReportsScenario.Name => ReportsScenario.RunAsync(postgres, seed, mutation),
        CorrectiveActionScenario.Name => CorrectiveActionScenario.RunAsync(postgres, seed, mutation),
        ApprovedAlternateScenario.Name => ApprovedAlternateScenario.RunAsync(postgres, seed, mutation),
        CustomerConcessionScenario.Name => CustomerConcessionScenario.RunAsync(postgres, seed, mutation),
        EngineeringRevisionScenario.Name => EngineeringRevisionScenario.RunAsync(postgres, seed, mutation),
        ExpeditedOrderScenario.Name => ExpeditedOrderScenario.RunAsync(postgres, seed, mutation),
        DocumentErrorScenario.Name => DocumentErrorScenario.RunAsync(postgres, seed, mutation),
        ScanAndPrintScenario.Name => ScanAndPrintScenario.RunAsync(postgres, seed, mutation),
        ConcurrencyScenario.Name => ConcurrencyScenario.RunAsync(postgres, seed, mutation),
        RecoveryScenario.Name => RecoveryScenario.RunAsync(postgres, seed, mutation),
        BreakdownAndPmScenario.Name => BreakdownAndPmScenario.RunAsync(postgres, seed, mutation),
        CalibrationExpiryScenario.Name => CalibrationExpiryScenario.RunAsync(postgres, seed, mutation),
        SupplierDelayScenario.Name => SupplierDelayScenario.RunAsync(postgres, seed, mutation),
        InaccurateInventoryScenario.Name => InaccurateInventoryScenario.RunAsync(postgres, seed, mutation),
        ReceivingFaiScenario.Name => ReceivingFaiScenario.RunAsync(postgres, seed, mutation),
        OperatorCalloutScenario.Name => OperatorCalloutScenario.RunAsync(postgres, seed, mutation),
        PtoAndTrainingScenario.Name => PtoAndTrainingScenario.RunAsync(postgres, seed, mutation),
        StaffingTransferScenario.Name => StaffingTransferScenario.RunAsync(postgres, seed, mutation),
        MrbDispositionScenario.Name => MrbDispositionScenario.RunAsync(postgres, seed, mutation),
        SplitDispositionScenario.Name => SplitDispositionScenario.RunAsync(postgres, seed, mutation),
        NetworkInterruptionScenario.Name => NetworkInterruptionScenario.RunAsync(postgres, seed, mutation),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario."),
    };

    private static EventLog Plan(string scenario, int seed) => scenario switch
    {
        HoldScopeScenario.Name => HoldScopeScenario.Plan(seed),
        ProductionExecutionScenario.Name => ProductionExecutionScenario.Plan(seed),
        LoneWorkRuleScenario.Name => LoneWorkRuleScenario.Plan(seed),
        DemandChangeScenario.Name => DemandChangeScenario.Plan(seed),
        MrpPurchaseScenario.Name => MrpPurchaseScenario.Plan(seed),
        CustomerShipmentScenario.Name => CustomerShipmentScenario.Plan(seed),
        LabelPrintingScenario.Name => LabelPrintingScenario.Plan(seed),
        ReportsScenario.Name => ReportsScenario.Plan(seed),
        CorrectiveActionScenario.Name => CorrectiveActionScenario.Plan(seed),
        ApprovedAlternateScenario.Name => ApprovedAlternateScenario.Plan(seed),
        CustomerConcessionScenario.Name => CustomerConcessionScenario.Plan(seed),
        EngineeringRevisionScenario.Name => EngineeringRevisionScenario.Plan(seed),
        ExpeditedOrderScenario.Name => ExpeditedOrderScenario.Plan(seed),
        DocumentErrorScenario.Name => DocumentErrorScenario.Plan(seed),
        ScanAndPrintScenario.Name => ScanAndPrintScenario.Plan(seed),
        ConcurrencyScenario.Name => ConcurrencyScenario.Plan(seed),
        RecoveryScenario.Name => RecoveryScenario.Plan(seed),
        BreakdownAndPmScenario.Name => BreakdownAndPmScenario.Plan(seed),
        CalibrationExpiryScenario.Name => CalibrationExpiryScenario.Plan(seed),
        SupplierDelayScenario.Name => SupplierDelayScenario.Plan(seed),
        InaccurateInventoryScenario.Name => InaccurateInventoryScenario.Plan(seed),
        ReceivingFaiScenario.Name => ReceivingFaiScenario.Plan(seed),
        OperatorCalloutScenario.Name => OperatorCalloutScenario.Plan(seed),
        PtoAndTrainingScenario.Name => PtoAndTrainingScenario.Plan(seed),
        StaffingTransferScenario.Name => StaffingTransferScenario.Plan(seed),
        MrbDispositionScenario.Name => MrbDispositionScenario.Plan(seed),
        SplitDispositionScenario.Name => SplitDispositionScenario.Plan(seed),
        NetworkInterruptionScenario.Name => NetworkInterruptionScenario.Plan(seed),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario."),
    };
}
