using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Forge.Connectivity;
using Forge.Connectivity.Contracts;
using Forge.Execution;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Host.Connectivity;

/// <summary>The system identity machine connections act as.</summary>
public static class ConnectivitySystem
{
    public static readonly ForgePrincipal Principal = new(
        new PersonId(Guid.Empty),
        "Machine connections",
        true,
        new HashSet<string>(StringComparer.Ordinal) { Roles.Connectivity },
        new HashSet<ProgramId>(),
        DataMarking.None);

    public static CommandContext Context(Guid correlation, string key, string what) =>
        new(Principal, correlation, key, CommandSource.Integration, what, SystemActors.Connectivity);
}

/// <summary>Polls each active machine connection when it is due and records what it reported, or why it did not answer.</summary>
public sealed class MachinePoller(IEnumerable<IMachineConnector> connectors, ConnectivityService connectivity, IKernelUnitOfWorkFactory unitOfWork, TimeProvider time,
    ILogger<MachinePoller> logger, Forge.Host.Security.ISecretProtector secrets)
{
    private readonly Dictionary<ConnectorKind, IMachineConnector> byKind = connectors.ToDictionary(c => c.Kind);
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastPoll = new(StringComparer.Ordinal);

    /// <summary>Polls every due connection (all active ones when <paramref name="all"/>); returns how many were polled.</summary>
    public async Task<int> RunOnceAsync(bool all, CancellationToken ct)
    {
        IReadOnlyList<MachineConnection> connections;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct)) connections = await read.Store<IConnectivityLookup>().ListConnectionsAsync(ct);
        var polled = 0;
        foreach (var connection in connections.Where(c => c.Active && c.Kind != ConnectorKind.Manual))
        {
            var now = time.GetUtcNow();
            if (!all && lastPoll.TryGetValue(connection.MachineCode, out var last) && now - last < TimeSpan.FromSeconds(connection.PollSeconds)) continue;
            lastPoll[connection.MachineCode] = now;
            IReadOnlyList<MachineReading> readings = [];
            string? error = null;
            try
            {
                readings = byKind.TryGetValue(connection.Kind, out var connector)
                    ? await connector.ReadAsync(Revealed(connection), now, ct)
                    : throw new MachineConnectorException($"No {connection.Kind} connector is installed.");
            }
            catch (MachineConnectorException ex)
            {
                error = ex.Message;
            }

            try
            {
                await connectivity.RecordAsync(new RecordReadings(connection.MachineCode, readings, error),
                    ConnectivitySystem.Context(ForgeIds.New(time), string.Create(CultureInfo.InvariantCulture, $"system:machines:{connection.MachineCode}:{now.UtcTicks}"), "Machine connection poll"), ct);
            }
            catch (CommandRejectedException ex)
            {
                logger.LogWarning("Recording {Machine} readings was refused: {Code} {Message}", connection.MachineCode, ex.Code, ex.Message);
            }

            polled++;
        }

        return polled;
    }

    /// <summary>The connection with its secret settings decrypted, for the connector only.</summary>
    private MachineConnection Revealed(MachineConnection connection)
    {
        var secret = ConnectorCatalog.Of(connection.Kind).Settings.Where(s => s.Secret).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        if (!connection.Settings.Keys.Any(secret.Contains)) return connection;
        return connection with { Settings = connection.Settings.ToDictionary(p => p.Key, p => secret.Contains(p.Key) ? secrets.Unprotect(p.Value) : p.Value, StringComparer.Ordinal) };
    }
}

public sealed class MachinePollingWorker(MachinePoller poller, TimeProvider time, ILogger<MachinePollingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), time, stoppingToken);
                await poller.RunOnceAsync(false, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Machine polling failed; trying again shortly.");
            }
        }
    }
}

/// <summary>
/// Owner decisions 2026-11-01: a connected machine that stays stopped past its threshold gets a stoppage, unexplained until a person
/// gives it a reason; when the machine runs again the open stoppage on it ends. Delivered at least once.
/// </summary>
public sealed class MachineDowntimeSubscriber(ProductionActualsService actuals, IKernelUnitOfWorkFactory unitOfWork, TimeProvider time, ILogger<MachineDowntimeSubscriber> logger) : IEventSubscriber
{
    public string Name => "machine-downtime";

    public bool Handles(string eventType) => eventType is nameof(MachineStopped) or nameof(MachineRunning);

    public async Task HandleAsync(long sequence, EventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        try
        {
            if (envelope.EventType == nameof(MachineStopped))
            {
                var e = JsonSerializer.Deserialize<MachineStopped>(envelope.Payload, CanonicalJson.Options)!;
                if (e.At < time.GetUtcNow().AddDays(-7)) return;
                await actuals.StartStoppageAsync(new StartStoppage(e.MachineCode, StoppageCategory.Unplanned, StoppageReasons.Unexplained, null, null,
                        $"Detected from the machine connection: {e.State}{(e.Alarm is null ? string.Empty : $" ({e.Alarm})")}. Give it a reason.", e.At, null, StoppageSource.Connector),
                    ConnectivitySystem.Context(envelope.CorrelationId, string.Create(CultureInfo.InvariantCulture, $"system:machines:stop:{sequence}"), "Stoppage detected from the machine"), ct);
                return;
            }

            var running = JsonSerializer.Deserialize<MachineRunning>(envelope.Payload, CanonicalJson.Options)!;
            StoppageInfo? open;
            await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
                open = (await read.Store<IProductionActualsLookup>().ListStoppagesAsync(running.At, running.At, true, ct)).FirstOrDefault(s => s.MachineCode == running.MachineCode);
            if (open is null) return;
            var ended = running.At < open.StartedAt ? open.StartedAt : running.At;
            await actuals.EndStoppageAsync(new EndStoppage(open.StoppageId, ended > time.GetUtcNow() ? null : ended),
                ConnectivitySystem.Context(envelope.CorrelationId, string.Create(CultureInfo.InvariantCulture, $"system:machines:run:{sequence}"), "Machine running again"), ct);
        }
        catch (CommandRejectedException ex)
        {
            logger.LogWarning("Machine downtime for event {Sequence} was refused: {Code} {Message}", sequence, ex.Code, ex.Message);
        }
    }
}
