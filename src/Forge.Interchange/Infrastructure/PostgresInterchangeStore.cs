using Forge.Interchange.Contracts;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Interchange.Infrastructure;

internal sealed class PostgresInterchangeStore(PostgresUnitOfWork uow) : IInterchangeStore, IInterchangeLookup
{
    private const string PartnerSql =
        "SELECT code, name, kind, party_code, edi_qualifier, edi_id, our_qualifier, our_id, outbound, active, test, updated_by, updated_at FROM interchange.trading_partner";

    private const string KeySql =
        "SELECT k.key_id, k.partner_code, k.prefix, k.scopes, k.issued_by, k.issued_at, k.revoked_by, k.revoked_at FROM interchange.api_key k";

    private const string DocumentSql =
        """
        SELECT document_id, direction, partner_code, transaction_set, interchange_control, group_control, transaction_control, status, business_ref, summary, problems,
               file_name, content, created_at, created_by, updated_at, updated_by
        FROM interchange.document
        """;

    private const string WebhookSql = "SELECT webhook_id, name, url, events, partner_code, active, secret_prefix, updated_by, updated_at FROM interchange.webhook";

    private const string DeliverySql =
        "SELECT delivery_id, webhook_id, event_sequence, event_name, payload, status, attempts, next_attempt_at, last_status_code, last_error, created_at, delivered_at FROM interchange.webhook_delivery";

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Texts(IReadOnlyList<string> values) => new() { Value = values.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text };

