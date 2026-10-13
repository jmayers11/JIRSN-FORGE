using Forge.Execution.Contracts;
using Forge.Kernel.Engines;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Planning.Engines;
using Forge.Procurement.Contracts;
using Forge.Quality.Contracts;

namespace Forge.Planning.Tests;

public sealed class MaterialSourceVisibilityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
    private static readonly Guid Private = Guid.Parse("018f0000-0000-7000-8000-000000004321");
    private static readonly ForgePrincipal Viewer = new(new PersonId(Guid.NewGuid()), "Viewer", true,
        new HashSet<string> { Roles.Planner, Roles.Buyer }, new HashSet<ProgramId>(), DataMarking.None);
    private static OrderInfo Order(long number, Guid? program = null) => new(number, "COMP", "A", 6m, new DateOnly(2026, 10, 3), 3,
        OrderStatus.Released, null, program, Now, [], []);

    [Fact]
    public void Material_projection_keeps_visible_consumer_evidence_true_allocation_and_canonical_fingerprints()
    {
        var headers = new[] { Order(101), Order(202, Private) };
        var inputs = new MaterialSnapshot(new DateOnly(2026, 10, 1), [new("COMP", 4m)], [],
            [new("WO202", "COMP", 6m, new DateOnly(2026, 10, 3), "Order 202 (Planned)")],
            [new(101, 3, headers[0].DueDate, [new(1, "COMP", "A", 10m, "EA")]), new(202, 9, headers[1].DueDate, [new(1, "PRIVATE-PART", "A", 5m, "EA")])]);
        var canonical = EngineRunner.Run(new MaterialEngine(), inputs, new MaterialRequest(), Now);
        var bytes = CanonicalJson.Serialize(canonical);
        var access = new MaterialSourceVisibility(Viewer, headers);
        var evaluation = access.ForEvaluation(canonical.Output);
        Assert.Equal(2, evaluation.Count);
        var evaluatedAllocations = evaluation.Single(o => o.OrderNumber == 101).Lines.Single().Allocations;
        Assert.Equal("WO202", evaluatedAllocations[evaluatedAllocations.Count - 1].Source);
        Assert.Equal(MaterialState.Expected, evaluation[0].State);
        Assert.Equal(new DateOnly(2026, 10, 3), evaluation[0].ReadyOn);
        foreach (var reference in new[] { "Order 202", "WO202", "order:202" })
            Assert.DoesNotContain(reference, evaluation[0].Summary, StringComparison.Ordinal);
        var display = access.Material(canonical);
        var row = Assert.Single(display.Output);
        Assert.Equal(101, row.OrderNumber);
        var displayedAllocations = row.Lines.Single().Allocations;
        var supplied = displayedAllocations[displayedAllocations.Count - 1];
        Assert.Equal((MaterialSourceVisibility.Restricted, 6m, new DateOnly(2026, 10, 3)), (supplied.Source, supplied.Quantity, supplied.DueDate));
        var node = Assert.Single(display.Evidence.Children);
        Assert.StartsWith("Order 101:", node.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-PART", CanonicalJson.Serialize(display), StringComparison.Ordinal);
        Assert.Equal(canonical.InputFingerprint, display.InputFingerprint);
        Assert.Equal(canonical.OutputFingerprint, display.OutputFingerprint);
        Assert.Equal(bytes, CanonicalJson.Serialize(canonical));
        Assert.Equal("1 released order(s): 0 ready, 1 waiting for receipts, 0 short.", display.Evidence.Detail);
        var readyRun = EngineRunner.Run(new MaterialEngine(), inputs with { Stock = [new("COMP", 10m)] }, new MaterialRequest(), Now);
        Assert.Equal(EvidenceOutcome.Fail, readyRun.Evidence.Outcome); // The inaccessible owner is still short internally.
        var readyDisplay = access.Material(readyRun);
        Assert.Equal(EvidenceOutcome.Pass, readyDisplay.Evidence.Outcome);
        Assert.Equal("1 released order(s): 1 ready, 0 waiting for receipts, 0 short.", readyDisplay.Evidence.Detail);
        Assert.Equal(readyRun.InputFingerprint, readyDisplay.InputFingerprint);
        Assert.Equal(readyRun.OutputFingerprint, readyDisplay.OutputFingerprint);
        Assert.Equal("COMP-202 quantity 202, due 2026-10-03", access.Text("COMP-202 quantity 202, due 2026-10-03"));
        Assert.Equal(MaterialSourceVisibility.Restricted, access.Text("order:202"));
        Assert.Equal(MaterialSourceVisibility.Restricted, access.Text("WO202/PRIVATE-PART"));
    }

    [Fact]
    public void Purchase_source_projection_uses_all_actual_program_resources_and_known_link_forms_only()
    {
        var po = new PurchaseOrderInfo(77, "Private supplier (division)", PurchaseOrderStatus.Approved, "buyer", Now, "manager", Now,
            [new(1, "COMP", 6m, "EA", new DateOnly(2026, 10, 3), PurchaseLineStatus.Open)]);
        var scopes = new Dictionary<long, IReadOnlyList<ResourceRef>> { [77] = [new("PurchaseOrder", "77", new ProgramId(Private))] };
        var hidden = new MaterialSourceVisibility(Viewer, [Order(101)], [po], scopes);
        foreach (var source in new[] { "PO77/1", "po:77", "purchase-order:77", "PO 77 line 1 (Private supplier (division))" })
            Assert.Equal(MaterialSourceVisibility.Restricted, hidden.Text(source));
        var granted = new MaterialSourceVisibility(Viewer with { Programs = new HashSet<ProgramId> { new(Private) } }, [Order(101)], [po], scopes);
        Assert.True(granted.CanSeePurchaseOrder(77));
        Assert.Equal("po:77", granted.Text("po:77"));
        Assert.Equal("purchase-order:77", granted.Text("purchase-order:77"));
        Assert.Equal("Quantity 77 COMP-77", hidden.Text("Quantity 77 COMP-77"));
        Assert.Equal(MaterialSourceVisibility.Restricted, hidden.Text("PO999/1"));
        var row = hidden.Row(new Forge.Planning.Reporting.ReportRow(["po:77", 77m], ["po:77"]));
        Assert.Equal(MaterialSourceVisibility.Restricted, row.Cells[0]);
        Assert.Equal(77m, row.Cells[1]);
        Assert.Equal(MaterialSourceVisibility.Restricted, Assert.Single(row.Trace));
    }

    [Fact]
    public void Explicit_private_purchase_trial_cannot_reveal_its_recorded_promise_and_authorized_trial_carries_actual_program_scope()
    {
        var order = Order(101) with
        {
            Materials = [new(1, "COMP", "A", 6m, "EA")],
            Operations = [new(10, "Assemble", "ASSY-WC", TestFactory.Assemble, [], [], 60m, 1, OperationStatus.Pending)],
        };
        var po = new PurchaseOrderInfo(77, "Private supplier", PurchaseOrderStatus.Approved, "buyer", Now, "manager", Now,
            [new(1, "COMP", 6m, "EA", new DateOnly(2026, 10, 3), PurchaseLineStatus.Open)]);
        var scopes = new Dictionary<long, IReadOnlyList<ResourceRef>> { [77] = [new("PurchaseOrder", "77", new ProgramId(Private))] };
        var input = new MaterialSnapshot(new DateOnly(2026, 10, 1), [], [], [new("PO77/1", "COMP", 6m, new DateOnly(2026, 10, 3), "PO 77 line 1 (Private supplier)")],
            [new(101, 3, order.DueDate, order.Materials)]);
        var material = new MaterialEngine().Evaluate(input, new MaterialRequest(), Now).Output;
        var option = new RecoveryOption("expedited-procurement", "Expedited Procurement", false, "", [], "#/procurement", Permissions.PurchasingManage,
            "RequiresInputs", [101], [], [], [], [], []);
        var selection = new RecoverySelection("expedited-procurement", OrderNumber: 101, PurchaseOrder: 77, PurchaseOrderLine: 1, ProposedDate: new DateOnly(2026, 10, 2));
        var factory = TestFactory.Slitting();
        factory.AddWorker("Purchase recovery operator", "ASSY-WC");
        var snapshot = factory.Build();
        var schedule = new SchedulingEngine().Evaluate(snapshot, new ScheduleRequest(Now, Now.AddDays(14), [order], material), Now).Output;
        var hidden = new MaterialSourceVisibility(Viewer, [order], [po], scopes);
        var facts = new RecoverySourceFacts(input, [], [], [], [], [], [], [po], [], new Dictionary<long, RecoveryOrderChecks>(),
            new Dictionary<string, RecoverySourceCheck>(), hidden);
        var blocked = RecoveryTrialEvaluator.Evaluate(option, selection, snapshot, [order], material, new Dictionary<long, OrderQuality>(), schedule, facts, Viewer, new ForgeAuthorizer());
        Assert.False(blocked.Feasible);
        Assert.Contains(blocked.Constraints, c => c.Contains("unavailable", StringComparison.Ordinal));
        Assert.DoesNotContain("Private supplier", CanonicalJson.Serialize(blocked), StringComparison.Ordinal);
        Assert.DoesNotContain("2026-10-03", CanonicalJson.Serialize(blocked), StringComparison.Ordinal);
        Assert.False(Assert.Single(blocked.SourceAuthority).Permitted);
        Assert.Null(Assert.Single(blocked.SourceAuthority).ResourceId);
        var allowedViewer = Viewer with { Programs = new HashSet<ProgramId> { new(Private) } };
        var allowedFacts = facts with { Visibility = new MaterialSourceVisibility(allowedViewer, [order], [po], scopes) };
        var allowed = RecoveryTrialEvaluator.Evaluate(option, selection, snapshot, [order], material, new Dictionary<long, OrderQuality>(), schedule, allowedFacts, allowedViewer, new ForgeAuthorizer());
        var actual = Assert.Single(allowed.SourceAuthority);
        Assert.Equal(Private, actual.ProgramId);
        Assert.True(actual.Permitted);
        Assert.Contains(allowed.Evidence, e => e.Contains("2026-10-03", StringComparison.Ordinal));
        Assert.Equal(new DateOnly(2026, 10, 3), input.Supplies.Single().DueDate);
        Assert.Equal("PO77/1", material.Single().Lines.Single().Allocations.Single().Source);
    }

    [Fact]
    public void Protected_mrp_context_and_substitution_details_are_display_only_and_do_not_change_netting()
    {
        var headers = new[] { Order(101), Order(202, Private) };
        var requirement = new MrpRequirement("WO202/PRIVATE-PART", "COMP", 5m, new DateOnly(2026, 10, 3), 1, "OrderMaterial",
            "Restricted program work Planned, rev SECRET line 1", new("PRIVATE-PART", 1, Forge.Engineering.Contracts.SubstitutionPolicy.Automatic, Private, "Private customer"));
        MrpItem Buy(string part) => new(part, part, "EA", Forge.Engineering.Contracts.MakeBuy.Buy, 1, 0m, Forge.Engineering.Contracts.LotPolicy.LotForLot, 0m, 1m);
        var relationship = new Forge.Engineering.Contracts.AlternateInfo(444, "COMP", "PRIVATE-ALT", Forge.Engineering.Contracts.AlternateKind.Equivalent,
            Forge.Engineering.Contracts.AlternateStatus.Approved, 1m, Private, "Private customer", "PRIVATE-PART", null, null, null, null, null, null,
            null, null, null, "person:engineer", Now.AddDays(-1), "person:approver", Now.AddDays(-1), null, null, null, null);
        var inputs = new MrpSnapshot(new DateOnly(2026, 10, 1), [Buy("COMP"), Buy("PRIVATE-ALT")], [],
            [new("COMP", 0m, 0m, 0m), new("PRIVATE-ALT", 10m, 0m, 0m)], [requirement], [], [relationship]);
        var run = EngineRunner.Run(new MrpEngine(), inputs, new MrpRequest(), Now);
        Assert.Equal(5m, Assert.Single(run.Output.Substitutions!).PrimaryQuantity);
        var item = run.Output.Items.Single(i => i.PartNumber == "COMP");
        var bytes = CanonicalJson.Serialize(run);
        var projected = new MaterialSourceVisibility(Viewer, headers).Mrp(run);
        var anonymous = projected.Output.Items.Single(i => i.PartNumber == "COMP").Requirements.Single();
        Assert.Null(anonymous.Line);
        Assert.Equal(MaterialSourceVisibility.Restricted, anonymous.Key);
        Assert.Equal(MaterialSourceVisibility.Restricted, anonymous.Description);
        Assert.Empty(projected.Output.Substitutions!);
        Assert.Equal(item.Buckets, projected.Output.Items.Single(i => i.PartNumber == "COMP").Buckets);
        Assert.Equal(5m, projected.Output.Items.Single(i => i.PartNumber == "COMP").Buckets.Sum(b => b.Substituted));
        Assert.DoesNotContain("PRIVATE-PART", CanonicalJson.Serialize(projected), StringComparison.Ordinal);
        Assert.DoesNotContain("Private customer", CanonicalJson.Serialize(projected), StringComparison.Ordinal);
        Assert.Equal(run.InputFingerprint, projected.InputFingerprint);
        Assert.Equal(run.OutputFingerprint, projected.OutputFingerprint);
        Assert.Equal(bytes, CanonicalJson.Serialize(run));
        var approvalInputs = inputs with { Requirements = [requirement with { Line = requirement.Line! with { Policy = Forge.Engineering.Contracts.SubstitutionPolicy.Suggest } }] };
        var approvalRun = EngineRunner.Run(new MrpEngine(), approvalInputs, new MrpRequest(), Now);
        var suggested = Assert.Single(approvalRun.Output.Exceptions, e => e.Kind == "AlternateSuggested");
        Assert.Contains("alternate 444", suggested.Detail, StringComparison.Ordinal);
        var approvalBytes = CanonicalJson.Serialize(approvalRun);
        var approvalDisplay = new MaterialSourceVisibility(Viewer, headers).Mrp(approvalRun);
        var anonymousSuggestion = Assert.Single(approvalDisplay.Output.Exceptions, e => e.Kind == "AlternateSuggested");
        Assert.Equal("COMP", anonymousSuggestion.PartNumber);
        Assert.Equal(MaterialSourceVisibility.Restricted + " requires source approval.", anonymousSuggestion.Detail);
        Assert.Equal(approvalRun.Output.PlannedOrders.Sum(o => o.Quantity), approvalDisplay.Output.PlannedOrders.Sum(o => o.Quantity));
        Assert.Equal(5m, approvalDisplay.Output.PlannedOrders.Sum(o => o.Quantity));
        Assert.Equal(approvalRun.InputFingerprint, approvalDisplay.InputFingerprint);
        Assert.Equal(approvalRun.OutputFingerprint, approvalDisplay.OutputFingerprint);
        Assert.Equal(approvalBytes, CanonicalJson.Serialize(approvalRun));
    }
}
