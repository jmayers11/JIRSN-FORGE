using Forge.Kernel.Postgres;
using Forge.Maintenance.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Maintenance.Infrastructure;

internal sealed class PostgresAssetStore(PostgresUnitOfWork uow) : IAssetStore, IAssetLookup
{
    private const string AssetSql =
        """
        SELECT asset_code, name, asset_type, manufacturer, model, serial_number, location, work_center_code, machine_code, criticality, commissioned_on,
               expected_life_years, replacement_cost, status, created_by, created_at, decommission_requested_by, decommission_requested_at, decommission_reason
        FROM maintenance.asset
        """;

    private const string ComponentSql =
        """
        SELECT component_id, asset_code, part_number, revision, position, quantity, installed_work_order, installed_by, installed_at,
               removed_work_order, removed_by, removed_at, removal_reason, source_transaction_id
        FROM maintenance.asset_component
        """;

    public async Task InsertAssetAsync(AssetInfo a, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(a);
        await using var cmd = uow.Command(
            """
            INSERT INTO maintenance.asset (asset_code, name, asset_type, manufacturer, model, serial_number, location, work_center_code, machine_code, criticality,
                commissioned_on, expected_life_years, replacement_cost, status, created_by, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16)
            """,
            a.AssetCode, a.Name, a.Type.ToString(), Text(a.Manufacturer), Text(a.Model), Text(a.SerialNumber), Text(a.Location), Text(a.WorkCenterCode), Text(a.MachineCode),
            a.Criticality.ToString(), Date(a.CommissionedOn), Int(a.ExpectedLifeYears), Dec(a.ReplacementCost), a.Status.ToString(), a.CreatedBy, a.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAssetAsync(AssetInfo a, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(a);
        await using var cmd = uow.Command(
            """
            UPDATE maintenance.asset SET name = $2, manufacturer = $3, model = $4, serial_number = $5, location = $6, criticality = $7, commissioned_on = $8,
                expected_life_years = $9, replacement_cost = $10, status = $11, decommission_requested_by = $12, decommission_requested_at = $13, decommission_reason = $14
            WHERE asset_code = $1
            """,
            a.AssetCode, a.Name, Text(a.Manufacturer), Text(a.Model), Text(a.SerialNumber), Text(a.Location), a.Criticality.ToString(), Date(a.CommissionedOn),
            Int(a.ExpectedLifeYears), Dec(a.ReplacementCost), a.Status.ToString(), Text(a.DecommissionRequestedBy), Time(a.DecommissionRequestedAt), Text(a.DecommissionReason));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertEventAsync(AssetEvent e, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);
        await using var cmd = uow.Command(
            "INSERT INTO maintenance.asset_event (asset_code, kind, from_status, status, detail, actor, at, reason) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)",
            e.AssetCode, e.Kind, Text(e.FromStatus?.ToString()), e.Status.ToString(), e.Detail, e.Actor, e.At, Text(e.Reason));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertComponentAsync(AssetComponentInfo c, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        await using var cmd = uow.Command(
            """
            INSERT INTO maintenance.asset_component (asset_code, part_number, revision, position, quantity, installed_work_order, installed_by, installed_at, source_transaction_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) RETURNING component_id
            """,
            c.AssetCode, c.PartNumber, Text(c.Revision), c.Position, c.Quantity, Long(c.InstalledByWorkOrder), c.InstalledBy, c.InstalledAt, Long(c.SourceTransactionId));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task RemoveComponentAsync(long componentId, long? workOrderId, string actor, DateTimeOffset at, string reason, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE maintenance.asset_component SET removed_work_order = $2, removed_by = $3, removed_at = $4, removal_reason = $5 WHERE component_id = $1",
            componentId, Long(workOrderId), actor, at, reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<AssetInfo>> ListAssetsAsync(CancellationToken ct) => Read(AssetSql + " ORDER BY asset_code", ReadAsset, ct);

    public async Task<AssetInfo?> FindAssetAsync(string assetCode, CancellationToken ct) =>
        (await Read(AssetSql + " WHERE asset_code = $1", ReadAsset, ct, assetCode)).SingleOrDefault();

    public Task<IReadOnlyList<AssetEvent>> ListAssetHistoryAsync(string assetCode, CancellationToken ct) =>
        Read(
            "SELECT event_id, asset_code, kind, from_status, status, detail, actor, at, reason FROM maintenance.asset_event WHERE asset_code = $1 ORDER BY event_id",
            r => new AssetEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : Enum.Parse<AssetStatus>(r.GetString(3)), Enum.Parse<AssetStatus>(r.GetString(4)),
                r.GetString(5), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7), Opt(r, 8)),
            ct,
            assetCode);

    public Task<IReadOnlyList<AssetComponentInfo>> ListComponentsAsync(string? assetCode, string? partNumber, bool includeRemoved, CancellationToken ct) =>
        Read(
            ComponentSql + " WHERE ($1::text IS NULL OR asset_code = $1) AND ($2::text IS NULL OR part_number = $2) AND ($3 OR removed_at IS NULL) ORDER BY asset_code, component_id",
            r => new AssetComponentInfo(r.GetInt64(0), r.GetString(1), r.GetString(2), Opt(r, 3), r.GetString(4), r.GetDecimal(5) / 1.000000000000000000000000000000000m,
                r.IsDBNull(6) ? null : r.GetInt64(6), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetInt64(9), Opt(r, 10),
                r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11), Opt(r, 12), r.IsDBNull(13) ? null : r.GetInt64(13)),
            ct,
            Text(assetCode),
            Text(partNumber),
            includeRemoved);

    private static AssetInfo ReadAsset(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), Enum.Parse<AssetType>(r.GetString(2)), Opt(r, 3), Opt(r, 4), Opt(r, 5), Opt(r, 6), Opt(r, 7), Opt(r, 8),
        Enum.Parse<AssetCriticality>(r.GetString(9)), r.IsDBNull(10) ? null : r.GetFieldValue<DateOnly>(10), r.IsDBNull(11) ? null : r.GetInt32(11),
        r.IsDBNull(12) ? null : r.GetDecimal(12) / 1.000000000000000000000000000000000m, Enum.Parse<AssetStatus>(r.GetString(13)), r.GetString(14), r.GetFieldValue<DateTimeOffset>(15),
        Opt(r, 16), r.IsDBNull(17) ? null : r.GetFieldValue<DateTimeOffset>(17), Opt(r, 18));

    private static string? Opt(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Int(int? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer };

    private static NpgsqlParameter Long(long? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    private static NpgsqlParameter Dec(decimal? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric };

    private static NpgsqlParameter Date(DateOnly? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date };

    private static NpgsqlParameter Time(DateTimeOffset? value) =>
        new() { Value = (object?)value?.ToUniversalTime() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    private async Task<IReadOnlyList<T>> Read<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
    {
        var list = new List<T>();
        await using var cmd = uow.Command(sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(map(reader));
        }

        return list;
    }
}
