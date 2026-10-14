using Forge.Kernel.Security;

namespace Forge.Host.Identification;

public sealed partial class LabelService
{
    public async Task<object> ConfigurationAsync(ForgePrincipal principal, string subjectCode, string facilityCode, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var subject = await access.SubjectAsync(uow, principal, subjectCode, ct);
        var policy = await access.PolicyAsync(uow, principal, subject, facilityCode, Permissions.IdentificationRead, pipeline.Time.GetUtcNow(), ct);
        var rules = Rules(policy); var life = Lifecycle(policy);
        return new
        {
            @namespace = access.Namespace, geometryUnit = "pixels", supportedSymbologies = new[] { "Code128", "QR", "DataMatrix" },
            printerMode = "BrowserObserved", trustedHardware = false,
            policy = new { code = policy.PolicyCode, revision = policy.PolicyRevision, fingerprint = policy.Fingerprint,
                rules.RequireCopyAuthorization, life.RequireIndependentCopyAuthorization, life.ActiveCopyMode, life.MaximumActiveCopies,
                life.RequiredConfirmation, life.AllowedTemplates, life.AllowedSymbologies, life.AllowedSubjectKinds,
                life.AllowReprint, life.AllowReplacement, life.AllowVoid, life.AllowAliasCreate, life.AllowAliasReplacement, life.AllowAliasVoid }
        };
    }
}
