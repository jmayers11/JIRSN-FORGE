using Npgsql;

namespace Forge.Host.Security;

/// <summary>Development only: creates the configured database on a local server if it does not exist yet.</summary>
internal static class DevelopmentDatabase
{
    public static async Task EnsureExistsAsync(string connectionString, ILogger logger, CancellationToken ct)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var name = target.Database;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(ct);
        await using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection))
        {
            exists.Parameters.AddWithValue("name", name);
            if (await exists.ExecuteScalarAsync(ct) is not null)
            {
                return;
            }
        }

        // Database names cannot be parameters; quote the identifier.
        var quoted = "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
#pragma warning disable CA2100 // The name comes from configuration and is quoted as an identifier.
        await using var create = new NpgsqlCommand("CREATE DATABASE " + quoted, connection);
#pragma warning restore CA2100
        await create.ExecuteNonQueryAsync(ct);
        logger.LogWarning("Created development database {Database}.", name);
    }
}
