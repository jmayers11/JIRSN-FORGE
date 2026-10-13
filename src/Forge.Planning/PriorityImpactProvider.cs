using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Engines;
using Forge.Kernel.Serialization;
using Forge.Kernel.Security;
using Forge.Planning.Engines;
using Forge.Quality.Contracts;

namespace Forge.Planning;

/// <summary>Compares the deterministic finite-capacity schedule before and after one priority change.</summary>
public sealed class PriorityImpactProvider : IPriorityImpactProvider
{
    private const int HorizonDays = 14;

    public async Task<PriorityChangePreview> PreviewAsync(IKernelUnitOfWork uow, long orderNumber, int newPriority, DateTimeOffset asOf, CancellationToken ct, ForgePrincipal? viewer = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        if (newPriority is < 1 or > 9)
        {
            throw new Forge.Kernel.Commands.CommandRejectedException("invalid_priority", "Priority must be between 1 (highest) and 9.");
        }

        var normalizedAsOf = new DateTimeOffset(asOf.UtcDateTime.Ticks / 10 * 10, TimeSpan.Zero);
        var timeAtPreview = new FixedTime(normalizedAsOf);
        var lookup = uow.Store<IExecutionLookup>();
        var target = await lookup.FindOrderAsync(orderNumber, ct)
            ?? throw new Forge.Kernel.Commands.CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
        var orders = new List<OrderInfo>();
        foreach (var summary in (await lookup.ListOrdersAsync(ct)).Where(o => o.Status == OrderStatus.Released).OrderBy(o => o.OrderNumber))
        {
            orders.Add(await lookup.FindOrderAsync(summary.OrderNumber, ct) ?? summary);
        }

        var snapshot = await SnapshotLoader.LoadAsync(uow, timeAtPreview, ct);
        var material = (await MaterialPlanningService.RunMaterialAsync(uow, timeAtPreview, ct)).Output;
        var quality = await ScheduleQuality.LoadAsync(uow, orders, ct);
        var engine = new SchedulingEngine();
        var currentRequest = new ScheduleRequest(normalizedAsOf, normalizedAsOf.AddDays(HorizonDays), orders, material, quality);
        var proposedOrders = orders.Select(o => o.OrderNumber == orderNumber ? o with { Priority = newPriority } : o).ToList();
        var proposedRequest = currentRequest with { Orders = proposedOrders };
        var current = EngineRunner.Run(engine, snapshot, currentRequest, normalizedAsOf);
        var proposed = EngineRunner.Run(engine, snapshot, proposedRequest, normalizedAsOf);
        var fingerprint = CanonicalJson.Fingerprint(new
        {
            orderNumber,
            currentPriority = target.Priority,
            newPriority,
            asOf = normalizedAsOf,
            currentInputFingerprint = current.InputFingerprint,
            currentOutputFingerprint = current.OutputFingerprint,
            proposedInputFingerprint = proposed.InputFingerprint,
            proposedOutputFingerprint = proposed.OutputFingerprint,
        });
        var before = current.Output.Orders.ToDictionary(o => o.OrderNumber);
        var after = proposed.Output.Orders.ToDictionary(o => o.OrderNumber);
        var changes = before.Keys.Union(after.Keys).Order()
            .Where(number => number == orderNumber || before[number].ProjectedCompletion != after[number].ProjectedCompletion || before[number].Status != after[number].Status)
            .Select(number => new PriorityOrderImpact(
                number,
                after[number].PartNumber,
                after[number].ProgramId,
                before[number].ProjectedCompletion,
                after[number].ProjectedCompletion,
                before[number].Status.ToString(),
                after[number].Status.ToString(),
                before[number].Explanation,
                after[number].Explanation))
            .ToList();

        if (viewer is not null)
        {
            var access = await MaterialSourceVisibility.LoadAsync(uow, viewer, ct, authorizer: new ForgeAuthorizer(timeAtPreview));
            changes = changes.Select(o => o with { ExplanationBefore = access.Text(o.ExplanationBefore), ExplanationAfter = access.Text(o.ExplanationAfter) }).ToList();
        }
        return new PriorityChangePreview(orderNumber, target.Priority, newPriority, normalizedAsOf, fingerprint, changes);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
