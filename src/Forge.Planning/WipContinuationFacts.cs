using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;

namespace Forge.Planning;

/// <summary>An existing human disposition and its released engineering source, never an inferred waiver.</summary>
public sealed record WipContinuationEvidence(ChangeInfo Change, ChangeActionInfo Action, RevisionInfo SupersedingRevision)
{
    public bool Permits(OrderInfo order, RevisionInfo? revision, DateTimeOffset asOf)
    {
        if (revision is not { Status: RevisionStatus.Released, EffectiveFrom: { } from, EffectiveTo: { } to }
            || from > asOf || to > asOf || revision.PartNumber != order.PartNumber || revision.Revision != order.Revision
            || !order.Operations.Any(operation => operation.Status != OperationStatus.Pending)
            || Change.Status != ChangeStatus.Released || Change.ReleasedAt is not { } released || released > asOf
            || Action.ChangeNumber != Change.Number || Action.Kind != ChangeActionKind.WipDisposition
            || Action.Target != order.OrderNumber.ToString(CultureInfo.InvariantCulture) || Action.PartNumber != order.PartNumber
            || Action.ProgramId != order.ProgramId || Action.Status != ChangeActionStatus.Resolved
            || Action.Resolution != nameof(WipResolution.CompleteAtOldRevision)
            || string.IsNullOrWhiteSpace(Action.Notes) || string.IsNullOrWhiteSpace(Action.ResolvedBy)
            || Action.ResolvedAt is not { } resolved || resolved < released || resolved < Action.RaisedAt || resolved > asOf
            || SupersedingRevision.Status != RevisionStatus.Released || SupersedingRevision.PartNumber != order.PartNumber
            || SupersedingRevision.Revision != Action.Revision || SupersedingRevision.EffectiveFrom != to)
            return false;
        return Change.ActiveLines.Any(line => line.ChangeNumber == Change.Number && line.Kind == ChangeLineKind.ItemRevision
            && line.PartNumber == order.PartNumber && line.FromRevision == order.Revision && line.ToRevision == Action.Revision);
    }
}

/// <summary>Read each Contracts-backed action set once and cache distinct source changes/revisions in the same unit of work.</summary>
internal static class WipContinuationFacts
{
    public static async Task<IReadOnlyDictionary<long, WipContinuationEvidence>> LoadAsync(IKernelUnitOfWork uow,
        IReadOnlyList<OrderInfo> orders, DateTimeOffset asOf, CancellationToken ct)
    {
        var result = new Dictionary<long, WipContinuationEvidence>();
        if (orders.Count == 0) return result;
        var byTarget = orders.ToDictionary(order => order.OrderNumber.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var actions = (await uow.Store<IPropagationLookup>().ListActionsAsync(null, false, ct))
            .Where(action => action.Kind == ChangeActionKind.WipDisposition && action.Status == ChangeActionStatus.Resolved
                && action.Resolution == nameof(WipResolution.CompleteAtOldRevision)
                && byTarget.TryGetValue(action.Target, out var order) && action.PartNumber == order.PartNumber)
            .ToList();
        var changes = new Dictionary<string, ChangeInfo?>(StringComparer.Ordinal);
        var revisions = new Dictionary<string, IReadOnlyList<RevisionInfo>>(StringComparer.Ordinal);
        foreach (var group in actions.GroupBy(action => action.Target, StringComparer.Ordinal))
        {
            var order = byTarget[group.Key];
            if (!revisions.TryGetValue(order.PartNumber, out var partRevisions))
                revisions.Add(order.PartNumber, partRevisions = await uow.Store<IEngineeringLookup>().ListRevisionsAsync(order.PartNumber, ct));
            var old = partRevisions.SingleOrDefault(revision => revision.Revision == order.Revision);
            var valid = new List<WipContinuationEvidence>();
            foreach (var action in group)
            {
                if (!changes.TryGetValue(action.ChangeNumber, out var change))
                    changes.Add(action.ChangeNumber, change = await uow.Store<IChangeLookup>().FindChangeAsync(action.ChangeNumber, ct));
                var successor = partRevisions.SingleOrDefault(revision => revision.Revision == action.Revision);
                if (change is null || successor is null) continue;
                var evidence = new WipContinuationEvidence(change, action, successor);
                if (evidence.Permits(order, old, asOf)) valid.Add(evidence);
            }
            // Ambiguous source records do not become a last-write-wins authority decision.
            if (valid.Count == 1) result.Add(order.OrderNumber, valid[0]);
        }
        return result;
    }
}
