using System.Globalization;
using Forge.Competency.Contracts;
using Forge.Engineering.Contracts;
using Forge.Execution;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Planning;
using Forge.Programs.Contracts;
using Forge.Quality.Contracts;
using Forge.Workforce;
using Forge.Workforce.Contracts;

namespace Forge.Host.Planning;

public sealed record ProposeReplacementOrder(string PartNumber, decimal Quantity, DateOnly DueDate, int Priority, long? NcrId, long? ReplacesOrderNumber, string Rationale);

public sealed record DecideReplacementOrder(Guid ProposalId, bool Approve, string Note, bool AcceptUncoveredOperations = false);

/// <summary>
/// Manufacturing replacement recovery (Sprint 10 WW-S10-35). When production is scrapped or lost, a planner or engineer proposes a
/// replacement order. Before anyone approves it, Forge checks every operation of the part's released routing with the workforce
/// recovery engine: whether it could run with the people there now, who else holds the current qualifications and machine
/// authorization, and what moving each of them would leave unstaffed in their source work center. The check is stored with the
/// proposal. A different person who may release orders approves it (creating a planned order through the normal order rules) or
/// rejects it. Nobody is assigned or moved by a proposal, and an operation nobody can cover must be accepted explicitly.
/// </summary>
/// <remarks>This orchestration spans Planning, Execution, Engineering and Quality, so it lives in the host.</remarks>
public sealed class ReplacementOrderService(CommandPipeline pipeline, ReadinessService readiness, IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer)
{
    private const string SourceContext = "Planning";
    private const string Entity = "ReplacementOrderProposal";
    private const int MaximumChecks = 24;

