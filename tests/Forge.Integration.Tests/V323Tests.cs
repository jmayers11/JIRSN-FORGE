using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V323Tests(PostgresFixture postgres)
{
    [Fact]
    public async Task Scenarios_are_private_and_preview_returns_fifteen_options_without_writing_production_state()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("eng2", "ManufacturingEngineer");
        var start = DateTimeOffset.UtcNow.AddHours(1);
        var created = await plant.Ok(plant.Engineer, "/api/planning/scenarios", new
        {
            name = "Slitter outage recovery",
            change = new { kind = "MachineUnavailable", machineCode = "SLITTER-A", startsAt = start, durationHours = 4 },
            reason = "Compare outage recovery options"
        });
        var id = Guid.Parse(created.GetProperty("entityId").GetString()!);

        var list = await plant.Engineer.GetFromJsonAsync<JsonElement[]>("/api/planning/scenarios");
        Assert.NotNull(list);
        Assert.Contains(list!, item => item.GetProperty("scenarioId").GetGuid() == id);
        Assert.Equal(HttpStatusCode.NotFound, (await plant.As("eng2").GetAsync($"/api/planning/scenarios/{id:D}")).StatusCode);

        await using var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        var before = await CountAsync(connection, "SELECT count(*) FROM kernel.event_log") + await CountAsync(connection, "SELECT count(*) FROM kernel.audit_log");
        var response = await plant.Engineer.PostAsync($"/api/planning/scenarios/{id:D}/run", content: null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var result = JsonDocument.Parse(body);
        var options = result.RootElement.GetProperty("recoveryOptions").EnumerateArray().ToArray();
        Assert.Equal(15, options.Length);
        Assert.All(options, option =>
        {
            Assert.False(string.IsNullOrWhiteSpace(option.GetProperty("title").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(option.GetProperty("feasibility").GetString()));
            Assert.NotEmpty(option.GetProperty("consequences").EnumerateArray());
            Assert.False(string.IsNullOrWhiteSpace(option.GetProperty("requiredPermission").GetString()));
        });
        Assert.Contains("No source record", result.RootElement.GetProperty("evidenceNote").GetString(), StringComparison.Ordinal);

        var answerResponse = await plant.Engineer.PostAsJsonAsync("/api/analyst/ask", new { question = "What are my recovery options?" });
        var answerText = await answerResponse.Content.ReadAsStringAsync();
        Assert.True(answerResponse.IsSuccessStatusCode, answerText);
        using var answer = JsonDocument.Parse(answerText);
        Assert.Equal("RecoveryOptions", answer.RootElement.GetProperty("interpretation").GetProperty("intent").GetString());
        var answerRows = answer.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(15, answerRows.Length);
        Assert.All(answerRows, row => Assert.Equal("Slitter outage recovery", row.GetProperty("cells")[0].GetString()));

        var hiddenAnswerResponse = await plant.As("eng2").PostAsJsonAsync("/api/analyst/ask", new { question = "What are my recovery options?" });
        var hiddenAnswerText = await hiddenAnswerResponse.Content.ReadAsStringAsync();
        Assert.True(hiddenAnswerResponse.IsSuccessStatusCode, hiddenAnswerText);
        using var hiddenAnswer = JsonDocument.Parse(hiddenAnswerText);
        Assert.Empty(hiddenAnswer.RootElement.GetProperty("rows").EnumerateArray());
        Assert.DoesNotContain("Slitter outage recovery", hiddenAnswerText, StringComparison.Ordinal);

        var after = await CountAsync(connection, "SELECT count(*) FROM kernel.event_log") + await CountAsync(connection, "SELECT count(*) FROM kernel.audit_log");
        Assert.Equal(before, after);

        var hiddenList = await plant.As("eng2").GetFromJsonAsync<JsonElement[]>("/api/planning/scenarios");
        Assert.Empty(hiddenList ?? []);
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
