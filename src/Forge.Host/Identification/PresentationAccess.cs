using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.WorkAssignments;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Host.Identification;

public sealed class PresentationAccess(IdentificationResolver subjects, IForgeAuthorizer authorizer,
    IWorkSourceResolver sources, IQmsPolicyReader policies, IConfiguration configuration)
{
    public string Namespace
    {
        get
        {
            try { return CanonicalIdentification.Parse("forge:v1:" + configuration[CanonicalIdentification.NamespaceConfigurationKey] + ":order:1",
                configuration[CanonicalIdentification.NamespaceConfigurationKey]).Namespace; }
            catch (IdentificationException error) { throw new CommandRejectedException(error.Code, error.Message); }
        }
    }

    public Task<IdentificationSubject> SubjectAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string code, CancellationToken ct) =>
        subjects.ResolveAuthoritySubjectAsync(uow, principal, code, ct);
    public Task ActiveSubjectAsync(IKernelUnitOfWork uow, IdentificationSubject subject, CancellationToken ct) => subjects.RequireActivePrimitiveAsync(uow,subject,ct);

    public void Permit(ForgePrincipal principal, string permission, ResourceRef resource)
    {
        var decision = authorizer.Authorize(principal, permission, resource);
        if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision, resource);
    }

    public async Task CurrentSourcesAsync(IKernelUnitOfWork uow, ForgePrincipal principal, IReadOnlyList<PresentationReadSource> recorded, CancellationToken ct)
    {
        principal = await uow.People.LoadPrincipalAsync(principal.PersonId, ct) ?? throw Missing();
        foreach (var source in recorded)
        {
            Permit(principal, source.Permission, source.Resource);
            if (source.Resource.Type is "Lot" or "Person" or "TransformationOutput") continue; // CapturedSubjectAsync point-resolves these owner facts separately.
            if (source.Resource.Id is not { } id) throw Missing();
            var current = await sources.ResolveAsync(uow, source.Resource.Type, id, ct) ?? throw Missing();
            if (current.Resources.Count == 0) throw Missing();
            foreach (var resource in current.Resources) Permit(principal, current.ReadPermission, resource);
        }
    }

    public async Task<QmsAdoptedPolicy> PolicyAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        IdentificationSubject subject, string facilityCode, string permission, DateTimeOffset now, CancellationToken ct,
        string owningType = "LabelCopy", string? owningId = null)
    {
        _ = Namespace;
        principal = await uow.People.LoadPrincipalAsync(principal.PersonId, ct) ?? throw Missing();
        var facility = BusinessCode.Require(facilityCode, "Facility");
        if (!(await uow.Store<IResourcesLookup>().ListSitesAsync(ct)).Any(site => site.Code == facility)) throw Missing();
        var programs = subject.Resources.Select(resource => resource.Program?.Value).Where(program => program is not null).Distinct().ToArray();
        if (programs.Length > 1) throw new CommandRejectedException("label_policy_context", "The source requires an explicitly resolved complete policy context.");
        foreach (var resource in subject.Resources)
        {
            if (resource.FacilityId is { } sourceFacility && sourceFacility != facility)
                throw new CommandRejectedException("label_facility_mismatch", "The selected facility does not match the current source.");
            Permit(principal, permission, OwnResource(resource, facility, owningType, owningId));
        }
        var context = new QmsPolicyContext(facility, programs.SingleOrDefault(),
            subject.Resources.Select(resource => resource.WorkCenterId).FirstOrDefault(value => value is not null),
            ActivityCode: subject.ActivityCode, MachineCode: subject.MachineCode,
            OperationSequence: subject.Display.Sequence, PartNumber: subject.Display.PartNumber,
            PartRevision: subject.Display.Revision, SubjectKind: subject.PresentedKind ?? subject.Display.Kind);
        return await policies.RequireAsync(uow, principal, QmsPolicyPurpose.LabelCopy, context, now, ct);
    }

    public async Task<IReadOnlyList<PresentationReadSource>> DocumentsAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        IReadOnlyList<QmsDocumentReference> documents, DateTimeOffset now, bool requireEffective, CancellationToken ct)
    {
        principal = await uow.People.LoadPrincipalAsync(principal.PersonId, ct) ?? throw Missing();
        if (documents is null || documents.Count is < 1 or > 20) throw new CommandRejectedException("label_template_documents", "Reference bounded exact controlled documents for the template.");
        var result = new List<PresentationReadSource>();
        foreach (var reference in documents)
        {
            var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(reference.DocumentNumber, ct) ?? throw Missing();
            var revision = document.Revisions.FirstOrDefault(row => row.Revision == reference.Revision) ?? throw Missing();
            var resource = new ResourceRef("DocumentRevision", reference.DocumentNumber + "/" + reference.Revision,
                document.ProgramId is { } program ? new ProgramId(program) : null, document.Markings);
            Permit(principal, Permissions.EngineeringRead, resource);
            var day = DateOnly.FromDateTime(now.UtcDateTime);
            if (requireEffective && (document.Authority == DocumentAuthority.Reference
                || revision.Status is not (DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable)
                || revision.EffectiveFrom is null || revision.EffectiveFrom > day || revision.EffectiveTo is { } end && end <= day))
                throw new CommandRejectedException("label_template_document_effectivity", "The exact template document must be controlled and effective now.");
            result.Add(new(Permissions.EngineeringRead, resource));
        }
        return result;
    }

    public async Task<IdentificationSubject> CapturedSubjectAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        LabelSubjectSnapshot snapshot, IReadOnlyList<PresentationReadSource> recordedSources, CancellationToken ct)
    {
        principal = await uow.People.LoadPrincipalAsync(principal.PersonId, ct) ?? throw Missing();
        foreach (var resource in snapshot.Resources) Permit(principal, snapshot.ReadPermission, resource);
        var current = await SubjectAsync(uow, principal, snapshot.PresentationCode ?? snapshot.CanonicalSubjectCode, ct);
        await CurrentSourcesAsync(uow, principal, recordedSources, ct);
        return current;
    }

    public static LabelSubjectSnapshot Snapshot(IdentificationSubject source) => new(source.Display, source.CanonicalSubjectCode, source.ReadPermission, source.Resources,
        source.PresentationCode ?? source.Display.Code, source.PresentedKind ?? source.Display.Kind);
    public static IReadOnlyList<PresentationReadSource> Capture(QmsAdoptedPolicy policy, IReadOnlyList<PresentationReadSource> templateSources) =>
        policy.Sources.Select(source => new PresentationReadSource(source.Permission, source.Resource)).Concat(templateSources).Distinct().ToArray();
    public static ResourceRef OwnResource(ResourceRef source, string facility, string type, string? id) =>
        source with { Type = type, Id = id, FacilityId = facility };
    public static CommandAuthority Authority(IdentificationSubject subject, string facility, string? type = null, string? id = null)
    {
        var owned = subject.Resources.Select(resource => OwnResource(resource,facility,type ?? "LabelCopy",id)).Distinct().ToArray();
        return new(owned[0], AdditionalResources: owned.Skip(1).ToArray());
    }
    public static string Key(Guid id) => id.ToString("D");
    public static string TemplateKey(string code, int revision) => code + "/" + revision.ToString(CultureInfo.InvariantCulture);
    public static CommandRejectedException Missing() => new("presentation_not_found", "The presentation or its source is not accessible.");
}
