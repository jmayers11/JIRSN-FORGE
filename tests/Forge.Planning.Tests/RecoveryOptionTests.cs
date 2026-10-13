using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Procurement.Contracts;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

public sealed class RecoveryOptionTests
{
    private static readonly ForgePrincipal Actor = new(new PersonId(Guid.Parse("018f0000-0000-7000-8000-000000009999")), "Recovery planner", true,
        new HashSet<string> { Roles.Planner, Roles.ManufacturingEngineer, Roles.Supervisor, Roles.MaterialHandler, Roles.Buyer, Roles.Shipper, Roles.MaintenancePlanner }, new HashSet<ProgramId>(), DataMarking.None);

    [Fact]
    public void Source_evidence_scope_covers_machine_targets_and_other_selected_trials_without_unrelated_orders()
    {
        var orders = new[] { Order(101), Order(102), Order(103) with
        {
            Operations = [Order(103).Operations[0] with { EligibleMachines = ["SLITTER-B"] }],
        } };
        var change = new ScenarioChange(ScenarioChangeKind.MachineUnavailable, MachineCode: "SLITTER-A", StartsAt: Now, DurationHours: 1);
        Assert.Equal(new long[] { 101, 102 }, RecoveryOptionEvaluator.SourceOrders(change, Plant(), orders).Select(o => o.OrderNumber));
        change = change with { RecoverySelections = [new("alternate-machine", OrderNumber: 101, Sequence: 10, MachineCode: "SLITTER-B")] };
        Assert.Equal(new long[] { 101 }, RecoveryOptionEvaluator.SourceOrders(change, Plant(), orders).Select(o => o.OrderNumber));
        change = change with { RecoverySelections = [.. change.RecoverySelections!, new("resequence", OrderNumber: 103, Priority: 1)] };
        Assert.Equal(new long[] { 101, 103 }, RecoveryOptionEvaluator.SourceOrders(change, Plant(), orders).Select(o => o.OrderNumber));
        Assert.Equal(3, orders.Length);
    }

    [Fact]
    public void Original_brief_catalog_has_exact_fifteen_types_and_source_authority()
    {
        var snapshot = Plant();
        var options = Evaluate(snapshot, [Order(101)], null);
        Assert.Equal(new[] { "Overtime", "Additional Shift", "Alternate Qualified Personnel", "Alternate Machine", "Alternate Work Center",
            "Approved Alternate Material", "Existing WIP", "Remnant Transformation", "Split Production", "Resequencing", "Expedited Procurement",
            "Expedited Material Movement", "Outside Processing", "Maintenance Timing Change", "Partial Shipment" }, options.Select(o => o.Title));
        Assert.All(options, o => { Assert.NotEmpty(o.Consequences); Assert.NotEmpty(o.Evidence); Assert.NotEmpty(o.SourceAuthority); });
        Assert.All(options.Where(o => o.Id != "alternate-machine"), o => { Assert.False(o.Feasible); Assert.Equal("RequiresInputs", o.Status); Assert.NotEmpty(o.RequiredInputs); });
    }

    [Fact]
    public void Selected_resequence_reprojects_displaced_commitment_without_changing_order_inputs()
    {
        var snapshot = Plant();
        var first = Order(101) with { Priority = 1 };
        var second = Order(102) with { Priority = 5 };
        var option = Select(Evaluate(snapshot, [first, second], new("resequence", OrderNumber: 102, Priority: 1)), "resequence");
        Assert.True(option.Feasible);
        Assert.Equal("Feasible", option.Status);
        // Equal priority still dispatches by order number; use a lower baseline priority on 101 for a meaningful reorder.
        var displaced = Select(Evaluate(snapshot, [first with { Priority = 2 }, second], new("resequence", OrderNumber: 102, Priority: 1)), "resequence");
        Assert.Contains(displaced.Projections, p => p.OrderNumber == 101 && p.ShiftHours > 0);
        Assert.Contains(displaced.Projections, p => p.OrderNumber == 102 && p.ShiftHours < 0);
        Assert.Equal(5, second.Priority);
        Assert.Contains(displaced.Consequences, s => s.Contains("Order 101", StringComparison.Ordinal));
    }

