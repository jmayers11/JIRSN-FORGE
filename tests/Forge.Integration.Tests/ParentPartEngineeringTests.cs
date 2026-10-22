using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ParentPartEngineeringTests(PostgresFixture postgres)
{
    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task Parent_revision_bom_and_routing_updates_are_audited_draft_only_commands()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        var engineer = plant.Engineer;
        const string revision = "/api/engineering/items/ROLL-100/revisions/B";

        await plant.Ok(engineer, "/api/engineering/items/ROLL-100/revisions", new { revision = "B" });
        await plant.Ok(engineer, $"{revision}/bom", new { lineNumber = 10, componentPartNumber = "COIL-AL", quantityPer = 2m, scrapPercent = 1m });
        await plant.Ok(engineer, $"{revision}/routing", new
        {
            sequence = 10,
            description = "Slit",
            workCenterCode = "SLIT-WC",
            activityCode = "RUN_SLITTER",
            eligibleMachines = new[] { "SLITTER-A" },
            requiredQualifications = Array.Empty<string>(),
            setupMinutes = 30m,
            runMinutesPerUnit = 1.5m,
            crewSize = 1,
        });

        await plant.Ok(engineer, $"{revision}/bom/10/update", new
        {
            componentPartNumber = "COIL-AL",
            quantityPer = 2.75m,
            scrapPercent = 3.5m,
            reason = "Updated material allowance",
        });
        await plant.Ok(engineer, $"{revision}/routing/10/update", new
        {
            sequence = 10,
            description = "Slit and inspect",
            workCenterCode = "SLIT-WC",
            activityCode = "RUN_SLITTER",
            eligibleMachines = new[] { "SLITTER-A" },
            requiredQualifications = Array.Empty<string>(),
            setupMinutes = 25m,
            runMinutesPerUnit = 1.25m,
            crewSize = 1,
            setupFamily = "COIL-AL",
            familySetupMinutes = 5m,
            reason = "Engineering method update",
        });

        var detail = await Get(engineer, revision);
        var line = detail.GetProperty("bom")[0];
        Assert.Equal(2.75m, line.GetProperty("quantityPer").GetDecimal());
        Assert.Equal(3.5m, line.GetProperty("scrapPercent").GetDecimal());
        var operation = detail.GetProperty("routing")[0];
        Assert.Equal("Slit and inspect", operation.GetProperty("description").GetString());
        Assert.Equal(25m, operation.GetProperty("setupMinutes").GetDecimal());
        Assert.Equal("COIL-AL", operation.GetProperty("setupFamily").GetString());
        Assert.Equal(5m, operation.GetProperty("familySetupMinutes").GetDecimal());

        await plant.Ok(plant.Approver, $"{revision}/release", new { reason = "Approved engineering revision" });
        var frozenBom = await plant.Try(engineer, $"{revision}/bom/10/update", new { componentPartNumber = "COIL-AL", quantityPer = 4m, scrapPercent = 0m, reason = "Late edit" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, frozenBom.StatusCode);
        Assert.Contains("revision_released", await frozenBom.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var frozenRouting = await plant.Try(engineer, $"{revision}/routing/10/update", new
        {
            sequence = 10,
            description = "Late edit",
            workCenterCode = "SLIT-WC",
            activityCode = "RUN_SLITTER",
            eligibleMachines = new[] { "SLITTER-A" },
            setupMinutes = 25m,
            runMinutesPerUnit = 1.25m,
            crewSize = 1,
            reason = "Late edit",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, frozenRouting.StatusCode);
        Assert.Contains("revision_released", await frozenRouting.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
