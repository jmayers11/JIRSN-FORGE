using System.Text.Json;
using Forge.Inventory.Contracts;
using Forge.Kernel.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Inventory.Infrastructure;

internal sealed class PostgresCarrierStore(PostgresUnitOfWork uow) : ICarrierLookup, ICarrierStore
{
    private const string CarrierSql =
        """
        SELECT shipment_id, account_code, carrier, service, tracking, pro_number, bol_number, cost, currency, ship_to, packages, label IS NOT NULL, label_reference, status,
               tracking_summary, last_tracked_at, delivered_at, updated_by, updated_at
        FROM inventory.shipment_carrier
        """;

    public async Task<IReadOnlyList<CarrierAccountInfo>> ListCarrierAccountsAsync(CancellationToken ct)
    {
        var list = new List<CarrierAccountInfo>();
        await using var cmd = uow.Command("SELECT code, kind, name, account_number, sandbox, active, settings::text, credentials IS NOT NULL, updated_by, updated_at FROM inventory.carrier_account ORDER BY code");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new CarrierAccountInfo(r.GetString(0), Enum.Parse<CarrierKind>(r.GetString(1)), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4), r.GetBoolean(5),
                JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(6)) ?? [], r.GetBoolean(7), r.GetString(8), r.GetFieldValue<DateTimeOffset>(9)));
        }

        return list;
    }

    public async Task<string?> CarrierCredentialsAsync(string code, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT credentials FROM inventory.carrier_account WHERE code = $1", code);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<IReadOnlyList<ShipmentCarrierInfo>> ListShipmentCarriersAsync(CancellationToken ct) => await ReadAsync(CarrierSql + " ORDER BY shipment_id DESC LIMIT 1000", ct);

    public async Task<ShipmentCarrierInfo?> FindShipmentCarrierAsync(long shipmentId, CancellationToken ct) =>
        (await ReadAsync(CarrierSql + " WHERE shipment_id = $1", ct, shipmentId)).SingleOrDefault();

    public async Task<(byte[] Content, string ContentType)?> ReadLabelAsync(long shipmentId, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT label, label_content_type FROM inventory.shipment_carrier WHERE shipment_id = $1 AND label IS NOT NULL", shipmentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetFieldValue<byte[]>(0), r.IsDBNull(1) ? "application/octet-stream" : r.GetString(1)) : null;
    }

    public async Task UpsertCarrierAccountAsync(CarrierAccountInfo a, string? encryptedCredentials, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.carrier_account (code, kind, name, account_number, sandbox, active, settings, credentials, updated_by, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8, $9, $10)
            ON CONFLICT (code) DO UPDATE SET kind = EXCLUDED.kind, name = EXCLUDED.name, account_number = EXCLUDED.account_number, sandbox = EXCLUDED.sandbox, active = EXCLUDED.active,
                settings = EXCLUDED.settings, credentials = COALESCE(EXCLUDED.credentials, inventory.carrier_account.credentials), updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at
            """,
            a.Code, a.Kind.ToString(), a.Name, Text(a.AccountNumber), a.Sandbox, a.Active, JsonSerializer.Serialize(a.Settings), Text(encryptedCredentials), a.UpdatedBy, a.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertShipmentCarrierAsync(ShipmentCarrierInfo c, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.shipment_carrier (shipment_id, account_code, carrier, service, tracking, pro_number, bol_number, cost, currency, ship_to, packages, label_reference, status,
                tracking_summary, last_tracked_at, delivered_at, updated_by, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10::jsonb, $11::jsonb, $12, $13, $14, $15, $16, $17, $18)
            ON CONFLICT (shipment_id) DO UPDATE SET account_code = EXCLUDED.account_code, carrier = EXCLUDED.carrier, service = EXCLUDED.service, tracking = EXCLUDED.tracking,
                pro_number = EXCLUDED.pro_number, bol_number = EXCLUDED.bol_number, cost = EXCLUDED.cost, currency = EXCLUDED.currency, ship_to = EXCLUDED.ship_to, packages = EXCLUDED.packages,
                label_reference = EXCLUDED.label_reference, status = EXCLUDED.status, tracking_summary = EXCLUDED.tracking_summary, last_tracked_at = EXCLUDED.last_tracked_at,
                delivered_at = EXCLUDED.delivered_at, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at
            """,
            c.ShipmentId, Text(c.AccountCode), c.Carrier, Text(c.Service), Text(c.Tracking), Text(c.ProNumber), Text(c.BolNumber),
            new NpgsqlParameter { Value = (object?)c.Cost ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric }, Text(c.Currency),
            Text(c.ShipTo is null ? null : JsonSerializer.Serialize(c.ShipTo)), JsonSerializer.Serialize(c.Packages), Text(c.LabelReference), c.Status.ToString(), Text(c.TrackingSummary),
            Time(c.LastTrackedAt), Time(c.DeliveredAt), c.UpdatedBy, c.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveLabelAsync(long shipmentId, byte[]? content, string? contentType, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.shipment_carrier (shipment_id, carrier, packages, status, updated_by, updated_at, label, label_content_type)
            VALUES ($1, 'pending', '[]'::jsonb, 'Labeled', 'system', now(), $2, $3)
            ON CONFLICT (shipment_id) DO UPDATE SET label = EXCLUDED.label, label_content_type = EXCLUDED.label_content_type
            """,
            shipmentId, new NpgsqlParameter { Value = (object?)content ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bytea }, Text(contentType));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<ShipmentCarrierInfo>> ReadAsync(string sql, CancellationToken ct, params object?[] args)
    {
        var list = new List<ShipmentCarrierInfo>();
        await using var cmd = uow.Command(sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new ShipmentCarrierInfo(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetDecimal(7), r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : JsonSerializer.Deserialize<CarrierAddress>(r.GetString(9)), JsonSerializer.Deserialize<List<CarrierPackage>>(r.GetString(10)) ?? [],
                r.GetBoolean(11), r.IsDBNull(12) ? null : r.GetString(12), Enum.Parse<CarrierShipmentStatus>(r.GetString(13)), r.IsDBNull(14) ? null : r.GetString(14),
                r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15), r.IsDBNull(16) ? null : r.GetFieldValue<DateTimeOffset>(16), r.GetString(17), r.GetFieldValue<DateTimeOffset>(18)));
        }

        return list;
    }

    private static NpgsqlParameter Text(string? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Time(DateTimeOffset? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz };
}
