using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Host.Identification;
using Forge.Kernel.Security;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V41IdentificationTests(PostgresFixture postgres)
{
    private const string Namespace = "V41.Plant_A";
    private const string Endpoint = "/api/identification/resolve";

    private static string Code(long order, int? sequence = null) => sequence is { } value
        ? $"forge:v1:{Namespace}:operation:{order.ToString(CultureInfo.InvariantCulture)}:{value.ToString(CultureInfo.InvariantCulture)}"
        : $"forge:v1:{Namespace}:order:{order.ToString(CultureInfo.InvariantCulture)}";

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> ResolveAsync(HttpClient client, string code)
    {
        // Resolution is a read. It needs neither a command reason nor an idempotency key.
        using var response = await client.PostAsJsonAsync(Endpoint, new { code });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static void Source(JsonElement result, long order, int? sequence, string status)
    {
        Assert.Equal((Code(order, sequence), Namespace, sequence is null ? "order" : "operation", order, "ROLL-100", "A", status),
            (result.GetProperty("code").GetString(), result.GetProperty("namespace").GetString(), result.GetProperty("kind").GetString(),
             result.GetProperty("orderNumber").GetInt64(), result.GetProperty("partNumber").GetString(), result.GetProperty("revision").GetString(), result.GetProperty("status").GetString()));
        if (sequence is { } value) Assert.Equal(value, result.GetProperty("sequence").GetInt32());
        else Assert.Equal(JsonValueKind.Null, result.GetProperty("sequence").ValueKind);
    }

    private static async Task<(int Status, string? Title, string? Detail, string? Code)> FailureAsync(HttpClient client, string? code, HttpStatusCode status, string outcome)
    {
        using var response = await client.PostAsJsonAsync(Endpoint, new { code });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var error = document.RootElement;
        Assert.Equal(outcome, error.GetProperty("code").GetString());
        Assert.Equal((int)status, error.GetProperty("status").GetInt32());
        Assert.DoesNotContain("ROLL-100", body, StringComparison.Ordinal);
        if (code is not null) Assert.DoesNotContain(code, body, StringComparison.Ordinal);
        return (error.GetProperty("status").GetInt32(), error.GetProperty("title").GetString(), error.GetProperty("detail").GetString(), error.GetProperty("code").GetString());
    }

    private static async Task<string[]> StateAsync(ForgeApp app)
    {
        // Read the real source rows, audit values/hash chain and delivery/idempotency rows.
        // Normal setup/mutations happen outside each before/after resolution interval.
        string[] tables = ["execution.production_order", "execution.order_operation", "execution.order_material", "execution.labor", "execution.training_session", "execution.production_report", "kernel.audit_log", "kernel.outbox", "kernel.event_log", "kernel.idempotency"];
        var result = new List<string>();
        await using var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        foreach (var table in tables)
        {
            await using var command = new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(source) ORDER BY to_jsonb(source)::text)::text,'[]') FROM {table} source", connection);
            result.Add(table + ":" + (string)(await command.ExecuteScalarAsync())!);
        }
        return result.ToArray();
    }

    [Fact]
    public async Task Real_source_resolution_uses_current_status_and_scoped_access_without_writes_or_stale_success()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: builder => builder.UseSetting(CanonicalIdentification.NamespaceConfigurationKey, Namespace));
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("identification-programs", "ProgramManager");
        var programs = new List<Guid>();
        foreach (var suffix in new[] { "A", "B" })
            programs.Add(Guid.Parse((await plant.Ok(plant.As("identification-programs"), "/api/programs/", new { code = "IDENT-" + suffix, name = "Identification source " + suffix })).GetProperty("entityId").GetString()!));
        const string role = "IdentificationOrderReader";
        await plant.Ok(plant.Admin, "/api/kernel/roles", new { role, displayName = "Read only actual program A orders", permissions = new[] { Permissions.OrdersRead }, reason = "Configure the existing source read permission" });
        await plant.Ok(plant.Admin, $"/api/kernel/roles/{role}/permission-scopes", new { permission = Permissions.OrdersRead, scopeType = "program", scopeId = programs[0].ToString("D"), reason = "Source read authority is limited to A" });
        var readerId = await plant.Person("identification-reader", role);
        await plant.Person("identification-no-read", "Auditor");
        foreach (var person in new[] { plant.People["eng"], plant.People["planner"], readerId, plant.People["identification-no-read"] })
        foreach (var program in programs)
            await plant.Ok(plant.Admin, $"/api/kernel/people/{person}/programs", new { programId = program, reason = "Independent source program membership" });
        var orders = new List<long>();
        foreach (var program in programs)
            orders.Add(Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), priority = 3, programId = program, reason = "Create a real source order" })));
        var reader = plant.As("identification-reader");
        var before = await StateAsync(app);
        Source(await ResolveAsync(reader, Code(orders[0])), orders[0], null, "Planned");
        var concealed = await FailureAsync(reader, Code(long.MaxValue), HttpStatusCode.NotFound, "identification_not_found");
        Assert.Equal(concealed, await FailureAsync(reader, Code(orders[0], 10), HttpStatusCode.NotFound, "identification_not_found")); // no frozen operations until release
        Assert.Equal(concealed, await FailureAsync(reader, Code(orders[1]), HttpStatusCode.NotFound, "identification_not_found")); // membership B does not override the scoped role
        Assert.Equal(concealed, await FailureAsync(plant.As("identification-no-read"), Code(orders[0]), HttpStatusCode.NotFound, "identification_not_found"));
        using (var unauthenticated = await app.CreateClient().PostAsJsonAsync(Endpoint, new { code = Code(orders[0]) }))
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using (var unregistered = await plant.As("authenticated-unregistered-subject").PostAsJsonAsync(Endpoint, new { code = Code(orders[0]) }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, unregistered.StatusCode);
            Assert.DoesNotContain("ROLL-100", await unregistered.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        Assert.Equal(before, await StateAsync(app));

        foreach (var order in orders)
            await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Release the real routing and BOM" });
        before = await StateAsync(app);
        for (var repeat = 0; repeat < 3; repeat++)
        {
            Source(await ResolveAsync(reader, Code(orders[0])), orders[0], null, "Released");
            Source(await ResolveAsync(reader, Code(orders[0], 10)), orders[0], 10, "Pending");
        }
        Assert.Equal(concealed, await FailureAsync(reader, Code(orders[0], int.MaxValue), HttpStatusCode.NotFound, "identification_not_found"));
        Assert.Equal(concealed, await FailureAsync(reader, Code(orders[1], 10), HttpStatusCode.NotFound, "identification_not_found"));
        Assert.Equal(before, await StateAsync(app));

        // Observe a real operation transition through ordinary authorized shop-floor commands.
        await plant.Person("identification-terminal", "ShopFloorTerminal");
        foreach (var person in new[] { plant.People["sarah"], plant.People["identification-terminal"] })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{person}/programs", new { programId = programs[0], reason = "Current source access for actual work" });
        const string badge = "IDENT-SARAH-41";
        var terminal = plant.As("identification-terminal");
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge });
        await plant.Ok(terminal, "/api/shopfloor/clock-in", new { badge });
        await plant.Ok(terminal, "/api/shopfloor/start", new { badge, orderNumber = orders[0], sequence = 10, machineCode = "SLITTER-A" });
        before = await StateAsync(app);
        Source(await ResolveAsync(reader, Code(orders[0], 10)), orders[0], 10, "InProgress");
        Source(await ResolveAsync(reader, Code(orders[0])), orders[0], null, "Released");
        Assert.Equal(before, await StateAsync(app));
        await plant.Ok(terminal, "/api/shopfloor/stop", new { badge });
        await plant.Ok(plant.Engineer, $"/api/orders/{orders[0]}/hold", new { reason = "Hold the actual source order" });
        before = await StateAsync(app);
        Source(await ResolveAsync(reader, Code(orders[0])), orders[0], null, "Held");
        Source(await ResolveAsync(reader, Code(orders[0], 10)), orders[0], 10, "InProgress");
        Assert.Equal(before, await StateAsync(app));

        await plant.Ok(plant.Admin, $"/api/kernel/people/{readerId}/programs/{programs[0]}/revoke", new { reason = "Withdraw current source membership" });
        before = await StateAsync(app);
        Assert.Equal(concealed, await FailureAsync(reader, Code(orders[0]), HttpStatusCode.NotFound, "identification_not_found"));
        Assert.Equal(concealed, await FailureAsync(reader, Code(orders[0], 10), HttpStatusCode.NotFound, "identification_not_found"));
        Assert.Equal(before, await StateAsync(app));
        await plant.Ok(plant.Admin, $"/api/kernel/people/{readerId}/programs", new { programId = programs[0], reason = "Restore independently authorized source membership" });
        before = await StateAsync(app);
        Source(await ResolveAsync(reader, Code(orders[0])), orders[0], null, "Held");
        Source(await ResolveAsync(reader, Code(orders[0], 10)), orders[0], 10, "InProgress");
        Assert.Equal(before, await StateAsync(app));

        await plant.Ok(plant.Admin, $"/api/kernel/roles/{role}/permissions/{Permissions.OrdersRead}/revoke", new { reason = "Withdraw the source read permission while membership remains" });
        before = await StateAsync(app);
        Assert.Equal(concealed, await FailureAsync(reader, Code(orders[0]), HttpStatusCode.NotFound, "identification_not_found"));
        Assert.Equal(before, await StateAsync(app));
        await plant.Ok(plant.Admin, $"/api/kernel/roles/{role}/permission-scopes", new { permission = Permissions.OrdersRead, scopeType = "program", scopeId = programs[0].ToString("D"), reason = "Restore only A source read authority" });
        before = await StateAsync(app);
        Source(await ResolveAsync(reader, Code(orders[0])), orders[0], null, "Held");
        Assert.Equal(concealed, await FailureAsync(reader, Code(orders[1]), HttpStatusCode.NotFound, "identification_not_found"));
        Assert.Equal(before, await StateAsync(app));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bad namespace")]
    [InlineData("V41\n")]
    public async Task Absent_or_invalid_namespace_is_unavailable_even_for_a_malformed_code(string? configuredNamespace)
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: builder => builder.UseSetting(CanonicalIdentification.NamespaceConfigurationKey, configuredNamespace));
        var client = app.As(ForgeApp.AdminSubject);
        var before = await StateAsync(app);
        await FailureAsync(client, Code(1), HttpStatusCode.ServiceUnavailable, "identification_not_configured");
        await FailureAsync(client, "not-canonical", HttpStatusCode.ServiceUnavailable, "identification_not_configured");
        Assert.Equal(before, await StateAsync(app));
    }

    [Fact]
    public async Task Malformed_and_foreign_namespace_requests_return_safe_errors_and_write_nothing()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: builder => builder.UseSetting(CanonicalIdentification.NamespaceConfigurationKey, Namespace));
        var client = app.As(ForgeApp.AdminSubject);
        var before = await StateAsync(app);
        foreach (var malformed in new string?[] { null, "forge:v1:V41.Plant_A:order:01", "forge:v2:V41.Plant_A:order:1", "https://example.test/private-order/1", new string('x', 257) })
            await FailureAsync(client, malformed, HttpStatusCode.BadRequest, "invalid_identification");
        await FailureAsync(client, "forge:v1:Other:order:1", HttpStatusCode.BadRequest, "foreign_namespace");
        await FailureAsync(client, "forge:v1:v41.Plant_A:order:1", HttpStatusCode.BadRequest, "foreign_namespace");
        Assert.Equal(before, await StateAsync(app));
    }
}
