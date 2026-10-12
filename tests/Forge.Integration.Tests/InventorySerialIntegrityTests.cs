using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class InventorySerialIntegrityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_material_serial_in_two_places_is_detected_but_a_move_or_split_lot_is_valid()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        var stores = plant.As("stores");
        foreach (var (part, serialized) in new[] { ("INTEGRITY-SERIAL", true), ("INTEGRITY-LOT", false) })
        {
            await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 2, lotTracked = !serialized, serialized });
            await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
            await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Integrity detector reference material" });
        }
        await plant.Ok(stores, "/api/inventory/locations", new { code = "SERIAL-RACK", siteCode = "PLANT1", name = "Serial rack", kind = "Stores" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "INTEGRITY-SERIAL", quantity = 1m, locationCode = "STORES", lotNumber = "SN-ONE", reason = "Receive a unique physical serial" });
        await plant.Ok(stores, "/api/inventory/transfers", new { partNumber = "INTEGRITY-SERIAL", quantity = 1m, fromLocation = "STORES", toLocation = "SERIAL-RACK", lotNumber = "SN-ONE", status = "Released", reason = "Move the same physical serial" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "INTEGRITY-LOT", quantity = 10m, locationCode = "STORES", lotNumber = "LOT-MANY", reason = "Receive a divisible lot" });
        await plant.Ok(stores, "/api/inventory/transfers", new { partNumber = "INTEGRITY-LOT", quantity = 4m, fromLocation = "STORES", toLocation = "SERIAL-RACK", lotNumber = "LOT-MANY", status = "Released", reason = "A lot may occupy two locations" });
        Assert.Empty(await plant.Engineer.GetFromJsonAsync<JsonElement[]>("/api/integrity/exceptions") ?? []);

        // Bypass the command gate to seed the exact impossible ledger state the detector must find.
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO inventory.transaction(kind,part_number,quantity,to_location,to_status,lot_number,at,actor,reason)
            VALUES('Receipt','INTEGRITY-SERIAL',1,'STORES','Released','SN-ONE',now(),'integrity-test','Seed impossible duplicate physical presence')
            """, connection))
            Assert.Equal(1, await seed.ExecuteNonQueryAsync());

        var finding = Assert.Single(await plant.Engineer.GetFromJsonAsync<JsonElement[]>("/api/integrity/exceptions") ?? []);
        Assert.Equal("SerialInMultipleLocations", finding.GetProperty("kind").GetString());
        Assert.Equal("DataOrProcessException", finding.GetProperty("classification").GetString());
        Assert.Equal("inventory", finding.GetProperty("source").GetString());
        var evidence = finding.GetProperty("evidence").GetString()!;
        Assert.Contains("INTEGRITY-SERIAL serial SN-ONE", evidence, StringComparison.Ordinal);
        Assert.Contains("SERIAL-RACK", evidence, StringComparison.Ordinal);
        Assert.Contains("STORES", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("misconduct", finding.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }
}