    [Fact]
    public void Complete_selected_calendar_trial_is_feasible_but_down_equipment_remains_infeasible()
    {
        var snapshot = Plant();
        var people = snapshot.People.Select(p => p.PersonId).ToArray();
        var choice = new RecoverySelection("overtime", OrderNumber: 101, WorkCenterCode: "SLIT-WC", ShiftCode: "DAY",
            AdditionalMinutes: 120, ProposedShiftCode: "OVERTIME", PersonIds: people);
        var option = Select(Evaluate(snapshot, [Order(101)], choice), "overtime");
        Assert.True(option.Feasible);
        Assert.Contains(option.SourceAuthority, a => a.Command == "SetWorkerShift" && a.Permission == Permissions.WorkforceManage);
        Assert.DoesNotContain(snapshot.Shifts, s => s.Code == "OVERTIME");
        var blocked = snapshot with { Downtime = [new DowntimeWindow(7, "SLITTER-A", DowntimeKind.Unplanned, Now, null, "Recorded failure")] };
        var unavailable = Select(Evaluate(blocked, [Order(101)], choice), "overtime");
        Assert.False(unavailable.Feasible);
        Assert.Equal("Infeasible", unavailable.Status);
        Assert.NotEmpty(unavailable.Constraints);
    }

    [Fact]
    public void Additional_shift_needs_selected_workers_and_cannot_create_qualification()
    {
        var snapshot = Plant();
        snapshot = snapshot with { Shifts = [.. snapshot.Shifts, new ShiftInfo("EXTRA", "PLANT1", "Additional coverage", new TimeOnly(10, 0), 480, [1, 2, 3, 4, 5])] };
        var choice = new RecoverySelection("additional-shift", OrderNumber: 101, WorkCenterCode: "SLIT-WC", ShiftCode: "EXTRA", PersonIds: snapshot.People.Select(p => p.PersonId).ToArray());
        Assert.True(Select(Evaluate(snapshot, [Order(101)], choice), choice.OptionId).Feasible);
        var unqualified = snapshot with { People = snapshot.People.Select(p => p with { ValidQualifications = [] }).ToArray() };
        var option = Select(Evaluate(unqualified, [Order(101)], choice), choice.OptionId);
        Assert.Equal("Infeasible", option.Status);
        Assert.False(option.Feasible);
        Assert.Equal("RequiresInputs", Select(Evaluate(snapshot, [Order(101)], choice with { PersonIds = null }), choice.OptionId).Status);
    }

    [Theory]
    [InlineData("alternate-work-center")]
    [InlineData("split-production")]
    public void Complete_unsupported_source_proposal_is_infeasible_with_explicit_evidence_gap(string id)
    {
        var choice = new RecoverySelection(id, OrderNumber: 101, Sequence: 10, WorkCenterCode: "PAINT-WC", SplitQuantities: [4m, 6m]);
        var option = Select(Evaluate(Plant(), [Order(101)], choice), id);
        Assert.False(option.Feasible);
        Assert.Equal("Infeasible", option.Status);
        Assert.Empty(option.RequiredInputs);
        Assert.Contains(option.Constraints, s => s.Contains("source", StringComparison.Ordinal));
        Assert.NotEmpty(option.Consequences);
        Assert.NotEmpty(option.SourceAuthority);
    }

