using Forge.Kernel.Postgres;
using Forge.Sales.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Sales.Infrastructure;

internal sealed class PostgresReceivablesStore(PostgresUnitOfWork uow) : IReceivablesLookup, IReceivablesStore
{
    private const string PaymentSql =
        "SELECT payment_id, customer_code, received_on, amount, currency, method, reference, note, status, recorded_by, recorded_at, voided_by, voided_at, void_reason FROM sales.payment";

    public async Task<IReadOnlyList<PaymentInfo>> ListPaymentsAsync(string? customerCode, CancellationToken ct) =>
        await ReadPaymentsAsync(PaymentSql + " WHERE ($1::text IS NULL OR customer_code = $1) ORDER BY payment_id DESC", ct, Text(customerCode));

    public async Task<PaymentInfo?> FindPaymentAsync(long paymentId, CancellationToken ct) =>
        (await ReadPaymentsAsync(PaymentSql + " WHERE payment_id = $1", ct, paymentId)).SingleOrDefault();

    public async Task<CreditPolicy> CreditPolicyAsync(CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT past_due_days, include_open_orders, set_by, set_at FROM sales.credit_policy ORDER BY policy_id DESC LIMIT 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new CreditPolicy(r.IsDBNull(0) ? null : r.GetInt32(0), r.GetBoolean(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)) : CreditPolicy.Default;
    }

    public async Task<IReadOnlyList<CustomerCredit>> ListCustomerCreditAsync(CancellationToken ct)
    {
        var list = new List<CustomerCredit>();
        await using var cmd = uow.Command("SELECT customer_code, credit_limit, updated_by, updated_at FROM sales.customer_credit ORDER BY customer_code");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new CustomerCredit(r.GetString(0), r.IsDBNull(1) ? null : r.GetDecimal(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)));
        return list;
    }

    public async Task<IReadOnlyList<CreditRelease>> ListCreditReleasesAsync(string? customerCode, CancellationToken ct)
    {
        var list = new List<CreditRelease>();
        await using var cmd = uow.Command(
            "SELECT release_id, customer_code, sales_order_number, shipment_id, reason, released_by, released_at FROM sales.credit_release WHERE ($1::text IS NULL OR customer_code = $1) ORDER BY release_id DESC",
            Text(customerCode));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new CreditRelease(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.GetString(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6)));
        return list;
    }

    public async Task LockCustomerReceivablesAsync(string customerCode, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT pg_advisory_xact_lock(hashtextextended('receivables:' || $1, 0))", customerCode);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertPaymentAsync(PaymentInfo p, CancellationToken ct)
    {
        long id;
        await using (var cmd = uow.Command(
            """
            INSERT INTO sales.payment (customer_code, received_on, amount, currency, method, reference, note, status, recorded_by, recorded_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, 'Recorded', $8, $9) RETURNING payment_id
            """,
            p.CustomerCode, p.ReceivedOn, p.Amount, p.Currency, p.Method.ToString(), Text(p.Reference), Text(p.Note), p.RecordedBy, p.RecordedAt))
        {
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        foreach (var a in p.Applications)
        {
            await using var cmd = uow.Command("INSERT INTO sales.payment_application (payment_id, invoice_number, amount) VALUES ($1, $2, $3)", id, a.InvoiceNumber, a.Amount);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return id;
    }

    public async Task VoidPaymentAsync(long paymentId, string actor, DateTimeOffset at, string reason, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE sales.payment SET status = 'Voided', voided_by = $2, voided_at = $3, void_reason = $4 WHERE payment_id = $1 AND status = 'Recorded'",
            paymentId, actor, at, reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertCustomerCreditAsync(CustomerCredit c, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO sales.customer_credit (customer_code, credit_limit, updated_by, updated_at) VALUES ($1, $2, $3, $4)
            ON CONFLICT (customer_code) DO UPDATE SET credit_limit = EXCLUDED.credit_limit, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at
            """,
            c.CustomerCode, new NpgsqlParameter { Value = (object?)c.CreditLimit ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric }, c.UpdatedBy, c.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertCreditPolicyAsync(CreditPolicy p, CancellationToken ct)
    {
        await using var cmd = uow.Command("INSERT INTO sales.credit_policy (past_due_days, include_open_orders, set_by, set_at) VALUES ($1, $2, $3, $4)",
            new NpgsqlParameter { Value = (object?)p.PastDueDays ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer }, p.IncludeOpenOrders, p.SetBy, p.SetAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertCreditReleaseAsync(CreditRelease r, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO sales.credit_release (customer_code, sales_order_number, shipment_id, reason, released_by, released_at) VALUES ($1, $2, $3, $4, $5, $6) RETURNING release_id",
            r.CustomerCode, Long(r.SalesOrderNumber), Long(r.ShipmentId), r.Reason, r.ReleasedBy, r.ReleasedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<IReadOnlyList<PaymentInfo>> ReadPaymentsAsync(string sql, CancellationToken ct, params object?[] args)
    {
        var heads = new List<PaymentInfo>();
        await using (var cmd = uow.Command(sql, args))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                heads.Add(new PaymentInfo(r.GetInt64(0), r.GetString(1), r.GetFieldValue<DateOnly>(2), r.GetDecimal(3), r.GetString(4), Enum.Parse<PaymentMethod>(r.GetString(5)),
                    r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), Enum.Parse<PaymentStatus>(r.GetString(8)), r.GetString(9),
                    r.GetFieldValue<DateTimeOffset>(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12),
                    r.IsDBNull(13) ? null : r.GetString(13), []));
            }
        }

        if (heads.Count == 0) return heads;
        var apps = new Dictionary<long, List<PaymentApplication>>();
        await using (var cmd = uow.Command("SELECT payment_id, invoice_number, amount FROM sales.payment_application WHERE payment_id = ANY($1) ORDER BY payment_id, invoice_number",
            new NpgsqlParameter { Value = heads.Select(h => h.PaymentId).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint }))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt64(0);
                if (!apps.TryGetValue(id, out var list)) apps[id] = list = [];
                list.Add(new PaymentApplication(r.GetInt64(1), r.GetDecimal(2)));
            }
        }

        return heads.Select(h => h with { Applications = apps.GetValueOrDefault(h.PaymentId) ?? [] }).ToList();
    }

    private static NpgsqlParameter Text(string? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Long(long? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };
}

public static class ReceivablesRegistration
{
    public static IServiceCollection AddForgeReceivables(this IServiceCollection services)
    {
        services.AddForgeStore<IReceivablesLookup>(uow => new PostgresReceivablesStore(uow));
        services.AddForgeStore<IReceivablesStore>(uow => new PostgresReceivablesStore(uow));
        services.AddSingleton<ReceivablesService>();
        return services;
    }
}
