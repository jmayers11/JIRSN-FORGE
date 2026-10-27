using Forge.Kernel.Holds;
using Forge.Planning.Engines;

namespace Forge.Planning.Tests;

public sealed class OperationalEvidenceTests
{
    [Fact]
    public void Same_order_context_does_not_merge_independent_causes()
    {
        var firstCause = new OperationalRecordId("execution", "RootCause", "41");
        var secondCause = new OperationalRecordId("execution", "RootCause", "42");
        var signals = new[]
        {
            Signal("material-risk", firstCause, OperationalCauseState.Confirmed, "1001"),
            Signal("quality-ncr", secondCause, OperationalCauseState.Confirmed, "1001"),
        };

        var associations = OperationalEvidenceBuilder.Associations(signals);

        var association = Assert.Single(associations);
        Assert.Equal(OperationalEvidenceRelation.SameOrderContext, association.Relationship);
        Assert.Equal("1001", association.ScopeId);
        Assert.False(association.CommonCauseProven);
        Assert.Equal(2, association.SignalIds.Count);
    }

    [Fact]
    public void Same_machine_context_does_not_merge_independent_signals()
    {
        var machine = new OperationalRecordId("maintenance", "Downtime", "17");
        var signals = new[]
        {
            new OperationalEvidenceSignal("down-1", "MachineDowntime", OperationalEvidenceState.Active, machine, null,
                OperationalCauseState.Unattributed, null, null, null, null,
                [new("Machine", "SL-01", OperationalEvidenceRelation.ExplicitSourceLink, true)]),
            new OperationalEvidenceSignal("down-2", "ProductionStoppage", OperationalEvidenceState.Open, machine, null,
                OperationalCauseState.Unattributed, null, null, null, null,
                [new("Machine", "SL-01", OperationalEvidenceRelation.ExplicitSourceLink, true)]),
        };

        var association = Assert.Single(OperationalEvidenceBuilder.Associations(signals));

        Assert.Equal(OperationalEvidenceRelation.SameMachineContext, association.Relationship);
        Assert.False(association.CommonCauseProven);
    }

    [Fact]
    public void Same_part_context_does_not_merge_independent_signals()
    {
        OperationalEvidenceSignal ForPart(string id, string cause) => new(id, "Test", OperationalEvidenceState.Active,
            new OperationalRecordId("test", "Record", cause), null, OperationalCauseState.Unattributed, null, null, null, null,
            [new("Part", "COMP-1", OperationalEvidenceRelation.CalculatedPartRequirement, true)]);

        var association = Assert.Single(OperationalEvidenceBuilder.Associations([ForPart("a", "1"), ForPart("b", "2")]));

        Assert.Equal(OperationalEvidenceRelation.SamePartContext, association.Relationship);
        Assert.False(association.CommonCauseProven);
    }

    [Fact]
    public void Only_the_same_confirmed_cause_identifier_creates_a_proven_cause_group()
    {
        var cause = new OperationalRecordId("execution", "RootCause", "51");
        var signals = new[]
        {
            Signal("stoppage", cause, OperationalCauseState.Confirmed, "1001"),
            Signal("operation-block", cause, OperationalCauseState.Confirmed, "1002"),
            Signal("proposal", cause, OperationalCauseState.Proposed, "1003"),
        };

        var causeGroup = Assert.Single(OperationalEvidenceBuilder.Associations(signals),
            association => association.Relationship == OperationalEvidenceRelation.SameCauseIdentifier);

        Assert.Equal(cause.Key, causeGroup.ScopeId);
        Assert.False(causeGroup.CommonCauseProven);
    }

    [Fact]
    public void A_cause_group_is_proven_only_when_every_member_is_confirmed()
    {
        var cause = new OperationalRecordId("execution", "RootCause", "61");
        var confirmed = OperationalEvidenceBuilder.Associations(new[]
        {
            Signal("a", cause, OperationalCauseState.Confirmed, "1001"),
            Signal("b", cause, OperationalCauseState.Confirmed, "1002"),
        });

        Assert.True(Assert.Single(confirmed).CommonCauseProven);
    }

    [Fact]
    public void The_same_allocated_supply_record_is_linked_without_claiming_a_proven_root_cause()
    {
        var supply = new OperationalRecordId("procurement", "PurchaseOrderLine", "512/3");
        OperationalEvidenceSignal WithSupply(string id, string order) => new(id, "MaterialRisk", OperationalEvidenceState.LateSupply,
            null, null, OperationalCauseState.Unattributed, null, null, null, null,
            [new("Order", order, OperationalEvidenceRelation.ExplicitOrderLink, true),
             new("Supply", supply.Key, OperationalEvidenceRelation.AllocatedSupply, true, supply)]);

        var association = Assert.Single(OperationalEvidenceBuilder.Associations(
            [WithSupply("risk-a", "1001"), WithSupply("risk-b", "1002")]));

        Assert.Equal(OperationalEvidenceRelation.SameSourceRecord, association.Relationship);
        Assert.Equal(supply.Key, association.ScopeId);
        Assert.False(association.CommonCauseProven);
    }