    [Fact]
    public void Selected_personnel_trial_checks_recorded_presence_and_open_labor_before_reallocation()
    {
        var snapshot = Plant();
        var worker = snapshot.People[0];
        var choice = new RecoverySelection("alternate-personnel", OrderNumber: 101, Sequence: 10, PersonId: worker.PersonId, MachineCode: "SLITTER-A");
        var option = Select(Evaluate(snapshot, [Order(101)], choice), choice.OptionId);
        Assert.True(option.Feasible);
        Assert.Contains(option.Evidence, e => e.Contains("Reallocation", StringComparison.Ordinal));
        var absent = snapshot with { People = snapshot.People.Select(p => p.PersonId == worker.PersonId ? p with { Presence = null } : p).ToArray() };
        Assert.Equal("Infeasible", Select(Evaluate(absent, [Order(101)], choice), choice.OptionId).Status);
        var busy = snapshot with { People = snapshot.People.Select(p => p.PersonId == worker.PersonId ? p with { ActiveOperation = "999/10" } : p).ToArray() };
        var blocked = Select(Evaluate(busy, [Order(101)], choice), choice.OptionId);
        Assert.Equal("Infeasible", blocked.Status);
        Assert.Contains(blocked.Constraints, e => e.Contains("open direct labor", StringComparison.Ordinal));
        Assert.Equal(StageRoll, worker.Assignment!.ActivityCode);
    }

    [Fact]
    public void Approved_alternate_trial_uses_released_directional_conversion_and_blocks_a_proposal()
    {
        var snapshot = Plant();
        var order = Order(101) with { Materials = [new OrderMaterialInfo(1, "COIL", "A", 5m, "EA")] };
        var alternate = new AlternateInfo(7, "COIL", "ALT-COIL", AlternateKind.Equivalent, AlternateStatus.Approved, 2m,
            null, null, null, null, null, null, null, null, null, null, null, null, "person:engineer", Now.AddDays(-3),
            "person:approver", Now.AddDays(-2), "Released equivalent", null, null, null);
        var position = new StockPosition("ALT-COIL", "ALT-1", "STORES", "STORES", LocationKind.Stores, StockStatus.Released, 10m, Custody.Company, null, true, null, null);
        var facts = Facts([order]) with { Alternates = [alternate], Positions = [position], Locations = [new LocationInfo("STORES", "PLANT1", "Stores", LocationKind.Stores, true)],
            Material = Facts([order]).Material with { Stock = [new MaterialStock("ALT-COIL", 10m)] },
            SourceChecks = new Dictionary<string, RecoverySourceCheck> { ["approved-alternate"] = new(true, [], ["Source issue preparation passed"]) } };
        var choice = new RecoverySelection("approved-alternate", OrderNumber: 101, PartNumber: "ALT-COIL", PrimaryPartNumber: "COIL", AlternateId: 7,
            LotNumber: "ALT-1", FromLocation: "STORES", Quantity: 10m);
        var feasible = Select(Evaluate(snapshot, [order], choice, facts), choice.OptionId);
        Assert.True(feasible.Feasible);
        Assert.Contains(feasible.Evidence, e => e.Contains("as 5 COIL", StringComparison.Ordinal));
        var proposed = facts with { Alternates = [alternate with { Status = AlternateStatus.Proposed }] };
        var blocked = Select(Evaluate(snapshot, [order], choice, proposed), choice.OptionId);
        Assert.Equal("Infeasible", blocked.Status);
        Assert.Contains(blocked.Constraints, e => e.Contains("engineering approval", StringComparison.Ordinal));
    }

