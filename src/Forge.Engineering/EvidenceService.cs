using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Engineering;

public sealed record VerifySignedEvidence(string DocumentNumber, string Revision, string SourceSigner, DateTimeOffset SourceSignedAt, string Meaning);

public sealed record SignedEvidenceVerified(long VerificationId, string DocumentNumber, string Revision) : IDomainEvent
{
    public static string EventType => nameof(SignedEvidenceVerified);
    public static int SchemaVersion => 1;
}

public sealed class EvidenceService(CommandPipeline pipeline)
{
    public Task<CommandResult> VerifyAsync(VerifySignedEvidence c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var spec = new CommandSpec("Engineering", nameof(VerifySignedEvidence), Permissions.QualityInspect,
            ResourceRef.Of("EvidenceVerification", c.DocumentNumber), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var number = BusinessCode.Require(c.DocumentNumber, "Document");
            var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(number, ct);
            if (document is null || !DocumentAccess.CanSee(context.Principal, document))
                throw new CommandRejectedException("document_not_found", "The evidence document does not exist.");
            var revision = document.Revisions.FirstOrDefault(r => r.Revision == c.Revision?.Trim().ToUpperInvariant());
            if (document.Authority != DocumentAuthority.Reference || revision?.Status != DocumentRevisionStatus.Uncontrolled || revision.FileSha256 is null)
                throw new CommandRejectedException("reference_file_required", "Verify a reference attachment with its original file; engineering release is a different action.");
            var signer = (c.SourceSigner ?? string.Empty).Trim();
            var meaning = (c.Meaning ?? string.Empty).Trim();
            if (signer.Length is < 1 or > 200 || meaning.Length is < 1 or > 1000 || c.SourceSignedAt == default || c.SourceSignedAt > now)
                throw new CommandRejectedException("signature_evidence_required", "Record the source signer's identity, signature date and meaning after reviewing the original signed file.");
            if (await uow.Store<IEvidenceLookup>().FindVerificationAsync(number, revision.Revision, ct) is { } prior)
                return CommandOutcome.NoChange(CommandResult.NoChange(prior.VerificationId.ToString(CultureInfo.InvariantCulture), "This exact file revision already has verification evidence."));
            var verification = new EvidenceVerification(0, number, revision.Revision, revision.FileSha256, signer, c.SourceSignedAt, meaning, context.Actor, now);
            var id = await uow.Store<IEvidenceStore>().InsertVerificationAsync(verification, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            var program = document.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
            return new CommandOutcome(CommandResult.Ok(key, "Source signature verification recorded; structured inspection results remain required."),
                [pipeline.Event(new SignedEvidenceVerified(id, number, revision.Revision), context, "Engineering", "EvidenceVerification", key, program, document.Markings)],
                new AuditChange("EvidenceVerification", key, null, CanonicalJson.Serialize(verification with { VerificationId = id }), program));
        }, ct);
    }
}
