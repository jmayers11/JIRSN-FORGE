using Forge.Kernel.Postgres;
using Forge.Quality.Contracts;
using Npgsql;

namespace Forge.Quality.Infrastructure;

internal sealed class PostgresSpcStore(PostgresUnitOfWork uow) : ISpcStore, ISpcLookup
{
    private const string SignalSql =
        """
        SELECT signal_id, part_number, sequence, number, characteristic_name, inspection_id, order_number, rule, description, value, detected_at, status,
               acknowledged_by, acknowledged_at, note
        FROM quality.spc_signal
        """;

    public async Task InsertPolicyAsync(SpcPolicy policy, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO quality.spc_policy (minimum_cpk, critical_minimum_cpk, minimum_points, notify_on_signal, effective_from, set_by, reason) VALUES ($1, $2, $3, $4, $5, $6, $7)",
            policy.MinimumCpk, policy.CriticalMinimumCpk, policy.MinimumPoints, policy.NotifyOnSignal, policy.EffectiveFrom, policy.SetBy, policy.Reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetCriticalAsync(SpcCharacteristicKey key, bool critical, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = critical
            ? uow.Command("INSERT INTO quality.spc_critical_characteristic (part_number, sequence, number, marked_at, marked_by) VALUES ($1, $2, $3, $4, $5) ON CONFLICT DO NOTHING",
                key.PartNumber, key.Sequence, key.Number, at, actor)
            : uow.Command("DELETE FROM quality.spc_critical_characteristic WHERE part_number = $1 AND sequence = $2 AND number = $3", key.PartNumber, key.Sequence, key.Number);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long?> InsertSignalAsync(SpcSignal s, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO quality.spc_signal (part_number, sequence, number, characteristic_name, inspection_id, order_number, rule, description, value, detected_at, status)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 'Open') ON CONFLICT DO NOTHING RETURNING signal_id
            """,
            s.Key.PartNumber, s.Key.Sequence, s.Key.Number, s.CharacteristicName, s.InspectionId, s.OrderNumber, s.Rule.ToString(), s.Description, s.Value, s.DetectedAt);
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    public async Task<SpcSignal?> FindSignalAsync(long signalId, CancellationToken ct) => (await ReadSignalsAsync(SignalSql + " WHERE signal_id = $1", ct, signalId)).SingleOrDefault();

    public async Task<bool> AcknowledgeSignalAsync(long signalId, DateTimeOffset at, string actor, string note, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE quality.spc_signal SET status = 'Acknowledged', acknowledged_at = $2, acknowledged_by = $3, note = $4 WHERE signal_id = $1 AND status = 'Open'", signalId, at, actor, note);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<SpcPolicy> PolicyAsync(CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "SELECT minimum_cpk, critical_minimum_cpk, minimum_points, notify_on_signal, effective_from, set_by, reason FROM quality.spc_policy ORDER BY effective_from DESC, policy_id DESC LIMIT 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new SpcPolicy(r.GetDecimal(0) / 1.00m, r.GetDecimal(1) / 1.00m, r.GetInt32(2), r.GetBoolean(3), r.GetFieldValue<DateTimeOffset>(4), r.GetString(5), r.GetString(6))
            : SpcPolicy.Default;
    }

    public async Task<IReadOnlyList<SpcCharacteristicKey>> ListCriticalAsync(CancellationToken ct)
    {
        var list = new List<SpcCharacteristicKey>();
        await using var cmd = uow.Command("SELECT part_number, sequence, number FROM quality.spc_critical_characteristic ORDER BY part_number, sequence, number");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new SpcCharacteristicKey(r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
        return list;
    }

    public async Task<IReadOnlyList<SpcMeasurement>> ListMeasurementsAsync(string? partNumber, DateTimeOffset from, CancellationToken ct)
    {
        var list = new List<SpcMeasurement>();
        await using var cmd = uow.Command(
            """
            SELECT i.part_number, i.sequence, r.number, r.name, r.lower_limit, r.upper_limit, i.inspection_id, i.order_number, i.at, r.measured
            FROM quality.inspection i JOIN quality.inspection_result r ON r.inspection_id = i.inspection_id
            WHERE r.kind = 'Variable' AND r.measured IS NOT NULL AND i.at >= $1 AND ($2::text IS NULL OR i.part_number = $2)
            ORDER BY i.at, i.inspection_id, r.number
            """,
            from, new NpgsqlParameter { Value = (object?)partNumber ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new SpcMeasurement(new SpcCharacteristicKey(r.GetString(0), r.GetInt32(1), r.GetInt32(2)), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetDecimal(4) / 1.000000000000000000000000000000000m, r.IsDBNull(5) ? null : r.GetDecimal(5) / 1.000000000000000000000000000000000m,
                new SpcPoint(r.GetInt64(6), r.GetInt64(7), r.GetFieldValue<DateTimeOffset>(8), r.GetDecimal(9) / 1.000000000000000000000000000000000m)));
        }

        return list;
    }

    public Task<IReadOnlyList<SpcSignal>> ListSignalsAsync(bool openOnly, CancellationToken ct) =>
        ReadSignalsAsync(SignalSql + (openOnly ? " WHERE status = 'Open'" : string.Empty) + " ORDER BY signal_id DESC LIMIT 500", ct);

    public async Task<bool> SignalExistsAsync(SpcCharacteristicKey key, long inspectionId, SpcRule rule, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT EXISTS (SELECT 1 FROM quality.spc_signal WHERE part_number = $1 AND sequence = $2 AND number = $3 AND inspection_id = $4 AND rule = $5)",
            key.PartNumber, key.Sequence, key.Number, inspectionId, rule.ToString());
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<IReadOnlyList<SpcSignal>> ReadSignalsAsync(string sql, CancellationToken ct, params object?[] args)
    {
        var list = new List<SpcSignal>();
        await using var cmd = uow.Command(sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new SpcSignal(r.GetInt64(0), new SpcCharacteristicKey(r.GetString(1), r.GetInt32(2), r.GetInt32(3)), r.GetString(4), r.GetInt64(5), r.GetInt64(6),
                Enum.Parse<SpcRule>(r.GetString(7)), r.GetString(8), r.GetDecimal(9) / 1.000000000000000000000000000000000m, r.GetFieldValue<DateTimeOffset>(10),
                Enum.Parse<SpcSignalStatus>(r.GetString(11)), r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13), r.IsDBNull(14) ? null : r.GetString(14)));
        }

        return list;
    }
}
