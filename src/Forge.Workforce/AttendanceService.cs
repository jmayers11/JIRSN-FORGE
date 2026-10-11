using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Workforce.Contracts;

namespace Forge.Workforce;

public sealed record IssueBadge(PersonId PersonId, Secret Badge);

public sealed record RevokeBadge(PersonId PersonId);

/// <summary>Set or change the PIN on one's own badge, at a terminal. Changing it needs the current PIN.</summary>
public sealed record SetBadgePin(Secret Badge, Secret? CurrentPin, Secret NewPin);

public sealed record ClockIn(Secret Badge);

public sealed record ClockOut(Secret Badge);

public sealed record BadgeIssued(Guid PersonId) : IDomainEvent
{
    public static string EventType => nameof(BadgeIssued);

    public static int SchemaVersion => 1;
}

public sealed record BadgeRevoked(Guid PersonId) : IDomainEvent
{
    public static string EventType => nameof(BadgeRevoked);

    public static int SchemaVersion => 1;
}

public sealed record BadgePinSet(Guid PersonId) : IDomainEvent
{
    public static string EventType => nameof(BadgePinSet);

    public static int SchemaVersion => 1;
}

/// <summary>Contains no badge value or PIN; emitted for Sentinel security monitoring and investigation.</summary>
public sealed record BadgePinVerificationFailed(Guid PersonId, int ConsecutiveFailures, bool Locked) : IDomainEvent
{
    public static string EventType => nameof(BadgePinVerificationFailed);

    public static int SchemaVersion => 1;
}

public sealed record EmployeeClockedIn(Guid PersonId, long AttendanceId, DateTimeOffset At) : IDomainEvent
{
    public static string EventType => nameof(EmployeeClockedIn);

    public static int SchemaVersion => 1;
}

public sealed record EmployeeClockedOut(Guid PersonId, long AttendanceId, DateTimeOffset At, decimal Hours) : IDomainEvent
{
    public static string EventType => nameof(EmployeeClockedOut);

    public static int SchemaVersion => 1;
}

/// <summary>A badge row as stored: digest only, never the badge number.</summary>
public sealed record BadgeRow(string Digest, Guid PersonId, bool Revoked, byte[]? PinHash, byte[]? PinSalt);

public interface IAttendanceStore
{
    /// <returns>False if the digest is already registered (to anyone, active or revoked).</returns>
    Task<bool> InsertBadgeAsync(string digest, PersonId person, DateTimeOffset at, string actor, CancellationToken ct);

    /// <returns>Number of active badges revoked for the person.</returns>
    Task<int> RevokeBadgesAsync(PersonId person, DateTimeOffset at, string actor, CancellationToken ct);

    Task<BadgeRow?> FindBadgeAsync(string digest, CancellationToken ct);

    Task SetPinAsync(string digest, byte[] hash, byte[] salt, DateTimeOffset at, CancellationToken ct);

    Task<long> OpenAttendanceAsync(PersonId person, DateTimeOffset at, string actor, CancellationToken ct);

    Task CloseAttendanceAsync(long attendanceId, DateTimeOffset at, string actor, CancellationToken ct);
}

/// <summary>
/// Badges and attendance. Attendance (paid time on site), direct labor (time on an operation),
/// operation authentication and electronic signoff are different things; this service owns only
/// the first, plus the badge that identifies a person for all of them.
/// </summary>
public sealed class AttendanceService(CommandPipeline pipeline, IBadgePinVerifier pinVerifier)
{
    public const string SourceContext = "Workforce";
    public static readonly TimeSpan ClockInPresence = TimeSpan.FromHours(12);
    private const string WorkerEntity = "Worker";
    private const string BadgeEntity = "Badge";

    public Task<CommandResult> IssueBadgeAsync(IssueBadge c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(IssueBadge), Permissions.BadgesManage, ResourceRef.Of(BadgeEntity, c.PersonId));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var person = await uow.People.FindAsync(c.PersonId, ct) ?? throw new CommandRejectedException("person_not_found", "Register the person in Forge first.");
            if (!person.IsActive)
            {
                throw new CommandRejectedException("person_inactive", "Person is not active.");
            }

            var digest = Digest(c.Badge);
            var store = uow.Store<IAttendanceStore>();
            var revoked = await store.RevokeBadgesAsync(c.PersonId, now, context.Actor, ct);
            if (!await store.InsertBadgeAsync(digest, c.PersonId, now, context.Actor, ct))
            {
                throw new CommandRejectedException("badge_in_use", "That badge is already registered. Each badge identifies one person, once.");
            }

