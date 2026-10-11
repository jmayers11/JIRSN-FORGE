using Forge.Engineering.Contracts;
using Forge.Kernel.Postgres;

namespace Forge.Engineering.Infrastructure;

internal sealed class PostgresEvidenceStore(PostgresUnitOfWork uow) : IEvidenceLookup, IEvidenceStore
{
    public async Task<EvidenceVerification?> FindVerificationAsync(string documentNumber, string revision, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            SELECT verification_id, document_number, revision, file_sha256, source_signer, source_signed_at, meaning, verified_by, verified_at
            FROM engineering.evidence_verification WHERE document_number=$1 AND revision=$2
            """, documentNumber, revision);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new EvidenceVerification(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6), reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8)) : null;
    }

    public async Task<long> InsertVerificationAsync(EvidenceVerification verification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(verification);
        await using var cmd = uow.Command("""
            INSERT INTO engineering.evidence_verification (document_number, revision, file_sha256, source_signer, source_signed_at, meaning, verified_by, verified_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8) ON CONFLICT (document_number, revision) DO NOTHING RETURNING verification_id
            """, verification.DocumentNumber, verification.Revision, verification.FileSha256, verification.SourceSigner, verification.SourceSignedAt,
            verification.Meaning, verification.VerifiedBy, verification.VerifiedAt);
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : (await FindVerificationAsync(verification.DocumentNumber, verification.Revision, ct))!.VerificationId;
    }
}
