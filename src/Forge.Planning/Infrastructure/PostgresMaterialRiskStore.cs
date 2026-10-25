using Forge.Kernel.Postgres;

namespace Forge.Planning.Infrastructure;

/// <summary>Orders currently at risk from material (Sprint 10 WW-S10-30).</summary>
internal sealed class PostgresMaterialRiskStore(PostgresUnitOfWork uow) : IMaterialRiskStore
{
    public async Task<IReadOnlyList<MaterialRiskRecord>> ListOpenAsync(CancellationToken ct)
    {
        var list = new List<MaterialRiskRecord>();
        await using var cmd = uow.Command("SELECT order_number, kind, message, detected_at, updated_at FROM planning.material_risk ORDER BY order_number");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new MaterialRiskRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4)));
        return list;
    }

    public async Task UpsertAsync(MaterialRiskRecord risk, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(risk);
        await using var cmd = uow.Command(
            """
            INSERT INTO planning.material_risk (order_number, kind, message, detected_at, updated_at) VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (order_number) DO UPDATE SET kind = EXCLUDED.kind, message = EXCLUDED.message, updated_at = EXCLUDED.updated_at
            """,
            risk.OrderNumber, risk.Kind, risk.Message, risk.DetectedAt, risk.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveAsync(long orderNumber, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM planning.material_risk WHERE order_number = $1", orderNumber);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
