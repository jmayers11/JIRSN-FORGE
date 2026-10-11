using Forge.Kernel.Holds;
using Forge.Kernel.Security;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

/// <summary>V3.10: a hold stops only the activities its kind is configured to block, on the records it is on.</summary>
public class HoldPolicyTests
{
    private static Hold On(HoldKind kind, HoldTargetType type, string id, string? part = null) =>
        new(501, kind, type, id, part, "Placed by hand", "Test", null, [], HoldService.DefaultResolution(kind), Now, "person:a");

    private static readonly IReadOnlyList<HoldKindPolicy> Defaults = HoldKindPolicy.Defaults;

    [Fact]
    public void Each_kind_stops_only_its_configured_activities()
    {
        var supplier = On(HoldKind.Procurement, HoldTargetType.Supplier, "ACME");
        Assert.True(supplier.Stops(new HoldCheck(HoldActivity.Purchase, SupplierCode: "ACME"), Defaults));
        Assert.True(supplier.Stops(new HoldCheck(HoldActivity.Receive, SupplierCode: "ACME"), Defaults));
        Assert.False(supplier.Stops(new HoldCheck(HoldActivity.Ship, SupplierCode: "ACME"), Defaults));
        Assert.False(supplier.Stops(new HoldCheck(HoldActivity.Purchase, SupplierCode: "OTHER"), Defaults));

        var customer = On(HoldKind.Customer, HoldTargetType.Order, "10001");
        Assert.True(customer.Stops(new HoldCheck(HoldActivity.ReleaseOrder, 10001), Defaults));
        Assert.False(customer.Stops(new HoldCheck(HoldActivity.StartWork, 10001, 10), Defaults));

        var admin = On(HoldKind.Administrative, HoldTargetType.Part, "ROLL-100");
        Assert.All(Enum.GetValues<HoldActivity>(), a => Assert.True(admin.Stops(new HoldCheck(a, Parts: ["ROLL-100"]), Defaults)));

        // Reconfigured: production holds now also stop issuing material.
        var production = On(HoldKind.Production, HoldTargetType.Part, "COIL-AL");
        Assert.False(production.Stops(new HoldCheck(HoldActivity.IssueMaterial, Parts: ["COIL-AL"]), Defaults));
        var configured = Defaults.Select(p => p.Kind == HoldKind.Production ? p with { Blocks = [HoldActivity.StartWork, HoldActivity.IssueMaterial] } : p).ToList();
        Assert.True(production.Stops(new HoldCheck(HoldActivity.IssueMaterial, Parts: ["COIL-AL"]), configured));
    }

    [Fact]
    public void Targets_match_operations_lots_machines_and_programs_and_a_released_hold_stops_nothing()
    {
        var op = On(HoldKind.Production, HoldTargetType.Operation, "10001/20");
        Assert.True(op.Stops(new HoldCheck(HoldActivity.StartWork, 10001, 20), Defaults));
        Assert.False(op.Stops(new HoldCheck(HoldActivity.StartWork, 10001, 10), Defaults));

        var lot = On(HoldKind.Quality, HoldTargetType.Lot, "L-7", "COIL-AL");
        Assert.True(lot.Stops(new HoldCheck(HoldActivity.IssueMaterial, LotList: [new HoldLot("COIL-AL", "L-7")]), Defaults));
        Assert.False(lot.Stops(new HoldCheck(HoldActivity.IssueMaterial, LotList: [new HoldLot("TAPE-1", "L-7")]), Defaults));

        var machine = On(HoldKind.Safety, HoldTargetType.Machine, "SLITTER-A");
        Assert.False(machine.Stops(new HoldCheck(HoldActivity.StartWork, 10001, 10, WorkCenterCode: "SLIT-WC"), Defaults));
        Assert.True(machine.Stops(new HoldCheck(HoldActivity.StartWork, 10001, 10, WorkCenterCode: "SLIT-WC", MachineCode: "SLITTER-A"), Defaults));

        var programId = Guid.Parse("018f0000-0000-7000-8000-000000000099");
        var program = On(HoldKind.Customer, HoldTargetType.Program, programId.ToString());
        Assert.True(program.Stops(new HoldCheck(HoldActivity.Ship, ProgramId: programId), Defaults));

        Assert.False((op with { ReleasedAt = Now, ReleasedBy = "person:b", ReleaseReason = "Done" }).Stops(new HoldCheck(HoldActivity.StartWork, 10001, 20), Defaults));
    }

    [Fact]
    public void Release_authority_differs_by_kind()
    {
        Assert.Equal(Permissions.QualityDisposition, HoldService.PolicyOf(Defaults, HoldKind.Quality).ReleasePermission);
        Assert.Equal(Permissions.PurchasingApprove, HoldService.PolicyOf(Defaults, HoldKind.Procurement).ReleasePermission);
        Assert.Equal(Permissions.HoldsRelease, HoldService.PolicyOf(Defaults, HoldKind.Production).ReleasePermission);
        Assert.Equal(10, Defaults.Select(p => p.Kind).Distinct().Count());
    }
}