    [Fact]
    public void Material_movement_trial_fills_point_of_use_without_creating_or_issuing_quantity()
    {
        var snapshot = Plant();
        var order = Order(101) with { Materials = [new OrderMaterialInfo(1, "COIL", "A", 5m, "EA")] };
        var facts = Facts([order]);
        var position = new StockPosition("COIL", "COIL-1", "STORES", "STORES", LocationKind.Stores, StockStatus.Released, 5m, Custody.Company, null, true, null, null);
        facts = facts with { Positions = [position], Locations = [new LocationInfo("STORES", "PLANT1", "Stores", LocationKind.Stores, true),
                new LocationInfo("POINT", "PLANT1", "Machine staging", LocationKind.Staging, true, WorkCenterCode: "SLIT-WC")],
            Material = facts.Material with { Stock = [new MaterialStock("COIL", 5m)] },
            OrderChecks = facts.OrderChecks.ToDictionary(p => p.Key, p => p.Value with { PointOfUse = new OrderPointOfUse([], new HashSet<string> { "SLIT-WC" }) }),
            SourceChecks = new Dictionary<string, RecoverySourceCheck> { ["expedited-material-movement"] = new(true, [], ["Source transfer preparation passed"]) } };
        var choice = new RecoverySelection("expedited-material-movement", OrderNumber: 101, PartNumber: "COIL", LotNumber: "COIL-1", FromLocation: "STORES", ToLocation: "POINT", Quantity: 5m);
        var feasible = Select(Evaluate(snapshot, [order], choice, facts), choice.OptionId);
        Assert.True(feasible.Feasible);
        Assert.Contains(feasible.Evidence, e => e.Contains("PointOfUse", StringComparison.Ordinal));
        Assert.Empty(facts.Material.Issued);
        Assert.Empty(facts.OrderChecks[101].PointOfUse.AtPointOfUse);
        var invalid = Select(Evaluate(snapshot, [order], choice with { Quantity = 6m }, facts), choice.OptionId);
        Assert.Equal("Infeasible", invalid.Status);
    }

    [Fact]
    public void Selected_wip_issue_uses_stock_once_and_reservations_or_expiry_block_it()
    {
        var snapshot = Plant();
        var order = Order(101) with { Materials = [new OrderMaterialInfo(1, "WIP-COIL", "A", 5m, "EA")] };
        var choice = new RecoverySelection("existing-wip", OrderNumber: 101, PartNumber: "WIP-COIL", LotNumber: "WIP-1", FromLocation: "WIP", Quantity: 5m);
        var position = new StockPosition("WIP-COIL", "WIP-1", "WIP", "WIP", LocationKind.WipStorage, StockStatus.Released, 5m, Custody.Company, null, true, null, null);
        var facts = Facts([order]) with { Positions = [position], Locations = [new LocationInfo("WIP", "PLANT1", "WIP", LocationKind.WipStorage, true)],
            Material = Facts([order]).Material with { Stock = [new MaterialStock("WIP-COIL", 5m)] },
            SourceChecks = new Dictionary<string, RecoverySourceCheck> { ["existing-wip"] = new(true, [], ["Source issue preparation passed"]) } };
        var option = Select(Evaluate(snapshot, [order], choice, facts), choice.OptionId);
        Assert.True(option.Feasible);
        Assert.Contains(option.Evidence, s => s.Contains("common pool", StringComparison.Ordinal) || s.Contains("shared pool", StringComparison.Ordinal));
        Assert.Equal(5m, facts.Material.Stock.Single().Available);
        var expired = Select(Evaluate(snapshot, [order], choice, facts with { Positions = [position with { Usable = false }] }), choice.OptionId);
        Assert.False(expired.Feasible);
        Assert.Equal("Infeasible", expired.Status);
        var reserved = facts with { Tasks = [new MaterialTask(1, MaterialTaskKind.Stage, 999, null, "WIP-COIL", "WIP-1", 3m, "WIP", "STAGE", MaterialTaskStatus.Allocated, null, null, "person:handler", Now)] };
        var reservation = Select(Evaluate(snapshot, [order], choice, reserved), choice.OptionId);
        Assert.Equal("Infeasible", reservation.Status);
        Assert.Contains(reservation.Constraints, s => s.Contains("unreserved", StringComparison.Ordinal));
    }

