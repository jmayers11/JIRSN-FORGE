using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using Forge.Connectivity.Contracts;
using Forge.Host.Connectivity;

namespace Forge.Integration.Tests;

/// <summary>Owner decisions 2026-11-01: each connector turns what a machine reports into state, part count, scrap count and alarm.</summary>
public sealed class ConnectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    private static MachineConnection Connection(ConnectorKind kind, params (string Key, string Value)[] settings) =>
        new("M1", kind, new ReadOnlyDictionary<string, string>(settings.ToDictionary(s => s.Key, s => s.Value)), 5, 60, true, "test", Now);

    [Fact]
    public void Mtconnect_execution_part_count_and_faults_become_state_count_and_alarm()
    {
        const string Doc = """
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:1.3"><Streams>
              <DeviceStream name="VMC-1"><ComponentStream component="Controller"><Events><Execution>ACTIVE</Execution><PartCount>1234</PartCount><EmergencyStop>ARMED</EmergencyStop></Events>
                <Condition><Normal type="SYSTEM"/></Condition></ComponentStream></DeviceStream>
              <DeviceStream name="LATHE-2"><ComponentStream component="Controller"><Events><Execution>READY</Execution><PartCount>UNAVAILABLE</PartCount></Events>
                <Condition><Fault type="SYSTEM">Spindle overload</Fault></Condition></ComponentStream></DeviceStream>
            </Streams></MTConnectStreams>
            """;
        var vmc = MTConnectConnector.Parse(Doc, "VMC-1", Now);
        Assert.Equal((MachineState.Running, 1234L, (string?)null), (vmc.State, vmc.PartCount, vmc.Alarm));
        var lathe = MTConnectConnector.Parse(Doc, "lathe-2", Now);
        Assert.Equal((MachineState.Alarm, (long?)null, "Spindle overload"), (lathe.State, lathe.PartCount, lathe.Alarm));
        Assert.Equal(MachineState.Running, MTConnectConnector.Parse(Doc, null, Now).State);
        Assert.Contains("no device", Assert.Throws<MachineConnectorException>(() => MTConnectConnector.Parse(Doc, "NOPE", Now)).Message, StringComparison.Ordinal);
        Assert.Throws<MachineConnectorException>(() => MTConnectConnector.Parse("<broken", null, Now));
    }

    [Fact]
    public async Task Modbus_reads_state_and_32_bit_counters_from_holding_registers()
    {
        var request = ModbusTcpConnector.Request(7, 1, 100, 2);
        Assert.Equal(new byte[] { 0, 7, 0, 0, 0, 6, 1, 3, 0, 100, 0, 2 }, request);
        Assert.Equal(new ushort[] { 1, 0xFFFF }, ModbusTcpConnector.Response([0, 7, 0, 0, 0, 7, 1, 3, 4, 0, 1, 0xFF, 0xFF], 7, 2));
        Assert.Contains("exception code 2", Assert.Throws<MachineConnectorException>(() => ModbusTcpConnector.Response([0, 7, 0, 0, 0, 3, 1, 0x83, 2], 7, 2)).Message, StringComparison.Ordinal);

        // A fake PLC: register 10 = 1 (running), registers 20-21 = 70000 parts.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var plc = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var registers = new Dictionary<int, ushort> { [10] = 1, [20] = 1, [21] = 4464 };
            for (var i = 0; i < 2; i++)
            {
                var req = new byte[12];
                await stream.ReadExactlyAsync(req);
                var address = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(8));
                var count = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(10));
                var answer = new byte[9 + (count * 2)];
                req.AsSpan(0, 4).CopyTo(answer);
                BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(4), (ushort)(3 + (count * 2)));
                answer[6] = req[6];
                answer[7] = 3;
                answer[8] = (byte)(count * 2);
                for (var r = 0; r < count; r++) BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(9 + (r * 2)), registers.GetValueOrDefault(address + r));
                await stream.WriteAsync(answer);
            }
        });
        var reading = Assert.Single(await new ModbusTcpConnector().ReadAsync(
            Connection(ConnectorKind.ModbusTcp, ("host", "127.0.0.1"), ("port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("stateRegister", "10"), ("countRegister", "20")), Now, CancellationToken.None));
        await plc;
        Assert.Equal((MachineState.Running, 70000L), (reading.State, reading.PartCount));
    }

    [Fact]
    public void Mqtt_packets_are_encoded_and_status_messages_read()
    {
        Assert.Equal(new byte[] { 0x7F }, MqttConnector.RemainingLength(127));
        Assert.Equal(new byte[] { 0x80, 0x01 }, MqttConnector.RemainingLength(128));
        var connect = MqttConnector.Connect("forge-m1");
        Assert.Equal((0x10, 4, (byte)'M'), (connect[0], connect[3], connect[4]));
        var subscribe = MqttConnector.Subscribe(1, "plant/m1");
        Assert.Equal(0x82, subscribe[0]);
        byte[] publish = [0, 8, .. "plant/m1"u8.ToArray(), .. """{"state":"Alarm","partCount":12,"alarm":"Door open"}"""u8.ToArray()];
        var message = MqttConnector.Publish(publish)!.Value;
        Assert.Equal("plant/m1", message.Topic);
        var reading = MqttConnector.Reading(message, Now)!;
        Assert.Equal((MachineState.Alarm, 12L, "Door open"), (reading.State, reading.PartCount, reading.Alarm));
        Assert.Null(MqttConnector.Reading(("t", "not json"), Now));
    }

    [Fact]
    public void File_drop_csv_and_the_simulator_give_readings()
    {
        var rows = FileDropConnector.Parse(["timestamp,state,partCount,scrapCount,alarm", "2026-11-02T14:00:00Z,Running,100,2,", "2026-11-02T14:10:00Z,alarm,105,2,Jam"]).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal((MachineState.Alarm, 105L, "Jam"), (rows[1].State, rows[1].PartCount, rows[1].Alarm));
        var running = SimulatorConnector.Simulate(DateTimeOffset.FromUnixTimeSeconds(130 * 60 * 1000), 60, 120, 10);
        var stopped = SimulatorConnector.Simulate(DateTimeOffset.FromUnixTimeSeconds((130 * 60 * 1000) + (125 * 60)), 60, 120, 10);
        Assert.Equal((MachineState.Running, MachineState.Stopped), (running.State, stopped.State));
        Assert.Equal(120, stopped.PartCount - running.PartCount);
    }

    [Fact]
    public async Task Opc_ua_says_plainly_that_it_is_not_in_this_build()
    {
        var ex = await Assert.ThrowsAsync<MachineConnectorException>(() => new OpcUaConnector().ReadAsync(Connection(ConnectorKind.OpcUa), Now, CancellationToken.None));
        Assert.Contains("OPC Foundation", ex.Message, StringComparison.Ordinal);
    }
}
