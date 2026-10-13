using System.Net;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AttendanceReplayAuthorityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Shared_terminal_clock_in_retry_is_bound_to_current_badge_owner_without_a_second_attendance_or_secret_record()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var plant = await SlittingPlant.BuildAsync(app);
        await plant.Person("attendance-replay-kiosk", "ShopFloorTerminal");
        var kiosk = plant.As("attendance-replay-kiosk");
        const string sarah = "ATTENDANCE-REPLAY-SARAH";
        const string michael = "ATTENDANCE-REPLAY-MICHAEL";
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = sarah });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["michael"]}/badge", new { badge = michael });

        var first = await kiosk.CommandAsync("/api/shopfloor/clock-in", new { badge = sarah }, "attendance-owner-collision");
        var body = await first.Content.ReadAsStringAsync();
        Assert.True(first.IsSuccessStatusCode, body);
        using var result = JsonDocument.Parse(body);
        Assert.Equal(plant.People["sarah"].ToString(), result.RootElement.GetProperty("entityId").GetString());
        var repeated = await kiosk.CommandAsync("/api/shopfloor/clock-in", new { badge = sarah }, "attendance-owner-collision");
        Assert.True(repeated.IsSuccessStatusCode, await repeated.Content.ReadAsStringAsync());
        Assert.Equal(body, await repeated.Content.ReadAsStringAsync());

        var otherPerson = await kiosk.CommandAsync("/api/shopfloor/clock-in", new { badge = michael }, "attendance-owner-collision");
        Assert.Equal(HttpStatusCode.Conflict, otherPerson.StatusCode);
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var attendance = new NpgsqlCommand("SELECT person_id FROM workforce.attendance ORDER BY attendance_id", connection);
        await using (var reader = await attendance.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(plant.People["sarah"], reader.GetGuid(0));
            Assert.False(await reader.ReadAsync());
        }
        foreach (var table in new[] { "kernel.idempotency", "kernel.audit_log", "kernel.outbox", "kernel.event_log" })
        {
            await using var records = new NpgsqlCommand($"SELECT coalesce(string_agg(t::text, ' '), '') FROM {table} t", connection);
            var serialized = (string)(await records.ExecuteScalarAsync())!;
            Assert.DoesNotContain(sarah, serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(michael, serialized, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("clock-in")]
    [InlineData("clock-out")]
    [InlineData("pin")]
    public async Task Revoked_badge_cannot_replay_a_previously_successful_terminal_attendance_or_pin_command(string action)
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var plant = await SlittingPlant.BuildAsync(app);
        await plant.Person("attendance-revoke-kiosk", "ShopFloorTerminal");
        var kiosk = plant.As("attendance-revoke-kiosk");
        const string badge = "ATTENDANCE-REVOKED-SARAH";
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge });
        if (action == "clock-out") await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge });
        object command = action == "pin" ? new { badge, newPin = "70429183" } : new { badge };
        var url = "/api/shopfloor/" + action;
        var first = await kiosk.CommandAsync(url, command, "attendance-revoked-source");
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        var repeated = await kiosk.CommandAsync(url, command, "attendance-revoked-source");
        Assert.True(repeated.IsSuccessStatusCode, await repeated.Content.ReadAsStringAsync());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await repeated.Content.ReadAsStringAsync());

        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge/revoke", new { reason = "Badge access ended" });
        var denied = await kiosk.CommandAsync(url, command, "attendance-revoked-source");
        Assert.False(denied.IsSuccessStatusCode);
        Assert.Contains("unknown_badge", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        foreach (var table in new[] { "kernel.idempotency", "kernel.audit_log", "kernel.outbox", "kernel.event_log" })
        {
            await using var records = new NpgsqlCommand($"SELECT coalesce(string_agg(t::text, ' '), '') FROM {table} t", connection);
            var serialized = (string)(await records.ExecuteScalarAsync())!;
            Assert.DoesNotContain(badge, serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("70429183", serialized, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Shared_terminal_pin_key_cannot_replay_another_badge_owners_success()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var plant = await SlittingPlant.BuildAsync(app);
        await plant.Person("pin-replay-kiosk", "ShopFloorTerminal");
        var kiosk = plant.As("pin-replay-kiosk");
        const string sarah = "PIN-REPLAY-SARAH";
        const string michael = "PIN-REPLAY-MICHAEL";
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = sarah });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["michael"]}/badge", new { badge = michael });
        var first = await kiosk.CommandAsync("/api/shopfloor/pin", new { badge = sarah, newPin = "61749382" }, "pin-owner-collision");
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        var otherPerson = await kiosk.CommandAsync("/api/shopfloor/pin", new { badge = michael, newPin = "61749382" }, "pin-owner-collision");
        Assert.Equal(HttpStatusCode.Conflict, otherPerson.StatusCode);
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var stored = new NpgsqlCommand("SELECT person_id FROM workforce.badge WHERE pin_hash IS NOT NULL", connection);
        Assert.Equal(plant.People["sarah"], (Guid)(await stored.ExecuteScalarAsync())!);
    }
}