    [Fact]
    public void Procurement_trial_keeps_supply_quantity_and_shows_a_real_earlier_projection()
    {
        var snapshot = Plant();
        var order = Order(101) with { Materials = [new OrderMaterialInfo(1, "COIL", "A", 5m, "EA")] };
        var facts = Facts([order]);
        facts = facts with
        {
            Material = facts.Material with { Supplies = [new MaterialSupply("PO30/1", "COIL", 5m, new DateOnly(2026, 10, 7), "Approved supplier promise")] },
            PurchaseOrders = [new PurchaseOrderInfo(30, "Supplier", PurchaseOrderStatus.Approved, "person:buyer", Now.AddDays(-3), "person:manager", Now.AddDays(-2),
                [new PurchaseLineInfo(1, "COIL", 5m, "EA", new DateOnly(2026, 10, 7), PurchaseLineStatus.Open)], "SUP")],
        };
        var choice = new RecoverySelection("expedited-procurement", OrderNumber: 101, PurchaseOrder: 30, PurchaseOrderLine: 1, ProposedDate: new DateOnly(2026, 10, 2));
        var option = Select(Evaluate(snapshot, [order], choice, facts), choice.OptionId);
        Assert.True(option.Feasible);
        Assert.Contains(option.Projections, p => p.OrderNumber == 101 && p.ShiftHours < 0);
        Assert.Equal(new DateOnly(2026, 10, 7), facts.Material.Supplies.Single().DueDate);
        var invalid = Select(Evaluate(snapshot, [order], choice with { ProposedDate = new DateOnly(2026, 10, 8) }, facts), choice.OptionId);
        Assert.Equal("Infeasible", invalid.Status);
    }

    [Fact]
    public void Selected_maintenance_window_preserves_observed_failure_and_cannot_erase_source_downtime()
    {
        var snapshot = Plant() with { Downtime = [new DowntimeWindow(4, "SLITTER-A", DowntimeKind.Unplanned, Now, null, "Observed failure")] };
        var order = Order(101);
        var facts = Facts([order]) with { MaintenanceOrders = [new WorkOrderInfo(10, "SLITTER-A", WorkOrderKind.Corrective, null, "Repair", null, [],
            new DateOnly(2026, 10, 2), WorkOrderStatus.Open, null, null, 4, "person:tech", Now, null, null, null, null, null, null, null, null)] };
        var choice = new RecoverySelection("maintenance-timing", OrderNumber: 101, WorkOrderId: 10, StartsAt: Now.AddHours(1), EndsAt: Now.AddHours(2));
        var option = Select(Evaluate(snapshot, [order], choice, facts), choice.OptionId);
        Assert.Equal("Infeasible", option.Status);
        Assert.Contains(option.Constraints, s => s.Contains("recorded downtime", StringComparison.Ordinal));
        Assert.Null(snapshot.Downtime.Single().EndsAt);
    }

    [Fact]
    public void Source_action_verdicts_do_not_invent_delivery_benefits_and_missing_source_authority_blocks_them()
    {
        var snapshot = Plant();
        var order = Order(101);
        var facts = Facts([order]) with { SourceChecks = new Dictionary<string, RecoverySourceCheck>
        {
            ["outside-processing"] = new(true, [], ["Real dispatch gate passed"]),
            ["partial-shipment"] = new(false, ["ncr_open: source nonconformance blocks release"], []),
        } };
        var outside = Select(Evaluate(snapshot, [order], new("outside-processing", OrderNumber: 101, PartNumber: "COIL", LotNumber: "LOT", Quantity: 1,
            FromLocation: "STORES", ToLocation: "PROCESSOR", PurchaseOrder: 30, PurchaseOrderLine: 1, ProposedDate: new DateOnly(2026, 10, 3)), facts), "outside-processing");
        Assert.True(outside.Feasible);
        Assert.Contains(outside.Consequences, s => s.Contains("no order completion benefit is asserted", StringComparison.Ordinal));
        var partial = Select(Evaluate(snapshot, [order], new("partial-shipment", ShipmentId: 1), facts), "partial-shipment");
        Assert.Equal("Infeasible", partial.Status);
        Assert.Contains(partial.Constraints, s => s.Contains("ncr_open", StringComparison.Ordinal));
        var unauthorized = Actor with { EffectivePermissions = new HashSet<string> { Permissions.ScheduleRead, Permissions.ReadinessRead, Permissions.InventoryRead, Permissions.PurchasingRead } };
        Assert.Equal("NotAuthorized", Select(Evaluate(snapshot, [order], new("resequence", OrderNumber: 101, Priority: 1), facts, unauthorized), "resequence").Status);
    }

