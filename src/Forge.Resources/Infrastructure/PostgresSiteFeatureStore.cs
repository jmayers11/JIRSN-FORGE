using Forge.Kernel.Postgres;
using Forge.Resources.Contracts;

namespace Forge.Resources.Infrastructure;

internal sealed class PostgresSiteFeatureStore(PostgresUnitOfWork uow) : ISiteFeatureLookup, ISiteFeatureStore
{
    public async Task<IReadOnlyList<SiteFeatureState>> ListFeaturesAsync(CancellationToken ct)
    {
        var rows = new Dictionary<string, (bool Enabled, string By, DateTimeOffset At)>(StringComparer.Ordinal);
        await using (var cmd = uow.Command("SELECT code, enabled, updated_by, updated_at FROM resources.site_feature"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) rows[r.GetString(0)] = (r.GetBoolean(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3));
        }

        return SiteFeatures.All.Select(f => rows.TryGetValue(f.Code, out var row)
            ? new SiteFeatureState(f.Code, f.Name, f.Description, row.Enabled, row.By, row.At)
            : new SiteFeatureState(f.Code, f.Name, f.Description, true, null, null)).ToList();
    }

    public async Task SetFeatureAsync(string code, bool enabled, string actor, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO resources.site_feature (code, enabled, updated_by, updated_at) VALUES ($1, $2, $3, $4)
            ON CONFLICT (code) DO UPDATE SET enabled = EXCLUDED.enabled, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at
            """,
            code, enabled, actor, at);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
