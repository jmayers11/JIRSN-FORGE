using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Engines;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Engines;
using Forge.Quality.Contracts;

namespace Forge.Planning;

/// <summary>One open operation's answer to "can this happen now, and if not, why not?" (V3.9).</summary>
public sealed record OperationAnswer(
    long OrderNumber,
    string PartNumber,
    Guid? ProgramId,
    DateOnly DueDate,
    int Priority,
    int Sequence,
    string Description,
    string WorkCenter,
    string? Machine,
    bool Executable,
    string? PrimaryConstraint,
    string Why,
    IReadOnlyList<ReadinessDimension> Dimensions);

/// <summary>
/// Every operation of every released (or held) order that is not complete, answered with the same order-readiness
/// engine the order page uses (V3.9, V3 brief B): executable or not, the primary constraint and its explanation.
/// Material allocation, points of use and the training policy are read once for all orders.
/// </summary>
internal static class ExecutableOperations
{
    public static async Task<IReadOnlyList<OperationAnswer>> EvaluateAsync(IKernelUnitOfWork uow, TimeProvider time, ForgePrincipal principal, CancellationToken ct)
    {
        var snapshot = await SnapshotLoader.LoadAsync(uow, time, ct);
        var execution = uow.Store<IExecutionLookup>();
        var orders = (await execution.ListOrdersAsync(ct))
            .Where(o => o.Status is OrderStatus.Released or OrderStatus.Held)
            .Where(o => o.ProgramId is not { } p || principal.Programs.Contains(new ProgramId(p)))
            .OrderBy(o => o.Priority).ThenBy(o => o.DueDate).ThenBy(o => o.OrderNumber)
            .ToList();
        if (orders.Count == 0)
        {
            return [];
        }

        var material = (await MaterialPlanningService.RunMaterialAsync(uow, time, ct, principal)).Output.ToDictionary(m => m.OrderNumber);
        var pointOfUse = await ReadinessService.PointOfUseAsync(uow, snapshot.AsOf, ct);
        var engineering = uow.Store<IEngineeringLookup>();
        var fullOrders = new List<OrderInfo>();
        foreach (var summary in orders)
            fullOrders.Add(await execution.FindOrderAsync(summary.OrderNumber, ct)
                ?? throw new InvalidOperationException($"Order {summary.OrderNumber} disappeared from the snapshot."));
        var continuations = await WipContinuationFacts.LoadAsync(uow, fullOrders, snapshot.AsOf, ct);
        var engine = new OrderReadinessEngine();
        var answers = new List<OperationAnswer>();
        foreach (var order in fullOrders)
        {
            var revision = (await engineering.ListRevisionsAsync(order.PartNumber, ct)).SingleOrDefault(r => r.Revision == order.Revision);
            var quality = await uow.Store<IQualityLookup>().GetOrderQualityAsync(order.OrderNumber, order.PartNumber, ct);
            var documentation = await DocumentationService.ForOrderAsync(uow, order, snapshot.AsOf, principal, ct);
            var training = await ReadinessService.TrainingAsync(uow, documentation, ct);
            var forOrder = order.Status == OrderStatus.Released ? material.GetValueOrDefault(order.OrderNumber) : null;
            var result = engine.Evaluate(snapshot, new OrderReadinessRequest(order, revision, forOrder, quality, documentation, forOrder is null ? null : pointOfUse, training, continuations.GetValueOrDefault(order.OrderNumber)), snapshot.AsOf).Output;
            foreach (var op in result.Operations)
            {
                var info = order.Operations.First(o => o.Sequence == op.Sequence);
                if (info.Status == OperationStatus.Complete)
                {
                    continue;
                }

                var why = op.Executable
                    ? $"Can run now{(op.BestMachine is null ? string.Empty : $" on {op.BestMachine}")}."
                    : op.Dimensions.FirstOrDefault(d => d.Name == op.PrimaryConstraint && d.Outcome is EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate)?.Detail ?? $"Blocked by {op.PrimaryConstraint}.";
                answers.Add(new OperationAnswer(order.OrderNumber, order.PartNumber, order.ProgramId, order.DueDate, order.Priority, op.Sequence, op.Description, info.WorkCenterCode, op.BestMachine,
                    op.Executable, op.PrimaryConstraint, why, op.Dimensions));
            }
        }

        return answers;
    }
}
