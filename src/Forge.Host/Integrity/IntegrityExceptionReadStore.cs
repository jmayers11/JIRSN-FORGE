using Forge.Kernel.Security;
using Npgsql;

namespace Forge.Host.Integrity;

public sealed record IntegrityException(string ExceptionId, string Kind, string Classification, string Summary, string Evidence, string Source, DateTimeOffset ObservedAt, string Permission);

public sealed class IntegrityExceptionReadStore(NpgsqlDataSource dataSource)
{
    private sealed record Finding(string Id, string Kind, string Summary, string Evidence, string Source, DateTimeOffset ObservedAt, string Permission);

    public async Task<IReadOnlyList<IntegrityException>> ListAsync(ForgePrincipal principal, CancellationToken ct)
    {
        var findings = new List<Finding>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            SELECT 'negative-stock:'||part_number||':'||location_code||':'||coalesce(lot_number,'')||':'||status,
                   'NegativeStock', 'Ledger balance is below zero',
                   part_number||' at '||location_code||coalesce(' lot '||lot_number,'')||' ('||status||'): '||quantity::text,
                   'inventory',now(),'inventory.read'
            FROM inventory.balance WHERE quantity < 0
            UNION ALL
            SELECT 'duplicate-serial:'||serial_number,'DuplicateAssetSerial','One serial number is recorded on multiple assets',
                   'Serial '||serial_number||' is recorded on assets '||string_agg(asset_code, ', ' ORDER BY asset_code),
                   'maintenance',now(),'maintenance.read'
            FROM maintenance.asset WHERE serial_number IS NOT NULL AND trim(serial_number)<>''
            GROUP BY serial_number HAVING count(*)>1
            UNION ALL
            SELECT 'consumption-before-issue:'||t.transformation_id||':'||l.line_number,'ConsumptionBeforeIssue','Consumed material has no prior matching issue',
                   'Transformation '||t.transformation_id||' order '||t.order_number||' consumes '||l.quantity::text||' '||l.part_number||coalesce(' lot '||l.lot_number,'')||' at '||t.recorded_at::text,
                   'inventory',t.recorded_at,'inventory.read'
            FROM inventory.transformation t JOIN inventory.transformation_line l USING(transformation_id)
            WHERE l.role='Consumed' AND NOT EXISTS (
                SELECT 1 FROM inventory.transaction i WHERE i.order_number=t.order_number AND i.part_number=l.part_number
                  AND i.lot_number IS NOT DISTINCT FROM l.lot_number AND i.kind='Issue' AND i.at<=t.recorded_at)
            UNION ALL
            SELECT 'expired-calibration-use:'||u.event_id,'ExpiredCalibrationInUse','A tool use is recorded after its calibration expired',
                   'Tool '||u.tool_code||' was used at '||u.at::text||' after due date '||c.due_on::text,
                   'resources',u.at,'resources.read'
            FROM resources.tool_event u
            CROSS JOIN LATERAL (SELECT e.due_on FROM resources.tool_event e WHERE e.tool_code=u.tool_code AND e.kind IN ('Calibrated','Registered','Returned') AND e.at<=u.at AND e.due_on IS NOT NULL ORDER BY e.at DESC,e.event_id DESC LIMIT 1) c
            WHERE u.kind='Used' AND u.at::date>c.due_on
            ORDER BY 6 DESC
            LIMIT 500
            """, connection))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) findings.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6)));
        }
        return findings.Where(f => principal.HasPermission(f.Permission, ResourceRef.Of(f.Source, f.Id)))
            .Select(f => new IntegrityException(f.Id, f.Kind, "DataOrProcessException", f.Summary, f.Evidence, f.Source, f.ObservedAt, f.Permission)).ToArray();
    }
}
