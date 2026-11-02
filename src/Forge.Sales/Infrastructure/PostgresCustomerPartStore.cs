using Forge.Kernel.Postgres;
using Forge.Sales.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Sales.Infrastructure;

internal sealed class PostgresCustomerPartStore(PostgresUnitOfWork uow) : ICustomerPartStore, ICustomerPartLookup
{
    private const string Sql = "SELECT customer_code, customer_part, part_number, description, updated_by, updated_at FROM sales.customer_part";

    public async Task UpsertCustomerPartAsync(CustomerPartInfo p, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO sales.customer_part (customer_code, customer_part, part_number, description, updated_by, updated_at) VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (customer_code, customer_part) DO UPDATE SET part_number = EXCLUDED.part_number, description = EXCLUDED.description, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at
            """,
            p.CustomerCode, p.CustomerPart, p.PartNumber, new NpgsqlParameter { Value = (object?)p.Description ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text }, p.UpdatedBy, p.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteCustomerPartAsync(string customerCode, string customerPart, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM sales.customer_part WHERE customer_code = $1 AND customer_part = $2", customerCode, customerPart);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<CustomerPartInfo>> ListCustomerPartsAsync(string? customerCode, CancellationToken ct) =>
        await ReadAsync(Sql + " WHERE ($1::text IS NULL OR customer_code = $1) ORDER BY customer_code, customer_part", ct,
            new NpgsqlParameter { Value = (object?)customerCode ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });

    public async Task<CustomerPartInfo?> FindCustomerPartAsync(string customerCode, string customerPart, CancellationToken ct) =>
        (await ReadAsync(Sql + " WHERE customer_code = $1 AND customer_part = $2", ct, customerCode, customerPart)).SingleOrDefault();

    private async Task<IReadOnlyList<CustomerPartInfo>> ReadAsync(string sql, CancellationToken ct, params object?[] args)
    {
        var list = new List<CustomerPartInfo>();
        await using var cmd = uow.Command(sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new CustomerPartInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5)));
        return list;
    }
}