    [Fact]
    public void Supplier_delay_remains_binding_when_an_unrelated_resequence_trial_reallocates_material()
    {
        var snapshot = Plant();
        var order = Order(101) with { Materials = [new OrderMaterialInfo(1, "COIL", "A", 5m, "EA")] };
        var facts = Facts([order]);
        facts = facts with { Material = facts.Material with { Supplies = [new MaterialSupply("PO30/1", "COIL", 5m, new DateOnly(2026, 10, 2), "Recorded supplier promise")] } };
        var sourceMaterial = new MaterialEngine().Evaluate(facts.Material, new MaterialRequest(), Now).Output;
        var change = new ScenarioChange(ScenarioChangeKind.SupplierDelay, OrderNumber: order.OrderNumber, DelayDays: 14,
            RecoverySelections: [new RecoverySelection("resequence", OrderNumber: order.OrderNumber, Priority: 1)]);
        var privateMaterial = RecoverySourceFacts.ApplyScenarioMaterial(change, [order], sourceMaterial, facts.Material);
        var proposedMaterial = new MaterialEngine().Evaluate(privateMaterial, new MaterialRequest(), Now).Output;
        var baseline = new SchedulingEngine().Evaluate(snapshot, new ScheduleRequest(Now, Now.AddDays(14), [order], sourceMaterial), Now).Output;
        var proposed = new SchedulingEngine().Evaluate(snapshot, new ScheduleRequest(Now, Now.AddDays(14), [order], proposedMaterial), Now).Output;
        Assert.NotNull(baseline.Orders.Single().ProjectedCompletion);
        Assert.Null(proposed.Orders.Single().ProjectedCompletion);
        var option = Select(RecoveryOptionEvaluator.Evaluate(change, snapshot, [order], proposedMaterial,
            new Dictionary<long, OrderQuality>(), baseline, proposed, Actor, new ForgeAuthorizer(), facts with { Material = privateMaterial }), "resequence");
        Assert.Equal("Infeasible", option.Status);
        Assert.False(option.Feasible);
        Assert.Null(option.Projections.Single(p => p.OrderNumber == order.OrderNumber).ProposedCompletion);
        Assert.Equal(new DateOnly(2026, 10, 16), privateMaterial.Supplies.Single().DueDate);
        Assert.Equal(new DateOnly(2026, 10, 2), facts.Material.Supplies.Single().DueDate);
    }

    [Fact]
    public void Private_priority_and_unseen_queue_are_retained_when_a_different_order_is_resequenced()
    {
        var snapshot = Plant();
        var materials = new[] { new OrderMaterialInfo(1, "COIL", "A", 5m, "EA") };
        var first = Order(101) with { Priority = 3, Materials = materials };
        var second = Order(102) with { Priority = 9, Materials = materials };
        var facts = Facts([first, second]);
        facts = facts with { Material = facts.Material with
        {
            Stock = [new MaterialStock("COIL", 5m)],
            Orders = [.. facts.Material.Orders, new MaterialOrder(999, 4, first.DueDate, materials)],
        } };
        var sourceMaterial = new MaterialEngine().Evaluate(facts.Material, new MaterialRequest(), Now).Output;
        var privateOrders = new[] { first, second with { Priority = 1 } };
        var change = new ScenarioChange(ScenarioChangeKind.PriorityChange, OrderNumber: second.OrderNumber, Priority: 1,
            RecoverySelections: [new RecoverySelection("resequence", OrderNumber: first.OrderNumber, Priority: 2)]);
        var privateMaterial = RecoverySourceFacts.ApplyScenarioMaterial(change, privateOrders, sourceMaterial, facts.Material);
        var proposedMaterial = new MaterialEngine().Evaluate(privateMaterial, new MaterialRequest(), Now).Output;
        Assert.Equal(1, privateMaterial.Orders.Single(o => o.OrderNumber == second.OrderNumber).Priority);
        Assert.Equal(4, privateMaterial.Orders.Single(o => o.OrderNumber == 999).Priority);
        Assert.Equal(MaterialState.Ready, proposedMaterial.Single(m => m.OrderNumber == second.OrderNumber).State);
        Assert.Equal(MaterialState.Shortfall, proposedMaterial.Single(m => m.OrderNumber == first.OrderNumber).State);
        var baseline = new SchedulingEngine().Evaluate(snapshot, new ScheduleRequest(Now, Now.AddDays(14), [first, second], sourceMaterial), Now).Output;
        var proposed = new SchedulingEngine().Evaluate(snapshot, new ScheduleRequest(Now, Now.AddDays(14), privateOrders, proposedMaterial), Now).Output;
        var option = Select(RecoveryOptionEvaluator.Evaluate(change, snapshot, privateOrders, proposedMaterial,
            new Dictionary<long, OrderQuality>(), baseline, proposed, Actor, new ForgeAuthorizer(), facts with { Material = privateMaterial }), "resequence");
        Assert.Equal("Infeasible", option.Status);
        Assert.Null(option.Projections.Single(p => p.OrderNumber == first.OrderNumber).ProposedCompletion);
        Assert.DoesNotContain(option.Projections, p => p.OrderNumber == 999);
        Assert.Equal(9, facts.Material.Orders.Single(o => o.OrderNumber == second.OrderNumber).Priority);
    }

