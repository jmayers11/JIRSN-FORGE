using Forge.Kernel.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Forge.Kernel.Postgres;

/// <summary>
/// An in-process reaction to events (recalculation triggers, read models, integration forwarding).
/// Delivery is at-least-once and in event-log order, so handlers must be idempotent.
/// </summary>
public interface IEventSubscriber
{
    /// <summary>Stable name; the checkpoint is stored under it.</summary>
    string Name { get; }

    bool Handles(string eventType);

    Task HandleAsync(long sequence, EventEnvelope envelope, CancellationToken ct);
}

/// <summary>
/// Feeds each subscriber the event log from its checkpoint. The checkpoint advances only after the
/// handler succeeds; a failing handler stops that subscriber (not others) until it recovers.
/// </summary>
public sealed class SubscriptionPump(NpgsqlDataSource dataSource, IEnumerable<IEventSubscriber> subscribers, ILogger<SubscriptionPump> logger)
{
    public async Task<int> PumpOnceAsync(int batchSize, CancellationToken ct)
    {
        var delivered = 0;
        foreach (var subscriber in subscribers)
        {
            delivered += await PumpSubscriberAsync(subscriber, batchSize, ct);
        }

        return delivered;
    }

    private async Task<int> PumpSubscriberAsync(IEventSubscriber subscriber, int batchSize, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        long checkpoint;
        await using (var read = new NpgsqlCommand("SELECT last_sequence FROM kernel.subscriber_checkpoint WHERE subscriber = $1", connection))
        {
            read.Parameters.AddWithValue(subscriber.Name);
            checkpoint = await read.ExecuteScalarAsync(ct) is long value ? value : 0;
        }

        var batch = new List<(long Sequence, string EventType, string Envelope)>();
        await using (var select = new NpgsqlCommand("SELECT sequence, event_type, envelope FROM kernel.event_log WHERE sequence > $1 ORDER BY sequence LIMIT $2", connection))
        {
            select.Parameters.AddWithValue(checkpoint);
            select.Parameters.AddWithValue(batchSize);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                batch.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var delivered = 0;
        foreach (var (sequence, eventType, json) in batch)
        {
            if (subscriber.Handles(eventType))
            {
                try
                {
                    await subscriber.HandleAsync(sequence, EnvelopeSerializer.Deserialize(json), ct);
                    delivered++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Subscriber {Subscriber} failed at event {Sequence}; will retry.", subscriber.Name, sequence);
                    break;
                }
            }

            await using var save = new NpgsqlCommand(
                "INSERT INTO kernel.subscriber_checkpoint (subscriber, last_sequence) VALUES ($1, $2) ON CONFLICT (subscriber) DO UPDATE SET last_sequence = EXCLUDED.last_sequence",
                connection);
            save.Parameters.AddWithValue(subscriber.Name);
            save.Parameters.AddWithValue(sequence);
            await save.ExecuteNonQueryAsync(ct);
        }

        return delivered;
    }
}

public sealed class SubscriptionPumpService(SubscriptionPump pump, TimeProvider time, ILogger<SubscriptionPumpService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await pump.PumpOnceAsync(100, stoppingToken) == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), time, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (NpgsqlException ex)
            {
                logger.LogError(ex, "Subscription pump failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken);
            }
        }
    }
}
