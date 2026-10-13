using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Engines;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Engines;
using Forge.Quality.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Planning;

/// <summary>Authoritative operation-start readiness, using the existing command unit of work.</summary>
public sealed class OperationStartCheck(TimeProvider time) : IOperationStartCheck
{
    public async Task<OperationStartEvidence> RequireReadyAsync(IKernelUnitOfWork uow, OrderInfo order, OrderOperationInfo operation,
        string? machineCode, Guid operatorId, Guid? trainerId, ForgePrincipal terminal,
        DateTimeOffset now, CancellationToken ct)
    {
        var snapshot = await SnapshotLoader.LoadAsync(uow, time, ct);
        var center = snapshot.WorkCenters.FirstOrDefault(w => w.Code == operation.WorkCenterCode && w.Active)
            ?? throw new CommandRejectedException("operation_not_ready", "The operation's work center is no longer available.");
        var assignment = new WorkAssignment(center.SafetyAreaCode, center.Code, machineCode, operation.ActivityCode, trainerId is not null);
        snapshot = snapshot.WithAssignment(operatorId, assignment);
        if (trainerId is { } trainer)
        {
            snapshot = snapshot.WithAssignment(trainer, assignment with { AsTrainee = false });
        }

        var people = new List<ForgePrincipal> { terminal };
        foreach (var id in new[] { (Guid?)operatorId, trainerId }.OfType<Guid>().Distinct())
        {
            var person = await uow.People.LoadPrincipalAsync(new PersonId(id), ct)
                ?? throw new CommandRejectedException("worker_not_found", "A selected worker is no longer active.");
            if (order.ProgramId is { } program && !person.Programs.Contains(new ProgramId(program)))
            {
                throw new CommandRejectedException("order_not_found", "The order is not available to a selected worker.");
            }
            people.Add(person);
        }

        // Keep the real document identities for applicability and training. Clearance is checked
        // separately so redaction cannot silently remove a controlled training requirement.
        var documentation = await DocumentationService.ForOrderAsync(uow, order, now, null, ct);
        var documentSources = new List<ResourceRef>();
        foreach (var requirement in documentation.Documents.Where(d => d.OperationSequence is null || d.OperationSequence == operation.Sequence))
        {
            var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(requirement.DocumentNumber, ct);
            if (document is not null && people.Any(p => !DocumentAccess.CanSee(p, document)))
            {
                throw new CommandRejectedException("operation_not_ready", "Documentation: a required controlled document is restricted for a selected worker or terminal.");
            }
            if (document is not null)
            {
                var revisionAtUse = requirement.AtRelease ?? requirement.Applicable ?? requirement.PinnedRevision;
                documentSources.Add(new ResourceRef(revisionAtUse is null ? "Document" : "DocumentRevision",
                    revisionAtUse is null ? document.Number : $"{document.Number} rev {revisionAtUse}",
                    document.ProgramId is { } program ? new ProgramId(program) : null, document.Markings));
            }
        }

        var material = (await MaterialPlanningService.RunMaterialAsync(uow, time, ct, terminal)).Output.FirstOrDefault(m => m.OrderNumber == order.OrderNumber);
        var quality = await uow.Store<IQualityLookup>().GetOrderQualityAsync(order.OrderNumber, order.PartNumber, ct);
        var revision = (await uow.Store<IEngineeringLookup>().ListRevisionsAsync(order.PartNumber, ct)).SingleOrDefault(r => r.Revision == order.Revision);
        // Readiness must judge the selected machine, even when another eligible machine is ready.
        var selected = operation with { EligibleMachines = machineCode is null ? [] : [machineCode] };
        var selectedOrder = order with { Operations = order.Operations.Select(o => o.Sequence == selected.Sequence ? selected : o).ToList() };
        var continuations = await WipContinuationFacts.LoadAsync(uow, [selectedOrder], now, ct);
        var request = new OrderReadinessRequest(selectedOrder, revision, material, quality, documentation,
            await ReadinessService.PointOfUseAsync(uow, now, ct), Continuation: continuations.GetValueOrDefault(order.OrderNumber));
        var result = new OrderReadinessEngine().Evaluate(snapshot, request, now).Output.Operations.Single(o => o.Sequence == operation.Sequence);
        var failures = result.Dimensions.Where(d => d.Outcome is EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate).ToList();

        // The actual worker (and supervisor when training), rather than a different available
        // qualified candidate, must have the required controlled-document training.
        var training = await ReadinessService.TrainingAsync(uow, documentation, ct);
        foreach (var id in new[] { (Guid?)operatorId, trainerId }.OfType<Guid>().Distinct())
        {
            var person = snapshot.Person(id);
            if (ExecutableChecks.Training(training, operation.Sequence, person is null ? [] : [person], 1) is { Outcome: EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate } untrained)
            {
                failures.Add(untrained);
            }
        }
        if (failures.Count > 0)
        {
            throw new CommandRejectedException("operation_not_ready", string.Join(" ", failures.Select(d => d.Name is "Documentation" or "Training"
                ? $"{d.Name}: the selected operation's controlled-document requirement is not satisfied."
                : $"{d.Name}: {d.Detail}")));
        }
        return new OperationStartEvidence($"order-readiness/{OrderReadinessEngine.CurrentVersion}: " + string.Join(" ", result.Dimensions.Select(d => $"{d.Name}: {d.Outcome}.")),
            documentSources.Aggregate(DataMarking.None, (markings, source) => markings | source.Markings), documentSources);
    }
}
