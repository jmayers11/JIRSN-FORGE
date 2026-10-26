using Forge.Inventory.Contracts;
using Forge.Kernel.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Inventory.Infrastructure;

internal sealed class PostgresCostingStore(PostgresUnitOfWork uow) : ICostingStore
{
    public async Task<IReadOnlyList<CostingPolicy>> ListPoliciesAsync(CancellationToken ct)
    {
        var list = new List<CostingPolicy>();
        await using var cmd = uow.Command("SELECT policy_id, method, currency, labor_rate, overhead_rate, effective_from, set_by, reason FROM inventory.costing_policy ORDER BY effective_from, policy_id");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new CostingPolicy(r.GetInt64(0), Enum.Parse<CostingMethod>(r.GetString(1)), r.GetString(2), r.IsDBNull(3) ? null : D(r.GetDecimal(3)),
                r.IsDBNull(4) ? null : D(r.GetDecimal(4)), r.GetFieldValue<DateTimeOffset>(5), r.GetString(6), r.GetString(7)));
        return list;
    }

    public async Task<IReadOnlyList<StandardCost>> ListStandardCostsAsync(string? partNumber, CancellationToken ct)
    {
        var list = new List<StandardCost>();
        await using var cmd = uow.Command(
            "SELECT cost_id, part_number, material, labor, overhead, currency, effective_from, set_by, reason FROM inventory.standard_cost WHERE ($1::text IS NULL OR part_number = $1) ORDER BY part_number, effective_from, cost_id",
            new NpgsqlParameter { Value = (object?)partNumber ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new StandardCost(r.GetInt64(0), r.GetString(1), D(r.GetDecimal(2)), D(r.GetDecimal(3)), D(r.GetDecimal(4)), r.GetString(5),
                r.GetFieldValue<DateTimeOffset>(6), r.GetString(7), r.GetString(8)));
        return list;
    }

    public async Task<long> InsertPolicyAsync(CostingPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await using var cmd = uow.Command(
            "INSERT INTO inventory.costing_policy (method, currency, labor_rate, overhead_rate, effective_from, set_by, reason) VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING policy_id",
            policy.Method.ToString(), policy.Currency, Num(policy.LaborRatePerHour), Num(policy.OverheadRatePerHour), policy.EffectiveFrom, policy.SetBy, policy.Reason);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> InsertStandardCostAsync(StandardCost cost, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cost);
        await using var cmd = uow.Command(
            "INSERT INTO inventory.standard_cost (part_number, material, labor, overhead, currency, effective_from, set_by, reason) VALUES ($1, $2, $3, $4, $5, $6, $7, $8) RETURNING cost_id",
            cost.PartNumber, cost.Material, cost.Labor, cost.Overhead, cost.Currency, cost.EffectiveFrom, cost.SetBy, cost.Reason);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static NpgsqlParameter Num(decimal? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric };

    private static decimal D(decimal value) => value / 1.000000000000000000000000000000000m;
}
