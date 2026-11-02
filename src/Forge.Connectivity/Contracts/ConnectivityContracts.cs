using System.Globalization;

namespace Forge.Connectivity.Contracts;

// Machine connectivity (owner decisions 2026-11-01): a plant picks, per machine, how it reports to Forge. Every connector turns what
// the machine says into the same few facts — running or not, part count, scrap count, alarm — so OEE and downtime work the same
// whichever is used. Connectors only read; Forge never sends commands to a machine.

public enum ConnectorKind
{
    /// <summary>An MTConnect agent's /current document (common on CNC machines).</summary>
    MTConnect,

    /// <summary>An OPC UA server (most PLCs and newer equipment).</summary>
    OpcUa,

    /// <summary>MQTT topics from an IoT gateway or retrofit sensor kit.</summary>
    Mqtt,

    /// <summary>Modbus TCP holding registers (older PLCs and simple counters).</summary>
    ModbusTcp,

    /// <summary>CSV files a machine or historian writes to a folder.</summary>
    FileDrop,

    /// <summary>No connection: status and counts come from people at the terminal.</summary>
    Manual,

    /// <summary>A simulated machine for testing and demonstrations.</summary>
    Simulator,
}

public enum MachineState
{
    /// <summary>Producing.</summary>
    Running,

    /// <summary>Powered and ready but not producing.</summary>
    Idle,

    /// <summary>Stopped.</summary>
    Stopped,

    /// <summary>Stopped with an alarm or fault.</summary>
    Alarm,

    /// <summary>No answer from the connection; says nothing about the machine itself.</summary>
    Offline,
}

/// <summary>One thing a machine reported at one time.</summary>
public sealed record MachineReading(DateTimeOffset At, MachineState State, long? PartCount = null, long? ScrapCount = null, string? Alarm = null);

/// <param name="Settings">The connector's settings (address, register numbers, topic …); see <see cref="ConnectorCatalog"/>.</param>
/// <param name="StopAfterSeconds">How long a machine must stay stopped or idle before Forge records a stoppage (short pauses are not downtime).</param>
public sealed record MachineConnection(string MachineCode, ConnectorKind Kind, IReadOnlyDictionary<string, string> Settings, int PollSeconds, int StopAfterSeconds, bool Active,
    string UpdatedBy, DateTimeOffset UpdatedAt);

/// <summary>What Forge last heard from a machine.</summary>
/// <param name="StopReported">Whether the current stop has been recorded as a stoppage.</param>
public sealed record MachineStatus(string MachineCode, MachineState State, DateTimeOffset Since, DateTimeOffset? LastSeen, long? PartCount, long? ScrapCount, string? Alarm, string? LastError,
    bool StopReported);

/// <summary>A setting a connector needs.</summary>
/// <param name="Secret">A password or key: stored encrypted and never shown again.</param>
public sealed record ConnectorSetting(string Name, string Description, bool Required, string? Default = null, bool Secret = false);

public sealed record ConnectorDescription(ConnectorKind Kind, string Name, string Description, IReadOnlyList<ConnectorSetting> Settings);

/// <summary>The connectors a plant can choose from and the settings each needs.</summary>
public static class ConnectorCatalog
{
    public static readonly IReadOnlyList<ConnectorDescription> All =
    [
        new(ConnectorKind.MTConnect, "MTConnect", "Reads an MTConnect agent's current document: execution state, part count and conditions.",
            [new("url", "Agent address, e.g. http://10.0.0.21:5000", true), new("device", "Device name when the agent serves several", false)]),
        new(ConnectorKind.OpcUa, "OPC UA", "Reads node values from an OPC UA server (OPC Foundation client). Security None needs no certificates; Sign or SignAndEncrypt uses Forge's own certificate.",
            [new("endpoint", "opc.tcp://host:4840", true), new("stateNode", "Node id of the running state, e.g. ns=2;s=Machine.Running", true),
             new("runningValue", "Value meaning running (default true or 1)", false), new("countNode", "Node id of the part counter", false), new("scrapNode", "Node id of the scrap counter", false),
             new("security", "None, Sign or SignAndEncrypt", false, "None"), new("trustServer", "true to accept the server's certificate without adding it to the trusted folder", false, "false"),
             new("username", "User name, when the server needs one", false), new("password", "Password", false, null, true)]),
        new(ConnectorKind.Mqtt, "MQTT", "Subscribes to a topic carrying JSON {\"state\":\"Running\",\"partCount\":123,\"scrapCount\":2,\"alarm\":null}; retained messages are read on each poll.",
            [new("host", "Broker host", true), new("port", "Broker port (1883, or 8883 with TLS)", false), new("topic", "Topic, e.g. plant/press-1/status", true),
             new("tls", "true to connect with TLS (the broker's certificate must be trusted by this computer)", false, "false"),
             new("username", "User name, when the broker needs one", false), new("password", "Password", false, null, true)]),
        new(ConnectorKind.ModbusTcp, "Modbus TCP", "Reads holding registers: running state, part counter and scrap counter.",
            [new("host", "PLC or counter address", true), new("port", "Port", false, "502"), new("unit", "Unit id", false, "1"),
             new("stateRegister", "Holding register of the running state", true), new("runningValue", "Value meaning running", false, "1"),
             new("countRegister", "First of two holding registers of the part counter (32-bit, high word first)", false), new("scrapRegister", "First of two holding registers of the scrap counter", false)]),
        new(ConnectorKind.FileDrop, "File drop (CSV)", "Reads CSV files a machine or historian writes: timestamp,state,partCount,scrapCount,alarm. Read files are moved to a 'read' subfolder.",
            [new("folder", "Folder the files arrive in", true)]),
        new(ConnectorKind.Manual, "Manual", "No connection. Operators and supervisors report downtime and quantities at the terminal.", []),
        new(ConnectorKind.Simulator, "Simulator", "A simulated machine that runs, counts parts and stops now and then. For testing and demonstrations.",
            [new("cycleSeconds", "Seconds per part", false, "60"), new("stopEveryMinutes", "Minutes between simulated stops", false, "120"), new("stopMinutes", "Minutes each simulated stop lasts", false, "10")]),
    ];

    public static ConnectorDescription Of(ConnectorKind kind) => All.Single(c => c.Kind == kind);

    /// <summary>A setting's value, or its default.</summary>
    public static string? Setting(MachineConnection connection, string name)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Settings.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : Of(connection.Kind).Settings.FirstOrDefault(s => s.Name == name)?.Default;
    }

    public static int IntSetting(MachineConnection connection, string name, int fallback) =>
        int.TryParse(Setting(connection, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}

public interface IConnectivityLookup
{
    Task<IReadOnlyList<MachineConnection>> ListConnectionsAsync(CancellationToken ct);

    Task<MachineConnection?> FindConnectionAsync(string machineCode, CancellationToken ct);

    Task<IReadOnlyList<MachineStatus>> ListStatusAsync(CancellationToken ct);

    /// <summary>Readings in [from, to), oldest first; only changes of state or count are kept.</summary>
    Task<IReadOnlyList<(string MachineCode, MachineReading Reading)>> ListReadingsAsync(string? machineCode, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}