    private static SiteSnapshot Plant()
    {
        var factory = Slitting().SlittingAreaRule();
        factory.AddWorker("Operator", "SLIT-WC", SlitII, FirstAid);
        factory.AddWorker("Responder", "SLIT-WC", SlitII);
        return factory.Build();
    }

    private static OrderInfo Order(long number) => new(number, "ROLL-100", "A", 10m, new DateOnly(2026, 10, 9), 3,
        OrderStatus.Released, null, null, Now, [new OrderOperationInfo(10, "Slit", "SLIT-WC", RunSlitter, ["SLITTER-A"], [], 120m, 1, OperationStatus.Pending)], []);

    private static RecoverySourceFacts Facts(IReadOnlyList<OrderInfo> orders) => new(
        new MaterialSnapshot(new DateOnly(2026, 10, 1), [], [], [], orders.Select(o => new MaterialOrder(o.OrderNumber, o.Priority, o.DueDate, o.Materials)).ToArray()),
        [], [], [], [], [], [], [], [], orders.ToDictionary(o => o.OrderNumber, o => new RecoveryOrderChecks(
            new RevisionInfo(o.PartNumber, o.Revision, RevisionStatus.Released, Now.AddDays(-30), null),
            new OrderDocumentation(o.OrderNumber, o.PartNumber, o.Revision, ItemClass.Make, false, [], "No enforced documentation"),
            new OrderTraining(TrainingPolicy.Off, []), new OrderPointOfUse([], new HashSet<string>()))), new Dictionary<string, RecoverySourceCheck>());

    private static IReadOnlyList<RecoveryOption> Evaluate(SiteSnapshot snapshot, IReadOnlyList<OrderInfo> orders, RecoverySelection? choice,
        RecoverySourceFacts? facts = null, ForgePrincipal? actor = null)
    {
        facts ??= Facts(orders);
        var material = new MaterialEngine().Evaluate(facts.Material, new MaterialRequest(), Now).Output;
        var quality = new Dictionary<long, OrderQuality>();
        var schedule = new SchedulingEngine().Evaluate(snapshot, new ScheduleRequest(Now, Now.AddDays(14), orders, material, quality), Now).Output;
        var change = new ScenarioChange(ScenarioChangeKind.MachineUnavailable, MachineCode: "SLITTER-B", OrderNumber: orders[0].OrderNumber,
            StartsAt: Now, DurationHours: 1, RecoverySelections: choice is null ? null : [choice]);
        return RecoveryOptionEvaluator.Evaluate(change, snapshot, orders, material, quality, schedule, schedule, actor ?? Actor, new ForgeAuthorizer(), facts);
    }

    private static RecoveryOption Select(IReadOnlyList<RecoveryOption> options, string id) => options.Single(o => o.Id == id);
}
