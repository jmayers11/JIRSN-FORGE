using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Workforce;

/// <summary>Moves a worker to another work center for a while, with a required estimated end date (ADR 0057 §5).</summary>
public sealed record StartTemporaryMove(PersonId PersonId, string ToWorkCenterCode, DateOnly StartsOn, DateOnly EstimatedEndOn);

/// <summary>
/// Extends an active temporary move to a later end date, or renews one that has ended: a new move to the same work
/// center from today. Either way the new end date and the reason are recorded.
/// </summary>
public sealed record ExtendTemporaryMove(long MoveId, DateOnly NewEndOn, bool Renew = false);

/// <summary>Ends an active temporary move early: the worker is back home from today.</summary>
public sealed record EndTemporaryMove(long MoveId);

/// <summary>Permanently changes a worker's home work center from a date (ADR 0057 §5). A separate, deliberate action.</summary>
public sealed record TransferWorker(PersonId PersonId, string ToWorkCenterCode, DateOnly EffectiveFrom);

/// <summary>System: reminds supervisors of moves about to end and returns workers whose moves ran out (ADR 0057 §5).</summary>
public sealed record ReconcileTemporaryMoves(DateOnly Today);

public sealed record TemporaryMoveStarted(long MoveId, Guid PersonId, string FromWorkCenter, string WorkCenterCode, DateOnly StartsOn, DateOnly EndsOn, string Message) : IDomainEvent
{
    public static string EventType => nameof(TemporaryMoveStarted);

    public static int SchemaVersion => 1;
}

public sealed record TemporaryMoveChanged(long MoveId, Guid PersonId, string Kind, DateOnly PreviousEndsOn, DateOnly EndsOn, string WorkCenterCode, string Message) : IDomainEvent
{
    public static string EventType => nameof(TemporaryMoveChanged);

    public static int SchemaVersion => 1;
}

public sealed record TemporaryMoveEndingSoon(long MoveId, Guid PersonId, DateOnly EndsOn, string WorkCenterCode, string Message) : IDomainEvent
{
    public static string EventType => nameof(TemporaryMoveEndingSoon);

    public static int SchemaVersion => 1;
}

/// <summary>A temporary move ended. <paramref name="WorkCenterCode"/> is the home work center the worker is back in.</summary>
public sealed record TemporaryMoveEnded(long MoveId, Guid PersonId, string Outcome, DateOnly EndsOn, string WorkCenterCode, string Message) : IDomainEvent
{
    public static string EventType => nameof(TemporaryMoveEnded);

    public static int SchemaVersion => 1;
}

public sealed record WorkerTransferred(long TransferId, Guid PersonId, string FromWorkCenter, string WorkCenterCode, DateOnly EffectiveFrom, string Message) : IDomainEvent
{
    public static string EventType => nameof(WorkerTransferred);

    public static int SchemaVersion => 1;
}

public sealed partial class WorkforceService
{
    /// <summary>How long one temporary move may be planned for at a time; longer is a transfer.</summary>
    public const int MaximumMoveDays = 180;

    /// <summary>Supervisors are reminded this many working days before a move's end date (ADR 0057 §5.4).</summary>
    public const int ReminderWorkingDays = 2;

    private const string MoveEntity = "TemporaryMove";

