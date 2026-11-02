using Forge.Inventory.Contracts;
using Forge.Kernel.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Inventory.Infrastructure;

internal sealed class PostgresAllocationStore(PostgresUnitOfWork uow) : IAllocationLookup, IAllocationStore
{
    public async Task<AllocationPolicy> AllocationPolicyAsync(CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT rules, set_by, set_at FROM inventory.allocation_policy ORDER BY policy_id DESC LIMIT 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new AllocationPolicy(r.GetFieldValue<string[]>(0).Select(Enum.Parse<AllocationRule>).ToList(), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2))
            : AllocationPolicy.Default;
    }

    public async Task<IReadOnlyList<ReservationAlternate>> ListReservationAlternatesAsync(CancellationToken ct)
    {
        var list = new List<ReservationAlternate>();
        await using var cmd = uow.Command("SELECT reservation_id, primary_part, factor, alternate_id FROM inventory.reservation_alternate");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ReservationAlternate(r.GetInt64(0), r.GetString(1), r.GetDecimal(2), r.IsDBNull(3) ? null : r.GetInt64(3)));
        return list;
    }

    public async Task<IReadOnlyList<AlternateIssue>> ListAlternateIssuesAsync(CancellationToken ct)
    {
        var list = new List<AlternateIssue>();
        await using var cmd = uow.Command(
            """
            SELECT order_number, part_number, substitutes_for, sum(CASE WHEN kind = 'Issue' THEN 1 ELSE -1 END * quantity), sum(CASE WHEN kind = 'Issue' THEN 1 ELSE -1 END * COALESCE(primary_quantity, quantity))
            FROM inventory.transaction WHERE kind IN ('Issue', 'Return') AND order_number IS NOT NULL AND substitutes_for IS NOT NULL
            GROUP BY 1, 2, 3
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new AlternateIssue(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4)));
        return list;
    }

    public async Task InsertAllocationPolicyAsync(AllocationPolicy p, CancellationToken ct)
    {
        await using var cmd = uow.Command("INSERT INTO inventory.allocation_policy (rules, set_by, set_at) VALUES ($1, $2, $3)",
            new NpgsqlParameter { Value = p.Rules.Select(x => x.ToString()).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text }, p.SetBy, p.SetAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertReservationAlternateAsync(ReservationAlternate a, CancellationToken ct)
    {
        await using var cmd = uow.Command("INSERT INTO inventory.reservation_alternate (reservation_id, primary_part, factor, alternate_id) VALUES ($1, $2, $3, $4)",
            a.ReservationId, a.PrimaryPart, a.Factor, new NpgsqlParameter { Value = (object?)a.AlternateId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
