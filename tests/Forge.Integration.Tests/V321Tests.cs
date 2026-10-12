using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V321Tests(PostgresFixture postgres)
{
    [Fact]
    public async Task Integrity_finds_seeded_anomalies_stays_silent_on_reference_data_and_uses_neutral_language()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        Assert.Empty(await plant.Engineer.GetFromJsonAsync<JsonElement[]>("/api/integrity/exceptions") ?? []);

        await using var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO inventory.transaction(kind,part_number,quantity,from_location,from_status,at,actor)
            SELECT 'Issue',i.part_number,999999999,l.code,'Released',now(),'integrity-test'
            FROM engineering.item i CROSS JOIN inventory.location l
            WHERE NOT EXISTS(SELECT 1 FROM inventory.balance b WHERE b.part_number=i.part_number AND b.location_code=l.code)
            LIMIT 1;
            INSERT INTO maintenance.asset(asset_code,name,asset_type,serial_number,criticality,status,created_by,created_at)
            VALUES('V321-ASSET-A','Integrity test asset A','Other','V321-DUPLICATE','Low','Active','integrity-test',now()),
                  ('V321-ASSET-B','Integrity test asset B','Other','V321-DUPLICATE','Low','Active','integrity-test',now());
            INSERT INTO inventory.transformation(order_number,reason,recorded_at,actor) VALUES(999999,'Integrity detector seed',now(),'integrity-test');
            INSERT INTO inventory.transformation_line(transformation_id,line_number,role,part_number,quantity)
            SELECT max(transformation_id),1,'Consumed','V321-UNISSUED',1 FROM inventory.transformation;
            INSERT INTO resources.tool_type(code,name,kind,calibration_days,changed_at,changed_by) VALUES('V321-GAUGE','Integrity test gauge','Gauge',365,now(),'integrity-test');
            INSERT INTO resources.tool(code,type_code,description,custody,condition,calibration_due,status,created_at,created_by,changed_at,changed_by)
            VALUES('V321-TOOL','V321-GAUGE','Integrity detector seed','InHouse','Good',CURRENT_DATE-1,'Available',now(),'integrity-test',now(),'integrity-test');
            INSERT INTO resources.tool_event(tool_code,kind,at,actor,due_on) VALUES('V321-TOOL','Registered',now()-interval '2 days','integrity-test',CURRENT_DATE-1);
            INSERT INTO resources.tool_event(tool_code,kind,at,actor,uses) VALUES('V321-TOOL','Used',now(),'integrity-test',1);
            """, connection);
        Assert.Equal(9, await cmd.ExecuteNonQueryAsync());

        var findings = await plant.Engineer.GetFromJsonAsync<JsonElement[]>("/api/integrity/exceptions");
        Assert.NotNull(findings);
        var kinds = findings!.Select(x => x.GetProperty("kind").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("NegativeStock", kinds);
        Assert.Contains("DuplicateAssetSerial", kinds);
        Assert.Contains("ConsumptionBeforeIssue", kinds);
        Assert.Contains("ExpiredCalibrationInUse", kinds);
        Assert.All(findings, x => Assert.Equal("DataOrProcessException", x.GetProperty("classification").GetString()));
        Assert.All(findings, x => Assert.DoesNotContain("misconduct", x.GetRawText(), StringComparison.OrdinalIgnoreCase));
    }
}
