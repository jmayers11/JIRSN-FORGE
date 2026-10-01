using Forge.Competency.Contracts;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Engines;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Engines;
using Forge.Resources.Contracts;
using Forge.SafetyRules.Contracts;
using Forge.Workforce;
using Forge.Workforce.Contracts;

namespace Forge.Planning;

/// <summary>
/// Read-side entry point for Phase 2 planning: loads one consistent snapshot, runs the deterministic
/// engines and returns their results with evidence and fingerprints. Nothing here writes data.
/// </summary>
public sealed class ReadinessService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    private readonly OperationReadinessEngine readiness = new();
    private readonly ReallocationImpactEngine reallocation = new();
    private readonly OrderReadinessEngine orders = new();

    /// <summary>Readiness of each operation of a production order, now.</summary>
    public async Task<EngineRun<OrderReadinessResult>> EvaluateOrderAsync(ForgePrincipal principal, long orderNumber, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Demand(principal);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
        if (order is null || (order.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program))))
        {
            // A program-segregated order is reported exactly like a missing one.
            throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
        }

        var revision = (await uow.Store<IEngineeringLookup>().ListRevisionsAsync(order.PartNumber, ct)).SingleOrDefault(r => r.Revision == order.Revision);
        var snapshot = await LoadSnapshotAsync(uow, ct);
        return EngineRunner.Run(orders, snapshot, new OrderReadinessRequest(order, revision), snapshot.AsOf);
    }

    public async Task<EngineRun<ReadinessResult>> EvaluateMachineAsync(ForgePrincipal principal, string machineCode, string activityCode, CancellationToken ct)
    {
        Demand(principal);
        var machine = BusinessCode.Require(machineCode, "Machine");
        var activity = BusinessCode.Require(activityCode, "Activity");
        var snapshot = await LoadSnapshotAsync(ct);
        var info = snapshot.Machines.FirstOrDefault(m => m.Code == machine)
            ?? throw new CommandRejectedException("unknown_machine", $"Machine {machine} does not exist.");
        var work = new WorkItem(info.SafetyAreaCode, info.WorkCenterCode, info.Code, activity);
        return EngineRunner.Run(readiness, snapshot, work, snapshot.AsOf);
    }

    /// <summary>Readiness of everything people are working on now, optionally for one safety area.</summary>
    public async Task<IReadOnlyList<EngineRun<ReadinessResult>>> EvaluateActiveWorkAsync(ForgePrincipal principal, string? safetyAreaCode, CancellationToken ct)
    {
        Demand(principal);
        var area = safetyAreaCode is null ? null : BusinessCode.Require(safetyAreaCode, "Safety area");
        var snapshot = await LoadSnapshotAsync(ct);
        return snapshot.ActiveWork()
            .Where(w => area is null || w.SafetyAreaCode == area)
            .Select(w => EngineRunner.Run(readiness, snapshot, w, snapshot.AsOf))
            .ToList();
    }

    /// <summary>Impact of moving a person, evaluated on a copy of the current state. Nothing is committed.</summary>
    public async Task<EngineRun<ReallocationPreview>> PreviewReallocationAsync(ForgePrincipal principal, AssignWork proposal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        Demand(principal);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var proposed = await WorkforceService.ResolveAssignmentAsync(uow.Store<IResourcesLookup>(), proposal, ct);
        var snapshot = await LoadSnapshotAsync(uow, ct);
        if (snapshot.Person(proposal.PersonId.Value) is null)
        {
            throw new CommandRejectedException("worker_not_found", "This person is not a registered worker.");
        }

        return EngineRunner.Run(reallocation, snapshot, new ReallocationRequest(proposal.PersonId.Value, proposed), snapshot.AsOf);
    }

    public async Task<SiteSnapshot> LoadSnapshotAsync(CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return await LoadSnapshotAsync(uow, ct);
    }

    private async Task<SiteSnapshot> LoadSnapshotAsync(IKernelUnitOfWork uow, CancellationToken ct)
    {
        var at = time.GetUtcNow();
        at = new DateTimeOffset(at.UtcDateTime.Ticks / 10 * 10, TimeSpan.Zero); // database precision

        var resources = uow.Store<IResourcesLookup>();
        var workers = await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(at, ct);
        var qualifications = await uow.Store<ICompetencyLookup>().GetQualificationsAsync(workers.Select(w => new PersonId(w.PersonId)).ToList(), ct);
        var valid = qualifications
            .Where(q => q.IsValidAt(at))
            .GroupBy(q => q.PersonId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(q => q.Code).Order(StringComparer.Ordinal).ToList());

        var people = workers
            .Select(w => new PersonSnapshot(
                w.PersonId,
                w.DisplayName,
                w.IsActive,
                w.PresenceKind,
                w.PresentUntil,
                w.Unavailability,
                w.Assignment,
                valid.TryGetValue(w.PersonId, out var codes) ? codes : []))
            .OrderBy(p => p.PersonId)
            .ToList();

        return new SiteSnapshot(
            at,
            await resources.ListSafetyAreasAsync(ct),
            await resources.ListWorkCentersAsync(ct),
            await resources.ListMachinesAsync(ct),
            await resources.ListMachineActivitiesAsync(ct),
            await resources.ListWorkCenterActivitiesAsync(ct),
            await resources.ListActivityTypesAsync(ct),
            people,
            await uow.Store<ISafetyRuleLookup>().GetEffectiveRulesAsync(at, ct));
    }

    private void Demand(ForgePrincipal principal)
    {
        var decision = authorizer.Authorize(principal, Permissions.ReadinessRead, ResourceRef.Of("Readiness"));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }
    }
}
