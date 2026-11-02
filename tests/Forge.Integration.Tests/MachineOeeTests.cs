using System.Globalization;
using System.Net;
using System.Text.Json;
using Forge.Host.Connectivity;
using Forge.Host.Notifications;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// Machine connections, downtime and OEE per shift through the real host (owner decisions 2026-11-01): a connected machine that stops
/// gets an unexplained stoppage that ends when it runs again; the operator explains it at the terminal and can ask maintenance to come;
/// a supervisor enters a stop an operator told them about; OEE per machine per shift counts the downtime and the reported quantities.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MachineOeeTests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-0001";

    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Stamp(int minutesAgo) => DateTime.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task PropagateAsync(ForgeApp app)
    {
        var relay = app.Services.GetRequiredService<OutboxRelay>();
        for (var round = 0; round < 3; round++)
        {
            while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
            {
            }

            await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
        }

        var delivery = app.Services.GetRequiredService<NotificationDeliveryService>();
        while (await delivery.DispatchDueAsync(50, CancellationToken.None) > 0)
        {
        }
    }

    [Fact]
    public async Task A_detected_stop_is_explained_at_the_terminal_a_told_stop_is_entered_for_the_operator_and_oee_counts_both()
    {
        var folder = Path.Combine(Path.GetTempPath(), "forge-machine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Person("kiosk", "ShopFloorTerminal");
        await plant.Person("sup", "Supervisor");
        var (k, sup) = (plant.As("kiosk"), plant.As("sup"));
        await plant.Ok(e, "/api/execution/reason-codes", new { code = "JAM", kind = "Downtime", category = "Maintenance", description = "Material jam" });

        // Only engineers and maintenance planners choose connections; settings are checked.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(sup, "/api/connectivity/machines/SLITTER-A", new { kind = "FileDrop", settings = new { folder }, pollSeconds = 5, stopAfterSeconds = 60, active = true, reason = "x" })).StatusCode);
        var missing = await plant.Try(e, "/api/connectivity/machines/SLITTER-A", new { kind = "ModbusTcp", settings = new { port = "502" }, pollSeconds = 5, stopAfterSeconds = 60, active = true, reason = "PLC" });
        Assert.Contains("setting_required", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(e, "/api/connectivity/machines/SLITTER-A", new { kind = "FileDrop", settings = new { folder }, pollSeconds = 5, stopAfterSeconds = 60, active = true, reason = "Historian export" });
        var catalog = await Get(sup, "/api/connectivity/connectors");
        Assert.Equal(7, catalog.GetArrayLength());

        // The machine ran, then stopped twenty minutes ago.
        await File.WriteAllLinesAsync(Path.Combine(folder, "a.csv"), [$"{Stamp(30)},Running,100,0,", $"{Stamp(20)},Stopped,110,1,"]);
        var poller = app.Services.GetRequiredService<MachinePoller>();
        Assert.Equal(1, await poller.RunOnceAsync(true, CancellationToken.None));
        await PropagateAsync(app);
        var status = Assert.Single((await Get(sup, "/api/connectivity/machines")).GetProperty("status").EnumerateArray());
        Assert.Equal(("Stopped", 110L), (status.GetProperty("state").GetString(), status.GetProperty("partCount").GetInt64()));
        var detected = Assert.Single((await Get(sup, "/api/execution/stoppages?open=true")).EnumerateArray()).GetProperty("stoppage");
        Assert.Equal(("UNEXPLAINED", "Connector"), (detected.GetProperty("reasonCode").GetString(), detected.GetProperty("source").GetString()));
        Assert.Contains((await Get(sup, "/api/notifications/inbox")).EnumerateArray(), a => a.GetProperty("title").GetString() == "Machine stop needs a reason");

        // Nobody else may use the unexplained reason; the operator explains the stop at the terminal and asks maintenance to come.
        var unexplained = await plant.Try(sup, "/api/execution/stoppages", new { machineCode = "SLITTER-A", category = "Unplanned", reasonCode = "UNEXPLAINED" });
        Assert.Contains("reason_required", await unexplained.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("unknown_badge", await (await plant.Try(k, "/api/shopfloor/downtime", new { badge = "NOPE", machineCode = "SLITTER-A", category = "Maintenance", reasonCode = "JAM", requestMaintenance = false })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(k, "/api/shopfloor/downtime", new { badge = Sarah, machineCode = "SLITTER-A", category = "Maintenance", reasonCode = "JAM", note = "Coil wrapped on the arbor", requestMaintenance = true });
        var explained = Assert.Single((await Get(sup, "/api/execution/stoppages?open=true")).EnumerateArray()).GetProperty("stoppage");
        Assert.Equal(("JAM", "Maintenance", $"person:{plant.People["sarah"]}"), (explained.GetProperty("reasonCode").GetString(), explained.GetProperty("category").GetString(), explained.GetProperty("reportedBy").GetString()));

        // Running again five minutes ago: the stoppage ends then.
        await File.WriteAllLinesAsync(Path.Combine(folder, "b.csv"), [$"{Stamp(5)},Running,111,1,"]);
        await poller.RunOnceAsync(true, CancellationToken.None);
        await PropagateAsync(app);
        Assert.Empty((await Get(sup, "/api/execution/stoppages?open=true")).EnumerateArray());
        await plant.Ok(sup, "/api/maintenance/machines/SLITTER-A/restore", new { reason = "Jam cleared and checked" });

        // A stop an operator told the supervisor about, entered on their behalf.
        var told = Id(await plant.Ok(sup, "/api/execution/stoppages", new { machineCode = "SLITTER-A", category = "Maintenance", reasonCode = "JAM", startedAt = DateTimeOffset.UtcNow.AddMinutes(-3), reportedBy = plant.People["sarah"], note = "Sarah radioed it in" }));
        await plant.Ok(sup, $"/api/execution/stoppages/{told}/end", new { });
        var entered = (await Get(sup, "/api/execution/stoppages?days=1")).EnumerateArray().Select(v => v.GetProperty("stoppage")).Single(s => s.GetProperty("stoppageId").GetInt64() == told);
        Assert.Equal(("Recorded", $"person:{plant.People["sarah"]}", $"person:{plant.People["sup"]}"),
            (entered.GetProperty("source").GetString(), entered.GetProperty("reportedBy").GetString(), entered.GetProperty("startedBy").GetString()));

        // Production on the machine: 9 good, 1 scrap.
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        await plant.Ok(k, "/api/shopfloor/clock-in", new { badge = Sarah });
        await plant.Ok(k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        await plant.Ok(k, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 9m, scrap = 1m, scrapReason = "Edge wave", complete = false });

        var oee = await Get(sup, $"/api/oee?from={Day(-1)}&to={Day(1)}&machine=SLITTER-A");
        var shift = oee.GetProperty("shifts").EnumerateArray().First(s => s.GetProperty("inProgress").GetBoolean());
        Assert.True(shift.GetProperty("downtimeMinutes").GetDecimal() >= 15m, shift.GetRawText());
        Assert.Equal((9m, 1m, 0.9m), (shift.GetProperty("good").GetDecimal(), shift.GetProperty("scrap").GetDecimal(), shift.GetProperty("quality").GetDecimal()));
        Assert.Equal("JAM", shift.GetProperty("reasons")[0].GetProperty("reasonCode").GetString());
        Assert.True(shift.GetProperty("availability").GetDecimal() < 1m);
        Assert.Contains(oee.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("does not measure or rank the people", StringComparison.Ordinal));

        // Passwords for machine connections are stored encrypted, never shown, and kept when the masked value is sent back.
        await plant.Ok(e, "/api/connectivity/machines/SLITTER-A", new { kind = "OpcUa", settings = new Dictionary<string, string> { ["endpoint"] = "opc.tcp://127.0.0.1:1", ["stateNode"] = "ns=2;s=Run", ["username"] = "forge", ["password"] = "s3cret-pw" },
            pollSeconds = 5, stopAfterSeconds = 60, active = false, reason = "PLC upgrade" });
        string Stored()
        {
            using var db = new Npgsql.NpgsqlConnection(app.ConnectionString);
            db.Open();
            using var cmd = new Npgsql.NpgsqlCommand("SELECT settings->>'password' FROM connectivity.machine_connection WHERE machine_code = 'SLITTER-A'", db);
            return (string)cmd.ExecuteScalar()!;
        }

        var encrypted = Stored();
        Assert.StartsWith("enc:", encrypted, StringComparison.Ordinal);
        var shown = (await Get(sup, "/api/connectivity/machines")).GetProperty("connections")[0].GetProperty("settings");
        Assert.Equal("••••••", shown.GetProperty("password").GetString());
        Assert.DoesNotContain("s3cret-pw", shown.GetRawText(), StringComparison.Ordinal);
        await plant.Ok(e, "/api/connectivity/machines/SLITTER-A", new { kind = "OpcUa", settings = new Dictionary<string, string> { ["endpoint"] = "opc.tcp://127.0.0.1:1", ["stateNode"] = "ns=2;s=Run", ["username"] = "forge", ["password"] = "••••••" },
            pollSeconds = 5, stopAfterSeconds = 120, active = false, reason = "Longer threshold" });
        Assert.Equal(encrypted, Stored());
    }
}
