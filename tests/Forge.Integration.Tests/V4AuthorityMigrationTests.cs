using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V4AuthorityMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Existing_custom_QMS_and_identification_roles_keep_their_exact_organization_permissions_during_upgrade()
    {
        var baseline = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync());
        var database = "forge_test_v4authority_" + Guid.NewGuid().ToString("N")[..12];
        var maintenance = new NpgsqlConnectionStringBuilder(baseline.ConnectionString) { Database = "postgres", Pooling = false };
        await using (var connection = new NpgsqlConnection(maintenance.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", connection);
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            baseline.Database = database; baseline.Pooling = false;
            await using var dataSource = NpgsqlDataSource.Create(baseline.ConnectionString);
            await using var connection = await dataSource.OpenConnectionAsync();
            await ExecuteAsync(connection, "CREATE TABLE public.forge_schema_migrations(migration_id text PRIMARY KEY,checksum text NOT NULL,applied_at timestamptz NOT NULL DEFAULT now())");
            var migrations = MigrationRunner.LoadEmbedded(typeof(MigrationRunner).Assembly);
            foreach (var migration in migrations.TakeWhile(migration => migration.Id != "0008_qms_authority"))
            {
                await using var transaction = await connection.BeginTransactionAsync();
                await using (var command = new NpgsqlCommand(migration.Sql, connection, transaction)) await command.ExecuteNonQueryAsync();
                await using (var record = new NpgsqlCommand("INSERT INTO public.forge_schema_migrations(migration_id,checksum) VALUES($1,$2)", connection, transaction))
                {
                    record.Parameters.AddWithValue(migration.Id); record.Parameters.AddWithValue(CanonicalJson.Sha256Hex(migration.Sql));
                    await record.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
            }
            await ExecuteAsync(connection, """
                INSERT INTO kernel.role_definition(role_name,display_name) VALUES('QmsAuthor','Existing local QMS role'),('LabelAuthor','Existing local identification role');
                INSERT INTO kernel.role_permission(role_name,permission,granted_by) VALUES('QmsAuthor','engineering.read','person:legacy-owner'),('LabelAuthor','inventory.read','person:legacy-owner');
                INSERT INTO kernel.role_permission_scope(role_name,permission,scope_type,scope_id) VALUES('QmsAuthor','engineering.read','facility','LEGACY-A'),('LabelAuthor','inventory.read','facility','LEGACY-A');
                """);
            var before = await SnapshotAsync(connection);
            var runner = new MigrationRunner(dataSource, [new(typeof(MigrationRunner).Assembly, string.Empty)], NullLogger<MigrationRunner>.Instance);
            var applied = await runner.MigrateAsync(CancellationToken.None);
            Assert.Contains("0008_qms_authority", applied); Assert.Contains("0009_qms_common_authority", applied); Assert.Contains("0012_identification_authority", applied);
            Assert.Equal(before, await SnapshotAsync(connection));
            Assert.Empty(await runner.MigrateAsync(CancellationToken.None));
            Assert.Equal(before, await SnapshotAsync(connection));
            await using var newlyAssigned = new NpgsqlCommand("SELECT count(*) FROM kernel.role_assignment WHERE role IN ('QmsAuthor','QmsReviewer','QmsApprover','QmsPolicyConsumer','LabelAuthor','LabelApprover','LabelPrinter','ScanOperator','IdentificationAdministrator')", connection);
            Assert.Equal(0L, await newlyAssigned.ExecuteScalarAsync());
        }
        finally { await postgres.DropDatabaseAsync(database); }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    { await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync(); }

    private static async Task<string> SnapshotAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object(
                'roles',(SELECT jsonb_agg(to_jsonb(role_row) ORDER BY role_row.role_name) FROM kernel.role_definition role_row WHERE role_row.role_name IN ('QmsAuthor','LabelAuthor')),
                'permissions',(SELECT jsonb_agg(to_jsonb(permission_row) ORDER BY permission_row.role_name,permission_row.permission) FROM kernel.role_permission permission_row WHERE permission_row.role_name IN ('QmsAuthor','LabelAuthor')),
                'scopes',(SELECT jsonb_agg(to_jsonb(scope_row) ORDER BY scope_row.role_name,scope_row.permission,scope_row.scope_type,scope_row.scope_id) FROM kernel.role_permission_scope scope_row WHERE scope_row.role_name IN ('QmsAuthor','LabelAuthor')))::text
            """, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
