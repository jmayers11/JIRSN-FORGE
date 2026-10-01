using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class KernelRoundTripTests(PostgresFixture postgres)
{
    private static readonly Guid Falcon = Guid.Parse("018f0000-0000-7000-8000-000000000001");

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<long> Count(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Migrations_are_idempotent()
    {
        var cs = await postgres.CreateDatabaseAsync();
        await using var app = new ForgeApp(cs);
        _ = app.Services; // starting the host applies every migration
        var again = await app.Services.GetRequiredService<MigrationRunner>().MigrateAsync(CancellationToken.None);
        Assert.Empty(again);
        Assert.Equal(15, await Count(cs, "SELECT count(*) FROM public.forge_schema_migrations"));
    }

    [Fact]
    public async Task Command_flows_to_state_event_log_and_audit()
    {
        var cs = await postgres.CreateDatabaseAsync();
        await using var app = new ForgeApp(cs);
        var admin = app.As(ForgeApp.AdminSubject);

        var registered = await Json(await admin.CommandAsync("/api/kernel/people",
            new { idpSubject = "sarah", displayName = "Sarah", markings = new[] { "ControlledUnclassified" }, reason = "New planner" }, "reg-sarah"));
        var sarahId = registered.GetProperty("entityId").GetString();

        var granted = await Json(await admin.CommandAsync($"/api/kernel/people/{sarahId}/programs",
            new { programId = Falcon, reason = "Assigned to Falcon" }, "grant-sarah-falcon"));
        Assert.Equal("ok", granted.GetProperty("code").GetString());

        // Relay the outbox and check the event log carries every event in order.
        var relay = app.Services.GetRequiredService<OutboxRelay>();
        while (await relay.RelayBatchAsync(10, CancellationToken.None) > 0)
        {
        }

        Assert.Equal(0, await Count(cs, "SELECT count(*) FROM kernel.outbox WHERE relayed_at IS NULL"));
        Assert.Equal(4, await Count(cs, "SELECT count(*) FROM kernel.event_log")); // bootstrap person + role, sarah registered, access granted
        Assert.Equal(1, await Count(cs, $"SELECT count(*) FROM kernel.event_log WHERE event_type = 'ProgramAccessGranted' AND program_id = '{Falcon}'"));

        // Audit is readable by the administrator and the chain verifies.
        var audit = await Json(await admin.GetAsync($"/api/kernel/audit?entityType=Person&entityId={sarahId}"));
        Assert.Equal(2, audit.GetArrayLength());
        Assert.Equal("GrantProgramAccess", audit[0].GetProperty("entry").GetProperty("action").GetString());
        Assert.Equal("Assigned to Falcon", audit[0].GetProperty("entry").GetProperty("reason").GetString());

        var verify = await Json(await admin.GetAsync("/api/kernel/audit/verify"));
        Assert.True(verify.GetProperty("intact").GetBoolean());

        // Sarah can see her own principal, including the program and marking.
        var me = await Json(await app.As("sarah").GetAsync("/api/kernel/me"));
        Assert.Equal(Falcon, me.GetProperty("programs")[0].GetGuid());
        Assert.Equal("ControlledUnclassified", me.GetProperty("markings").GetString());
    }

    [Fact]
    public async Task Retried_command_is_applied_once()
    {
        var cs = await postgres.CreateDatabaseAsync();
        await using var app = new ForgeApp(cs);
        var admin = app.As(ForgeApp.AdminSubject);
        var body = new { idpSubject = "michael", displayName = "Michael", reason = "New operator" };

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => admin.CommandAsync("/api/kernel/people", body, "same-key")));
        var ids = new List<string?>();
        foreach (var response in responses)
        {
            ids.Add((await Json(response)).GetProperty("entityId").GetString());
        }

        Assert.Single(ids.Distinct());
        Assert.Equal(1, await Count(cs, "SELECT count(*) FROM kernel.person WHERE idp_subject = 'michael'"));
        Assert.Equal(1, await Count(cs, "SELECT count(*) FROM kernel.audit_log WHERE action = 'RegisterPerson'"));

        var conflict = await admin.CommandAsync("/api/kernel/people", new { idpSubject = "other", displayName = "Other", reason = "x" }, "same-key");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Denials_and_rule_violations_are_explained_and_audited()
    {
        var cs = await postgres.CreateDatabaseAsync();
        await using var app = new ForgeApp(cs);
        var admin = app.As(ForgeApp.AdminSubject);
        await Json(await admin.CommandAsync("/api/kernel/people", new { idpSubject = "sarah", displayName = "Sarah", reason = "New planner" }, "reg"));
        var me = await Json(await admin.GetAsync("/api/kernel/me"));
        var adminId = me.GetProperty("personId").GetGuid();

        // No role: denied with reasons.
        var sarah = app.As("sarah");
        var denied = await sarah.GetAsync("/api/kernel/people");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("kernel.people.read", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var deniedCommand = await sarah.CommandAsync($"/api/kernel/people/{adminId}/roles", new { role = "Auditor", reason = "x" }, "k");
        Assert.Equal(HttpStatusCode.Forbidden, deniedCommand.StatusCode);

        // Separation of duties.
        var self = await admin.CommandAsync($"/api/kernel/people/{adminId}/programs", new { programId = Falcon, reason = "x" }, "self");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
        Assert.Contains("separation_of_duties", await self.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Missing idempotency key and unknown identity.
        var noKey = await admin.PostAsJsonAsync("/api/kernel/people", new { idpSubject = "x", displayName = "x", reason = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.As("stranger").GetAsync("/api/kernel/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/kernel/me")).StatusCode);

        Assert.Equal(1, await Count(cs, "SELECT count(*) FROM kernel.audit_log WHERE outcome = 'Denied'"));
        Assert.Equal(1, await Count(cs, "SELECT count(*) FROM kernel.audit_log WHERE outcome = 'Rejected'"));
        Assert.Equal(1, await Count(cs, "SELECT count(*) FROM kernel.outbox WHERE envelope LIKE '%\"eventType\":\"AuthorizationDenied\"%'"));
        Assert.Equal(0, await Count(cs, "SELECT count(*) FROM kernel.program_access"));
    }

    [Fact]
    public async Task Audit_history_cannot_be_edited_and_tampering_is_detected()
    {
        var cs = await postgres.CreateDatabaseAsync();
        await using var app = new ForgeApp(cs);
        var admin = app.As(ForgeApp.AdminSubject);
        await Json(await admin.CommandAsync("/api/kernel/people", new { idpSubject = "sarah", displayName = "Sarah", reason = "New planner" }, "reg"));

        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using (var update = new NpgsqlCommand("UPDATE kernel.audit_log SET reason = 'rewritten'", connection))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
            Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);
        }

        // A superuser can bypass the trigger; the hash chain still exposes the edit.
        await using (var bypass = new NpgsqlCommand(
            "ALTER TABLE kernel.audit_log DISABLE TRIGGER audit_log_append_only; UPDATE kernel.audit_log SET reason = 'rewritten' WHERE sequence = 2; ALTER TABLE kernel.audit_log ENABLE TRIGGER audit_log_append_only;",
            connection))
        {
            await bypass.ExecuteNonQueryAsync();
        }

        var verify = await Json(await admin.GetAsync("/api/kernel/audit/verify"));
        Assert.False(verify.GetProperty("intact").GetBoolean());
        Assert.Equal(2, verify.GetProperty("firstBrokenSequence").GetInt64());
    }
}
