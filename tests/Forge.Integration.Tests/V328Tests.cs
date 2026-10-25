using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V328Tests(PostgresFixture postgres)
{
    [Fact]
    public async Task PostgreSql_backup_restores_a_disposable_database_and_the_restored_host_reads_it()
    {
        var sourceConnection = await postgres.CreateDatabaseAsync();
        var sourceBuilder = new NpgsqlConnectionStringBuilder(sourceConnection);
        var targetDatabase = "forge_test_restore_" + Guid.NewGuid().ToString("N")[..12];
        var archivePath = Path.Combine(Path.GetTempPath(), "forge-v328-" + Guid.NewGuid().ToString("N") + ".dump");
        var maintenance = new NpgsqlConnectionStringBuilder(sourceBuilder.ConnectionString) { Database = "postgres", Pooling = false };
        try
        {
            if (Environment.GetEnvironmentVariable("FORGE_TEST_RESTORE_BASELINE") == "1")
            {
                Assert.True(postgres.UsesRestoredBaseline, "The full verification gate must restore its database baseline before running tests.");
                await using var baseline = new NpgsqlConnection(sourceConnection);
                await baseline.OpenAsync();
                await using var migrations = new NpgsqlCommand("SELECT count(*) FROM public.forge_schema_migrations", baseline);
                Assert.Equal(163L, await migrations.ExecuteScalarAsync());
            }
            await using (var app = new ForgeApp(sourceConnection))
            {
                var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
                await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = "V328-BACKUP", description = "Backup drill marker", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 3 });
                await plant.Ok(plant.Engineer, "/api/engineering/items/V328-BACKUP/revisions", new { revision = "A" });
                await plant.Ok(plant.Approver, "/api/engineering/items/V328-BACKUP/revisions/A/release", new { reason = "Seed the disposable backup drill" });
            }

            await postgres.BackupDatabaseAsync(sourceConnection, archivePath);
            Assert.True(new FileInfo(archivePath).Length > 0, "The database dump should contain the migrated source state.");
            await using (var admin = new NpgsqlConnection(maintenance.ConnectionString))
            {
                await admin.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE {targetDatabase}", admin);
                await create.ExecuteNonQueryAsync();
            }

            await postgres.RestoreDatabaseAsync(sourceConnection, targetDatabase, archivePath);
            var restoredConnection = new NpgsqlConnectionStringBuilder(sourceBuilder.ConnectionString) { Database = targetDatabase }.ConnectionString;
            await using (var restored = new ForgeApp(restoredConnection))
            {
                var engineer = restored.As("eng");
                var items = await engineer.GetFromJsonAsync<JsonElement[]>("/api/engineering/items");
                Assert.Contains(items ?? [], item => item.GetProperty("partNumber").GetString() == "V328-BACKUP");
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            try
            {
                await postgres.DropDatabaseAsync(targetDatabase);
            }
            finally
            {
                if (File.Exists(archivePath)) File.Delete(archivePath);
            }
        }
    }
}
