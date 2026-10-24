using System.Globalization;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>An approved positive adjustment can open a lot that has never been received (WhiskerWorks opening balances).</summary>
[Collection(PostgresCollection.Name)]
public sealed class OpeningBalanceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_approved_opening_balance_records_the_new_lot_and_its_stock()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("ivy", "InventoryController");
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = "SUB-OPEN", description = "Made subassembly", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 2, lotTracked = true });
        var request = long.Parse((await plant.Ok(plant.As("stores"), "/api/inventory/adjustments",
            new { partNumber = "SUB-OPEN", locationCode = "STORES", lotNumber = "SUB-OPEN-01", status = "Released", change = 500m, reason = "Opening balance" }))
            .GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
        await plant.Ok(plant.As("ivy"), $"/api/inventory/requests/{request}/approve", new { reason = "Counted on the floor" });

        var response = await plant.As("ivy").GetAsync("/api/inventory/stock?part=SUB-OPEN");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        var row = Assert.Single(JsonDocument.Parse(text).RootElement.EnumerateArray());
        Assert.Equal("SUB-OPEN-01", row.GetProperty("lotNumber").GetString());
        Assert.Equal(500m, row.GetProperty("quantity").GetDecimal());
    }
}
