using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Forge.Connectivity.Contracts;

namespace Forge.Host.Connectivity;

/// <summary>Why a connection gave no reading this time.</summary>
public sealed class MachineConnectorException(string message) : Exception(message);

/// <summary>Reads one machine through one kind of connection. Connectors only read; none sends anything that changes a machine.</summary>
public interface IMachineConnector
{
    ConnectorKind Kind { get; }

    Task<IReadOnlyList<MachineReading>> ReadAsync(MachineConnection connection, DateTimeOffset now, CancellationToken ct);
}

/// <summary>MTConnect: the agent's /current document. Execution ACTIVE is running, READY idle, other states stopped; a Fault condition or a triggered emergency stop is an alarm.</summary>
public sealed class MTConnectConnector(IHttpClientFactory clients) : IMachineConnector
{
    public ConnectorKind Kind => ConnectorKind.MTConnect;

    public async Task<IReadOnlyList<MachineReading>> ReadAsync(MachineConnection connection, DateTimeOffset now, CancellationToken ct)
    {
        var url = ConnectorCatalog.Setting(connection, "url")!.TrimEnd('/');
        var device = ConnectorCatalog.Setting(connection, "device");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        string xml;
        try
        {
            xml = await clients.CreateClient("forge-machines").GetStringAsync(new Uri(device is null ? $"{url}/current" : $"{url}/{Uri.EscapeDataString(device)}/current"), timeout.Token);
        }
        catch (HttpRequestException ex) { throw new MachineConnectorException($"MTConnect agent did not answer: {ex.Message}"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MachineConnectorException("MTConnect agent did not answer within 5 seconds."); }
        return [Parse(xml, device, now)];
    }

    public static MachineReading Parse(string xml, string? device, DateTimeOffset now)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException ex) { throw new MachineConnectorException($"The MTConnect document could not be read: {ex.Message}"); }
        var stream = doc.Descendants().Where(e => e.Name.LocalName == "DeviceStream")
            .FirstOrDefault(e => device is null || string.Equals((string?)e.Attribute("name"), device, StringComparison.OrdinalIgnoreCase))
            ?? throw new MachineConnectorException(device is null ? "The MTConnect document has no device." : $"The MTConnect agent has no device {device}.");
        string? Value(string name) => stream.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        var execution = Value("Execution");
        var state = execution switch
        {
            "ACTIVE" => MachineState.Running,
            "READY" => MachineState.Idle,
            null or "UNAVAILABLE" => MachineState.Offline,
            _ => MachineState.Stopped,
        };
        var fault = stream.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
        string? alarm = null;
        if (fault is not null)
        {
            alarm = string.IsNullOrWhiteSpace(fault.Value) ? (string?)fault.Attribute("type") ?? "Fault" : fault.Value.Trim();
            state = MachineState.Alarm;
        }
        else if (Value("EmergencyStop") == "TRIGGERED")
        {
            alarm = "Emergency stop";
            state = MachineState.Alarm;
        }

        long? Count(string name) => long.TryParse(Value(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        return new MachineReading(now, state, Count("PartCount"), null, alarm);
    }
}

/// <summary>Modbus TCP: function 3 (read holding registers). Counters are two registers, high word first.</summary>
public sealed class ModbusTcpConnector : IMachineConnector
{
    public ConnectorKind Kind => ConnectorKind.ModbusTcp;