            var id = c.PersonId.ToString();
            return new CommandOutcome(
                CommandResult.Ok(id, revoked > 0 ? $"New badge issued to {person.DisplayName}; the previous badge no longer works." : $"Badge issued to {person.DisplayName}."),
                [pipeline.Event(new BadgeIssued(c.PersonId.Value), context, SourceContext, BadgeEntity, id)],
                new AuditChange(BadgeEntity, id, revoked > 0 ? "previous badge" : null, "badge issued"));
        }, ct);
    }

    public Task<CommandResult> RevokeBadgeAsync(RevokeBadge c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(RevokeBadge), Permissions.BadgesManage, ResourceRef.Of(BadgeEntity, c.PersonId), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var id = c.PersonId.ToString();
            if (await uow.Store<IAttendanceStore>().RevokeBadgesAsync(c.PersonId, now, context.Actor, ct) == 0)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "No active badge."));
            }

            return new CommandOutcome(
                CommandResult.Ok(id, "Badge revoked."),
                [pipeline.Event(new BadgeRevoked(c.PersonId.Value), context, SourceContext, BadgeEntity, id)],
                new AuditChange(BadgeEntity, id, "active", "revoked"));
        }, ct);
    }

    public Task<CommandResult> SetBadgePinAsync(SetBadgePin c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(SetBadgePin), Permissions.AttendanceRecord, new ResourceRef(BadgeEntity, null));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IAttendanceStore>();
            var badge = await ActiveBadgeAsync(store, c.Badge, ct);
            if (!Badges.IsValidPin(c.NewPin.Reveal()))
            {
                throw new CommandRejectedException("invalid_pin", "A PIN is 4 to 8 digits.");
            }

            if (badge.PinHash is not null && (c.CurrentPin is null || !await pinVerifier.VerifyAsync(c.Badge.Reveal(), c.CurrentPin.Reveal(), ct)))
            {
                throw new CommandRejectedException("pin_mismatch", "The current PIN does not match.");
            }

            var (hash, salt) = Badges.HashPin(c.NewPin.Reveal());
            await store.SetPinAsync(badge.Digest, hash, salt, now, ct);
            var id = badge.PersonId.ToString();
            return new CommandOutcome(
                CommandResult.Ok(id, badge.PinHash is null ? "PIN set." : "PIN changed."),
                [pipeline.Event(new BadgePinSet(badge.PersonId), context, SourceContext, BadgeEntity, id)],
                new AuditChange(BadgeEntity, id, badge.PinHash is null ? null : "pin", "pin"));
        }, ct);
    }

    public Task<CommandResult> ClockInAsync(ClockIn c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(ClockIn), Permissions.AttendanceRecord, new ResourceRef(WorkerEntity, null));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IAttendanceStore>();
            var badge = await ActiveBadgeAsync(store, c.Badge, ct);
            var lookup = uow.Store<IWorkforceLookup>();
            if (!await lookup.WorkerExistsAsync(badge.PersonId, ct))
            {
                throw new CommandRejectedException("worker_not_found", "This badge belongs to someone who is not a registered worker.");
            }

            var id = badge.PersonId.ToString();
            var open = await lookup.FindOpenAttendanceAsync(badge.PersonId, ct);
            if (open is not null)
            {
                // A second scan is not a second shift.
                return CommandOutcome.NoChange(CommandResult.NoChange(id, $"Already clocked in since {open.ClockInAt:u}."));
            }

            var person = new PersonId(badge.PersonId);
            var attendanceId = await store.OpenAttendanceAsync(person, now, context.Actor, ct);

            // Clocking in is presence evidence, and it expires: a scan this morning is not proof of presence tonight.
            await uow.Store<IWorkforceStore>().InsertPresenceAsync(person, PresenceKind.ClockedIn, now, now + ClockInPresence, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(id, "Clocked in."),
                [
                    pipeline.Event(new EmployeeClockedIn(badge.PersonId, attendanceId, now), context, SourceContext, WorkerEntity, id),
                    pipeline.Event(new PresenceRecorded(badge.PersonId, nameof(PresenceKind.ClockedIn), now + ClockInPresence), context, SourceContext, WorkerEntity, id),
                ],
                new AuditChange(WorkerEntity, id, null, CanonicalJson.Serialize(new { attendance = "in", attendanceId })));
        }, ct);
    }

    public Task<CommandResult> ClockOutAsync(ClockOut c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(ClockOut), Permissions.AttendanceRecord, new ResourceRef(WorkerEntity, null));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IAttendanceStore>();
            var badge = await ActiveBadgeAsync(store, c.Badge, ct);
            var id = badge.PersonId.ToString();
            var open = await uow.Store<IWorkforceLookup>().FindOpenAttendanceAsync(badge.PersonId, ct);
            if (open is null)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Not clocked in."));
            }

            await store.CloseAttendanceAsync(open.AttendanceId, now, context.Actor, ct);
            await uow.Store<IWorkforceStore>().EndPresenceAsync(new PersonId(badge.PersonId), now, ct);
            var hours = decimal.Round((decimal)(now - open.ClockInAt).TotalHours, 2);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Clocked out after {hours} h."),
                [
                    pipeline.Event(new EmployeeClockedOut(badge.PersonId, open.AttendanceId, now, hours), context, SourceContext, WorkerEntity, id),
                    pipeline.Event(new PresenceEnded(badge.PersonId), context, SourceContext, WorkerEntity, id),
                ],
                new AuditChange(WorkerEntity, id, CanonicalJson.Serialize(new { attendance = "in", open.AttendanceId }), CanonicalJson.Serialize(new { attendance = "out", hours })));
        }, ct);
    }

    private static string Digest(Secret badge)
    {
        try
        {
            return Badges.Digest(badge.Reveal());
        }
        catch (ArgumentException e)
        {
            throw new CommandRejectedException("invalid_badge", e.Message);
        }
    }

    private static async Task<BadgeRow> ActiveBadgeAsync(IAttendanceStore store, Secret badge, CancellationToken ct)
    {
        var row = await store.FindBadgeAsync(Digest(badge), ct);
        return row is { Revoked: false } ? row : throw new CommandRejectedException("unknown_badge", "Badge not recognized. See your supervisor.");
    }
}
