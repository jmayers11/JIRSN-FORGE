using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Engines;
using Forge.Kernel.Security;
using Forge.Planning.Engines;
using Forge.Quality.Contracts;

namespace Forge.Planning;

public sealed record MaterialOrderScheduleImpact(
    long OrderNumber,
    string PartNumber,
    DateOnly DueDate,
    DateTimeOffset DueAt,
    string BeforeStatus,
    string ProposedStatus,
    DateTimeOffset? BeforeCompletion,
    DateTimeOffset? ProposedCompletion,
    decimal? CompletionShiftHours);

public sealed record MaterialReceiptImpactPreview(
    long TaskId,
    long OrderNumber,
    int? OperationSequence,
    string PartNumber,
    decimal HypotheticalAcceptedQuantity,
    string Destination,
    DateTimeOffset AsOf,
    string BaselineMaterialState,
    string ProposedMaterialState,
    string BaselineMaterialSummary,
    string ProposedMaterialSummary,
    MaterialOrderScheduleImpact TargetOrder,
    IReadOnlyList<MaterialOrderScheduleImpact> OtherOrderImpacts,
    string Assumption);

/// <summary>
/// A read-only receipt what-if. It nets the hypothetical quantity once through the canonical material engine,
/// then runs the same finite-capacity scheduler against both material snapshots.
/// </summary>
public static class MaterialReceiptImpactCalculator
{
    public const string Assumption = "Assumes an independent receiver accepts the remaining in-transit BOM quantity for this order at its destination at the current site date. The hypothetical supply remains pegged to this order while the finite-capacity scheduler accounts for all visible order commitments; no receipt, approval, inventory transaction, or assignment is written. Actual acceptance, point-of-use checks, quality holds, safety, and authorization gates still apply.";

    public static MaterialReceiptImpactPreview Evaluate(
        SiteSnapshot snapshot,
        MaterialSnapshot material,
        IReadOnlyList<OrderInfo> orders,
        IReadOnlyDictionary<long, OrderQuality> quality,
        IReadOnlyDictionary<string, int> leadTimes,
        long taskId,
        long orderNumber,
        int? operationSequence,
        string partNumber,
        decimal quantity,
        string destination)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(leadTimes);
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "The hypothetical receipt quantity must be positive.");
        if (!orders.Any(order => order.OrderNumber == orderNumber && order.Status == OrderStatus.Released))
            throw new ArgumentException("The target must be a released production order.", nameof(orderNumber));

        var baselineMaterial = EngineRunner.Run(new MaterialEngine(), material, new MaterialRequest(), snapshot.AsOf);
        var proposedInput = material with
        {
            Supplies = [.. material.Supplies, new MaterialSupply($"ReceiptPreview/{taskId}", partNumber, quantity,
                material.Today, $"Hypothetical accepted receipt for order {orderNumber}", orderNumber)],
        };
        var proposedMaterial = EngineRunner.Run(new MaterialEngine(), proposedInput, new MaterialRequest(), snapshot.AsOf);

        var horizon = snapshot.AsOf.AddDays(62);
        var baselineSchedule = EngineRunner.Run(new SchedulingEngine(), snapshot,
            new ScheduleRequest(snapshot.AsOf, horizon, orders, baselineMaterial.Output, quality, leadTimes), snapshot.AsOf).Output;
        var proposedSchedule = EngineRunner.Run(new SchedulingEngine(), snapshot,
            new ScheduleRequest(snapshot.AsOf, horizon, orders, proposedMaterial.Output, quality, leadTimes), snapshot.AsOf).Output;
        var beforeMaterials = baselineMaterial.Output.SingleOrDefault(row => row.OrderNumber == orderNumber);
        var afterMaterials = proposedMaterial.Output.SingleOrDefault(row => row.OrderNumber == orderNumber);
        var target = Compare(orderNumber, baselineSchedule, proposedSchedule)
            ?? throw new InvalidOperationException("The target production order was not returned by the schedule engine.");
        var otherImpacts = proposedSchedule.Orders
            .Where(row => row.OrderNumber != orderNumber)
            .Select(row => Compare(row.OrderNumber, baselineSchedule, proposedSchedule))
            .Where(row => row is not null && (row.BeforeStatus != row.ProposedStatus || row.BeforeCompletion != row.ProposedCompletion))
            .Select(row => row!)
            .OrderByDescending(row => row.CompletionShiftHours ?? 0)
            .ThenBy(row => row.DueDate)
            .ThenBy(row => row.OrderNumber)
            .ToArray();

        return new MaterialReceiptImpactPreview(
            taskId,
            orderNumber,
            operationSequence,
            partNumber,
            quantity,
            destination,
            snapshot.AsOf,
            beforeMaterials?.State.ToString() ?? "Unknown",
            afterMaterials?.State.ToString() ?? "Unknown",
            beforeMaterials?.Summary ?? "Material status is unavailable.",
            afterMaterials?.Summary ?? "Material status is unavailable.",
            target,
            otherImpacts,
            Assumption);
    }

    private static MaterialOrderScheduleImpact? Compare(long number, ScheduleResult before, ScheduleResult proposed)
    {
        var a = before.Orders.SingleOrDefault(row => row.OrderNumber == number);
        var b = proposed.Orders.SingleOrDefault(row => row.OrderNumber == number);
        if (a is null || b is null) return null;
        decimal? shift = a.ProjectedCompletion is { } beforeAt && b.ProjectedCompletion is { } afterAt
            ? decimal.Round((decimal)(afterAt - beforeAt).TotalHours, 2, MidpointRounding.AwayFromZero)
            : null;
        return new MaterialOrderScheduleImpact(number, b.PartNumber, b.DueDate, b.DueAt,
            a.Status.ToString(), b.Status.ToString(), a.ProjectedCompletion, b.ProjectedCompletion, shift);
    }
}

