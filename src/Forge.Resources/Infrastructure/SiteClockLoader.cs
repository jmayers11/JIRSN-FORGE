using Forge.Resources.Contracts;
using Npgsql;

namespace Forge.Resources.Infrastructure;

/// <summary>Reads every site's zone into the <see cref="SiteClock"/> (creation order: the first site is home). Run after migrations.</summary>
public sealed class SiteClockLoader(NpgsqlDataSource db, SiteClock clock)
{
    public async Task LoadAsync(CancellationToken ct)
    {
        var sites = new List<(string, string)>();
        try
        {
            await using var cmd = db.CreateCommand("SELECT code, time_zone FROM resources.site ORDER BY created_at, code");
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sites.Add((reader.GetString(0), reader.GetString(1)));
            }
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Not migrated yet: no sites, so the default zone applies until one is created.
        }

        clock.Load(sites);
    }
}
