using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// A real PostgreSQL 16 for the whole test run. By default it starts a throwaway Docker container
/// through the docker CLI (no extra packages); set FORGE_TEST_POSTGRES to a superuser connection
/// string to use an existing server instead. Every test gets its own empty database, migrated when the host starts.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string Password = "forge-test-only";
    private string? containerId;
    private string adminConnectionString = string.Empty;
    private string? restoreBaselineArchive;
    private string? restoreBaselineConnection;
    private readonly ConcurrentBag<string> createdDatabases = [];

    public bool UsesRestoredBaseline => restoreBaselineArchive is not null;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("FORGE_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(external))
        {
            adminConnectionString = external;
        }
        else
        {
            var run = await Docker($"run -d --rm -e POSTGRES_PASSWORD={Password} -p 127.0.0.1::5432 postgres:16");
            if (run.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Integration tests need PostgreSQL. Start Docker Desktop, or set FORGE_TEST_POSTGRES to a superuser connection string. docker said: " + run.Error);
            }

            containerId = run.Output.Trim();
            var port = await Docker($"port {containerId} 5432/tcp");
            var hostPort = port.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim().Split(':')[^1];
            adminConnectionString = $"Host=127.0.0.1;Port={hostPort};Username=postgres;Password={Password};Database=postgres;Pooling=false";

            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (true)
            {
                try
                {
                    await using var connection = new NpgsqlConnection(adminConnectionString);
                    await connection.OpenAsync();
                    await using var cmd = new NpgsqlCommand("SELECT 1", connection);
                    await cmd.ExecuteScalarAsync();
                    break;
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(500);
                }
            }
        }

        if (Environment.GetEnvironmentVariable("FORGE_TEST_RESTORE_BASELINE") == "1")
        {
            restoreBaselineConnection = await CreateDatabaseAsync();
            await using (var app = new ForgeApp(restoreBaselineConnection))
            {
                _ = app.Services; // Migrate and bootstrap exactly as the real host does.
            }

            var archive = Path.Combine(Path.GetTempPath(), "forge-v328-baseline-" + Guid.NewGuid().ToString("N") + ".dump");
            await BackupDatabaseAsync(restoreBaselineConnection, archive);
            restoreBaselineArchive = archive;
        }
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = "forge_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await cmd.ExecuteNonQueryAsync();
            createdDatabases.Add(name);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name, Pooling = true }.ConnectionString;
        // The release gate runs every integration test on a restored, migrated baseline.
        // Standalone test runs still exercise migration from an empty database.
        if (restoreBaselineArchive is not null)
        {
            await RestoreDatabaseAsync(connectionString, name, restoreBaselineArchive);
        }
        return connectionString;
    }

    public async Task BackupDatabaseAsync(string connectionString, string archivePath)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database
            ?? throw new InvalidOperationException("A database name is required for the V3.28 backup drill.");
        RequireTestDatabase(database);
        if (containerId is not null)
        {
            await RunDockerArchiveAsync(["exec", "-i", containerId, "pg_dump", "--username=postgres", "--format=custom", "--no-owner", $"--dbname={database}"], archivePath, restore: false);
            return;
        }

        var start = PgTool("pg_dump", connectionString, database);
        start.ArgumentList.Add("--format=custom");
        start.ArgumentList.Add("--no-owner");
        start.ArgumentList.Add("--file");
        start.ArgumentList.Add(archivePath);
        start.ArgumentList.Add($"--dbname={database}");
        await RunAsync(start);
    }

    public async Task RestoreDatabaseAsync(string connectionString, string targetDatabase, string archivePath)
    {
        RequireTestDatabase(targetDatabase);
        if (containerId is not null)
        {
            await RunDockerArchiveAsync(["exec", "-i", containerId, "pg_restore", "--username=postgres", "--exit-on-error", "--no-owner", $"--dbname={targetDatabase}"], archivePath, restore: true);
            return;
        }

        var start = PgTool("pg_restore", connectionString, targetDatabase);
        start.ArgumentList.Add("--exit-on-error");
        start.ArgumentList.Add("--no-owner");
        start.ArgumentList.Add($"--dbname={targetDatabase}");
        await RunAsync(start, archivePath);
    }

    public async Task DisposeAsync()
    {
        if (containerId is not null)
        {
            await Docker($"rm -f {containerId}");
        }
        else
        {
            // Docker tears down every test database with its container. An external test
            // server must receive the equivalent cleanup, limited to this fixture's names.
            // Never enumerate or remove databases belonging to other runs or real users.
            var failures = new List<Exception>();
            foreach (var database in createdDatabases.Distinct(StringComparer.Ordinal))
            {
                try { await DropDatabaseAsync(database); }
                catch (NpgsqlException ex) { failures.Add(new InvalidOperationException($"Cleanup failed for this fixture's database {database}.", ex)); }
            }
            if (failures.Count > 0) throw new AggregateException("Disposable test database cleanup did not complete.", failures);
        }
        if (restoreBaselineArchive is not null) File.Delete(restoreBaselineArchive);
    }

    public async Task DropDatabaseAsync(string database)
    {
        RequireTestDatabase(database);
        var maintenance = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(maintenance.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin)
        {
            // Removing a migrated database can exceed Npgsql's default 30 seconds after
            // the full suite's disk workload. Bound cleanup without hiding a failure.
            CommandTimeout = 300,
        };
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task<(int ExitCode, string Output, string Error)> Docker(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await output, await error);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, string.Empty, "docker CLI not found: " + ex.Message);
        }
    }

    private static void RequireTestDatabase(string database)
    {
        if (!database.StartsWith("forge_test_", StringComparison.Ordinal)
            || database.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
        {
            throw new InvalidOperationException("The V3.28 backup drill only accepts generated forge_test_* databases.");
        }
    }

    private static ProcessStartInfo PgTool(string tool, string connectionString, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var executable = tool;
        if (OperatingSystem.IsWindows())
        {
            var candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL", "18", "bin", tool + ".exe");
            if (File.Exists(candidate)) executable = candidate;
        }

        var start = new ProcessStartInfo(executable) { RedirectStandardError = true, UseShellExecute = false };
        start.Environment["PGHOST"] = builder.Host;
        start.Environment["PGPORT"] = builder.Port.ToString(CultureInfo.InvariantCulture);
        start.Environment["PGUSER"] = builder.Username;
        start.Environment["PGDATABASE"] = database;
        if (!string.IsNullOrEmpty(builder.Password)) start.Environment["PGPASSWORD"] = builder.Password;
        var sslMode = builder.SslMode.ToString().ToLowerInvariant();
        start.Environment["PGSSLMODE"] = sslMode switch { "verifyca" => "verify-ca", "verifyfull" => "verify-full", _ => sslMode };
        return start;
    }

    private static async Task RunDockerArchiveAsync(string[] arguments, string archivePath, bool restore)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (restore) start.RedirectStandardInput = true; else start.RedirectStandardOutput = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start docker for the V3.28 backup drill.");
        var stderr = process.StandardError.ReadToEndAsync();
        if (restore)
        {
            await using var input = File.OpenRead(archivePath);
            await input.CopyToAsync(process.StandardInput.BaseStream);
            process.StandardInput.Close();
        }
        else
        {
            await using var output = File.Create(archivePath);
            await process.StandardOutput.BaseStream.CopyToAsync(output);
        }

        await process.WaitForExitAsync();
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"PostgreSQL backup/restore command failed ({process.ExitCode}): {error}");
    }

    private static async Task RunAsync(ProcessStartInfo start, string? archivePath = null)
    {
        start.RedirectStandardOutput = false;
        if (archivePath is not null) start.RedirectStandardInput = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the PostgreSQL backup utility.");
        var error = process.StandardError.ReadToEndAsync();
        if (archivePath is not null)
        {
            await using var input = File.OpenRead(archivePath);
            await input.CopyToAsync(process.StandardInput.BaseStream);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync();
        var message = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException($"PostgreSQL backup/restore command failed ({process.ExitCode}): {message}");
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
