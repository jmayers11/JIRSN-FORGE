using Forge.Engineering.Contracts;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Engineering.Infrastructure;

internal sealed class PostgresDocumentStore(PostgresUnitOfWork uow) : IDocumentStore, IDocumentLookup
{
    private const string DocumentSql = "SELECT number, title, doc_type, authority, external_authority, external_number, program_id, markings, created_by, created_at FROM engineering.document";

    private const string RevisionSql =
        """
        SELECT r.document_number, r.revision, r.status, r.file_sha256, r.file_name, f.size_bytes, r.change_note, r.review_by, r.author, r.authored_at,
               r.submitted_by, r.submitted_at, r.approved_by, r.approved_at, r.released_by, r.released_at, r.effective_from, r.effective_to, r.obsoleted_by, r.obsoleted_at
        FROM engineering.document_revision r LEFT JOIN engineering.document_file f ON f.sha256 = r.file_sha256
        """;

    private const string LinkSql =
        "SELECT link_id, document_number, revision, target_type, target_id, target_label, program_id, purpose, note, linked_by, linked_at, detached_by, detached_at, detach_reason FROM engineering.document_link";

    public async Task<bool> InsertFileAsync(string sha256, byte[] content, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var cmd = uow.Command(
            "INSERT INTO engineering.document_file (sha256, size_bytes, content, stored_by, stored_at) VALUES ($1, $2, $3, $4, $5) ON CONFLICT DO NOTHING",
            sha256, (long)content.Length, new NpgsqlParameter { Value = content, NpgsqlDbType = NpgsqlDbType.Bytea }, actor, at);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> InsertDocumentAsync(DocumentInfo document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        await using var cmd = uow.Command(
            """
            INSERT INTO engineering.document (number, title, doc_type, authority, external_authority, external_number, program_id, markings, created_by, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) ON CONFLICT DO NOTHING
            """,
            document.Number, document.Title, document.Type.ToString(), document.Authority.ToString(), Text(document.ExternalAuthority), Text(document.ExternalNumber),
            Uuid(document.ProgramId), (int)document.Markings, document.CreatedBy, document.CreatedAt);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task UpdateDocumentAsync(string number, string title, DataMarking markings, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE engineering.document SET title = $2, markings = $3, changed_at = $4, changed_by = $5 WHERE number = $1",
            number, title, (int)markings, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> NextReferenceNumberAsync(CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT nextval('engineering.reference_document_seq')");
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> InsertRevisionAsync(DocumentRevisionInfo r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        await using var cmd = uow.Command(
            """
            INSERT INTO engineering.document_revision (document_number, revision, status, file_sha256, file_name, change_note, review_by, author, authored_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) ON CONFLICT DO NOTHING
            """,
            r.DocumentNumber, r.Revision, r.Status.ToString(), Text(r.FileSha256), Text(r.FileName), Text(r.ChangeNote), Date(r.ReviewBy), r.Author, r.AuthoredAt);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task UpdateRevisionAsync(DocumentRevisionInfo r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        await using var cmd = uow.Command(
            """
            UPDATE engineering.document_revision SET status = $3, file_sha256 = $4, file_name = $5, change_note = $6, review_by = $7,
                submitted_by = $8, submitted_at = $9, approved_by = $10, approved_at = $11, released_by = $12, released_at = $13,
                effective_from = $14, effective_to = $15, obsoleted_by = $16, obsoleted_at = $17
            WHERE document_number = $1 AND revision = $2
            """,
            r.DocumentNumber, r.Revision, r.Status.ToString(), Text(r.FileSha256), Text(r.FileName), Text(r.ChangeNote), Date(r.ReviewBy),
            Text(r.SubmittedBy), Time(r.SubmittedAt), Text(r.ApprovedBy), Time(r.ApprovedAt), Text(r.ReleasedBy), Time(r.ReleasedAt),
            Date(r.EffectiveFrom), Date(r.EffectiveTo), Text(r.ObsoletedBy), Time(r.ObsoletedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long?> InsertLinkAsync(DocumentLinkInfo link, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);
        await using var cmd = uow.Command(
            """
            INSERT INTO engineering.document_link (document_number, revision, target_type, target_id, target_label, program_id, purpose, note, linked_by, linked_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) ON CONFLICT DO NOTHING RETURNING link_id
            """,
            link.DocumentNumber, Text(link.Revision), link.TargetType.ToString(), link.TargetId, link.TargetLabel, Uuid(link.ProgramId), link.Purpose.ToString(),
            Text(link.Note), link.LinkedBy, link.LinkedAt);
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    public async Task DetachLinkAsync(long linkId, DateTimeOffset at, string actor, string reason, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE engineering.document_link SET detached_at = $2, detached_by = $3, detach_reason = $4 WHERE link_id = $1 AND detached_at IS NULL",
            linkId, at, actor, reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(CancellationToken ct)
    {
        var documents = await Read(DocumentSql + " ORDER BY number", ReadDocument, ct);
        var revisions = (await Read(RevisionSql + " ORDER BY r.document_number, r.authored_at", ReadRevision, ct)).ToLookup(r => r.DocumentNumber);
        return documents.Select(d => d with { Revisions = revisions[d.Number].ToList() }).ToList();
    }

    public async Task<DocumentInfo?> FindDocumentAsync(string number, CancellationToken ct)
    {
        var document = (await Read(DocumentSql + " WHERE number = $1", ReadDocument, ct, number)).SingleOrDefault();
        return document is null
            ? null
            : document with { Revisions = await Read(RevisionSql + " WHERE r.document_number = $1 ORDER BY r.authored_at", ReadRevision, ct, number) };
    }

    public Task<IReadOnlyList<DocumentLinkInfo>> ListLinksAsync(DocumentTargetType? targetType, string? targetId, string? documentNumber, bool includeDetached, CancellationToken ct) =>
        Read(
            LinkSql + " WHERE ($1::text IS NULL OR target_type = $1) AND ($2::text IS NULL OR target_id = $2) AND ($3::text IS NULL OR document_number = $3) AND ($4 OR detached_at IS NULL) ORDER BY linked_at, link_id",
            ReadLink,
            ct,
            Text(targetType?.ToString()),
            Text(targetId),
            Text(documentNumber),
            includeDetached);

    public async Task<DocumentLinkInfo?> FindLinkAsync(long linkId, CancellationToken ct) =>
        (await Read(LinkSql + " WHERE link_id = $1", ReadLink, ct, linkId)).SingleOrDefault();

    public async Task<DocumentFileInfo?> FindFileAsync(string sha256, CancellationToken ct) =>
        (await Read(
            "SELECT sha256, size_bytes, stored_by, stored_at FROM engineering.document_file WHERE sha256 = $1",
            r => new DocumentFileInfo(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)),
            ct,
            sha256)).SingleOrDefault();

    public async Task<byte[]?> ReadFileAsync(string sha256, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT content FROM engineering.document_file WHERE sha256 = $1", sha256);
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }

    private static DocumentInfo ReadDocument(NpgsqlDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        Enum.Parse<DocumentType>(r.GetString(2)),
        Enum.Parse<DocumentAuthority>(r.GetString(3)),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetGuid(6),
        (DataMarking)r.GetInt32(7),
        r.GetString(8),
        r.GetFieldValue<DateTimeOffset>(9),
        []);

    private static DocumentRevisionInfo ReadRevision(NpgsqlDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        Enum.Parse<DocumentRevisionStatus>(r.GetString(2)),
        Opt(r, 3),
        Opt(r, 4),
        r.IsDBNull(5) ? null : r.GetInt64(5),
        Opt(r, 6),
        r.IsDBNull(7) ? null : r.GetFieldValue<DateOnly>(7),
        r.GetString(8),
        r.GetFieldValue<DateTimeOffset>(9),
        Opt(r, 10),
        OptTime(r, 11),
        Opt(r, 12),
        OptTime(r, 13),
        Opt(r, 14),
        OptTime(r, 15),
        r.IsDBNull(16) ? null : r.GetFieldValue<DateOnly>(16),
        r.IsDBNull(17) ? null : r.GetFieldValue<DateOnly>(17),
        Opt(r, 18),
        OptTime(r, 19));

    private static DocumentLinkInfo ReadLink(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        Opt(r, 2),
        Enum.Parse<DocumentTargetType>(r.GetString(3)),
        r.GetString(4),
        r.GetString(5),
        r.IsDBNull(6) ? null : r.GetGuid(6),
        Enum.Parse<DocumentPurpose>(r.GetString(7)),
        Opt(r, 8),
        r.GetString(9),
        r.GetFieldValue<DateTimeOffset>(10),
        Opt(r, 11),
        OptTime(r, 12),
        Opt(r, 13));

    private static string? Opt(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? OptTime(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Uuid(Guid? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid };

    private static NpgsqlParameter Date(DateOnly? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date };

    private static NpgsqlParameter Time(DateTimeOffset? value) =>
        new() { Value = (object?)value?.ToUniversalTime() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    private async Task<IReadOnlyList<T>> Read<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
    {
        var list = new List<T>();
        await using var cmd = uow.Command(sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(map(reader));
        }

        return list;
    }
}
