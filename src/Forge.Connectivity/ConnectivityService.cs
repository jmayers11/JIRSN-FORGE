using System.Globalization;
using System.Text.RegularExpressions;
using Forge.Connectivity.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Connectivity;

public sealed record ConfigureConnection(string MachineCode, ConnectorKind Kind, IReadOnlyDictionary<string, string>? Settings, int PollSeconds, int StopAfterSeconds, bool Active);

/// <summary>What one poll of a machine's connection returned: its readings, or the error that kept it from answering.</summary>
public sealed record RecordReadings(string MachineCode, IReadOnlyList<MachineReading> Readings, string? Error);

public sealed record MachineConnectionChanged(string MachineCode, string Kind, bool Active) : IDomainEvent
{
    public static string EventType => nameof(MachineConnectionChanged);

    public static int SchemaVersion => 1;
}

/// <summary>A connected machine stayed stopped, idle or in alarm long enough to be downtime.</summary>
public sealed record MachineStopped(string MachineCode, DateTimeOffset At, string State, string? Alarm) : IDomainEvent
{
    public static string EventType => nameof(MachineStopped);

    public static int SchemaVersion => 1;
}

/// <summary>A connected machine that was recorded as stopped is running again.</summary>
public sealed record MachineRunning(string MachineCode, DateTimeOffset At) : IDomainEvent
{
    public static string EventType => nameof(MachineRunning);

    public static int SchemaVersion => 1;
}

/// <summary>A machine's connection stopped answering.</summary>
public sealed record MachineConnectionLost(string MachineCode, string Error) : IDomainEvent
{
    public static string EventType => nameof(MachineConnectionLost);

    public static int SchemaVersion => 1;
}

public interface IConnectivityStore
{
    Task UpsertConnectionAsync(MachineConnection connection, CancellationToken ct);

    Task<MachineStatus?> LockStatusAsync(string machineCode, CancellationToken ct);

    Task SaveStatusAsync(MachineStatus status, CancellationToken ct);

    Task InsertReadingsAsync(string machineCode, IReadOnlyList<MachineReading> readings, CancellationToken ct);
}

