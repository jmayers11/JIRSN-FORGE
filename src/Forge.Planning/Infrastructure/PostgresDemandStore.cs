using Forge.Kernel.Postgres;
using Npgsql;

namespace Forge.Planning.Infrastructure;

internal sealed class PostgresDemandStore(PostgresUnitOfWork uow) : IDemandStore
{
    private const string Select = "SELECT demand_id, part_number, quantity, need_date, priority, kind, customer, reference, program_id, status, created_by, created_at FROM planning.demand";

    public async Task<long> InsertAsync(DemandInfo demand, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(demand);
        await using var cmd = uow.Command(
            """
            INSERT INTO planning.demand (part_number, quantity, need_date, priority, kind, customer, reference, program_id, status, created_at, created_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) RETURNING demand_id
            """,
            demand.PartNumber, demand.Quantity, demand.NeedDate, demand.Priority, demand.Kind.ToString(), Text(demand.Customer), Text(demand.Reference),
            new NpgsqlParameter { Value = (object?)demand.ProgramId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid },
            demand.Status.ToString(), demand.CreatedAt, demand.CreatedBy);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateAsync(DemandInfo demand, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(demand);
        await using var cmd = uow.Command(
            "UPDATE planning.demand SET quantity = $2, need_date = $3, priority = $4, status = $5, changed_at = $6, changed_by = $7 WHERE demand_id = $1",
            demand.DemandId, demand.Quantity, demand.NeedDate, demand.Priority, demand.Status.ToString(), at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DemandInfo?> FindAsync(long demandId, CancellationToken ct) =>
        (await ReadAsync(Select + " WHERE demand_id = $1", ct, demandId)).SingleOrDefault();

    public Task<IReadOnlyList<DemandInfo>> ListAsync(bool includeClosed, CancellationToken ct) =>
        ReadAsync(Select + (includeClosed ? string.Empty : " WHERE status = 'Open'") + " ORDER BY need_date, priority, demand_id", ct);

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text };

    private async Task<IReadOnlyList<DemandInfo>> ReadAsync(string sql, CancellationToken ct, params object?[] args)
    {
        var list = new List<DemandInfo>();
        await using var cmd = uow.Command(sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new DemandInfo(
                r.GetInt64(0),
                r.GetString(1),
                (r.GetDecimal(2) / 1.000000000000000000000000000000000m),
                r.GetFieldValue<DateOnly>(3),
                r.GetInt32(4),
                Enum.Parse<DemandKind>(r.GetString(5)),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetGuid(8),
                Enum.Parse<DemandStatus>(r.GetString(9)),
                r.GetString(10),
                r.GetFieldValue<DateTimeOffset>(11)));
        }

        return list;
    }
}
