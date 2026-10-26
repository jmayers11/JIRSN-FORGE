using System.Net.Http.Json;
using System.Text.Json;
using Forge.Kernel.Security;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// Sprint 9 demo tenant boundary through the real API: a simulated clock, a saved baseline and a reset exist only on a host started in
/// demo mode against a database designated as a demo-only tenant. A live plant (PLANT1) is refused, and a reset archives instead of deleting.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DemoTenantTests(PostgresFixture postgres)
{
    private static void Demo(IWebHostBuilder builder) => builder.UseSetting("Forge:Demo:Enabled", "true");

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> Rejection(SlittingPlant.Plant plant, HttpClient client, string url, object body) =>
        await (await plant.Try(client, url, body)).Content.ReadAsStringAsync();

    [Fact]
    public async Task A_live_plant_or_a_normal_host_never_gets_a_simulated_clock()
    {
        await using (var normal = new ForgeApp(await postgres.CreateDatabaseAsync()))
        {
            var plant = new SlittingPlant.Plant(normal);
            Assert.False((await Get(plant.Admin, "/api/demo/status")).GetProperty("enabled").GetBoolean());
            Assert.Contains("demo_disabled", await Rejection(plant, plant.Admin, "/api/demo/designate", new { demoCode = "WW-DEMO", siteCode = "WW-P01", reason = "Try" }), StringComparison.Ordinal);
        }

        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: Demo);
        var (live, _) = await SlittingPlant.BuildEngineeredAsync(app);
        Assert.Contains("live_site", await Rejection(live, live.Admin, "/api/demo/designate", new { demoCode = "WW-DEMO", siteCode = "PLANT1", reason = "Try" }), StringComparison.Ordinal);
        Assert.Contains("not_demo_only", await Rejection(live, live.Admin, "/api/demo/designate", new { demoCode = "WW-DEMO", siteCode = "WW-P01", reason = "Try" }), StringComparison.Ordinal);
        Assert.Contains("not_demo", await Rejection(live, live.Admin, "/api/demo/clock/advance", new { minutes = 60, reason = "Try" }), StringComparison.Ordinal);
        Assert.False((await Get(live.Admin, "/api/demo/status")).GetProperty("designated").GetBoolean());
    }

    [Fact]
    public async Task The_demo_clock_drives_expiry_and_a_reset_restores_the_baseline_and_archives_the_rest()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var database = new NpgsqlConnectionStringBuilder(connection).Database!;
        try
        {
            await using var app = new ForgeApp(connection, configureBuilder: Demo);
            var plant = new SlittingPlant.Plant(app);
            await plant.Person("eng", "ManufacturingEngineer");
            await plant.Ok(plant.Engineer, "/api/resources/sites", new { code = "WW-P01", name = "WhiskerWorks", timeZone = TestSite.TimeZone });
            await plant.Person("sam", "SecurityAdministrator");
            await plant.Person("pat", "Planner");
            var recipient = await plant.Person("rita", "Auditor");
            var (sam, pat) = (plant.As("sam"), plant.As("pat"));

            // Only a security administrator designates; then a planner can run the clock but never reset.
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await plant.Try(pat, "/api/demo/designate", new { demoCode = "WW-DEMO", siteCode = "WW-P01", reason = "x" })).StatusCode);
            await plant.Ok(sam, "/api/demo/designate", new { demoCode = "WW-DEMO", siteCode = "WW-P01", reason = "Isolated WhiskerWorks demo" });
            Assert.Contains("invalid_advance", await Rejection(plant, pat, "/api/demo/clock/advance", new { minutes = -15, reason = "Back" }), StringComparison.Ordinal);

            // A two-hour delegation expires when the simulated clock passes it: time-driven state follows the demo clock.
            var start = (await Get(pat, "/api/demo/status")).GetProperty("simulatedNow").GetDateTimeOffset();
            var delegation = Guid.Parse((await plant.Ok(sam, "/api/kernel/delegations", new
            {
                recipient, permissions = new[] { Permissions.DemoControl }, scopes = Array.Empty<object>(), startsAt = start, expiresAt = start.AddHours(2), reason = "Cover the demo for two hours",
            })).GetProperty("entityId").GetString()!);
            await plant.Ok(plant.Admin, $"/api/kernel/delegations/{delegation}/approve", new { reason = "Demo cover" });
            async Task<string> DelegationState() => (await Get(sam, "/api/kernel/delegations")).EnumerateArray()
                .Single(d => d.GetProperty("delegationId").GetGuid() == delegation).GetProperty("state").GetString()!;
            Assert.Equal("Active", await DelegationState());
            await plant.Ok(pat, "/api/demo/clock/advance", new { minutes = 180, reason = "+1h ×3" });
            Assert.Equal("Expired", await DelegationState());
            var status = await Get(pat, "/api/demo/status");
            Assert.Equal(10800, status.GetProperty("offsetSeconds").GetInt64());
            Assert.Equal("SIMULATED DEMO CLOCK", status.GetProperty("label").GetString());
            Assert.True(status.GetProperty("simulatedNow").GetDateTimeOffset() >= start.AddHours(3));

            // Baseline, change, compare, reset.
            await plant.Ok(pat, "/api/demo/baseline", new { reason = "Clean S01 baseline" });
            await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = "WW-AFTER-BASELINE", description = "Added during a scenario", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 1 });
            await plant.Ok(pat, "/api/demo/clock/advance", new { minutes = 480, reason = "+1 shift" });
            var compare = await Get(pat, "/api/demo/compare");
            Assert.Contains(compare.GetProperty("rows").EnumerateArray(), r => r.GetProperty("metric").GetString() == "Released orders");
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await plant.Try(pat, "/api/demo/reset", new { reason = "x" })).StatusCode);

            // A baseline saved by an older build lacks the newest schema: make it look like one. Reset must bring the copy up to date.
            await AgeBaselineAsync(connection, database + "_baseline");

            var reset = await plant.Ok(sam, "/api/demo/reset", new { reason = "Restart the scenario run" });
            Assert.Contains("_archive_", reset.GetProperty("message").GetString()!, StringComparison.Ordinal);
            Assert.Contains("brought up to date with 1 newer migration", reset.GetProperty("message").GetString()!, StringComparison.Ordinal);
            Assert.Equal(System.Net.HttpStatusCode.OK, (await pat.GetAsync(new Uri("/api/planning/replacement-orders", UriKind.Relative))).StatusCode);
            var items = await plant.Engineer.GetFromJsonAsync<JsonElement[]>("/api/engineering/items");
            Assert.DoesNotContain(items ?? [], i => i.GetProperty("partNumber").GetString() == "WW-AFTER-BASELINE");
            var after = await Get(pat, "/api/demo/status");
            Assert.Equal(10800, after.GetProperty("offsetSeconds").GetInt64());   // the baseline's clock, not the later +1 shift
            Assert.True(after.GetProperty("designated").GetBoolean());
            Assert.Equal("Clean S01 baseline", after.GetProperty("baseline").GetProperty("reason").GetString());

            // Nothing was dropped: the replaced state survives as an archive database.
            Assert.Single(await DatabasesAsync(connection, database + "_archive_%"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            foreach (var extra in await DatabasesAsync(connection, database + "\\_%"))
                await postgres.DropDatabaseAsync(extra);
        }
    }

    private static async Task AgeBaselineAsync(string connection, string baseline)
    {
        var maintenance = new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false };
        await using (var admin = new NpgsqlConnection(maintenance.ConnectionString))
        {
            await admin.OpenAsync();
            await using var allow = new NpgsqlCommand($"ALTER DATABASE \"{baseline}\" ALLOW_CONNECTIONS true", admin);
            await allow.ExecuteNonQueryAsync();
        }

        var target = new NpgsqlConnectionStringBuilder(connection) { Database = baseline, Pooling = false };
        await using (var db = new NpgsqlConnection(target.ConnectionString))
        {
            await db.OpenAsync();
            await using var age = new NpgsqlCommand(
                "DROP TABLE planning.replacement_order_proposal; DELETE FROM public.forge_schema_migrations WHERE migration_id = 'planning/0010_replacement_order_proposal'", db);
            await age.ExecuteNonQueryAsync();
        }

        await using (var admin = new NpgsqlConnection(maintenance.ConnectionString))
        {
            await admin.OpenAsync();
            await using var deny = new NpgsqlCommand($"ALTER DATABASE \"{baseline}\" ALLOW_CONNECTIONS false", admin);
            await deny.ExecuteNonQueryAsync();
        }
    }

    private static async Task<IReadOnlyList<string>> DatabasesAsync(string connection, string pattern)
    {
        var maintenance = new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(maintenance.ConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT datname FROM pg_database WHERE datname LIKE $1 ORDER BY datname", admin);
        cmd.Parameters.Add(new NpgsqlParameter { Value = pattern });
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }
}