    public async Task<IReadOnlyList<MachineReading>> ReadAsync(MachineConnection connection, DateTimeOffset now, CancellationToken ct)
    {
        var host = ConnectorCatalog.Setting(connection, "host")!;
        var port = ConnectorCatalog.IntSetting(connection, "port", 502);
        var unit = (byte)ConnectorCatalog.IntSetting(connection, "unit", 1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            var stream = client.GetStream();
            ushort transaction = 0;
            async Task<ushort[]> Read(int address, int count)
            {
                transaction++;
                await stream.WriteAsync(Request(transaction, unit, (ushort)address, (ushort)count), timeout.Token);
                var header = new byte[7];
                await stream.ReadExactlyAsync(header, timeout.Token);
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                if (length is < 2 or > 260) throw new MachineConnectorException("The Modbus answer has an impossible length.");
                var body = new byte[length - 1];
                await stream.ReadExactlyAsync(body, timeout.Token);
                return Response([.. header, .. body], transaction, count);
            }

            var state = (await Read(ConnectorCatalog.IntSetting(connection, "stateRegister", 0), 1))[0];
            long? Counter(ushort[] words) => ((long)words[0] << 16) | words[1];
            long? count = ConnectorCatalog.Setting(connection, "countRegister") is null ? null : Counter(await Read(ConnectorCatalog.IntSetting(connection, "countRegister", 0), 2));
            long? scrap = ConnectorCatalog.Setting(connection, "scrapRegister") is null ? null : Counter(await Read(ConnectorCatalog.IntSetting(connection, "scrapRegister", 0), 2));
            var running = state == ConnectorCatalog.IntSetting(connection, "runningValue", 1);
            return [new MachineReading(now, running ? MachineState.Running : MachineState.Stopped, count, scrap)];
        }
        catch (SocketException ex) { throw new MachineConnectorException($"Modbus device did not answer: {ex.Message}"); }
        catch (IOException ex) { throw new MachineConnectorException($"Modbus connection broke: {ex.Message}"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MachineConnectorException("Modbus device did not answer within 5 seconds."); }
    }

    /// <summary>A Modbus TCP "read holding registers" request.</summary>
    public static byte[] Request(ushort transaction, byte unit, ushort address, ushort count)
    {
        var frame = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transaction);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 6);
        frame[6] = unit;
        frame[7] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), address);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10), count);
        return frame;
    }

    /// <summary>The registers in a Modbus TCP answer; an exception answer becomes the reason.</summary>
    public static ushort[] Response(byte[] frame, ushort transaction, int count)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Length < 9 || BinaryPrimitives.ReadUInt16BigEndian(frame) != transaction) throw new MachineConnectorException("The Modbus answer does not match the request.");
        if ((frame[7] & 0x80) != 0) throw new MachineConnectorException($"The Modbus device refused the read (exception code {frame[8]}).");
        if (frame[7] != 3 || frame[8] != count * 2 || frame.Length < 9 + (count * 2)) throw new MachineConnectorException("The Modbus answer is not the registers asked for.");
        var words = new ushort[count];
        for (var i = 0; i < count; i++) words[i] = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(9 + (i * 2)));
        return words;
    }
}

/// <summary>
/// MQTT 3.1.1, read-only: connects, subscribes to the topic and reads what the broker delivers for two seconds — the retained
/// message, and any published meanwhile. The payload is JSON with state, partCount, scrapCount and alarm.
/// </summary>
public sealed class MqttConnector : IMachineConnector
{
    private static readonly byte[] Disconnect = [0xE0, 0x00];

    public ConnectorKind Kind => ConnectorKind.Mqtt;

    public async Task<IReadOnlyList<MachineReading>> ReadAsync(MachineConnection connection, DateTimeOffset now, CancellationToken ct)
    {
        var host = ConnectorCatalog.Setting(connection, "host")!;
        var port = ConnectorCatalog.IntSetting(connection, "port", 1883);
        var topic = ConnectorCatalog.Setting(connection, "topic")!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        var readings = new List<MachineReading>();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Connect("forge-" + connection.MachineCode.ToLowerInvariant()), timeout.Token);
            var (type, body) = await ReadPacketAsync(stream, timeout.Token);
            if (type != 2 || body.Length < 2 || body[1] != 0) throw new MachineConnectorException($"The MQTT broker refused the connection (code {(body.Length > 1 ? body[1] : -1)}).");
            await stream.WriteAsync(Subscribe(1, topic), timeout.Token);
            using var window = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            window.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                while (true)
                {
                    var (kind, payload) = await ReadPacketAsync(stream, window.Token);
                    if (kind == 3 && Publish(payload) is { } message && Reading(message, now) is { } reading) readings.Add(reading);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The read window ended.
            }

