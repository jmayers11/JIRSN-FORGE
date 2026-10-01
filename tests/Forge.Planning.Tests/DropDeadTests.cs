using Forge.Execution.Contracts;
using Forge.Planning.Engines;
using static Forge.Planning.Tests.CalendarTests;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

public class DropDeadTests
{
    [Fact]
    public void Spec_42_backward_schedule_through_working_time_and_supplier_lead_time()
    {
        var order = new OrderInfo(10001, "ROLL-100", "A", 10, new DateOnly(2026, 10, 9), 3, OrderStatus.Released, null, null, Now,
        [
            new OrderOperationInfo(10, "Slit", "SLIT-WC", RunSlitter, ["SLITTER-A"], [], 120, 1, OperationStatus.Pending),
            new OrderOperationInfo(20, "Kit", "SLIT-WC", Kitting, [], [], 600, 1, OperationStatus.Pending),
        ],
        []);

        var result = new DropDeadDateEngine().Evaluate(Slitting().Build(), new DropDeadRequest(order, 1, [new PurchasedComponent("COIL-AL", 14)]), Now).Output;

        Assert.Equal(At(2026, 10, 10, 4), result.NeedBy);                  // end of Friday Oct 9, New York
        Assert.Equal(At(2026, 10, 9, 4), result.ShipBy);                   // one day for shipping
        Assert.Equal(At(2026, 10, 7, 17), result.Operations[1].LatestStart); // 10 h of kitting: Thu 8 h + Wed 2 h
        Assert.Equal(At(2026, 10, 7, 15), result.Operations[0].LatestStart); // 2 h of slitting before that
        Assert.Equal(At(2026, 10, 7, 15), result.MaterialRequiredBy);
        Assert.Equal(At(2026, 9, 23, 15), Assert.Single(result.PurchaseBy).LatestStart);
        Assert.True(result.AlreadyMissed); // the coil should have been ordered by Sept 23
    }
}
