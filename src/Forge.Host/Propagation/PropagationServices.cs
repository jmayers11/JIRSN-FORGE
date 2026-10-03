using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Events;
using Forge.Kernel.Postgres;

namespace Forge.Host.Propagation;

/// <summary>
/// Propagation is a subscriber to EngineeringChangeReleased (V2 architecture review): delivered at least
/// once, in event-log order. A step that fails is recorded on the change and retried by
/// <see cref="PropagationRetryService"/>; the subscriber itself moves on, so one bad record never stops
/// every later change.
/// </summary>
public sealed class ChangePropagationSubscriber(ChangePropagationRunner runner) : IEventSubscriber
{
    public string Name => "change-propagation";

    public bool Handles(string eventType) => eventType == "EngineeringChangeReleased";

    public async Task HandleAsync(long sequence, EventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await runner.RunAsync(envelope.EntityId, ct);
    }
}

/// <summary>Runs every incomplete propagation again: failed steps are retried, waiting steps run once the change is in effect.</summary>
public sealed class PropagationRetry(IKernelUnitOfWorkFactory unitOfWork, ChangePropagationRunner runner)
{
    public async Task<int> RetryAllAsync(CancellationToken ct)
    {
        IReadOnlyList<ChangePropagationInfo> incomplete;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            incomplete = await read.Store<IPropagationLookup>().ListIncompleteAsync(ct);
        }

        foreach (var propagation in incomplete)
        {
            await runner.RunAsync(propagation.ChangeNumber, ct);
        }

        return incomplete.Count;
    }
}

public sealed class PropagationRetryService(PropagationRetry retry, TimeProvider time, ILogger<PropagationRetryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), time, stoppingToken);
                await retry.RetryAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Npgsql.NpgsqlException ex)
            {
                logger.LogError(ex, "Propagation retry failed; trying again later.");
            }
        }
    }
}
