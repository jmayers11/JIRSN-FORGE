using Forge.Kernel.Access;

namespace Forge.Engineering.Contracts;

/// <summary>Recorded verification of a signed source document; does not authenticate the external signer or release production authority.</summary>
public sealed record EvidenceVerification(long VerificationId, string DocumentNumber, string Revision, string FileSha256,
    string SourceSigner, DateTimeOffset SourceSignedAt, string Meaning, string VerifiedBy, DateTimeOffset VerifiedAt);

public interface IEvidenceLookup
{
    Task<EvidenceVerification?> FindVerificationAsync(string documentNumber, string revision, CancellationToken ct);
}

public interface IEvidenceStore
{
    Task<long> InsertVerificationAsync(EvidenceVerification verification, CancellationToken ct);
}