    private static NpgsqlParameter Long(long? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    private static NpgsqlParameter Int(int? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer };

    private static NpgsqlParameter At(DateTimeOffset? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    public async Task UpsertPartnerAsync(TradingPartnerInfo p, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO interchange.trading_partner (code, name, kind, party_code, edi_qualifier, edi_id, our_qualifier, our_id, outbound, active, test, updated_by, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
            ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name, party_code = EXCLUDED.party_code, edi_qualifier = EXCLUDED.edi_qualifier, edi_id = EXCLUDED.edi_id,
                our_qualifier = EXCLUDED.our_qualifier, our_id = EXCLUDED.our_id, outbound = EXCLUDED.outbound, active = EXCLUDED.active, test = EXCLUDED.test,
                updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at
            """,
            p.Code, p.Name, p.Kind.ToString(), Text(p.PartyCode), Text(p.EdiQualifier), Text(p.EdiId), Text(p.OurQualifier), Text(p.OurId), Texts(p.Outbound), p.Active, p.Test, p.UpdatedBy, p.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertApiKeyAsync(ApiKeyInfo k, string hash, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO interchange.api_key (partner_code, prefix, key_hash, scopes, issued_by, issued_at) VALUES ($1, $2, $3, $4, $5, $6) RETURNING key_id",
            k.PartnerCode, k.Prefix, hash, Texts(k.Scopes), k.IssuedBy, k.IssuedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> RevokeApiKeyAsync(long keyId, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE interchange.api_key SET revoked_at = $2, revoked_by = $3 WHERE key_id = $1 AND revoked_at IS NULL", keyId, at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public Task<long?> InsertInboundAsync(InterchangeDocument d, CancellationToken ct) => InsertDocumentAsync(d, ct);

    public Task<long?> InsertOutboundAsync(InterchangeDocument d, CancellationToken ct) => InsertDocumentAsync(d, ct);

    private async Task<long?> InsertDocumentAsync(InterchangeDocument d, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO interchange.document (direction, partner_code, transaction_set, interchange_control, group_control, transaction_control, status, business_ref, summary, problems,
                file_name, content, created_at, created_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14) ON CONFLICT DO NOTHING RETURNING document_id
            """,
            d.Direction.ToString(), Text(d.PartnerCode), d.TransactionSet, d.InterchangeControl, d.GroupControl, d.TransactionControl, d.Status.ToString(), Text(d.BusinessRef),
            Text(d.Summary), Texts(d.Problems), Text(d.FileName), d.Content, d.CreatedAt, d.CreatedBy);
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    public async Task<bool> OutboundExistsAsync(string partnerCode, string transactionSet, string businessRef, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "SELECT EXISTS (SELECT 1 FROM interchange.document WHERE direction = 'Outbound' AND partner_code = $1 AND transaction_set = $2 AND business_ref = $3)",
            partnerCode, transactionSet, businessRef);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> NextControlAsync(string partnerCode, CancellationToken ct)
    {
        // ISA13 is nine digits; the counter wraps after 999,999,999.
        await using var cmd = uow.Command(
            "UPDATE interchange.trading_partner SET next_control = CASE WHEN next_control >= 999999999 THEN 1 ELSE next_control + 1 END WHERE code = $1 RETURNING CASE WHEN next_control = 1 THEN 999999999 ELSE next_control - 1 END",
            partnerCode);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateDocumentAsync(long documentId, DocumentStatus status, string? summary, IReadOnlyList<string> problems, string? businessRef, string? fileName, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE interchange.document SET status = $2, summary = $3, problems = $4, business_ref = $5, file_name = $6, updated_at = $7, updated_by = $8 WHERE document_id = $1",
            documentId, status.ToString(), Text(summary), Texts(problems), Text(businessRef), Text(fileName), at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<InterchangeDocument?> FindOutboundByGroupAsync(string partnerCode, string groupControl, CancellationToken ct) =>
        (await ReadAsync(DocumentSql + " WHERE direction = 'Outbound' AND partner_code = $1 AND group_control = $2 ORDER BY document_id DESC LIMIT 1", ReadDocument, ct, partnerCode, groupControl))
            .SingleOrDefault();

    public async Task<long> SaveWebhookAsync(WebhookInfo w, string? secret, CancellationToken ct)
    {
        await using var cmd = w.WebhookId == 0
            ? uow.Command(
                "INSERT INTO interchange.webhook (name, url, events, partner_code, active, secret, secret_prefix, updated_by, updated_at) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) RETURNING webhook_id",
                w.Name, w.Url, Texts(w.Events), Text(w.PartnerCode), w.Active, secret!, w.SecretPrefix, w.UpdatedBy, w.UpdatedAt)
            : uow.Command(
                """
                UPDATE interchange.webhook SET name = $2, url = $3, events = $4, partner_code = $5, active = $6, secret = COALESCE($7, secret), secret_prefix = $8, updated_by = $9, updated_at = $10
                WHERE webhook_id = $1 RETURNING webhook_id
                """,
                w.WebhookId, w.Name, w.Url, Texts(w.Events), Text(w.PartnerCode), w.Active, Text(secret), w.SecretPrefix, w.UpdatedBy, w.UpdatedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<long>> InsertDeliveriesAsync(IReadOnlyList<long> webhookIds, long sequence, string eventName, string payload, DateTimeOffset at, CancellationToken ct)
    {
        var ids = new List<long>();
        foreach (var webhook in webhookIds)
        {
            await using var cmd = uow.Command(
                "INSERT INTO interchange.webhook_delivery (webhook_id, event_sequence, event_name, payload, status, next_attempt_at, created_at) VALUES ($1, $2, $3, $4, 'Pending', $5, $5) ON CONFLICT DO NOTHING RETURNING delivery_id",
                webhook, sequence, eventName, payload, at);
            if (await cmd.ExecuteScalarAsync(ct) is long id) ids.Add(id);
        }

        return ids;
    }

    public async Task<WebhookDelivery?> FindDeliveryAsync(long deliveryId, CancellationToken ct) =>
        (await ReadAsync(DeliverySql + " WHERE delivery_id = $1 FOR UPDATE", ReadDelivery, ct, deliveryId)).SingleOrDefault();

    public async Task UpdateDeliveryAsync(long deliveryId, DeliveryStatus status, int attempts, DateTimeOffset nextAttemptAt, int? statusCode, string? error, DateTimeOffset? deliveredAt, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE interchange.webhook_delivery SET status = $2, attempts = $3, next_attempt_at = $4, last_status_code = $5, last_error = $6, delivered_at = $7 WHERE delivery_id = $1",
            deliveryId, status.ToString(), attempts, nextAttemptAt, Int(statusCode), Text(error), At(deliveredAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<TradingPartnerInfo>> ListPartnersAsync(CancellationToken ct) => ReadAsync(PartnerSql + " ORDER BY code", ReadPartner, ct);

    public async Task<TradingPartnerInfo?> FindPartnerAsync(string code, CancellationToken ct) =>
        (await ReadAsync(PartnerSql + " WHERE code = $1", ReadPartner, ct, code)).SingleOrDefault();

    public async Task<TradingPartnerInfo?> FindPartnerByEdiAsync(string qualifier, string id, CancellationToken ct) =>
        (await ReadAsync(PartnerSql + " WHERE edi_qualifier = $1 AND edi_id = $2", ReadPartner, ct, qualifier, id)).SingleOrDefault();

    public async Task<TradingPartnerInfo?> FindPartnerByPartyAsync(PartnerKind kind, string partyCode, CancellationToken ct) =>
        (await ReadAsync(PartnerSql + " WHERE kind = $1 AND party_code = $2", ReadPartner, ct, kind.ToString(), partyCode)).SingleOrDefault();

    public Task<IReadOnlyList<ApiKeyInfo>> ListApiKeysAsync(CancellationToken ct) => ReadAsync(KeySql + " ORDER BY k.key_id DESC", ReadKey, ct);

    public async Task<(ApiKeyInfo Key, TradingPartnerInfo Partner)?> FindActiveKeyAsync(string hash, CancellationToken ct)
    {
        var key = (await ReadAsync(KeySql + " WHERE k.key_hash = $1 AND k.revoked_at IS NULL", ReadKey, ct, hash)).SingleOrDefault();
        if (key is null) return null;
        var partner = await FindPartnerAsync(key.PartnerCode, ct);
        return partner is { Active: true } ? (key, partner) : null;
    }

    public Task<IReadOnlyList<InterchangeDocument>> ListDocumentsAsync(DocumentStatus? status, int take, CancellationToken ct) =>
        status is { } s
            ? ReadAsync(DocumentSql + " WHERE status = $1 ORDER BY document_id DESC LIMIT $2", ReadDocument, ct, s.ToString(), Math.Clamp(take, 1, 1000))
            : ReadAsync(DocumentSql + " ORDER BY document_id DESC LIMIT $1", ReadDocument, ct, Math.Clamp(take, 1, 1000));

    public async Task<InterchangeDocument?> FindDocumentAsync(long documentId, CancellationToken ct) =>
        (await ReadAsync(DocumentSql + " WHERE document_id = $1", ReadDocument, ct, documentId)).SingleOrDefault();

    public Task<IReadOnlyList<InterchangeDocument>> ListReadyAsync(CancellationToken ct) =>
        ReadAsync(DocumentSql + " WHERE direction = 'Outbound' AND status = 'Ready' ORDER BY document_id", ReadDocument, ct);

    public Task<IReadOnlyList<WebhookInfo>> ListWebhooksAsync(CancellationToken ct) => ReadAsync(WebhookSql + " ORDER BY webhook_id", ReadWebhook, ct);

    public async Task<string?> WebhookSecretAsync(long webhookId, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT secret FROM interchange.webhook WHERE webhook_id = $1", webhookId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public Task<IReadOnlyList<WebhookDelivery>> ListDeliveriesAsync(long? webhookId, int take, CancellationToken ct) =>
        ReadAsync(DeliverySql + " WHERE ($1::bigint IS NULL OR webhook_id = $1) ORDER BY delivery_id DESC LIMIT $2", ReadDelivery, ct, Long(webhookId), Math.Clamp(take, 1, 1000));

    public Task<IReadOnlyList<WebhookDelivery>> ListDueDeliveriesAsync(DateTimeOffset now, int take, CancellationToken ct) =>
        ReadAsync(DeliverySql + " WHERE status = 'Pending' AND next_attempt_at <= $1 ORDER BY next_attempt_at, delivery_id LIMIT $2", ReadDelivery, ct, now, Math.Clamp(take, 1, 500));

    private static TradingPartnerInfo ReadPartner(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), Enum.Parse<PartnerKind>(r.GetString(2)), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetFieldValue<string[]>(8),
        r.GetBoolean(9), r.GetBoolean(10), r.GetString(11), r.GetFieldValue<DateTimeOffset>(12));

    private static ApiKeyInfo ReadKey(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), null);

    private static InterchangeDocument ReadDocument(NpgsqlDataReader r) => new(
        r.GetInt64(0), Enum.Parse<DocumentDirection>(r.GetString(1)), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
        Enum.Parse<DocumentStatus>(r.GetString(7)), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetFieldValue<string[]>(10),
        r.IsDBNull(11) ? null : r.GetString(11), r.GetString(12), r.GetFieldValue<DateTimeOffset>(13), r.GetString(14),
        r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15), r.IsDBNull(16) ? null : r.GetString(16));

    private static WebhookInfo ReadWebhook(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetBoolean(5), r.GetString(6), r.GetString(7),
        r.GetFieldValue<DateTimeOffset>(8));

    private static WebhookDelivery ReadDelivery(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4), Enum.Parse<DeliveryStatus>(r.GetString(5)), r.GetInt32(6), r.GetFieldValue<DateTimeOffset>(7),
        r.IsDBNull(8) ? null : r.GetInt32(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetFieldValue<DateTimeOffset>(10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11));

    private async Task<IReadOnlyList<T>> ReadAsync<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
    {
        var list = new List<T>();
        await using var cmd = uow.Command(sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(map(reader));
        return list;
    }
}

public static class InterchangeRegistration
{
    public static IServiceCollection AddForgeInterchange(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(InterchangeRegistration).Assembly, "interchange");
        services.AddForgeStore<IInterchangeStore>(uow => new PostgresInterchangeStore(uow));
        services.AddForgeStore<IInterchangeLookup>(uow => new PostgresInterchangeStore(uow));
        services.AddSingleton<InterchangeService>();
        return services;
    }
}
