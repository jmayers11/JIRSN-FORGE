using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Reporting;
using Npgsql;

namespace Forge.Host.History;

public sealed class CauseMapReadStore(NpgsqlDataSource dataSource)
{
    private sealed record Row(Guid Id, string EventType, string SourceContext, string EntityType, string EntityId, DateTimeOffset At, Guid? CausationId, Guid? ProgramId, DataMarking Markings, string? Reason, int Depth);

    public async Task<CauseMapView> LoadAsync(ForgePrincipal principal, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            WITH RECURSIVE seeds AS MATERIALIZED (
                SELECT sequence,event_id,event_type,occurred_at,entity_type,entity_id,causation_id,program_id,envelope::jsonb AS envelope,
                       ARRAY[event_id]::uuid[] AS path,0 AS depth
                FROM kernel.event_log
                WHERE occurred_at >= $1 AND occurred_at <= $2
                  AND (program_id IS NULL OR program_id=ANY($3::uuid[]))
                ORDER BY occurred_at,event_id LIMIT 500
            ), chain AS (
                SELECT * FROM seeds
                UNION ALL
                SELECT p.sequence,p.event_id,p.event_type,p.occurred_at,p.entity_type,p.entity_id,p.causation_id,p.program_id,p.envelope::jsonb,
                       c.path || p.event_id,c.depth+1
                FROM chain c JOIN kernel.event_log p ON p.event_id=c.causation_id
                WHERE c.depth < 20 AND NOT p.event_id=ANY(c.path)
                  AND (p.program_id IS NULL OR p.program_id=ANY($3::uuid[]))
            )
            SELECT DISTINCT ON(event_id) event_id,event_type,coalesce(envelope->>'sourceContext','Unknown'),entity_type,entity_id,occurred_at,
                   causation_id,program_id,coalesce(envelope->>'markings','None'),envelope->>'reason',depth
            FROM chain ORDER BY event_id,depth
            """, connection);
        cmd.Parameters.AddWithValue(from);
        cmd.Parameters.AddWithValue(to);
        cmd.Parameters.AddWithValue(principal.Programs.Select(p => p.Value).ToArray());
        var rows = new List<Row>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var markings = Enum.TryParse<DataMarking>(reader.GetString(8), out var parsed) ? parsed : DataMarking.None;
                if ((markings & principal.Markings) != markings) continue;
                rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5),
                    reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.IsDBNull(7) ? null : reader.GetGuid(7), markings, reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt32(10)));
            }
        }

        var visible = rows.Select(r => r.Id).ToHashSet();
        var steps = rows.OrderBy(r => r.At).ThenBy(r => r.Id)
            .Select(r => new CauseMapStep(r.Id, r.EventType, r.SourceContext, r.EntityType, r.EntityId, r.At, r.Reason, "event:" + r.Id.ToString("D"))).ToArray();
        var links = rows.Where(r => r.CausationId is { } cause && visible.Contains(cause))
            .Select(r => new CauseMapLink(r.CausationId!.Value, r.Id)).ToArray();
        var seedIds = rows.Where(r => r.Depth == 0).Select(r => r.Id).ToHashSet();
        var linkedEffects = links.Select(l => l.EffectEventId).ToHashSet();
        var unlinked = seedIds.Where(id => !linkedEffects.Contains(id)).Order().ToArray();
        return new CauseMapView(steps, links, unlinked);
    }
}
