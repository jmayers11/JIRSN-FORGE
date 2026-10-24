using Forge.Inventory.Contracts;
using Npgsql;

namespace Forge.Inventory.Infrastructure;

/// <summary>Scrap requests and direct-scrap rules (Sprint 10 §20). Stock itself only ever changes in the ledger.</summary>
internal sealed partial class PostgresInventoryStore : IScrapStore, IScrapLookup
{
    private const string ScrapRequestSql =
        """
        SELECT request_id, kind, part_number, location_code, lot_number, stock_status, execute_status, quantity, reason_code, condition_note, notes,
               linked_order_number, linked_po_number, linked_work_order_id, linked_ncr, linked_supplier, requires_quality, policy_id,
               estimated_unit_value, currency, state, requested_by, requested_at, hold_transaction_id, decided_by, decided_at, decision_note,
               executed_by, executed_at, transaction_id, release_transaction_id, corrects_request_id
        FROM inventory.scrap_request
        """;

    private const string ScrapPolicySql =
        """
        SELECT policy_id, role_name, reason_code, max_quantity, max_value, state, proposed_by, proposed_at, decided_by, decided_at
        FROM inventory.scrap_policy
        """;

    public async Task<long> InsertScrapRequestAsync(ScrapRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.scrap_request (kind, part_number, location_code, lot_number, stock_status, execute_status, quantity, reason_code, condition_note, notes,
                linked_order_number, linked_po_number, linked_work_order_id, linked_ncr, linked_supplier, requires_quality, policy_id, estimated_unit_value, currency,
                state, requested_by, requested_at, hold_transaction_id, decided_by, decided_at, decision_note, executed_by, executed_at, transaction_id, corrects_request_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21, $22, $23, $24, $25, $26, $27, $28, $29, $30)
            RETURNING request_id
            """,
            request.Kind.ToString(), request.PartNumber, request.LocationCode, Text(request.LotNumber), request.StockStatus.ToString(), request.ExecuteStatus.ToString(),
            request.Quantity, request.ReasonCode.ToString(), Text(request.Condition), Text(request.Notes), Long(request.LinkedOrderNumber), Long(request.LinkedPoNumber),
            Long(request.LinkedWorkOrderId), Text(request.LinkedNcr), Text(request.LinkedSupplier), request.RequiresQuality, Long(request.PolicyId),
            Numeric(request.EstimatedUnitValue), Text(request.Currency), request.State.ToString(), request.RequestedBy, request.RequestedAt, Long(request.HoldTransactionId),
            Text(request.DecidedBy), Time(request.DecidedAt), Text(request.DecisionNote), Text(request.ExecutedBy), Time(request.ExecutedAt), Long(request.TransactionId),
            Long(request.CorrectsRequestId));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> UpdateScrapRequestAsync(ScrapRequest request, ScrapRequestState expected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var cmd = uow.Command(
            """
            UPDATE inventory.scrap_request SET
                execute_status = $3, state = $4, hold_transaction_id = $5, decided_by = $6, decided_at = $7, decision_note = $8,
                executed_by = $9, executed_at = $10, transaction_id = $11, release_transaction_id = $12
            WHERE request_id = $1 AND state = $2
            """,
            request.RequestId, expected.ToString(), request.ExecuteStatus.ToString(), request.State.ToString(), Long(request.HoldTransactionId),
            Text(request.DecidedBy), Time(request.DecidedAt), Text(request.DecisionNote), Text(request.ExecutedBy), Time(request.ExecutedAt),
            Long(request.TransactionId), Long(request.ReleaseTransactionId));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<long> InsertScrapPolicyAsync(ScrapPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.scrap_policy (role_name, reason_code, max_quantity, max_value, state, proposed_by, proposed_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            RETURNING policy_id
            """,
            policy.RoleName, Text(policy.ReasonCode?.ToString()), Numeric(policy.MaxQuantity), Numeric(policy.MaxValue), policy.State.ToString(),
            policy.ProposedBy, policy.ProposedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> DecideScrapPolicyAsync(long policyId, ScrapPolicyState expected, ScrapPolicyState state, string actor, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE inventory.scrap_policy SET state = $3, decided_by = COALESCE(decided_by, $4), decided_at = COALESCE(decided_at, $5) WHERE policy_id = $1 AND state = $2",
            policyId, expected.ToString(), state.ToString(), actor, at);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public Task<IReadOnlyList<ScrapRequest>> ListScrapRequestsAsync(bool openOnly, string? partNumber, CancellationToken ct) =>
        ReadAsync(
            ScrapRequestSql + " WHERE (NOT $1 OR state IN ('PendingApproval', 'PendingDisposition', 'Approved')) AND ($2::text IS NULL OR part_number = $2) ORDER BY request_id",
            ReadScrapRequest,
            ct,
            openOnly, Text(partNumber));

    public async Task<ScrapRequest?> FindScrapRequestAsync(long requestId, CancellationToken ct) =>
        (await ReadAsync(ScrapRequestSql + " WHERE request_id = $1", ReadScrapRequest, ct, requestId)).SingleOrDefault();

    public Task<IReadOnlyList<ScrapPolicy>> ListScrapPoliciesAsync(CancellationToken ct) =>
        ReadAsync(ScrapPolicySql + " ORDER BY policy_id", ReadScrapPolicy, ct);

    private static ScrapRequest ReadScrapRequest(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        Enum.Parse<ScrapRequestKind>(r.GetString(1)),
        r.GetString(2),
        r.GetString(3),
        NullableText(r, 4),
        Enum.Parse<StockStatus>(r.GetString(5)),
        Enum.Parse<StockStatus>(r.GetString(6)),
        Dec(r, 7),
        Enum.Parse<ScrapReasonCode>(r.GetString(8)),
        NullableText(r, 9),
        NullableText(r, 10),
        r.IsDBNull(11) ? null : r.GetInt64(11),
        r.IsDBNull(12) ? null : r.GetInt64(12),
        r.IsDBNull(13) ? null : r.GetInt64(13),
        NullableText(r, 14),
        NullableText(r, 15),
        r.GetBoolean(16),
        r.IsDBNull(17) ? null : r.GetInt64(17),
        r.IsDBNull(18) ? null : Dec(r, 18),
        NullableText(r, 19),
        Enum.Parse<ScrapRequestState>(r.GetString(20)),
        r.GetString(21),
        r.GetFieldValue<DateTimeOffset>(22),
        r.IsDBNull(23) ? null : r.GetInt64(23),
        NullableText(r, 24),
        r.IsDBNull(25) ? null : r.GetFieldValue<DateTimeOffset>(25),
        NullableText(r, 26),
        NullableText(r, 27),
        r.IsDBNull(28) ? null : r.GetFieldValue<DateTimeOffset>(28),
        r.IsDBNull(29) ? null : r.GetInt64(29),
        r.IsDBNull(30) ? null : r.GetInt64(30),
        r.IsDBNull(31) ? null : r.GetInt64(31));

    private static ScrapPolicy ReadScrapPolicy(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.IsDBNull(2) ? null : Enum.Parse<ScrapReasonCode>(r.GetString(2)),
        r.IsDBNull(3) ? null : Dec(r, 3),
        r.IsDBNull(4) ? null : Dec(r, 4),
        Enum.Parse<ScrapPolicyState>(r.GetString(5)),
        r.GetString(6),
        r.GetFieldValue<DateTimeOffset>(7),
        NullableText(r, 8),
        r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9));

    private static NpgsqlParameter Numeric(decimal? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Numeric };
}