            await stream.WriteAsync(Disconnect, CancellationToken.None);
        }
        catch (SocketException ex) { throw new MachineConnectorException($"MQTT broker did not answer: {ex.Message}"); }
        catch (IOException ex) { throw new MachineConnectorException($"MQTT connection broke: {ex.Message}"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && readings.Count == 0) { throw new MachineConnectorException("MQTT broker did not answer in time."); }

        return readings.Count == 0 ? [] : [readings[^1]];
    }

    public static byte[] Connect(string clientId)
    {
        var id = Encoding.UTF8.GetBytes(clientId);
        byte[] variable = [0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 4, 0x02, 0, 30, (byte)(id.Length >> 8), (byte)id.Length, .. id];
        return [0x10, .. RemainingLength(variable.Length), .. variable];
    }

    public static byte[] Subscribe(ushort packetId, string topic)
    {
        var t = Encoding.UTF8.GetBytes(topic);
        byte[] body = [(byte)(packetId >> 8), (byte)packetId, (byte)(t.Length >> 8), (byte)t.Length, .. t, 0];
        return [0x82, .. RemainingLength(body.Length), .. body];
    }

    public static byte[] RemainingLength(int length)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(length % 128);
            length /= 128;
            if (length > 0) b |= 0x80;
            bytes.Add(b);
        }
        while (length > 0);
        return [.. bytes];
    }

    /// <summary>The topic and payload of a PUBLISH packet body (QoS 0 or 1).</summary>
    public static (string Topic, string Payload)? Publish(byte[] body, int flags = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length < 2) return null;
        var length = (body[0] << 8) | body[1];
        if (body.Length < 2 + length) return null;
        var start = 2 + length + (((flags >> 1) & 3) > 0 ? 2 : 0);
        return (Encoding.UTF8.GetString(body, 2, length), Encoding.UTF8.GetString(body, start, body.Length - start));
    }

    public static MachineReading? Reading((string Topic, string Payload) message, DateTimeOffset now)
    {
        try
        {
            using var json = JsonDocument.Parse(message.Payload);
            var root = json.RootElement;
            if (!root.TryGetProperty("state", out var s) || !Enum.TryParse<MachineState>(s.GetString(), true, out var state)) return null;
            long? Number(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
            return new MachineReading(now, state, Number("partCount"), Number("scrapCount"), root.TryGetProperty("alarm", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<(int Type, byte[] Body)> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var first = new byte[1];
        await stream.ReadExactlyAsync(first, ct);
        int length = 0, multiplier = 1;
        var b = new byte[1];
        do
        {
            await stream.ReadExactlyAsync(b, ct);
            length += (b[0] & 0x7F) * multiplier;
            multiplier *= 128;
            if (multiplier > 128 * 128 * 128 * 128) throw new MachineConnectorException("The MQTT packet length is malformed.");
        }
        while ((b[0] & 0x80) != 0);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return (first[0] >> 4, body);
    }
}

/// <summary>OPC UA needs the OPC Foundation's client library, which this build does not include yet.</summary>
public sealed class OpcUaConnector : IMachineConnector
{
    public ConnectorKind Kind => ConnectorKind.OpcUa;

    public Task<IReadOnlyList<MachineReading>> ReadAsync(MachineConnection connection, DateTimeOffset now, CancellationToken ct) =>
        throw new MachineConnectorException("OPC UA needs the OPC Foundation client library, which is not part of this build yet. Choose another connector or ask for OPC UA to be added.");
}

/// <summary>CSV files in a folder: timestamp,state,partCount,scrapCount,alarm. Each read file moves to read/.</summary>
public sealed class FileDropConnector : IMachineConnector
{
    public ConnectorKind Kind => ConnectorKind.FileDrop;

    public async Task<IReadOnlyList<MachineReading>> ReadAsync(MachineConnection connection, DateTimeOffset now, CancellationToken ct)
    {
        var folder = ConnectorCatalog.Setting(connection, "folder")!;
        if (!Directory.Exists(folder)) throw new MachineConnectorException($"The folder {folder} does not exist.");
        var done = Directory.CreateDirectory(Path.Combine(folder, "read")).FullName;
        var readings = new List<MachineReading>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.csv").Order(StringComparer.Ordinal).ToList())
        {
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (IOException) { continue; }
            readings.AddRange(Parse(lines));
            File.Move(file, Path.Combine(done, Path.GetFileName(file)), overwrite: true);
        }

        return readings;
    }

    public static IEnumerable<MachineReading> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var line in lines)
        {
            var parts = line.Split(',');
            if (parts.Length < 2 || !DateTimeOffset.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                || !Enum.TryParse<MachineState>(parts[1].Trim(), true, out var state)) continue;
            long? Number(int i) => parts.Length > i && long.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
            yield return new MachineReading(at, state, Number(2), Number(3), parts.Length > 4 && parts[4].Trim().Length > 0 ? parts[4].Trim() : null);
        }
    }
}

/// <summary>
/// A simulated machine: it runs, making a part every cycle, and stops for a while at regular intervals. Its state is worked out from
/// the clock, so every poll agrees with the last one.
/// </summary>
public sealed class SimulatorConnector : IMachineConnector
{
    public ConnectorKind Kind => ConnectorKind.Simulator;

    public Task<IReadOnlyList<MachineReading>> ReadAsync(MachineConnection connection, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Task.FromResult<IReadOnlyList<MachineReading>>([Simulate(now, Math.Max(1, ConnectorCatalog.IntSetting(connection, "cycleSeconds", 60)),
            Math.Max(1, ConnectorCatalog.IntSetting(connection, "stopEveryMinutes", 120)), Math.Max(0, ConnectorCatalog.IntSetting(connection, "stopMinutes", 10)))]);
    }

    public static MachineReading Simulate(DateTimeOffset now, int cycleSeconds, int stopEveryMinutes, int stopMinutes)
    {
        var period = (stopEveryMinutes + stopMinutes) * 60L;
        var seconds = now.ToUnixTimeSeconds();
        var position = seconds % period;
        var running = position < stopEveryMinutes * 60L;
        var runningSeconds = (seconds / period * stopEveryMinutes * 60L) + Math.Min(position, stopEveryMinutes * 60L);
        var parts = runningSeconds / cycleSeconds;
        return new MachineReading(now, running ? MachineState.Running : MachineState.Stopped, parts, parts / 50);
    }
}
