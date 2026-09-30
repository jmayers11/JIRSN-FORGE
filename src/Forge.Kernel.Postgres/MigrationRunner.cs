using System.Reflection;
using Forge.Kernel.Serialization;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Forge.Kernel.Postgres;

/// <summary>
/// Embedded SQL migrations from one assembly. The kernel uses an empty prefix; each bounded
/// context uses its schema name so migration ids stay unique ("resources/0001_...").
/// </summary>
public sealed record MigrationSource(Assembly Assembly, string Prefix);

/// <summary>
/// Applies embedded, numbered SQL migrations in order, each in its own transaction, under an
/// advisory lock so concurrent hosts cannot race. A changed script that was already applied is an
/// error: history is never rewritten.
/// </summary>
public sealed class MigrationRunner(NpgsqlDataSource dataSource, IEnumerable<MigrationSource> sources, ILogger<MigrationRunner> logger)
{
    private const long LockKey = 0x46_4F_52_47_45; // "FORGE"

    public static IReadOnlyList<(string Id, string Sql)> LoadEmbedded(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        const string marker = ".Migrations.";
        return assembly.GetManifestResourceNames()
            .Where(n => n.Contains(marker, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                var id = n[(n.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..^".sql".Length];
                return (id, reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal));
            })
            .ToList();
    }

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct)
    {
        var migrations = sources
            .SelectMany(source => LoadEmbedded(source.Assembly).Select(m => (Id: source.Prefix.Length == 0 ? m.Id : source.Prefix + "/" + m.Id, m.Sql)))
            .ToList();
        var applied = new List<string>();

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await Execute(connection, null, $"SELECT pg_advisory_lock({LockKey})", ct);
        try
        {
            await Execute(connection, null, """
                CREATE TABLE IF NOT EXISTS public.forge_schema_migrations (
                    migration_id text PRIMARY KEY,
                    checksum     text NOT NULL,
                    applied_at   timestamptz NOT NULL DEFAULT now())
                """, ct);

            var existing = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var cmd = new NpgsqlCommand("SELECT migration_id, checksum FROM public.forge_schema_migrations", connection))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    existing[reader.GetString(0)] = reader.GetString(1);
                }
            }

            foreach (var (id, sql) in migrations)
            {
                var checksum = CanonicalJson.Sha256Hex(sql);
                if (existing.TryGetValue(id, out var appliedChecksum))
                {
                    if (!string.Equals(appliedChecksum, checksum, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Migration {id} was modified after it was applied. Add a new migration instead.");
                    }

                    continue;
                }

                await using var tx = await connection.BeginTransactionAsync(ct);
                await Execute(connection, tx, sql, ct);
                await using (var record = new NpgsqlCommand("INSERT INTO public.forge_schema_migrations (migration_id, checksum) VALUES ($1, $2)", connection, tx))
                {
                    record.Parameters.AddWithValue(id);
                    record.Parameters.AddWithValue(checksum);
                    await record.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                applied.Add(id);
                logger.LogInformation("Applied migration {MigrationId}", id);
            }
        }
        finally
        {
            await Execute(connection, null, $"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
        }

        return applied;
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
