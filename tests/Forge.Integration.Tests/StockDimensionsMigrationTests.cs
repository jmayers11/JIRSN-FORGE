using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class StockDimensionsMigrationTests(PostgresFixture postgres)
{
    private const string MigrationId = "inventory/0008_stock_dimensions";
    private const string OriginalChecksum = "8cd2b06fb72df876362dcc3b7cf542011af9b7616cde8cfdf701572158265b32";
    private const string CorrectedChecksum = "aeec3f3c44f3e2c94fbd47ca2ad49cbcfb520e2969854f28576511092a0e3aba";

    [Fact]
    public async Task Tracked_and_untracked_legacy_stock_preserve_every_balance_and_ledger_record_during_upgrade()
    {
        await using var reference = new ForgeApp(await postgres.CreateDatabaseAsync());
        var sources = reference.Services.GetServices<MigrationSource>().ToArray();
        var migrations = sources.SelectMany(source => MigrationRunner.LoadEmbedded(source.Assembly)
            .Select(migration => (Id: source.Prefix.Length == 0 ? migration.Id : source.Prefix + "/" + migration.Id, migration.Sql))).ToArray();
        var corrected = Assert.Single(migrations, migration => migration.Id == MigrationId).Sql;
        Assert.Equal(CorrectedChecksum, CanonicalJson.Sha256Hex(corrected));
        var original = corrected.Replace(
            "FULL JOIN v31_before before ON after.part_number = before.part_number\n"
            + "        AND after.location_code = before.location_code\n"
            + "        AND after.lot_number IS NOT DISTINCT FROM before.lot_number",
            "FULL JOIN v31_before before USING (part_number, location_code, lot_number)", StringComparison.Ordinal);
        Assert.Equal(OriginalChecksum, CanonicalJson.Sha256Hex(original));

        var database = "forge_test_v31upgrade_" + Guid.NewGuid().ToString("N")[..12];
        var builder = new NpgsqlConnectionStringBuilder(reference.ConnectionString);
        var maintenance = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = "postgres", Pooling = false };
        await using (var admin = new NpgsqlConnection(maintenance.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            builder.Database = database;
            builder.Pooling = false;
            await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            await using var connection = await dataSource.OpenConnectionAsync();
            await ExecuteAsync(connection, """
                CREATE TABLE public.forge_schema_migrations(migration_id text PRIMARY KEY,checksum text NOT NULL,applied_at timestamptz NOT NULL DEFAULT now())
                """);
            // Use the real dependency-ordered embedded migrations, stopping at the pre-V3.1 inventory schema.
            foreach (var migration in migrations.TakeWhile(migration => migration.Id != MigrationId))
            {
                await using var transaction = await connection.BeginTransactionAsync();
                await using (var migrate = new NpgsqlCommand(migration.Sql, connection, transaction))
                    await migrate.ExecuteNonQueryAsync();
                await using (var record = new NpgsqlCommand("INSERT INTO public.forge_schema_migrations(migration_id,checksum) VALUES($1,$2)", connection, transaction))
                {
                    record.Parameters.AddWithValue(migration.Id);
                    record.Parameters.AddWithValue(CanonicalJson.Sha256Hex(migration.Sql));
                    await record.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
            }

            await ExecuteAsync(connection, """
                INSERT INTO resources.site(code,name,created_at,created_by) VALUES('LEGACY','Legacy plant',now(),'legacy');
                INSERT INTO engineering.item(part_number,description,unit_of_measure,make_buy,lead_time_days,lot_tracked,serialized,created_at,created_by)
                VALUES('LEGACY-UNTRACKED','Untracked legacy stock','EA','Buy',1,false,false,now(),'legacy'),
                      ('LEGACY-TRACKED','Tracked legacy stock','EA','Buy',1,true,false,now(),'legacy');
                INSERT INTO inventory.location(code,site_code,name,kind,created_at,created_by)
                VALUES('LEGACY-STORES','LEGACY','Legacy stores','Stores',now(),'legacy'),
                      ('LEGACY-BIN','LEGACY','Legacy bin','Stores',now(),'legacy');
                INSERT INTO inventory.lot(part_number,lot_number,supplier_lot,first_received_at,created_by)
                VALUES('LEGACY-TRACKED','LOT-ONE','SUPPLIER-ONE',now(),'legacy');
                INSERT INTO inventory.transaction(kind,part_number,quantity,from_location,from_status,to_location,to_status,lot_number,at,actor,reason)
                VALUES('Receipt','LEGACY-UNTRACKED',100,NULL,NULL,'LEGACY-STORES','Available',NULL,now(),'legacy','Original untracked receipt'),
                      ('Transfer','LEGACY-UNTRACKED',30,'LEGACY-STORES','Available','LEGACY-BIN','Available',NULL,now(),'legacy','Original untracked move'),
                      ('Receipt','LEGACY-UNTRACKED',7,NULL,NULL,'LEGACY-STORES','Inspection',NULL,now(),'legacy','Untracked inspection stock'),
                      ('Receipt','LEGACY-UNTRACKED',3,NULL,NULL,'LEGACY-BIN','Hold',NULL,now(),'legacy','Untracked held stock'),
                      ('Receipt','LEGACY-TRACKED',50,NULL,NULL,'LEGACY-STORES','Available','LOT-ONE',now(),'legacy','Original tracked receipt'),
                      ('Transfer','LEGACY-TRACKED',20,'LEGACY-STORES','Available','LEGACY-BIN','Available','LOT-ONE',now(),'legacy','Original tracked move');
                """);
            var balances = await BalanceSnapshotAsync(connection);
            var ledger = await LedgerSnapshotAsync(connection);
            // Reproduce the released bug with the exact historical script, then roll it back completely.
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var historical = new NpgsqlCommand(original, connection, transaction);
                var rejected = await Assert.ThrowsAsync<PostgresException>(() => historical.ExecuteNonQueryAsync());
                Assert.Contains("V3.1 status rename changed", rejected.MessageText, StringComparison.Ordinal);
                await transaction.RollbackAsync();
            }
            Assert.Equal(balances, await BalanceSnapshotAsync(connection));
            Assert.Equal(ledger, await LedgerSnapshotAsync(connection));

            var runner = new MigrationRunner(dataSource, sources, NullLogger<MigrationRunner>.Instance);
            Assert.Contains(MigrationId, await runner.MigrateAsync(CancellationToken.None));
            Assert.Equal(balances, await BalanceSnapshotAsync(connection));
            Assert.Equal(ledger, await LedgerSnapshotAsync(connection));
            Assert.Empty(await runner.MigrateAsync(CancellationToken.None));
            await using var oldStatuses = new NpgsqlCommand("SELECT count(*) FROM inventory.transaction WHERE from_status='Available' OR to_status='Available'", connection);
            Assert.Equal(0L, await oldStatuses.ExecuteScalarAsync());
            await using var tamper = new NpgsqlCommand("UPDATE inventory.transaction SET quantity=quantity+1", connection);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync())).SqlState);
        }
        finally
        {
            await postgres.DropDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Only_the_reviewed_historical_checksum_is_compatible_without_reapplying_or_rewriting_history()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var runner = app.Services.GetRequiredService<MigrationRunner>();
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using (var originalRecord = new NpgsqlCommand("UPDATE public.forge_schema_migrations SET checksum=$1 WHERE migration_id=$2 AND checksum=$3", connection))
        {
            originalRecord.Parameters.AddWithValue(OriginalChecksum);
            originalRecord.Parameters.AddWithValue(MigrationId);
            originalRecord.Parameters.AddWithValue(CorrectedChecksum);
            Assert.Equal(1, await originalRecord.ExecuteNonQueryAsync());
        }
        Assert.Empty(await runner.MigrateAsync(CancellationToken.None));
        await using (var unchanged = new NpgsqlCommand("SELECT checksum FROM public.forge_schema_migrations WHERE migration_id=$1", connection))
        {
            unchanged.Parameters.AddWithValue(MigrationId);
            Assert.Equal(OriginalChecksum, await unchanged.ExecuteScalarAsync());
        }

        await using (var tamper = new NpgsqlCommand("UPDATE public.forge_schema_migrations SET checksum='unreviewed-edit' WHERE migration_id=$1", connection))
        {
            tamper.Parameters.AddWithValue(MigrationId);
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
        }
        var unreviewed = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.MigrateAsync(CancellationToken.None));
        Assert.Contains(MigrationId, unreviewed.Message, StringComparison.Ordinal);

        await using (var restoreReviewed = new NpgsqlCommand("UPDATE public.forge_schema_migrations SET checksum=$1 WHERE migration_id=$2", connection))
        {
            restoreReviewed.Parameters.AddWithValue(CorrectedChecksum);
            restoreReviewed.Parameters.AddWithValue(MigrationId);
            Assert.Equal(1, await restoreReviewed.ExecuteNonQueryAsync());
        }
        await using (var wrongMigration = new NpgsqlCommand("UPDATE public.forge_schema_migrations SET checksum=$1 WHERE migration_id='inventory/0009_material_identity'", connection))
        {
            wrongMigration.Parameters.AddWithValue(OriginalChecksum);
            Assert.Equal(1, await wrongMigration.ExecuteNonQueryAsync());
        }
        var wrongId = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.MigrateAsync(CancellationToken.None));
        Assert.Contains("inventory/0009_material_identity", wrongId.Message, StringComparison.Ordinal);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> BalanceSnapshotAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_agg(to_jsonb(b)||jsonb_build_object('status',CASE status WHEN 'Available' THEN 'Released' ELSE status END)
                ORDER BY part_number,location_code,lot_number NULLS FIRST,CASE status WHEN 'Available' THEN 'Released' ELSE status END)::text FROM inventory.balance b
            """, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> LedgerSnapshotAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object(
                'from_status',CASE from_status WHEN 'Available' THEN 'Released' ELSE from_status END,
                'to_status',CASE to_status WHEN 'Available' THEN 'Released' ELSE to_status END)
                ORDER BY transaction_id)::text FROM inventory.transaction t
            """, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