    [Fact]
    public void Only_late_receipts_at_the_ready_date_are_linked_as_late_supply_contributors()
    {
        var earlier = new DateOnly(2026, 11, 14);
        var readyOn = new DateOnly(2026, 11, 15);
        var status = new OrderMaterialStatus(1001, MaterialState.Expected, readyOn, false, "waiting", [
            new MaterialLineStatus("COMP-A", 2, 0, 2, 0, 0, readyOn,
            [new("PO1/1", "earlier", 1, earlier), new("PO1/2", "latest", 1, readyOn)]),
        ]);

        var linked = OperationalEvidenceService.RiskContributingAllocations(status, "LateSupply", new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 1));

        Assert.Equal("PO1/2", Assert.Single(linked).Source);
        Assert.Empty(OperationalEvidenceService.RiskContributingAllocations(status, "Shortfall", new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 1)));
    }

    [Theory]
    [InlineData("PO512/3", "procurement", "PurchaseOrderLine", "512/3")]
    [InlineData("WO2048", "execution", "ProductionOrder", "2048")]
    public void Material_supply_keys_map_to_typed_records(string key, string context, string type, string id)
    {
        Assert.True(OperationalEvidenceService.TrySupplyIdentifier(key, out var record));
        Assert.Equal((context, type, id), (record.Context, record.RecordType, record.RecordId));
    }

    [Theory]
    [InlineData("Stock")]
    [InlineData("Reserved")]
    [InlineData("POx/3")]
    [InlineData("WO")]
    public void Unidentified_material_sources_do_not_get_invented_record_ids(string key)
    {
        Assert.False(OperationalEvidenceService.TrySupplyIdentifier(key, out _));
    }

    [Fact]
    public void Downtime_projections_keep_the_source_record_type()
    {
        var recorded = OperationalEvidenceService.MaintenanceRecordIdentifier(new(17, "SL-01", Forge.Maintenance.Contracts.DowntimeKind.Unplanned,
            DateTimeOffset.UnixEpoch, null, "", SourceReference: "Downtime/17"));
        var maintenanceOrder = OperationalEvidenceService.MaintenanceRecordIdentifier(new(-24, "SL-01", Forge.Maintenance.Contracts.DowntimeKind.MaintenanceOverdue,
            DateTimeOffset.UnixEpoch, null, "", SourceReference: "WorkOrder/24"));

        Assert.Equal(new OperationalRecordId("maintenance", "Downtime", "17"), recorded);
        Assert.Equal(new OperationalRecordId("maintenance", "WorkOrder", "24"), maintenanceOrder);
    }

    [Fact]
    public void Exact_operation_and_component_holds_keep_their_recorded_target()
    {
        var order = new Forge.Execution.Contracts.OrderInfo(1001, "PARENT", "A", 10, new DateOnly(2026, 11, 12), 1,
            Forge.Execution.Contracts.OrderStatus.Released, null, null, null,
            [new Forge.Execution.Contracts.OrderOperationInfo(20, "Slit", "SLIT", "SLIT", ["SL-01"], [], 60, 1,
                Forge.Execution.Contracts.OperationStatus.Pending)],
            [new Forge.Execution.Contracts.OrderMaterialInfo(1, "COMP-1", null, 2, "EA")]);
        var hold = new Hold(7, HoldKind.Quality, HoldTargetType.Operation, "1001/20", null, "NCR 42", "Awaiting disposition",
            null, [], "Disposition the NCR", DateTimeOffset.UnixEpoch, "person:quality");
        var partHold = hold with { TargetType = HoldTargetType.Part, TargetId = "COMP-1" };

        var operationLink = Assert.Single(OperationalEvidenceService.HoldLinks(hold, [order]));
        var partLinks = OperationalEvidenceService.HoldLinks(partHold, [order]);

        Assert.Equal(("Operation", "1001/20"), (operationLink.TargetType, operationLink.TargetId));
        Assert.Contains(partLinks, link => link.TargetType == "Part" && link.TargetId == "COMP-1");
        Assert.Contains(partLinks, link => link.TargetType == "Order" && link.TargetId == "1001");
    }

    [Fact]
    public void Compatible_sources_must_share_the_same_nonempty_snapshot_token()
    {
        var snapshot = Guid.Parse("f84a180a-4e60-4ad6-9442-e17861b8485e");
        var included = new[]
        {
            new OperationalEvidenceSource("quality", OperationalEvidenceSourceStatus.Included, snapshot, "test"),
            new OperationalEvidenceSource("maintenance", OperationalEvidenceSourceStatus.Included, snapshot, "test"),
            new OperationalEvidenceSource("holds", OperationalEvidenceSourceStatus.NotAuthorized, null, "test"),
        };
        OperationalEvidenceSource[] mixed =
        [
            included[0],
            included[1] with { SnapshotId = Guid.Parse("ee601c78-9198-4bc8-9c11-910b0e94599c") },
        ];

        Assert.True(OperationalEvidenceBuilder.SourcesShareReadSnapshot(included));
        Assert.False(OperationalEvidenceBuilder.SourcesShareReadSnapshot(mixed));
    }

    private static OperationalEvidenceSignal Signal(string id, OperationalRecordId cause, OperationalCauseState state, string order) =>
        new(id, "Test", OperationalEvidenceState.Active, cause, cause, state, null, null, null, null,
            [new("Order", order, OperationalEvidenceRelation.ExplicitOrderLink, true)]);
}
