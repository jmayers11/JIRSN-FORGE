using Forge.Kernel.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Forge.Kernel.Postgres;

/// <summary>
/// Moves committed outbox entries into the append-only event log, in order, exactly once.
/// Rows are claimed with SKIP LOCKED so several hosts can relay without duplicates.
/// </summary>
public sealed class OutboxRelay(NpgsqlDataSource dataSource, TimeProvider time)
{
    public async Task<int> RelayBatchAsync(int batchSize, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var pending = new List<(Guid Id, string Envelope)>();
        await using (var select = new NpgsqlCommand(
            "SELECT event_id, envelope FROM kernel.outbox WHERE relayed_at IS NULL ORDER BY position LIMIT $1 FOR UPDATE SKIP LOCKED",
            connection,
            tx))
        {
            select.Parameters.AddWithValue(batchSize);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                pending.Add((reader.GetGuid(0), reader.GetString(1)));
            }
        }

        foreach (var (id, json) in pending)
        {
            var e = EnvelopeSerializer.Deserialize(json);
            await using (var insert = new NpgsqlCommand(
                """
                INSERT INTO kernel.event_log (event_id, event_type, schema_version, occurred_at, recorded_at, entity_type, entity_id,
                    correlation_id, causation_id, program_id, envelope)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                ON CONFLICT (event_id) DO NOTHING
                """,
                connection,
                tx))
            {
                insert.Parameters.AddWithValue(e.EventId);
                insert.Parameters.AddWithValue(e.EventType);
                insert.Parameters.AddWithValue(e.SchemaVersion);
                insert.Parameters.AddWithValue(e.OccurredAt);
                insert.Parameters.AddWithValue(e.RecordedAt);
                insert.Parameters.AddWithValue(e.EntityType);
                insert.Parameters.AddWithValue(e.EntityId);
                insert.Parameters.AddWithValue(e.CorrelationId);
                insert.Parameters.Add(new NpgsqlParameter { Value = (object?)e.CausationId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
                insert.Parameters.Add(new NpgsqlParameter { Value = (object?)e.ProgramId?.Value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
                insert.Parameters.AddWithValue(json);
                await insert.ExecuteNonQueryAsync(ct);
            }

            await using var mark = new NpgsqlCommand("UPDATE kernel.outbox SET relayed_at = $2, attempts = attempts + 1 WHERE event_id = $1", connection, tx);
            mark.Parameters.AddWithValue(id);
            mark.Parameters.AddWithValue(time.GetUtcNow());
            await mark.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return pending.Count;
    }
}

/// <summary>Runs the relay continuously in the host.</summary>
public sealed class OutboxRelayService(OutboxRelay relay, TimeProvider time, ILogger<OutboxRelayService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var relayed = await relay.RelayBatchAsync(100, stoppingToken);
                if (relayed == 0)
                {
                    await Task.Delay(IdleDelay, time, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (NpgsqlException ex)
            {
                logger.LogError(ex, "Outbox relay failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken);
            }
        }
    }
}
