using System.Text.Json;
using Forge.Kernel.Postgres;
using Forge.Procurement.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Procurement.Infrastructure;

internal sealed class PostgresSourcingStore(PostgresUnitOfWork uow) : ISourcingLookup, ISourcingStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string RfqSql = "SELECT rfq_id,requisition_id,title,supplier_codes,status,created_by,created_at,released_by,released_at,package::text,selected_quote_id,selected_by,selected_at,selection_reason FROM procurement.rfq";
    private const string QuoteSql = "SELECT quote_id,rfq_id,supplier_code,quantity,unit_price,currency,promised_date,valid_through,proposed_alternate_part,original_document_number,original_document_revision,recorded_by,recorded_at,note FROM procurement.supplier_quote";

    public async Task LockRfqAsync(long rfqId, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT pg_advisory_xact_lock(hashtextextended('forge.rfq:' || $1::text,0))", rfqId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RfqInfo>> ListRfqsAsync(CancellationToken ct)
    {
        var list = new List<RfqInfo>();
        await using var cmd = uow.Command(RfqSql + " ORDER BY rfq_id DESC");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(Rfq(reader));
        return list;
    }

    public async Task<RfqInfo?> FindRfqAsync(long rfqId, CancellationToken ct)
    {
        await using var cmd = uow.Command(RfqSql + " WHERE rfq_id=$1", rfqId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Rfq(reader) : null;
    }

    public async Task<IReadOnlyList<SupplierQuote>> ListQuotesAsync(long? rfqId, CancellationToken ct)
    {
        var list = new List<SupplierQuote>();
        await using var cmd = uow.Command(QuoteSql + " WHERE ($1::bigint IS NULL OR rfq_id=$1) ORDER BY quote_id DESC", Long(rfqId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(Quote(reader));
        return list;
    }

    public async Task<SupplierQuote?> SelectedQuoteForRequisitionAsync(long requisitionId, CancellationToken ct)
    {
        long? quoteId;
        await using (var cmd = uow.Command("SELECT selected_quote_id FROM procurement.rfq WHERE requisition_id=$1 AND status<>'Cancelled' ORDER BY rfq_id DESC LIMIT 1", requisitionId))
            quoteId = await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
        if (quoteId is not { } selected) return null;
        await using var quote = uow.Command(QuoteSql + " WHERE quote_id=$1", selected);
        await using var reader = await quote.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Quote(reader) : null;
    }

    public async Task<long> InsertRfqAsync(RfqInfo rfq, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rfq);
        await using var cmd = uow.Command("""
            INSERT INTO procurement.rfq(requisition_id,title,supplier_codes,status,created_by,created_at)
            VALUES ($1,$2,$3,'Draft',$4,$5) RETURNING rfq_id
            """, rfq.RequisitionId, rfq.Title, new NpgsqlParameter { Value = rfq.SupplierCodes.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text }, rfq.CreatedBy, rfq.CreatedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateRfqAsync(RfqInfo rfq, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rfq);
        await using var cmd = uow.Command("""
            UPDATE procurement.rfq SET status=$2,released_by=$3,released_at=$4,package=$5,selected_quote_id=$6,selected_by=$7,selected_at=$8,selection_reason=$9
            WHERE rfq_id=$1
            """, rfq.RfqId, rfq.Status.ToString(), Text(rfq.ReleasedBy), Time(rfq.ReleasedAt), new NpgsqlParameter { Value = rfq.Package is null ? DBNull.Value : JsonSerializer.Serialize(rfq.Package, Json), NpgsqlDbType = NpgsqlDbType.Jsonb },
            Long(rfq.SelectedQuoteId), Text(rfq.SelectedBy), Time(rfq.SelectedAt), Text(rfq.SelectionReason));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertQuoteAsync(SupplierQuote quote, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(quote);
        await using var cmd = uow.Command("""
            INSERT INTO procurement.supplier_quote(rfq_id,supplier_code,quantity,unit_price,currency,promised_date,valid_through,proposed_alternate_part,
                original_document_number,original_document_revision,recorded_by,recorded_at,note)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13) RETURNING quote_id
            """, quote.RfqId, quote.SupplierCode, quote.Quantity, quote.UnitPrice, quote.Currency, quote.PromisedDate, quote.ValidThrough, Text(quote.ProposedAlternatePart),
            quote.OriginalDocumentNumber, quote.OriginalDocumentRevision, quote.RecordedBy, quote.RecordedAt, Text(quote.Note));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static RfqInfo Rfq(NpgsqlDataReader r) => new(r.GetInt64(0),r.GetInt64(1),r.GetString(2),r.GetFieldValue<string[]>(3),Enum.Parse<RfqStatus>(r.GetString(4)),
        r.GetString(5),r.GetFieldValue<DateTimeOffset>(6),Str(r,7),At(r,8),r.IsDBNull(9) ? null : JsonSerializer.Deserialize<SourcingPackage>(r.GetString(9),Json),
        r.IsDBNull(10) ? null : r.GetInt64(10),Str(r,11),At(r,12),Str(r,13));
    private static SupplierQuote Quote(NpgsqlDataReader r) => new(r.GetInt64(0),r.GetInt64(1),r.GetString(2),r.GetDecimal(3),r.GetDecimal(4),r.GetString(5),
        r.GetFieldValue<DateOnly>(6),r.GetFieldValue<DateOnly>(7),Str(r,8),r.GetString(9),r.GetString(10),r.GetString(11),r.GetFieldValue<DateTimeOffset>(12),Str(r,13));
    private static string? Str(NpgsqlDataReader r,int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTimeOffset? At(NpgsqlDataReader r,int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
    private static NpgsqlParameter Time(DateTimeOffset? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz };
    private static NpgsqlParameter Long(long? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };
}