    public async Task<CommandResult> ProposeAsync(ProposeReplacementOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        // Refuse callers who could not propose before running any check on their behalf.
        var allowed = authorizer.Authorize(context.Principal, Permissions.OrdersManage, ResourceRef.Of(Entity));
        if (!allowed.IsPermitted) throw new ForgeAuthorizationException(allowed);

        var checkedAt = pipeline.Time.GetUtcNow();
        var (revision, checks) = await CheckAsync(context.Principal, part, checkedAt, ct);
        var spec = new CommandSpec(SourceContext, nameof(ProposeReplacementOrder), Permissions.OrdersManage, ResourceRef.Of(Entity), ReasonRequired: true);
        return await pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (c.Quantity is <= 0 or > 1_000_000 || c.Priority is < 1 or > 9)
                throw new CommandRejectedException("invalid_order", "Quantity must be above zero and priority between 1 (highest) and 9.");
            var rationale = BusinessCode.RequireName(c.Rationale, "Rationale");
            if (rationale.Length > 800) throw new CommandRejectedException("rationale_too_long", "Keep the rationale to 800 characters.");
            await RequireRevisionAsync(uow, part, revision, now, ct);

            // What it replaces: a scrapped NCR of the same part, and/or an order of the same part; the program carries over.
            var execution = uow.Store<IExecutionLookup>();
            Guid? program = null;
            long? replaces = c.ReplacesOrderNumber;
            if (c.NcrId is { } ncrId)
            {
                var ncr = await uow.Store<IQualityLookup>().FindNcrAsync(ncrId, ct);
                if (ncr is null || ncr.PartNumber != part)
                    throw new CommandRejectedException("ncr_not_found", $"NCR {ncrId} for {part} does not exist.");
                if (ncr.Disposition != Disposition.Scrap)
                    throw new CommandRejectedException("ncr_not_scrapped", $"NCR {ncrId} is not dispositioned scrap; only scrapped product is replaced.");
                if (c.Quantity > ncr.Quantity)
                    throw new CommandRejectedException("quantity_exceeds_scrap", $"NCR {ncrId} scrapped {Qty(ncr.Quantity)}; a replacement for it is at most that.");
                replaces ??= ncr.OrderNumber;
            }

            if (replaces is { } number)
            {
                var order = await execution.FindOrderAsync(number, ct);
                if (order is null || order.PartNumber != part || (order.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p))))
                    throw new CommandRejectedException("order_not_found", $"Order {number} for {part} does not exist.");
                program = order.ProgramId;
            }

            var proposal = new ReplacementOrderProposal(ForgeIds.New(pipeline.Time), part, revision, c.Quantity, c.DueDate, c.Priority, c.NcrId, replaces, program,
                ReplacementOrderStatus.Proposed, checks, checkedAt, rationale, context.Actor, PersonOf(context), now);
            await uow.Store<IReplacementOrderStore>().InsertAsync(proposal, ct);
            var id = proposal.ProposalId.ToString("D");
            var message = $"Replacement order proposed: {Qty(c.Quantity)} x {part} rev {revision}, due {c.DueDate:yyyy-MM-dd}"
                + $"{(c.NcrId is { } n ? $", replacing scrap on NCR {n}" : replaces is { } o ? $", replacing order {o}" : string.Empty)}. {Coverage(checks)} A planner decides it; no order exists until then.";
            var scope = program is { } g ? new ProgramId(g) : (ProgramId?)null;
            return new CommandOutcome(CommandResult.Ok(id, message),
                [pipeline.Event(new ReplacementOrderProposed(proposal.ProposalId, part, c.Quantity, message), context, SourceContext, Entity, id, scope)],
                new AuditChange(Entity, id, null, CanonicalJson.Serialize(proposal), scope));
        }, ct);
    }

    public Task<CommandResult> DecideAsync(DecideReplacementOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = c.ProposalId.ToString("D");
        var spec = new CommandSpec(SourceContext, nameof(DecideReplacementOrder), Permissions.OrdersRelease, ResourceRef.Of(Entity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IReplacementOrderStore>();
            var prior = await store.FindAsync(c.ProposalId, ct);
            if (prior is null || (prior.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p))))
                throw new CommandRejectedException("proposal_not_found", "The replacement order proposal does not exist.");
            if (prior.Status != ReplacementOrderStatus.Proposed)
                throw new CommandRejectedException("proposal_decided", $"The proposal was already {prior.Status.ToString().ToLowerInvariant()}.");
            if (string.Equals(prior.ProposedBy, context.Actor, StringComparison.Ordinal) || (prior.ProposedByPersonId is { } proposer && PersonOf(context) == proposer))
                throw new CommandRejectedException("independent_review_required", "The person who proposed a replacement order cannot decide it.");
            var note = BusinessCode.RequireName(c.Note, "Decision note");
            if (note.Length > 800) throw new CommandRejectedException("note_too_long", "Keep the decision note to 800 characters.");

            var scope = prior.ProgramId is { } g ? new ProgramId(g) : (ProgramId?)null;
            var decided = prior with
            {
                Status = c.Approve ? ReplacementOrderStatus.Approved : ReplacementOrderStatus.Rejected, DecidedBy = context.Actor, DecidedByPersonId = PersonOf(context),
                DecidedAt = now, DecisionNote = note, Version = prior.Version + 1,
            };
            var events = new List<EventEnvelope>();
            string message;
            if (c.Approve)
            {
                var uncovered = prior.Checks.Where(check => !check.Covered).ToList();
                if (uncovered.Count > 0 && !c.AcceptUncoveredOperations)
                    throw new CommandRejectedException("uncovered_operations",
                        $"Nobody qualified could cover {string.Join(", ", uncovered.Select(Describe))} when this was checked. Accept that explicitly to approve, or reject it.");
                // The checks were made against this revision's routing; a different revision in force needs a new proposal.
                await RequireRevisionAsync(uow, prior.PartNumber, prior.Revision, now, ct);
                if (prior.ProgramId is { } programId && await uow.Store<IProgramsLookup>().FindProgramAsync(programId, ct) is not { Status: ProgramStatus.Active })
                    throw new CommandRejectedException("program_closed", "The program is closed; it takes no new orders.");
                var number = await uow.Store<IExecutionStore>().InsertOrderAsync(prior.PartNumber, prior.Revision, prior.Quantity, prior.DueDate, prior.Priority, prior.ProgramId, now, context.Actor, ct);
                decided = decided with { OrderNumber = number };
                var orderId = number.ToString(CultureInfo.InvariantCulture);
                events.Add(pipeline.Event(new ProductionOrderCreated(number, prior.PartNumber, prior.Revision, prior.Quantity, prior.DueDate), context, "Execution", "ProductionOrder", orderId, scope));
                message = $"Replacement order approved: order {orderId} planned, {Qty(prior.Quantity)} x {prior.PartNumber} rev {prior.Revision}, due {prior.DueDate:yyyy-MM-dd}."
                    + $"{(uncovered.Count > 0 ? $" Approved knowing {uncovered.Count} operation(s) had no qualified cover." : string.Empty)} It is released and staffed through the normal steps; nobody was assigned.";
            }
            else
            {
                message = $"Replacement order for {Qty(prior.Quantity)} x {prior.PartNumber} rejected; no order was created.";
            }

            if (!await store.DecideAsync(decided, prior.Version, ct))
                throw new CommandRejectedException("proposal_changed", "Someone else decided this proposal first. Refresh and review it.");
            events.Insert(0, pipeline.Event(new ReplacementOrderDecided(decided.ProposalId, decided.Status.ToString(), decided.OrderNumber, message), context, SourceContext, Entity, id, scope));
            return new CommandOutcome(CommandResult.Ok(id, message), events, new AuditChange(Entity, id, CanonicalJson.Serialize(prior), CanonicalJson.Serialize(decided), scope));
        }, ct);
    }

    public async Task<IReadOnlyList<ReplacementOrderProposal>> ListAsync(ForgePrincipal principal, bool openOnly, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var allowed = authorizer.Authorize(principal, Permissions.OrdersRead, ResourceRef.Of(Entity));
        if (!allowed.IsPermitted) throw new ForgeAuthorizationException(allowed);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return (await uow.Store<IReplacementOrderStore>().ListAsync(openOnly, ct))
            .Where(p => p.ProgramId is not { } g || principal.Programs.Contains(new ProgramId(g))).ToList();
    }

    private async Task<(string Revision, List<ReplacementOperationCheck> Checks)> CheckAsync(ForgePrincipal principal, string part, DateTimeOffset at, CancellationToken ct)
    {
        IReadOnlyList<RoutingOperationInfo> routing;
        string revision;
        List<WorkerState> workers;
        ILookup<Guid, string> held;
        await using (var uow = await unitOfWork.BeginSnapshotAsync(ct))
        {
            workers = (await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(at, ct)).Where(w => w.IsActive).ToList();
            held = (await uow.Store<ICompetencyLookup>().GetQualificationsAsync(workers.Select(w => new PersonId(w.PersonId)).ToList(), ct))
                .Where(q => q.IsValidAt(at)).ToLookup(q => q.PersonId, q => q.Code);
            var engineering = uow.Store<IEngineeringLookup>();
            var item = await engineering.FindItemAsync(part, ct) ?? throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            if (item.MakeBuy != MakeBuy.Make) throw new CommandRejectedException("not_made", $"Part {part} is purchased, not made.");
            var effective = await engineering.GetEffectiveRevisionAsync(part, at, ct)
                ?? throw new CommandRejectedException("no_released_revision", $"Part {part} has no released revision in force.");
            revision = effective.Revision;
            routing = (await engineering.GetRevisionDetailAsync(part, revision, ct))?.Routing ?? [];
        }

        if (routing.Count == 0) throw new CommandRejectedException("no_routing", $"{part} rev {revision} has no routing to check.");
        var checks = new List<ReplacementOperationCheck>();
        foreach (var op in routing.OrderBy(o => o.Sequence))
            foreach (var machine in op.EligibleMachines.Count == 0 ? [null] : op.EligibleMachines.Order(StringComparer.Ordinal).Select(m => (string?)m))
            {
                if (checks.Count == MaximumChecks)
                    throw new CommandRejectedException("routing_too_large", $"A replacement check covers at most {MaximumChecks} operation and machine combinations.");
                checks.Add(await CheckOperationAsync(principal, op, machine, workers, held, ct));
            }

        return (revision, checks);
    }

    private async Task<ReplacementOperationCheck> CheckOperationAsync(ForgePrincipal principal, RoutingOperationInfo op, string? machine, IReadOnlyList<WorkerState> workers,
        ILookup<Guid, string> held, CancellationToken ct)
    {
        // The recovery engine checks the machine's operator qualifications, authorization and safety composition; the routing
        // operation's own required qualifications are checked here against each person's qualifications valid now.
        bool Holds(Guid person) => op.RequiredQualifications.All(code => held[person].Contains(code, StringComparer.Ordinal));
        try
        {
            var run = await readiness.RecommendWorkforceRecoveryAsync(principal, new AssignWork(new PersonId(Guid.Empty), null, op.WorkCenterCode, machine, op.ActivityCode, false), ct);
            var result = run.Output;
            var lacking = result.Candidates.Count(candidate => !Holds(candidate.PersonId));
            var onIt = workers.Where(w => w.PresenceKind is not null && w.Unavailability is null && w.Assignment is { } a && a.WorkCenterCode == op.WorkCenterCode
                && a.ActivityCode == op.ActivityCode && (machine is null || a.MachineCode == machine)).ToList();
            var readyNow = result.TargetReadyBefore && onIt.Any(w => Holds(w.PersonId));
            var constraint = result.TargetReadyBefore && !readyNow
                ? $"Nobody on it now holds {string.Join(", ", op.RequiredQualifications)}."
                : result.CurrentConstraint;
            IReadOnlyList<string> limitations = lacking == 0 ? result.Limitations
                : [.. result.Limitations, $"{lacking} more person(s) could run the equipment but lack {string.Join(", ", op.RequiredQualifications)}, which the operation requires; they are not listed."];
            var candidates = result.Candidates.Where(candidate => Holds(candidate.PersonId)).Select(candidate => new ReplacementCandidate(candidate.PersonId, candidate.PersonName, candidate.RequiresSourceBackfill,
                candidate.SourceImpacts.Select(impact => impact.Detail).ToList(),
                [.. candidate.Evidence, .. candidate.Moves.Where(move => move.PersonId != candidate.PersonId).Select(move => $"Backfill: {move.PersonName} to {Work(move.Assignment)}.")])).ToList();
            return new ReplacementOperationCheck(op.Sequence, op.Description, op.WorkCenterCode, machine, op.ActivityCode, readyNow, readyNow ? null : constraint, candidates, limitations);
        }
        catch (ArgumentException ex)
        {
            // Too many visible people to rank: the check is reported as not made rather than guessed.
            return new ReplacementOperationCheck(op.Sequence, op.Description, op.WorkCenterCode, machine, op.ActivityCode, false, "Not checked", [], [ex.Message]);
        }
    }

    private static async Task RequireRevisionAsync(IKernelUnitOfWork uow, string part, string revision, DateTimeOffset now, CancellationToken ct)
    {
        var effective = await uow.Store<IEngineeringLookup>().GetEffectiveRevisionAsync(part, now, ct)
            ?? throw new CommandRejectedException("no_released_revision", $"Part {part} has no released revision in force.");
        if (effective.Revision != revision)
            throw new CommandRejectedException("revision_changed", $"{part} rev {effective.Revision} is now in force; the operator check was for rev {revision}. Propose again.");
    }

    private static string Coverage(IReadOnlyList<ReplacementOperationCheck> checks)
    {
        var uncovered = checks.Where(c => !c.Covered).ToList();
        var moves = checks.Count(c => !c.ReadyNow && c.Candidates.Count > 0);
        return uncovered.Count == 0
            ? moves == 0 ? "Every operation can run with the people there now." : $"{moves} operation(s) need someone moved; the source-center effect of each option is recorded."
            : $"Nobody qualified could cover {string.Join(", ", uncovered.Select(Describe))}.";
    }

    private static string Describe(ReplacementOperationCheck c) => $"op {c.Sequence} {c.ActivityCode} on {c.MachineCode ?? c.WorkCenterCode}";

    private static string Work(WorkAssignment a) => $"{a.ActivityCode} on {a.MachineCode ?? a.WorkCenterCode ?? a.SafetyAreaCode}";

    private static Guid? PersonOf(CommandContext context) => context.Principal.PersonId.Value == Guid.Empty ? null : context.Principal.PersonId.Value;

    private static string Qty(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
