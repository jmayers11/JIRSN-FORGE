using Forge.Kernel.Postgres;
using Forge.Quality.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Quality.Infrastructure;

/// <summary>Rework and repair records (V3.5): opened, then closed once.</summary>
internal sealed class PostgresReworkStore(PostgresUnitOfWork uow) : IReworkLookup, IReworkStore
{
    private const string Sql =
        """
        SELECT rework_id, kind, order_number, sequence, part_number, quantity, ncr_id, reason_code, instructions, opened_at, opened_by,
               closed_at, closed_by, good_quantity, scrap_quantity, close_note
        FROM quality.rework
        """;

    public async Task<IReadOnlyList<ReworkInfo>> ListReworkAsync(long? orderNumber, bool openOnly, CancellationToken ct)
    {
        var list = new List<ReworkInfo>();
        await using var cmd = uow.Command(
            Sql + " WHERE ($1::bigint IS NULL OR order_number = $1) AND (NOT $2 OR closed_at IS NULL) ORDER BY rework_id",
            new NpgsqlParameter { Value = (object?)orderNumber ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint }, openOnly);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new ReworkInfo(
                r.GetInt64(0), Enum.Parse<ReworkKind>(r.GetString(1)), r.GetInt64(2), r.GetInt32(3), r.GetString(4), D(r.GetDecimal(5)),
                r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.GetFieldValue<DateTimeOffset>(9), r.GetString(10),
                r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11), r.IsDBNull(12) ? null : r.GetString(12),
                r.IsDBNull(13) ? null : D(r.GetDecimal(13)), r.IsDBNull(14) ? null : D(r.GetDecimal(14)), r.IsDBNull(15) ? null : r.GetString(15)));
        }

        return list;
    }

    public async Task<long> InsertReworkAsync(ReworkInfo rework, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rework);
        await using var cmd = uow.Command(
            """
            INSERT INTO quality.rework (kind, order_number, sequence, part_number, quantity, ncr_id, reason_code, instructions, opened_at, opened_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) RETURNING rework_id
            """,
            rework.Kind.ToString(), rework.OrderNumber, rework.Sequence, rework.PartNumber, rework.Quantity,
            new NpgsqlParameter { Value = (object?)rework.NcrId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint },
            new NpgsqlParameter { Value = (object?)rework.ReasonCode ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text },
            rework.Instructions, rework.OpenedAt, rework.OpenedBy);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task CloseReworkAsync(long reworkId, decimal good, decimal scrap, string note, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE quality.rework SET closed_at = $2, closed_by = $3, good_quantity = $4, scrap_quantity = $5, close_note = $6 WHERE rework_id = $1 AND closed_at IS NULL",
            reworkId, at, actor, good, scrap, note);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static decimal D(decimal value) => value / 1.000000000000000000000000000000000m;
}