/// <summary>
/// Machine connections (owner decisions 2026-11-01). A manufacturing engineer or maintenance planner chooses each machine's connector.
/// The system records what connections report; a stop that lasts longer than the machine's threshold becomes a stoppage (unexplained
/// until a person gives it a reason) and ends when the machine runs again. A lost connection is shown, never treated as a stop.
/// </summary>
public sealed partial class ConnectivityService(CommandPipeline pipeline)
{
    public const string SourceContext = "Connectivity";
    private const string Entity = "MachineConnection";
    private static readonly string[] UrlSettings = ["url"];
    private static readonly string[] NumberSettings = ["port", "unit", "stateRegister", "runningValue", "countRegister", "scrapRegister", "cycleSeconds", "stopEveryMinutes", "stopMinutes"];

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,40}$")]
    private static partial Regex SettingName();

    public Task<CommandResult> ConfigureAsync(ConfigureConnection c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var machine = BusinessCode.Require(c.MachineCode, "Machine");
        var spec = new CommandSpec(SourceContext, nameof(ConfigureConnection), Permissions.ConnectivityManage, ResourceRef.Of(Entity, machine), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (await uow.Store<IResourcesLookup>().FindMachineAsync(machine, ct) is not { Active: true })
                throw new CommandRejectedException("machine_not_found", $"There is no active machine {machine}.");
            if (c.PollSeconds is < 2 or > 3600) throw new CommandRejectedException("invalid_poll", "Poll every 2 seconds to 1 hour.");
            if (c.StopAfterSeconds is < 0 or > 3600) throw new CommandRejectedException("invalid_threshold", "A stop counts as downtime after 0 seconds to 1 hour.");
            var description = ConnectorCatalog.Of(c.Kind);
            var settings = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in c.Settings ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (!SettingName().IsMatch(name) || description.Settings.All(s => s.Name != name))
                    throw new CommandRejectedException("unknown_setting", $"{description.Name} has no setting {name}.");
                if (value.Length > 500) throw new CommandRejectedException("invalid_setting", $"{name} is too long.");
                settings[name] = value.Trim();
            }

            if (description.Settings.FirstOrDefault(s => s.Required && !settings.ContainsKey(s.Name)) is { } missing)
                throw new CommandRejectedException("setting_required", $"{description.Name} needs {missing.Name}: {missing.Description}.");
            foreach (var key in UrlSettings.Where(settings.ContainsKey))
                if (!Uri.TryCreate(settings[key], UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                    throw new CommandRejectedException("invalid_setting", $"{key} must be an http:// or https:// address.");
            foreach (var key in NumberSettings.Where(settings.ContainsKey))
                if (!int.TryParse(settings[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0 || n > 65535)
                    throw new CommandRejectedException("invalid_setting", $"{key} must be a whole number from 0 to 65535.");
            var before = await uow.Store<IConnectivityLookup>().FindConnectionAsync(machine, ct);
            var connection = new MachineConnection(machine, c.Kind, settings, c.PollSeconds, c.StopAfterSeconds, c.Active, context.Actor, now);
            await uow.Store<IConnectivityStore>().UpsertConnectionAsync(connection, ct);
            return new CommandOutcome(
                CommandResult.Ok(machine, $"{machine} reports through {description.Name}{(c.Active ? string.Empty : " (inactive)")}; a stop counts after {c.StopAfterSeconds} s."),
                [pipeline.Event(new MachineConnectionChanged(machine, c.Kind.ToString(), c.Active), context, SourceContext, Entity, machine)],
                new AuditChange(Entity, machine, before is null ? null : CanonicalJson.Serialize(before), CanonicalJson.Serialize(connection)));
        }, ct);
    }

    public Task<CommandResult> RecordAsync(RecordReadings c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var machine = BusinessCode.Require(c.MachineCode, "Machine");
        var spec = new CommandSpec(SourceContext, nameof(RecordReadings), Permissions.ConnectivityRecord, ResourceRef.Of(Entity, machine));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var connection = await uow.Store<IConnectivityLookup>().FindConnectionAsync(machine, ct)
                ?? throw new CommandRejectedException("not_connected", $"{machine} has no machine connection.");
            var store = uow.Store<IConnectivityStore>();
            var status = await store.LockStatusAsync(machine, ct) ?? new MachineStatus(machine, MachineState.Offline, now, null, null, null, null, null, false);
            var events = new List<EventEnvelope>();
            var kept = new List<MachineReading>();
            if (c.Error is { } error)
            {
                var text = error.Length > 300 ? error[..300] : error;
                if (status.LastError is null)
                    events.Add(pipeline.Event(new MachineConnectionLost(machine, text), context, SourceContext, Entity, machine));
                status = status with { LastError = text };
            }
            else
            {
                status = status with { LastError = null };
            }

            foreach (var r in (c.Readings ?? []).Where(r => r.At <= now.AddMinutes(1) && (status.LastSeen is null || r.At > status.LastSeen)).OrderBy(r => r.At))
            {
                var changed = r.State != status.State || r.PartCount != status.PartCount || r.ScrapCount != status.ScrapCount || r.Alarm != status.Alarm;
                if (changed) kept.Add(r);
                if (r.State != status.State && r.State != MachineState.Offline)
                {
                    if (r.State == MachineState.Running && status.StopReported)
                    {
                        events.Add(pipeline.Event(new MachineRunning(machine, r.At), context, SourceContext, Entity, machine));
                        status = status with { StopReported = false };
                    }

                    status = status with { State = r.State, Since = r.At };
                }

                status = status with { LastSeen = r.At, PartCount = r.PartCount ?? status.PartCount, ScrapCount = r.ScrapCount ?? status.ScrapCount, Alarm = r.Alarm };
            }

            // A stop becomes downtime only once it has lasted the machine's threshold.
            if (status.State is MachineState.Stopped or MachineState.Idle or MachineState.Alarm && !status.StopReported && (now - status.Since).TotalSeconds >= connection.StopAfterSeconds)
            {
                events.Add(pipeline.Event(new MachineStopped(machine, status.Since, status.State.ToString(), status.Alarm), context, SourceContext, Entity, machine));
                status = status with { StopReported = true };
            }

            if (kept.Count > 0) await store.InsertReadingsAsync(machine, kept, ct);
            await store.SaveStatusAsync(status, ct);
            return events.Count == 0 && kept.Count == 0
                ? CommandOutcome.NoChange(CommandResult.NoChange(machine, $"{machine}: {status.State}."))
                : new CommandOutcome(CommandResult.Ok(machine, string.Create(CultureInfo.InvariantCulture, $"{machine}: {status.State} since {status.Since:u}, {kept.Count} change(s).")), events, null);
        }, ct);
    }
}
