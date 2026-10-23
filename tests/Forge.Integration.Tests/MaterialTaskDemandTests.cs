using Forge.Inventory;
using Forge.Inventory.Contracts;

namespace Forge.Integration.Tests;

public sealed class MaterialTaskDemandTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Remaining_demand_accounts_for_a_short_pick_while_the_task_is_in_transit()
    {
        var inTransit = Task(MaterialTaskStatus.InTransit, picked: 12m);

        Assert.Equal(8m, MaterialTaskDemand.Remaining(20m, 0m, [inTransit]));
    }

    [Fact]
    public void Issued_and_delivered_amounts_do_not_create_a_duplicate_task_for_the_same_demand()
    {
        var delivered = Task(MaterialTaskStatus.Delivered, picked: 12m);

        Assert.Equal(8m, MaterialTaskDemand.Remaining(20m, 12m, [delivered]));
        Assert.Equal(0m, MaterialTaskDemand.Remaining(20m, 20m, [delivered]));
    }

    [Fact]
    public void Cancelled_tasks_release_their_unfilled_demand()
    {
        var cancelled = Task(MaterialTaskStatus.Cancelled, picked: null);

        Assert.Equal(20m, MaterialTaskDemand.Remaining(20m, 0m, [cancelled]));
    }

    [Fact]
    public void Additional_material_requests_never_consume_released_bom_demand()
    {
        var approvalNeeded = Task(MaterialTaskStatus.ApprovalNeeded, picked: null) with { IsNonBom = true };
        var rejected = Task(MaterialTaskStatus.Rejected, picked: null) with { IsNonBom = true };

        Assert.Equal(20m, MaterialTaskDemand.Remaining(20m, 0m, [approvalNeeded, rejected]));
    }

    private static MaterialTask Task(MaterialTaskStatus status, decimal? picked) => new(
        1, MaterialTaskKind.Stage, 1042, null, "COMP-1", null, 20m, "STORES", "WC-A-STAGE", status, picked,
        "test", "person:test", At);
}
