using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Serialization;
using Forge.Workforce;
using Forge.Workforce.Contracts;
using Npgsql;

namespace Forge.Workforce.Infrastructure;

internal sealed class PostgresBadgePinVerifier(NpgsqlDataSource dataSource, TimeProvider time) : IBadgePinVerifier
{
    private const int MaximumFailures = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(10);

    public async Task<bool> VerifyAsync(string badge, string pin, CancellationToken ct)
    {
        if (!Badges.TryNormalize(badge, out _) || !Badges.IsValidPin(pin))
        {
            return false;
        }

        var digest = Badges.Digest(badge);
        var now = time.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var read = new NpgsqlCommand(
            "SELECT b.person_id, b.revoked_at IS NULL AND p.is_active, b.pin_hash, b.pin_salt, b.pin_failed_attempts, b.pin_locked_until FROM workforce.badge b JOIN kernel.person p ON p.person_id = b.person_id WHERE b.badge_digest = $1 FOR UPDATE OF b",
            connection,
            transaction);
        read.Parameters.AddWithValue(digest);
        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            await reader.DisposeAsync();
            await transaction.CommitAsync(ct);
            return false;
        }

        var personId = reader.GetGuid(0);
        var active = reader.GetBoolean(1);
        var hash = reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2);
        var salt = reader.IsDBNull(3) ? null : reader.GetFieldValue<byte[]>(3);
        var failures = reader.GetInt32(4);
        DateTimeOffset? lockedUntil = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5);
        await reader.DisposeAsync();

        if (!active || hash is null || salt is null || lockedUntil > now)
        {
            await transaction.CommitAsync(ct);
            return false;
        }

        if (Badges.PinMatches(pin, hash, salt))
        {
            await using var reset = new NpgsqlCommand(
                "UPDATE workforce.badge SET pin_failed_attempts = 0, pin_locked_until = NULL WHERE badge_digest = $1",
                connection,
                transaction);
            reset.Parameters.AddWithValue(digest);
            await reset.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }

        var nextFailures = failures + 1;
        var nextLock = nextFailures >= MaximumFailures ? now + LockoutDuration : (DateTimeOffset?)null;
        await using var record = new NpgsqlCommand(
            "UPDATE workforce.badge SET pin_failed_attempts = $2, pin_locked_until = $3 WHERE badge_digest = $1",
            connection,
            transaction);
        record.Parameters.AddWithValue(digest);
        record.Parameters.AddWithValue(nextFailures);
        record.Parameters.AddWithValue((object?)nextLock ?? DBNull.Value);
        await record.ExecuteNonQueryAsync(ct);

        var envelope = EventFactory.Create(
            new BadgePinVerificationFailed(personId, nextFailures, nextLock is not null),
            "system:badge-pin-verifier",
            ForgeIds.New(time),
            "Badge PIN verification failed",
            time,
            "Workforce",
            "Person",
            personId.ToString());
        await using var enqueue = new NpgsqlCommand(
            "INSERT INTO kernel.outbox (event_id, envelope, enqueued_at) VALUES ($1, $2, $3)",
            connection,
            transaction);
        enqueue.Parameters.AddWithValue(envelope.EventId);
        enqueue.Parameters.AddWithValue(CanonicalJson.Serialize(envelope));
        enqueue.Parameters.AddWithValue(envelope.RecordedAt);
        await enqueue.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return false;
    }
}
