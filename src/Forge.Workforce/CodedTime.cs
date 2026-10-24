using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Workforce.Contracts;

namespace Forge.Workforce;

/// <param name="SupersedesEntryId">An approved entry this one corrects; it stays in history and is superseded once the correction is approved.</param>
public sealed record RecordCodedTime(
    Guid PersonId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    CodedTimeCode Code,
    bool Paid,
    string Note,
    string? WorkCenterCode = null,
    string? MachineCode = null,
    long? OrderNumber = null,
    int? Sequence = null,
    Guid? SupersedesEntryId = null);

public sealed record ReviewCodedTime(Guid EntryId, bool Approve);

public sealed record CodedTimeChanged(Guid EntryId, Guid PersonId, string Code, string State) : IDomainEvent
{
    public static string EventType => nameof(CodedTimeChanged);

    public static int SchemaVersion => 1;
}

public interface ICodedTimeStore
{
    Task InsertCodedTimeAsync(CodedTimeEntry entry, CancellationToken ct);

    /// <summary>Moves the review state; false when the entry's state changed since the caller read it.</summary>
    Task<bool> SetCodedTimeStateAsync(Guid entryId, CodedTimeState expected, CodedTimeState state, string? reviewer, DateTimeOffset? at, CancellationToken ct);
}

/// <summary>
/// Coded time (Sprint 10 §16.1): recorded by a supervisor, approved by a different person, corrected only by a superseding entry.
/// Nobody records or approves their own time here, and nothing about an entry is ever rewritten.
/// </summary>
public sealed class CodedTimeService(CommandPipeline pipeline)
{
    private const string SourceContext = "Workforce";
    private const string Entity = "CodedTime";

    public Task<CommandResult> RecordAsync(RecordCodedTime c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var id = ForgeIds.New(pipeline.Time);
        var key = id.ToString("D");
        var spec = new CommandSpec(SourceContext, nameof(RecordCodedTime), Permissions.WorkforceSupervise, ResourceRef.Of(Entity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (!Enum.IsDefined(c.Code))
                throw new CommandRejectedException("invalid_time_code", "Choose what the time was spent on.");
            if (c.EndsAt <= c.StartsAt || c.EndsAt - c.StartsAt > TimeSpan.FromHours(16))
                throw new CommandRejectedException("invalid_interval", "The end must be after the start, at most 16 hours later.");
            if (c.StartsAt > now)
                throw new CommandRejectedException("future_time", "Record time that has started, not planned time.");
            if (context.Principal.PersonId.Value == c.PersonId)
                throw new CommandRejectedException("own_time", "A supervisor records coded time for others; your own time is recorded by your supervisor.");
            if ((c.OrderNumber is null) != (c.Sequence is null))
                throw new CommandRejectedException("invalid_operation", "Give both the order and the operation, or neither.");
            var note = c.Note?.Trim();
            if (string.IsNullOrEmpty(note) || note.Length > 800)
                throw new CommandRejectedException("note_required", "Say what happened in at most 800 characters.");
            var person = await uow.People.FindAsync(new PersonId(c.PersonId), ct);
            if (person is not { IsActive: true } || !await uow.Store<IWorkforceLookup>().WorkerExistsAsync(c.PersonId, ct))
                throw new CommandRejectedException("worker_not_found", "Coded time must reference an active Forge worker.");

            var lookup = uow.Store<ICodedTimeLookup>();
            if (c.SupersedesEntryId is { } supersedes)
            {
                var original = await lookup.FindCodedTimeAsync(supersedes, ct)
                    ?? throw new CommandRejectedException("coded_time_not_found", "The entry to correct does not exist.");
                if (original.State != CodedTimeState.Approved || original.PersonId != c.PersonId)
                    throw new CommandRejectedException("not_correctable", "Only an approved entry of the same worker is corrected.");
            }

            var entry = new CodedTimeEntry(id, c.PersonId, c.StartsAt, c.EndsAt, c.Code, c.Paid, Optional(c.WorkCenterCode), Optional(c.MachineCode),
                c.OrderNumber, c.Sequence, note, CodedTimeState.PendingReview, context.Actor, now, null, null, c.SupersedesEntryId);
            await uow.Store<ICodedTimeStore>().InsertCodedTimeAsync(entry, ct);
            return new CommandOutcome(
                CommandResult.Ok(key, $"{c.Code} time for {person.DisplayName} recorded for review. It counts once someone else approves it."),
                [pipeline.Event(new CodedTimeChanged(id, c.PersonId, c.Code.ToString(), nameof(CodedTimeState.PendingReview)), context, SourceContext, Entity, key)],
                new AuditChange(Entity, key, null, CanonicalJson.Serialize(entry)));
        }, ct);
    }

    public Task<CommandResult> ReviewAsync(ReviewCodedTime c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var key = c.EntryId.ToString("D");
        var spec = new CommandSpec(SourceContext, nameof(ReviewCodedTime), Permissions.WorkforceSupervise, ResourceRef.Of(Entity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var entry = await uow.Store<ICodedTimeLookup>().FindCodedTimeAsync(c.EntryId, ct)
                ?? throw new CommandRejectedException("coded_time_not_found", "The entry does not exist.");
            if (entry.State != CodedTimeState.PendingReview)
                throw new CommandRejectedException("not_pending", $"This entry is {entry.State}.");
            if (entry.RecordedBy == context.Actor || entry.PersonId == context.Principal.PersonId.Value)
                throw new CommandRejectedException("independent_review_required", "Someone other than the recorder and the worker reviews coded time.");

            var store = uow.Store<ICodedTimeStore>();
            var state = c.Approve ? CodedTimeState.Approved : CodedTimeState.Rejected;
            if (!await store.SetCodedTimeStateAsync(entry.EntryId, CodedTimeState.PendingReview, state, context.Actor, now, ct))
                throw new CommandRejectedException("coded_time_changed", "The entry changed meanwhile; reload it.");
            var events = new List<EventEnvelope> { pipeline.Event(new CodedTimeChanged(entry.EntryId, entry.PersonId, entry.Code.ToString(), state.ToString()), context, SourceContext, Entity, key) };
            if (c.Approve && entry.SupersedesEntryId is { } original)
            {
                if (!await store.SetCodedTimeStateAsync(original, CodedTimeState.Approved, CodedTimeState.Superseded, null, null, ct))
                    throw new CommandRejectedException("coded_time_changed", "The corrected entry changed meanwhile; reload it.");
                events.Add(pipeline.Event(new CodedTimeChanged(original, entry.PersonId, entry.Code.ToString(), nameof(CodedTimeState.Superseded)), context, SourceContext, Entity,
                    original.ToString("D")));
            }

            return new CommandOutcome(
                CommandResult.Ok(key, c.Approve
                    ? $"Coded time approved{(entry.SupersedesEntryId is null ? "." : "; the entry it corrects stays in history as superseded.")}"
                    : "Coded time rejected; it does not count."),
                events, new AuditChange(Entity, key, nameof(CodedTimeState.PendingReview), state.ToString()));
        }, ct);
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}
