using System.Diagnostics;
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

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("FORGE_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(external))
        {
            adminConnectionString = external;
            return;
        }

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

    public async Task<string> CreateDatabaseAsync()
    {
        var name = "forge_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        // Empty database; the host applies every context's migrations on start.
        return new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name, Pooling = true }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (containerId is not null)
        {
            await Docker($"rm -f {containerId}");
        }
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
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