    public Task<CommandResult> StartTemporaryMoveAsync(StartTemporaryMove c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var spec = new CommandSpec(SourceContext, nameof(StartTemporaryMove), Permissions.WorkforceSupervise, ResourceRef.Of(WorkerEntity, c.PersonId), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var today = await uow.SiteDateAsync(now, ct);
            var worker = await ActiveWorkerAsync(uow, c.PersonId, now, ct);
            var to = await ActiveWorkCenterAsync(uow, c.ToWorkCenterCode, ct);
            if (c.StartsOn < today)
            {
                throw new CommandRejectedException("move_in_past", $"A temporary move starts today ({today:yyyy-MM-dd}) or later.");
            }

            if (c.EstimatedEndOn < c.StartsOn || c.EstimatedEndOn.DayNumber - c.StartsOn.DayNumber >= MaximumMoveDays)
            {
                throw new CommandRejectedException("invalid_move_end", $"The estimated end date is required, on or after the start, within {MaximumMoveDays} days. A longer move is a transfer.");
            }

            var lookup = uow.Store<IWorkforceLookup>();
            var home = await HomeOnAsync(uow, worker, c.StartsOn, ct);
            if (home == to)
            {
                throw new CommandRejectedException("already_home", $"{worker.DisplayName}'s home work center is {to} then.");
            }

            if ((await lookup.ListTemporaryMovesAsync(c.PersonId.Value, false, ct)).FirstOrDefault(m => m.StartsOn <= c.EstimatedEndOn && c.StartsOn <= m.EndsOn) is { } overlap)
            {
                throw new CommandRejectedException("move_overlaps", $"{worker.DisplayName} is already on move {overlap.MoveId} to {overlap.ToWorkCenter} from {overlap.StartsOn:yyyy-MM-dd} to {overlap.EndsOn:yyyy-MM-dd}. Extend or end it instead.");
            }

            var reason = Reason(context);
            var moveId = await uow.Store<IWorkforceStore>().InsertTemporaryMoveAsync(c.PersonId.Value, home, to, c.StartsOn, c.EstimatedEndOn, now, context.Actor, ct);
            var id = moveId.ToString(CultureInfo.InvariantCulture);
            var message = $"{worker.DisplayName} moves from {home} to {to} from {c.StartsOn:yyyy-MM-dd} until about {c.EstimatedEndOn:yyyy-MM-dd}.";
            return new CommandOutcome(
                CommandResult.Ok(id, message),
                [pipeline.Event(new TemporaryMoveStarted(moveId, c.PersonId.Value, home, to, c.StartsOn, c.EstimatedEndOn, message), context, SourceContext, MoveEntity, id)],
                new AuditChange(MoveEntity, id, null, CanonicalJson.Serialize(new { person = c.PersonId.Value, from = home, to, c.StartsOn, endsOn = c.EstimatedEndOn, reason })));
        }, ct);
    }

    public Task<CommandResult> ExtendTemporaryMoveAsync(ExtendTemporaryMove c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var id = c.MoveId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, c.Renew ? "RenewTemporaryMove" : nameof(ExtendTemporaryMove), Permissions.WorkforceSupervise, ResourceRef.Of(MoveEntity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var today = await uow.SiteDateAsync(now, ct);
            var store = uow.Store<IWorkforceStore>();
            var move = await store.FindTemporaryMoveAsync(c.MoveId, ct) ?? throw new CommandRejectedException("move_not_found", $"Temporary move {id} does not exist.");
            var reason = Reason(context);
            if (c.Renew && move.Status != TemporaryMoveStatus.Active)
            {
                // A move that has run out or ended is renewed as a new move from today, so the days in between stay as they were.
                if (c.NewEndOn < today || c.NewEndOn.DayNumber - today.DayNumber >= MaximumMoveDays)
                {
                    throw new CommandRejectedException("invalid_move_end", $"The new end date is today or later, within {MaximumMoveDays} days.");
                }

                var worker = await ActiveWorkerAsync(uow, new PersonId(move.PersonId), now, ct);
                await ActiveWorkCenterAsync(uow, move.ToWorkCenter, ct);
                var home = await HomeOnAsync(uow, worker, today, ct);
                if ((await uow.Store<IWorkforceLookup>().ListTemporaryMovesAsync(move.PersonId, false, ct)).FirstOrDefault(m => m.StartsOn <= c.NewEndOn && today <= m.EndsOn) is { } overlap)
                {
                    throw new CommandRejectedException("move_overlaps", $"{worker.DisplayName} is already on move {overlap.MoveId}. Extend it instead.");
                }

                var renewed = await store.InsertTemporaryMoveAsync(move.PersonId, home, move.ToWorkCenter, today, c.NewEndOn, now, context.Actor, ct);
                await store.ChangeTemporaryMoveAsync(c.MoveId, "Renewed", move.EndsOn, move.EndsOn, now, context.Actor, ct);
                var newId = renewed.ToString(CultureInfo.InvariantCulture);
                var renewedMessage = $"{move.DisplayName}'s move to {move.ToWorkCenter} is renewed from {today:yyyy-MM-dd} until about {c.NewEndOn:yyyy-MM-dd} (move {newId}).";
                return new CommandOutcome(
                    CommandResult.Ok(newId, renewedMessage),
                    [pipeline.Event(new TemporaryMoveStarted(renewed, move.PersonId, home, move.ToWorkCenter, today, c.NewEndOn, renewedMessage), context, SourceContext, MoveEntity, newId)],
                    new AuditChange(MoveEntity, newId, id, CanonicalJson.Serialize(new { renewalOf = c.MoveId, startsOn = today, endsOn = c.NewEndOn, reason })));
            }

            if (move.Status != TemporaryMoveStatus.Active)
            {
                throw new CommandRejectedException("move_not_active", $"Move {id} is {move.Status}; renew it to move {move.DisplayName} again.");
            }

            if (c.NewEndOn <= move.EndsOn || c.NewEndOn < today || c.NewEndOn.DayNumber - move.StartsOn.DayNumber >= MaximumMoveDays * 2)
            {
                throw new CommandRejectedException("invalid_move_end", $"The new end date must be after {move.EndsOn:yyyy-MM-dd} and not in the past. To end the move sooner, end it.");
            }

            var kind = c.Renew ? "Renewed" : "Extended";
            await store.ChangeTemporaryMoveAsync(c.MoveId, kind, move.EndsOn, c.NewEndOn, now, context.Actor, ct);
            var message = $"{move.DisplayName}'s move to {move.ToWorkCenter} is {kind.ToLowerInvariant()} until about {c.NewEndOn:yyyy-MM-dd} (was {move.EndsOn:yyyy-MM-dd}).";
            return new CommandOutcome(
                CommandResult.Ok(id, message),
                [pipeline.Event(new TemporaryMoveChanged(c.MoveId, move.PersonId, kind, move.EndsOn, c.NewEndOn, move.ToWorkCenter, message), context, SourceContext, MoveEntity, id)],
                new AuditChange(MoveEntity, id, move.EndsOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), CanonicalJson.Serialize(new { kind, endsOn = c.NewEndOn, reason })));
        }, ct);
    }

    public Task<CommandResult> EndTemporaryMoveAsync(EndTemporaryMove c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var id = c.MoveId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(EndTemporaryMove), Permissions.WorkforceSupervise, ResourceRef.Of(MoveEntity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var today = await uow.SiteDateAsync(now, ct);
            var store = uow.Store<IWorkforceStore>();
            var move = await store.FindTemporaryMoveAsync(c.MoveId, ct) ?? throw new CommandRejectedException("move_not_found", $"Temporary move {id} does not exist.");
            if (move.Status != TemporaryMoveStatus.Active)
            {
                throw new CommandRejectedException("move_not_active", $"Move {id} is already {move.Status}.");
            }

            // Back home from today; a move that had not started yet is cancelled (it ends the day before it would have begun).
            var last = today.AddDays(-1) < move.StartsOn ? move.StartsOn.AddDays(-1) : today.AddDays(-1);
            var reason = Reason(context);
            await store.CloseTemporaryMoveAsync(c.MoveId, TemporaryMoveStatus.EndedEarly, last, now, context.Actor, ct);
            var home = await HomeOnAsync(uow, new WorkerRef(move.PersonId, move.DisplayName, move.FromWorkCenter), today, ct);
            var message = last < move.StartsOn
                ? $"{move.DisplayName}'s move to {move.ToWorkCenter} is cancelled; {move.DisplayName} stays in {home}."
                : $"{move.DisplayName}'s move to {move.ToWorkCenter} ended early; {move.DisplayName} is back in {home} from {today:yyyy-MM-dd}.";
            return new CommandOutcome(
                CommandResult.Ok(id, message),
                [pipeline.Event(new TemporaryMoveEnded(c.MoveId, move.PersonId, nameof(TemporaryMoveStatus.EndedEarly), last, home, message), context, SourceContext, MoveEntity, id)],
                new AuditChange(MoveEntity, id, move.EndsOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), CanonicalJson.Serialize(new { status = "EndedEarly", endsOn = last, reason })));
        }, ct);
    }

    public Task<CommandResult> TransferWorkerAsync(TransferWorker c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var spec = new CommandSpec(SourceContext, nameof(TransferWorker), Permissions.WorkforceManage, ResourceRef.Of(WorkerEntity, c.PersonId), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (context.Principal.PersonId == c.PersonId)
            {
                throw new CommandRejectedException("separation_of_duties", "You cannot transfer yourself; another person must record it.");
            }

            var today = await uow.SiteDateAsync(now, ct);
            if (c.EffectiveFrom < today)
            {
                throw new CommandRejectedException("transfer_in_past", $"A transfer takes effect today ({today:yyyy-MM-dd}) or later, so past capacity and labor stay as they were.");
            }

            var worker = await ActiveWorkerAsync(uow, c.PersonId, now, ct);
            var to = await ActiveWorkCenterAsync(uow, c.ToWorkCenterCode, ct);
            var from = await HomeOnAsync(uow, worker, c.EffectiveFrom, ct);
            if (from == to)
            {
                throw new CommandRejectedException("already_home", $"{worker.DisplayName}'s home work center is already {to} then.");
            }

            var reason = Reason(context);
            var transferId = await uow.Store<IWorkforceStore>().InsertTransferAsync(c.PersonId.Value, from, to, c.EffectiveFrom, now, context.Actor, ct)
                ?? throw new CommandRejectedException("transfer_exists", $"{worker.DisplayName} already has a transfer taking effect {c.EffectiveFrom:yyyy-MM-dd}.");
            var id = c.PersonId.ToString();
            var message = $"{worker.DisplayName} transfers from {from} to {to} from {c.EffectiveFrom:yyyy-MM-dd}.";
            return new CommandOutcome(
                CommandResult.Ok(id, message),
                [pipeline.Event(new WorkerTransferred(transferId, c.PersonId.Value, from, to, c.EffectiveFrom, message), context, SourceContext, WorkerEntity, id)],
                new AuditChange(WorkerEntity, id, from, CanonicalJson.Serialize(new { homeWorkCenter = to, c.EffectiveFrom, reason })));
        }, ct);
    }

    /// <summary>
    /// Reminds supervisors of moves ending within <see cref="ReminderWorkingDays"/> working days (Monday to Friday, not
    /// counting site closures), once per end date, and returns workers whose move ran out with nobody extending it: they
    /// are treated as back home from the day after the end date (ADR 0057 §5.5).
    /// </summary>
    public Task<CommandResult> ReconcileTemporaryMovesAsync(ReconcileTemporaryMoves c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var spec = new CommandSpec(SourceContext, nameof(ReconcileTemporaryMoves), Permissions.WorkforceSupervise, ResourceRef.Of(MoveEntity));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IWorkforceStore>();
            var closed = (await uow.Store<IResourcesLookup>().ListSiteClosuresAsync(ct)).Select(s => s.Date).ToHashSet();
            var events = new List<EventEnvelope>();
            var returned = 0;
            var reminded = 0;
            foreach (var active in await uow.Store<IWorkforceLookup>().ListTemporaryMovesAsync(null, false, ct))
            {
                var move = await store.FindTemporaryMoveAsync(active.MoveId, ct);
                if (move is not { Status: TemporaryMoveStatus.Active })
                {
                    continue;
                }

                var id = move.MoveId.ToString(CultureInfo.InvariantCulture);
                if (move.EndsOn < c.Today)
                {
                    var back = move.EndsOn.AddDays(1);
                    var home = await HomeOnAsync(uow, new WorkerRef(move.PersonId, move.DisplayName, move.FromWorkCenter), back, ct);
                    await store.CloseTemporaryMoveAsync(move.MoveId, TemporaryMoveStatus.Returned, move.EndsOn, now, context.Actor, ct);
                    var message = $"{move.DisplayName} has returned to {home}: the temporary move to {move.ToWorkCenter} ended {move.EndsOn:yyyy-MM-dd} and was not extended.";
                    events.Add(pipeline.Event(new TemporaryMoveEnded(move.MoveId, move.PersonId, nameof(TemporaryMoveStatus.Returned), move.EndsOn, home, message), context, SourceContext, MoveEntity, id));
                    returned++;
                }
                else if (move.RemindedFor != move.EndsOn && WorkingDaysUntil(c.Today, move.EndsOn, closed) <= ReminderWorkingDays)
                {
                    await store.SetMoveRemindedAsync(move.MoveId, move.EndsOn, ct);
                    var home = await HomeOnAsync(uow, new WorkerRef(move.PersonId, move.DisplayName, move.FromWorkCenter), move.EndsOn.AddDays(1), ct);
                    var message = $"{move.DisplayName}'s temporary move to {move.ToWorkCenter} ends {move.EndsOn:yyyy-MM-dd}. Extend or renew it, or {move.DisplayName} returns to {home} the day after.";
                    events.Add(pipeline.Event(new TemporaryMoveEndingSoon(move.MoveId, move.PersonId, move.EndsOn, move.ToWorkCenter, message), context, SourceContext, MoveEntity, id));
                    reminded++;
                }
            }

            var summary = $"{returned} worker(s) returned home, {reminded} reminder(s) sent.";
            return events.Count == 0
                ? CommandOutcome.NoChange(CommandResult.NoChange(c.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "No temporary move to remind about or return."))
                : new CommandOutcome(CommandResult.Ok(c.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), summary), events,
                    new AuditChange(MoveEntity, c.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null, summary));
        }, ct);
    }

    /// <summary>Working days after <paramref name="today"/> up to and including <paramref name="end"/>: Monday to Friday, not site closures.</summary>
    public static int WorkingDaysUntil(DateOnly today, DateOnly end, IReadOnlySet<DateOnly> closed)
    {
        ArgumentNullException.ThrowIfNull(closed);
        var count = 0;
        for (var day = today.AddDays(1); day <= end; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !closed.Contains(day))
            {
                count++;
            }
        }

        return count;
    }

    private sealed record WorkerRef(Guid PersonId, string DisplayName, string RegisteredHome);

    private static async Task<WorkerRef> ActiveWorkerAsync(IKernelUnitOfWork uow, PersonId person, DateTimeOffset now, CancellationToken ct)
    {
        var state = (await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(now, ct)).FirstOrDefault(s => s.PersonId == person.Value)
            ?? throw new CommandRejectedException("worker_not_found", "Register the person as a worker first.");
        if (!state.IsActive)
        {
            throw new CommandRejectedException("person_inactive", "Person is not active.");
        }

        return new WorkerRef(state.PersonId, state.DisplayName, state.HomeWorkCenterCode);
    }

    private static async Task<string> ActiveWorkCenterAsync(IKernelUnitOfWork uow, string code, CancellationToken ct)
    {
        var center = BusinessCode.Require(code, "Work center");
        var found = await uow.Store<IResourcesLookup>().FindWorkCenterAsync(center, ct)
            ?? throw new CommandRejectedException("unknown_work_center", $"Work center {center} does not exist.");
        return found.Active ? found.Code : throw new CommandRejectedException("work_center_inactive", $"Work center {center} is not active.");
    }

    /// <summary>The worker's home work center on a site date, from the transfers recorded.</summary>
    private static async Task<string> HomeOnAsync(IKernelUnitOfWork uow, WorkerRef worker, DateOnly day, CancellationToken ct)
    {
        var periods = await uow.Store<IWorkforceLookup>().ListWorkCenterPeriodsAsync(ct);
        return periods.LastOrDefault(p => p.PersonId == worker.PersonId && p.Kind == "Home" && p.Covers(day))?.WorkCenterCode ?? worker.RegisteredHome;
    }

    private static string Reason(CommandContext context) =>
        string.IsNullOrWhiteSpace(context.Reason) ? throw new CommandRejectedException("reason_required", "Give a reason.") : context.Reason.Trim();
}
