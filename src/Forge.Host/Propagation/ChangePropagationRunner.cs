using System.Globalization;
using Forge.Engineering;
using Forge.Engineering.Contracts;
using Forge.Execution;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Procurement.Contracts;
using Forge.Programs.Contracts;
using Forge.Workforce.Contracts;
using Npgsql;

namespace Forge.Host.Propagation;

/// <summary>A test seam: called before each step runs. Production does nothing here.</summary>
public interface IPropagationFaults
{
    void BeforeStep(string changeNumber, PropagationStepInfo step);
}

public sealed class NoPropagationFaults : IPropagationFaults
{
    public void BeforeStep(string changeNumber, PropagationStepInfo step)
    {
    }
}

/// <summary>
/// Carries a released engineering change into the records it reaches (V2.5, review scenario 1). Each record
/// is one step, planned once and done once: future orders and released orders not started move to the new
/// revision (and get their documentation reissued); work in progress gets a disposition action, never an
/// edit; open lines of approved purchase orders get a review action, their technical package untouched;
/// people who work where a changed instruction is used get an acknowledgment action; completed orders are
/// checked to be left as they were. Every change it makes runs as system:propagation under the release's
/// correlation id. A failed step keeps its error and is tried again; the change shows "propagation
/// incomplete" until expected and actual match.
/// </summary>
public sealed class ChangePropagationRunner(
    IKernelUnitOfWorkFactory unitOfWork,
    CommandPipeline pipeline,
    ProductionOrderService orders,
    DocumentService documents,
    ChangeActionService actions,
    IPropagationFaults faults,
    TimeProvider time,
    NpgsqlDataSource dataSource)
{
    private const string Reason = "Propagation of a released engineering change";
    private const string NotMoved = "Not moved:";

    public async Task<ChangePropagationInfo?> RunAsync(string changeNumber, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        ChangeInfo change;
        ChangePropagationInfo propagation;
        List<PropagationStepInfo> planned;
        ForgePrincipal principal;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            var found = await read.Store<IChangeLookup>().FindChangeAsync(changeNumber, ct);
            var record = await read.Store<IPropagationLookup>().FindPropagationAsync(changeNumber, ct);
            if (found is not { Status: ChangeStatus.Released } || record is null)
            {
                return null;
            }

            (change, propagation) = (found, record);
            principal = new ForgePrincipal(
                new PersonId(Guid.Empty),
                "Change propagation",
                true,
                new HashSet<string>(StringComparer.Ordinal) { Roles.ChangePropagation },
                (await read.Store<IProgramsLookup>().ListProgramsAsync(ct)).Select(p => new ProgramId(p.ProgramId)).ToHashSet(),
                DataMarking.ControlledUnclassified | DataMarking.ExportControlled | DataMarking.Proprietary);
        }

        var inForce = change.EffectiveFrom is not { } from || from <= DateOnly.FromDateTime(now.UtcDateTime);
        var releaseEventId = await ReleaseEventIdAsync(change.Number, ct);
        string? firstError = null;

        // Plan, then do every step not done. A second pass plans again when an order started between planning
        // and moving it, so its disposition is raised in this same run.
        for (var pass = 0; pass < 2; pass++)
        {
            await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
            {
                planned = await PlanAsync(read, change, time.GetUtcNow(), ct);
            }

            await using (var write = await unitOfWork.BeginAsync(ct))
            {
                foreach (var step in planned)
                {
                    await write.Store<IPropagationStore>().PlanStepAsync(step, ct);
                }

                await write.CommitAsync(ct);
            }

            await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
            {
                propagation = (await read.Store<IPropagationLookup>().FindPropagationAsync(changeNumber, ct))!;
            }

            var replan = false;
            foreach (var step in propagation.Steps.Where(s => s.Status != PropagationStepStatus.Done))
            {
                var (status, detail, error) = await ExecuteAsync(change, propagation, step, principal, inForce, releaseEventId, ct);
                firstError ??= error;
                replan |= detail.Contains(NotMoved, StringComparison.Ordinal) && step.Status != PropagationStepStatus.Done;
                await using var write = await unitOfWork.BeginAsync(ct);
                await write.Store<IPropagationStore>().SetStepAsync(change.Number, step.StepKey, status, detail, error, time.GetUtcNow(), ct);
                await write.CommitAsync(ct);
            }

            if (!replan)
            {
                break;
            }
        }

        await using (var finish = await unitOfWork.BeginAsync(ct))
        {
            var steps = (await finish.Store<IPropagationLookup>().FindPropagationAsync(changeNumber, ct))!.Steps;
            var unresolved = steps.Count(s => s.Status != PropagationStepStatus.Done);
            var status = unresolved == 0 ? PropagationStatus.Complete : PropagationStatus.Incomplete;
            var error = firstError ?? (unresolved > 0 && !inForce ? $"waiting for the change to take effect on {change.EffectiveFrom:yyyy-MM-dd}" : null);
            await finish.Store<IPropagationStore>().FinishRunAsync(change.Number, status, error, time.GetUtcNow(), ct);
            var context = Context(principal, propagation.CorrelationId, $"{change.Number}:run:{propagation.Runs + 1}", releaseEventId);
            await finish.Outbox.EnqueueAsync(pipeline.Event(new PropagationFinished(change.Number, status.ToString(), steps.Count, unresolved), context, EngineeringService.SourceContext, "EngineeringChange", change.Number, change.ProgramId is { } p ? new ProgramId(p) : null), ct);
            await finish.CommitAsync(ct);
        }

        await using var result = await unitOfWork.BeginSnapshotAsync(ct);
        return await result.Store<IPropagationLookup>().FindPropagationAsync(changeNumber, ct);
    }

    /// <summary>Every record the change reaches now, as steps. Steps already planned are kept as they are.</summary>
    private static async Task<List<PropagationStepInfo>> PlanAsync(IKernelUnitOfWork uow, ChangeInfo change, DateTimeOffset now, CancellationToken ct)
    {
        var steps = new List<PropagationStepInfo>();
        void Add(string key, PropagationStepKind kind, string target, string? part, string detail) =>
            steps.Add(new PropagationStepInfo(change.Number, key, kind, target, part, detail, PropagationStepStatus.Pending, 0, null, now, null));

        var documentLookup = uow.Store<IDocumentLookup>();
        var engineering = uow.Store<IEngineeringLookup>();
        var itemLines = change.ActiveLines.Where(l => l.Kind == ChangeLineKind.ItemRevision).ToDictionary(l => l.PartNumber!, StringComparer.Ordinal);
        var documentLines = change.ActiveLines.Where(l => l.Kind == ChangeLineKind.DocumentRevision).ToList();
        var changedDocuments = documentLines.Select(l => l.DocumentNumber!).ToHashSet(StringComparer.Ordinal);
        var requirements = (await documentLookup.ListRequirementsAsync(null, null, false, ct)).Where(r => changedDocuments.Contains(r.DocumentNumber)).ToList();
        var orderLinks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in changedDocuments)
        {
            orderLinks.UnionWith((await documentLookup.ListLinksAsync(DocumentTargetType.ProductionOrder, null, document, false, ct))
                .Where(l => l.Purpose == DocumentPurpose.Follow).Select(l => l.TargetId));
        }

        var issued = (await uow.Store<IInventoryLookup>().ListOrderIssuesAsync(ct)).Where(i => i.NetIssued != 0).Select(i => i.OrderNumber).ToHashSet();
        var reachedParts = new HashSet<string>(itemLines.Keys, StringComparer.Ordinal);
        var execution = uow.Store<IExecutionLookup>();
        foreach (var listed in (await execution.ListOrdersAsync(ct)).Where(o => o.Status != OrderStatus.Cancelled).OrderBy(o => o.OrderNumber))
        {
            var id = listed.OrderNumber.ToString(CultureInfo.InvariantCulture);
            if (!itemLines.ContainsKey(listed.PartNumber) && !requirements.Any(r => r.PartNumber == listed.PartNumber) && !orderLinks.Contains(id))
            {
                continue;
            }

            // The list carries no operations; whether work started is read from the order itself.
            var order = await execution.FindOrderAsync(listed.OrderNumber, ct) ?? listed;
            var item = itemLines.TryGetValue(order.PartNumber, out var line) && order.Revision != line.ToRevision ? line : null;
            var documentReached = requirements.Any(r => r.PartNumber == order.PartNumber && r.Revision == order.Revision) || orderLinks.Contains(id);
            if (item is null && !documentReached)
            {
                continue;
            }

            var started = order.Operations.Any(op => op.Status != OperationStatus.Pending) || issued.Contains(order.OrderNumber) || order.Status == OrderStatus.Held;
            var label = $"Order {id} ({order.PartNumber} rev {order.Revision}, {order.Status})";
            if (order.Status == OrderStatus.Completed)
            {
                Add($"order:{id}:completed", PropagationStepKind.CompletedUnchanged, id, order.PartNumber, $"{label}: keeps rev {order.Revision} and its frozen copy.");
            }
            else if (started)
            {
                Add($"order:{id}:disposition", PropagationStepKind.WipDisposition, id, order.PartNumber, $"{label}: work in progress; quality dispositions it.");
            }
            else
            {
                if (item is not null)
                {
                    Add($"order:{id}:move", PropagationStepKind.MoveOrder, id, order.PartNumber, $"{label} → rev {item.ToRevision}.");
                }

                if (order.Status == OrderStatus.Released)
                {
                    Add($"order:{id}:reissue", PropagationStepKind.ReissueDocumentation, id, order.PartNumber, $"{label}: documentation reissued with the revisions in force.");
                }
            }
        }

        foreach (var r in requirements)
        {
            if ((await engineering.GetEffectiveRevisionAsync(r.PartNumber, now, ct))?.Revision == r.Revision)
            {
                reachedParts.Add(r.PartNumber);
            }
        }

        foreach (var po in (await uow.Store<IProcurementLookup>().ListPurchaseOrdersAsync(ct)).Where(p => p.Status == PurchaseOrderStatus.Approved).OrderBy(p => p.PoNumber))
        {
            foreach (var poLine in po.Lines.Where(l => l.Status == PurchaseLineStatus.Open && reachedParts.Contains(l.PartNumber)))
            {
                var target = $"{po.PoNumber}/{poLine.LineNumber}";
                Add($"po:{target}:review", PropagationStepKind.PurchaseReview, target, poLine.PartNumber, $"PO {po.PoNumber} line {poLine.LineNumber} ({po.Supplier}, {poLine.Quantity:0.###} {poLine.PartNumber}): the buyer reviews it against the change.");
            }
        }

        var instructions = new List<(ChangeLineInfo Line, HashSet<string> WorkCenters)>();
        foreach (var line in documentLines)
        {
            var document = await documentLookup.FindDocumentAsync(line.DocumentNumber!, ct);
            if (document?.Type is not (DocumentType.WorkInstruction or DocumentType.Procedure))
            {
                continue;
            }

            var centers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in requirements.Where(r => r.DocumentNumber == document.Number))
            {
                if (await engineering.GetRevisionDetailAsync(r.PartNumber, r.Revision, ct) is { } detail)
                {
                    centers.UnionWith(detail.Routing.Where(op => r.OperationSequence is not { } seq || op.Sequence == seq).Select(op => op.WorkCenterCode));
                }
            }

            centers.UnionWith((await documentLookup.ListLinksAsync(DocumentTargetType.WorkCenter, null, document.Number, false, ct)).Select(l => l.TargetId));
            instructions.Add((line, centers));
        }

        if (instructions.Count > 0)
        {
            var workers = (await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(now, ct)).Where(w => w.IsActive).OrderBy(w => w.DisplayName, StringComparer.Ordinal).ToList();
            foreach (var (line, centers) in instructions)
            {
                foreach (var worker in workers.Where(w => centers.Contains(w.HomeWorkCenterCode)))
                {
                    var target = $"{line.DocumentNumber}/{line.ToRevision}/{worker.PersonId:D}";
                    Add($"ack:{target}", PropagationStepKind.TrainingAcknowledgment, target, null, $"{worker.DisplayName} ({worker.HomeWorkCenterCode}) acknowledges {line.DocumentNumber} rev {line.ToRevision}.");
                }
            }
        }

        return steps;
    }

    private async Task<(PropagationStepStatus Status, string Detail, string? Error)> ExecuteAsync(
        ChangeInfo change, ChangePropagationInfo propagation, PropagationStepInfo step, ForgePrincipal principal, bool inForce, Guid? releaseEventId, CancellationToken ct)
    {
        var context = Context(principal, propagation.CorrelationId, $"{change.Number}:{step.StepKey}", releaseEventId);
        try
        {
            faults.BeforeStep(change.Number, step);
            switch (step.Kind)
            {
                case PropagationStepKind.MoveOrder:
                    {
                        if (!inForce)
                        {
                            return (PropagationStepStatus.Waiting, step.Detail, null);
                        }

                        var to = change.ActiveLines.Single(l => l.Kind == ChangeLineKind.ItemRevision && l.PartNumber == step.PartNumber).ToRevision;
                        var result = await orders.ApplyChangeRevisionAsync(new ApplyChangeRevision(long.Parse(step.Target, CultureInfo.InvariantCulture), to, change.Number), context, ct);
                        return (PropagationStepStatus.Done, result.Message, null);
                    }

                case PropagationStepKind.ReissueDocumentation:
                    {
                        if (!inForce)
                        {
                            return (PropagationStepStatus.Waiting, step.Detail, null);
                        }

                        var moved = propagation.Steps.FirstOrDefault(s => s.Kind == PropagationStepKind.MoveOrder && s.Target == step.Target);
                        if (moved is not null && moved.Status != PropagationStepStatus.Done && !await MovedAsync(step.Target, change, ct))
                        {
                            return (PropagationStepStatus.Pending, step.Detail, null);
                        }

                        if (await StartedAsync(step.Target, ct))
                        {
                            return (PropagationStepStatus.Done, $"{step.Detail} {NotMoved} work started before propagation reached it; it gets a disposition instead.", null);
                        }

                        var result = await documents.ReissueAsync(new ReissueOrderDocumentation(long.Parse(step.Target, CultureInfo.InvariantCulture)), context, ct);
                        return (PropagationStepStatus.Done, result.Message, null);
                    }

                case PropagationStepKind.WipDisposition:
                case PropagationStepKind.PurchaseReview:
                case PropagationStepKind.TrainingAcknowledgment:
                    {
                        var raise = await RaiseCommandAsync(change, step, ct);
                        var result = await actions.RaiseAsync(raise, context, ct);
                        return (PropagationStepStatus.Done, result.Message, null);
                    }

                default:
                    {
                        await using var read = await unitOfWork.BeginSnapshotAsync(ct);
                        var order = await read.Store<IExecutionLookup>().FindOrderAsync(long.Parse(step.Target, CultureInfo.InvariantCulture), ct);
                        return order is { Status: OrderStatus.Completed } && step.Detail.Contains($"rev {order.Revision} ", StringComparison.Ordinal)
                            ? (PropagationStepStatus.Done, step.Detail, null)
                            : (PropagationStepStatus.Failed, step.Detail, $"Completed order {step.Target} no longer matches what was recorded.");
                    }
            }
        }
        catch (CommandRejectedException ex) when (step.Kind == PropagationStepKind.MoveOrder && ex.Code == "order_started")
        {
            // Work started (or the order closed) before propagation reached it: it is not moved; the plan raises a disposition instead.
            return (PropagationStepStatus.Done, $"{step.Detail} {NotMoved} {ex.Message}", null);
        }
        catch (CommandRejectedException ex)
        {
            return (PropagationStepStatus.Failed, step.Detail, $"{ex.Code}: {ex.Message}");
        }
        catch (ForgeAuthorizationException ex)
        {
            return (PropagationStepStatus.Failed, step.Detail, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Npgsql.NpgsqlException)
        {
            return (PropagationStepStatus.Failed, step.Detail, ex.Message);
        }
    }

    private async Task<bool> StartedAsync(string orderNumber, CancellationToken ct)
    {
        await using var read = await unitOfWork.BeginSnapshotAsync(ct);
        var number = long.Parse(orderNumber, CultureInfo.InvariantCulture);
        var order = await read.Store<IExecutionLookup>().FindOrderAsync(number, ct);
        return order is null
            || order.Status is OrderStatus.Held or OrderStatus.Completed or OrderStatus.Cancelled
            || order.Operations.Any(op => op.Status != OperationStatus.Pending)
            || (await read.Store<IInventoryLookup>().ListOrderIssuesAsync(ct)).Any(i => i.OrderNumber == number && i.NetIssued != 0);
    }

    private async Task<bool> MovedAsync(string orderNumber, ChangeInfo change, CancellationToken ct)
    {
        await using var read = await unitOfWork.BeginSnapshotAsync(ct);
        var order = await read.Store<IExecutionLookup>().FindOrderAsync(long.Parse(orderNumber, CultureInfo.InvariantCulture), ct);
        return order is not null && change.ActiveLines.Any(l => l.Kind == ChangeLineKind.ItemRevision && l.PartNumber == order.PartNumber && l.ToRevision == order.Revision);
    }

    private async Task<RaiseChangeAction> RaiseCommandAsync(ChangeInfo change, PropagationStepInfo step, CancellationToken ct)
    {
        await using var read = await unitOfWork.BeginSnapshotAsync(ct);
        switch (step.Kind)
        {
            case PropagationStepKind.WipDisposition:
                {
                    var order = await read.Store<IExecutionLookup>().FindOrderAsync(long.Parse(step.Target, CultureInfo.InvariantCulture), ct);
                    var line = change.ActiveLines.FirstOrDefault(l => l.Kind == ChangeLineKind.ItemRevision && l.PartNumber == step.PartNumber);
                    return new RaiseChangeAction(change.Number, ChangeActionKind.WipDisposition, step.Target, step.PartNumber, null, line?.ToRevision, null, order?.ProgramId, step.Detail);
                }

            case PropagationStepKind.PurchaseReview:
                {
                    var parts = step.Target.Split('/');
                    var package = await read.Store<ITechnicalPackageLookup>().FindPackageAsync(long.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), ct);
                    var sent = package is null
                        ? " No technical package was recorded for it (approved before V2.5)."
                        : $" Its package (recorded {package.RecordedAt:yyyy-MM-dd}) sent rev {package.ItemRevision ?? "—"}{(package.Documents.Count > 0 ? "; " + string.Join(", ", package.Documents.Select(d => $"{d.DocumentNumber} rev {d.Revision ?? "—"}")) : string.Empty)}.";
                    return new RaiseChangeAction(change.Number, ChangeActionKind.PurchaseReview, step.Target, step.PartNumber, null, null, null, null, step.Detail + sent);
                }

            default:
                {
                    var parts = step.Target.Split('/');
                    return new RaiseChangeAction(change.Number, ChangeActionKind.TrainingAcknowledgment, step.Target, null, parts[0], parts[1], Guid.Parse(parts[2]), null, step.Detail);
                }
        }
    }

    private async Task<Guid?> ReleaseEventIdAsync(string changeNumber, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT event_id FROM kernel.event_log WHERE event_type='EngineeringChangeReleased' AND entity_type='EngineeringChange' AND entity_id=$1 ORDER BY sequence DESC LIMIT 1", connection);
        cmd.Parameters.AddWithValue(changeNumber);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is Guid id ? id : null;
    }

    private static CommandContext Context(ForgePrincipal principal, Guid correlation, string key, Guid? causationId = null) =>
        new(principal, correlation, "system:propagation:" + key, CommandSource.System, Reason, SystemActors.Propagation, causationId);
}