/// <summary>Permission-scoped source loader for a hypothetical receipt of an in-transit BOM task.</summary>
public sealed class MaterialReceiptImpactPreviewService(
    IKernelUnitOfWorkFactory unitOfWork,
    IForgeAuthorizer authorizer,
    TimeProvider time)
{
    public async Task<MaterialReceiptImpactPreview> PreviewAsync(ForgePrincipal principal, long taskId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Demand(principal, Permissions.InventoryRead, "Inventory");
        Demand(principal, Permissions.ScheduleRead, "Schedule");

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var inventory = uow.Store<IInventoryLookup>();
        var task = await inventory.FindMaterialTaskAsync(taskId, ct)
            ?? throw new CommandRejectedException("material_task_not_found", "The material task does not exist.");
        if (task.Status != MaterialTaskStatus.InTransit || task.Kind != MaterialTaskKind.Stage || task.IsNonBom || task.OrderNumber is not { } orderNumber)
            throw new CommandRejectedException("material_receipt_preview_unavailable", "Only an in-transit released-order BOM staging task can be used for this receipt preview.");

        var quantity = Math.Max(0m, (task.PickedQuantity ?? task.Quantity) - task.ReceivedQuantity - task.RejectedQuantity);
        if (quantity <= 0)
            throw new CommandRejectedException("material_receipt_preview_unavailable", "This task has no remaining quantity awaiting receipt.");

        var locations = await inventory.ListLocationsAsync(ct);
        var destination = locations.SingleOrDefault(location => location.Code == task.ToLocation && location.Active && location.PointOfUse)
            ?? throw new CommandRejectedException("material_receipt_preview_unavailable", "The task destination is not an active point-of-use location.");
        var execution = uow.Store<IExecutionLookup>();
        var headers = await execution.ListOrdersAsync(ct);
        var access = await MaterialSourceVisibility.LoadAsync(uow, principal, ct, headers, authorizer);
        if (!access.CanSeeOrder(orderNumber))
            throw new CommandRejectedException("material_task_not_found", "The material task does not exist.");

        var orders = new List<OrderInfo>();
        foreach (var header in headers.Where(order => order.Status == OrderStatus.Released).OrderBy(order => order.OrderNumber))
            if (await execution.FindOrderAsync(header.OrderNumber, ct) is { } order) orders.Add(order);
        if (!orders.Any(order => order.OrderNumber == orderNumber))
            throw new CommandRejectedException("material_receipt_preview_unavailable", "The task's production order is no longer released.");

        var snapshot = await SnapshotLoader.LoadAsync(uow, time, ct);
        var materialInput = await MaterialData.MaterialSnapshotAsync(uow, time, ct, headers, orders);
        var quality = await ScheduleQuality.LoadAsync(uow, orders, ct);
        var raw = MaterialReceiptImpactCalculator.Evaluate(snapshot, materialInput, orders, quality,
            await MaterialData.LeadTimesAsync(uow, ct), task.TaskId, orderNumber, task.OperationSequence,
            task.PartNumber, quantity, destination.Code);
        var before = access.Material(EngineRunner.Run(new MaterialEngine(), materialInput, new MaterialRequest(), snapshot.AsOf))
            .Output.SingleOrDefault(row => row.OrderNumber == orderNumber);
        var proposedInput = materialInput with
        {
            Supplies = [.. materialInput.Supplies, new MaterialSupply($"ReceiptPreview/{task.TaskId}", task.PartNumber,
                quantity, materialInput.Today, $"Hypothetical accepted receipt for order {orderNumber}", orderNumber)],
        };
        var after = access.Material(EngineRunner.Run(new MaterialEngine(), proposedInput, new MaterialRequest(), snapshot.AsOf))
            .Output.SingleOrDefault(row => row.OrderNumber == orderNumber);
        return raw with
        {
            BaselineMaterialSummary = access.Text(before?.Summary ?? "Material status is unavailable."),
            ProposedMaterialSummary = access.Text(after?.Summary ?? "Material status is unavailable."),
            OtherOrderImpacts = raw.OtherOrderImpacts.Where(row => access.CanSeeOrder(row.OrderNumber)).Take(10).ToArray(),
        };
    }

    private void Demand(ForgePrincipal principal, string permission, string resource)
    {
        var decision = authorizer.Authorize(principal, permission, ResourceRef.Of(resource));
        if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision);
    }
}
