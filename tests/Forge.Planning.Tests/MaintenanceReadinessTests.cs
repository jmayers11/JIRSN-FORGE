using Forge.Engineering.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Engines;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Workforce.Contracts;

namespace Forge.Planning.Tests;

/// <summary>V2.9: maintenance readiness dimensions each fail on their own; maintenance-only alternates cover parts (scenario 4); outage preview.</summary>
public sealed class MaintenanceReadinessTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 2, 7, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tom = new("11111111-0000-0000-0000-000000000001");

    private static WorkOrderInfo Order(string[]? quals = null, string? procedure = null, string? revision = null) =>
        new(3001, "SLITTER-B", WorkOrderKind.Preventive, 101, "Bearing PM", null, ["Replace bearing"], new DateOnly(2026, 11, 2), WorkOrderStatus.Open, null, null, null,
            "person:mp", At, null, null, null, null, null, null, null, null, null, null, "SL-02", procedure, revision, quals ?? []);

    private static AssetInfo Asset(AssetStatus status = AssetStatus.Active) =>
        new("SL-02", "Slitter B", AssetType.Production, null, null, null, null, "SLIT-WC", "SLITTER-B", AssetCriticality.High, null, null, null, status, "person:mp", At);

    private static AlternateInfo Alt(long id, string primary, string alternate, AlternateKind kind, AlternateStatus status, string? asset) =>
        new(id, primary, alternate, kind, status, 1, null, null, null, asset, null, null, null, null, null, null, null, null, "person:mp", At,
            status == AlternateStatus.Proposed ? null : "person:me", status == AlternateStatus.Proposed ? null : At, null, null, null, null);

    private static PersonSnapshot Person(Guid id, string name, params string[] quals) =>
        new(id, name, true, null, null, null, null, quals) { QualificationExpiry = quals.ToDictionary(q => q, _ => (DateTimeOffset?)null) };

    private static DocumentInfo Procedure(DocumentRevisionStatus status) =>
        new("MP-SL-01", "Slitter bearing change", DocumentType.Procedure, DocumentAuthority.Internal, null, null, null, DataMarking.None, "person:e", At,
            [new DocumentRevisionInfo("MP-SL-01", "B", status, null, null, null, null, null, "person:e", At, null, null, null, null, null, null, null, null, null, null)]);

    private static MaintenanceReadinessRequest Request(
        WorkOrderInfo? order = null,
        AssetInfo? asset = null,
        Dictionary<string, decimal>? stock = null,
        AlternateInfo[]? alternates = null,
        DocumentInfo? procedure = null,
        PersonSnapshot[]? people = null,
        UnavailabilityWindow[]? away = null,
        WorkOrderPart[]? issued = null) =>
        new(order ?? Order(), asset ?? Asset(), [new WorkOrderPartRequirement(3001, 1, "BRG-OEM", 2, "Rewind bearing")], issued ?? [],
            stock ?? new Dictionary<string, decimal>(), alternates ?? [], procedure, people ?? [], away ?? [], At, At.AddHours(3));

    private static ReadinessDimension Dim(MaintenanceReadiness r, string name) => r.Dimensions.Single(d => d.Name == name);

    [Fact]
    public void Scenario_4_a_maintenance_only_alternate_for_this_asset_covers_the_part_and_says_so()
    {
        var r = MaintenanceReadinessEngine.Decide(Request(
            stock: new() { ["BRG-ALT"] = 6 },
            alternates: [Alt(7, "BRG-OEM", "BRG-ALT", AlternateKind.MaintenanceOnly, AlternateStatus.Approved, "SL-02")]));
        Assert.Equal("Ready", r.Status);
        var part = Assert.Single(r.Parts);
        Assert.Equal((true, "BRG-ALT", 7L, 2m, 0m), (part.Covered, part.Alternate, part.AlternateId, part.AlternateQuantity, part.OnHand));
        Assert.Contains("approved alternate BRG-ALT (6 on hand, 2 needed)", Dim(r, "Material").Detail, StringComparison.Ordinal);
        Assert.Contains("for asset SL-02", Dim(r, "Material").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_alternate_for_another_asset_or_only_proposed_never_covers()
    {
        var r = MaintenanceReadinessEngine.Decide(Request(
            stock: new() { ["BRG-ALT"] = 6, ["BRG-X"] = 9 },
            alternates: [Alt(7, "BRG-OEM", "BRG-ALT", AlternateKind.MaintenanceOnly, AlternateStatus.Approved, "SL-03"), Alt(8, "BRG-OEM", "BRG-X", AlternateKind.Equivalent, AlternateStatus.Proposed, null)]));
        Assert.Equal(("Blocked", "Material"), (r.Status, r.Blocking));
        Assert.Contains("not usable without approval", Dim(r, "Material").Detail, StringComparison.Ordinal);
        Assert.False(Assert.Single(r.Parts).Covered);
    }

    [Fact]
    public void Scenario_5_a_part_with_no_stock_and_no_alternate_blocks_on_material_and_is_named()
    {
        var r = MaintenanceReadinessEngine.Decide(Request());
        Assert.Equal(("Blocked", "Material"), (r.Status, r.Blocking));
        Assert.Contains("BRG-OEM: 2 needed, 0 on hand; no approved alternate", Dim(r, "Material").Detail, StringComparison.Ordinal);
        Assert.All(r.Dimensions.Where(d => d.Name != "Material"), d => Assert.Equal(EvidenceOutcome.Pass, d.Outcome));
    }

    [Fact]
    public void Issued_parts_count_including_alternates_by_their_primary_quantity()
    {
        var r = MaintenanceReadinessEngine.Decide(Request(issued: [new WorkOrderPart(3001, "BRG-OEM", 1), new WorkOrderPart(3001, "BRG-ALT", 1, "BRG-OEM", 1)]));
        Assert.Equal("Ready", r.Status);
        Assert.Equal((2m, 0m), (r.Parts[0].Issued, r.Parts[0].StillNeeded));
    }

    [Fact]
    public void Each_dimension_fails_on_its_own()
    {
        var stock = new Dictionary<string, decimal> { ["BRG-OEM"] = 5 };
        var tom = Person(Tom, "Tom Technician", "BEARING-1");

        var asset = MaintenanceReadinessEngine.Decide(Request(asset: Asset(AssetStatus.Decommissioned), stock: stock));
        Assert.Equal(("Blocked", "Asset"), (asset.Status, asset.Blocking));
        Assert.Single(asset.Dimensions, d => d.Outcome == EvidenceOutcome.Fail);

        var draft = MaintenanceReadinessEngine.Decide(Request(order: Order(procedure: "MP-SL-01", revision: "B"), stock: stock, procedure: Procedure(DocumentRevisionStatus.Draft)));
        Assert.Equal(("Blocked", "Procedure"), (draft.Status, draft.Blocking));
        Assert.Contains("not released", Dim(draft, "Procedure").Detail, StringComparison.Ordinal);
        Assert.Single(draft.Dimensions, d => d.Outcome == EvidenceOutcome.Fail);
        Assert.Equal("Ready", MaintenanceReadinessEngine.Decide(Request(order: Order(procedure: "MP-SL-01", revision: "B"), stock: stock, procedure: Procedure(DocumentRevisionStatus.Released))).Status);

        var nobody = MaintenanceReadinessEngine.Decide(Request(order: Order(["BEARING-1"]), stock: stock, people: [Person(new Guid("22222222-0000-0000-0000-000000000002"), "Sam", "FORKLIFT")]));
        Assert.Equal(("Blocked", "Technician"), (nobody.Status, nobody.Blocking));
        Assert.Contains("nobody holds BEARING-1", Dim(nobody, "Technician").Detail, StringComparison.Ordinal);
        Assert.Single(nobody.Dimensions, d => d.Outcome == EvidenceOutcome.Fail);

        var away = MaintenanceReadinessEngine.Decide(Request(order: Order(["BEARING-1"]), stock: stock, people: [tom], away: [new UnavailabilityWindow(Tom, UnavailabilityKind.ApprovedPto, At.AddHours(-1), At.AddHours(1))]));
        Assert.Equal("Technician", away.Blocking);
        Assert.Contains("unavailable", Dim(away, "Technician").Detail, StringComparison.Ordinal);

        var ready = MaintenanceReadinessEngine.Decide(Request(order: Order(["BEARING-1"]), stock: stock, people: [tom]));
        Assert.Equal("Ready", ready.Status);
        Assert.Equal(["Tom Technician"], ready.Technicians);
    }

    private static ScheduledOperation Op(long order, string machine, int startHour, int hours, bool scheduled = true) =>
        new(order, 10, "Slit", "SLIT-WC", scheduled ? machine : null, "RUN_SLITTER", scheduled,
            scheduled ? At.AddHours(startHour) : null, scheduled ? At.AddHours(startHour + hours) : null, hours * 60, "Machine", string.Empty, [], [], []);

    private static ScheduledOrder Ord(long order, DeliveryStatus status, int completionHour) =>
        new(order, "ROLL", 3, new DateOnly(2026, 11, 5), At.AddDays(3), At.AddHours(completionHour), status, string.Empty);

    [Fact]
    public void Scenario_8_preview_says_what_moves_what_is_delayed_what_cannot_move_and_which_orders_become_late()
    {
        var without = new ScheduleResult(At, At.AddDays(14), [Ord(1, DeliveryStatus.OnTime, 4), Ord(2, DeliveryStatus.OnTime, 8), Ord(3, DeliveryStatus.OnTime, 12), Ord(4, DeliveryStatus.OnTime, 30)],
            [Op(1, "SLITTER-B", 1, 3), Op(2, "SLITTER-B", 4, 4), Op(3, "SLITTER-B", 8, 4), Op(4, "SLITTER-A", 20, 10)]);
        var with = new ScheduleResult(At, At.AddDays(14), [Ord(1, DeliveryStatus.OnTime, 5), Ord(2, DeliveryStatus.Late, 90), Ord(3, DeliveryStatus.Unschedulable, 0) with { ProjectedCompletion = null }, Ord(4, DeliveryStatus.OnTime, 30)],
            [Op(1, "SLITTER-A", 2, 3), Op(2, "SLITTER-B", 80, 4), Op(3, "SLITTER-B", 0, 0, scheduled: false), Op(4, "SLITTER-A", 20, 10)]);
        var p = OutagePreviewEngine.Compare("SLITTER-B", At, At.AddHours(48), without, with);
        Assert.Equal((3, 1, 1, 1, 2), (p.Affected, p.Moved, p.Delayed, p.CannotMove, p.OrdersLate));
        Assert.Equal(["Moves", "Delayed", "CannotMove"], p.Operations.Select(o => o.Effect).ToArray());
        Assert.Equal("SLITTER-A", p.Operations[0].MachineAfter);
        Assert.Equal([2L, 3L], p.Orders.Where(o => o.BecomesLate).Select(o => o.OrderNumber).ToArray());
        Assert.DoesNotContain(p.Orders, o => o.OrderNumber == 4);
        Assert.Contains("3 operation(s) affected — 1 move to another machine, 1 delayed, 1 cannot move; 2 order(s) projected late (2, 3)", p.Summary, StringComparison.Ordinal);

        var none = OutagePreviewEngine.Compare("SLITTER-C", At, At.AddHours(48), without, without);
        Assert.Equal((0, 0), (none.Affected, none.OrdersLate));
        Assert.Contains("no planned operation is affected; no order becomes late", none.Summary, StringComparison.Ordinal);
    }
}
