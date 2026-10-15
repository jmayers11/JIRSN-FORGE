using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Planning;
using Forge.Quality.Qms;
using static Forge.Quality.Review.QmsReviewService;

namespace Forge.Quality.Review;

public sealed record CreateMappingEvidenceAssessment(Guid Id, string MappingCode, int MappingRevision,
    QmsEvidenceAdequacy Adequacy, QmsConformityAssessment Assessment, string Interpretation,
    string AssessmentMethod, string ApplicabilityBasis, string Gap, string AuditorNotes, string Limitations,
    QmsAuditorValidation AuditorValidation, IReadOnlyList<QmsPlanningEvidenceReference> Evidence);
public sealed record TransitionMappingEvidenceAssessment(Guid Id, int ExpectedVersion, string Action, string Note,
    QmsAuditorValidation AuditorValidation);
public sealed record CreateEditionMappingAssessment(Guid Id, string FacilityCode, Guid? ProgramId, DataMarking Markings,
    string Standard, string FromEdition, string ToEdition, IReadOnlyList<QmsStandardReference> FromCriteria,
    IReadOnlyList<QmsStandardReference> ToCriteria, QmsEditionRelationship Relationship,
    string Basis, string Changes, string ApplicabilityImpact, string RequiredActions,
    QmsAuditorValidation AuditorValidation, IReadOnlyList<QmsPlanningEvidenceReference> Evidence);
public sealed record TransitionEditionMappingAssessment(Guid Id, int ExpectedVersion, string Action, string Note,
    QmsAuditorValidation AuditorValidation);

/// <summary>Evidence linkage, human adequacy, qualified conformity judgment and licensed verification remain separate facts.</summary>
public sealed class EvidenceMappingService(CommandPipeline pipeline, IKernelUnitOfWorkFactory factory,
    QmsReviewService definitions, ManagementReviewService reviews, QmsPlanningService planning,
    IAuditorCompetenceValidator competence, QmsOptions options)
{
    public async Task<QmsReviewPage<QmsMappingEvidenceAssessment>> ListMappingsAsync(ForgePrincipal principal, string? code, int take, string? after, CancellationToken ct)
    {
        Require(take is > 0 and <= 100, "review_page", "Choose a page size from 1 to 100.");
        if (code is not null) code = BusinessCode.Require(code, "Evidence mapping");
        await using var uow = await factory.BeginSnapshotAsync(ct);
        var result = new List<QmsMappingEvidenceAssessment>(); var cursor = after;
        while (true)
        {
            var page = await uow.Store<IQmsReviewLookup>().MappingAssessmentsAsync(code, 100, cursor, ct);
            foreach (var assessment in page.Items)
            {
                try { await MappingAuthorityAsync(uow, principal, assessment, ct); }
                catch (ForgeAuthorizationException) { continue; }
                catch (CommandRejectedException) { continue; }
                if (result.Count == take) return new(result, result[^1].Id.ToString("D"));
                result.Add(assessment);
            }
            if (page.NextCursor is null) return new(result, null);
            cursor = page.NextCursor;
        }
    }

    public async Task<QmsReviewPage<QmsEditionMappingAssessment>> ListEditionsAsync(ForgePrincipal principal, int take, string? after, CancellationToken ct)
    {
        Require(take is > 0 and <= 100, "review_page", "Choose a page size from 1 to 100.");
        await using var uow = await factory.BeginSnapshotAsync(ct);
        var result = new List<QmsEditionMappingAssessment>(); var cursor = after;
        while (true)
        {
            var page = await uow.Store<IQmsReviewLookup>().EditionAssessmentsAsync(100, cursor, ct);
            foreach (var assessment in page.Items)
            {
                try { await EditionAuthorityAsync(uow, principal, assessment, ct); }
                catch (ForgeAuthorizationException) { continue; }
                catch (CommandRejectedException) { continue; }
                if (result.Count == take) return new(result, result[^1].Id.ToString("D"));
                result.Add(assessment);
            }
            if (page.NextCursor is null) return new(result, null);
            cursor = page.NextCursor;
        }
    }

    public async Task<QmsMappingEvidenceAssessment> ReadMappingAsync(ForgePrincipal principal, Guid id, CancellationToken ct)
    {
        await using var uow = await factory.BeginSnapshotAsync(ct);
        var assessment = await MappingAsync(uow, id, ct); await MappingAuthorityAsync(uow, principal, assessment, ct);
        return assessment;
    }
    public async Task<QmsEditionMappingAssessment> ReadEditionAsync(ForgePrincipal principal, Guid id, CancellationToken ct)
    {
        await using var uow = await factory.BeginSnapshotAsync(ct);
        var assessment = await EditionAsync(uow, id, ct); await EditionAuthorityAsync(uow, principal, assessment, ct);
        return assessment;
    }
    public Task<CommandResult> CreateMappingAsync(CreateMappingEvidenceAssessment c, CommandContext context, CancellationToken ct) =>
        definitions.ExecuteAsync(nameof(CreateMappingEvidenceAssessment), c, c.MappingCode, Permissions.QmsManage, context,
            async (uow, definition, now) =>
            {
                Require(c.Id != Guid.Empty && await uow.Store<IQmsReviewLookup>().FindMappingAssessmentAsync(c.Id, ct) is null,
                    "mapping_assessment_exists", "Use a new opaque assessment identity.");
                var revision = Revision(definition, c.MappingRevision); Effective(revision, now);
                Require(definition.Kind == QmsReviewDefinitionKind.StandardEvidenceMapping, "mapping_definition_kind", "Select an actual evidence mapping definition.");
                var sources = await reviews.ResolveAsync(uow, context.Principal, c.Evidence, ct);
                var validation = await ValidateMappingAuditorAsync(uow, context, c.AuditorValidation, revision, now, ct);
                var pending = revision.RequiresLicensedVerification || sources.Any(source => source.RequiresLicensedVerification);
                QmsReviewRules.ValidateMappingAssessment(c.Adequacy, c.Assessment, pending, c.Interpretation, c.AssessmentMethod, c.ApplicabilityBasis, c.Gap, c.AuditorNotes, c.Limitations);
                Require(c.Assessment != QmsConformityAssessment.Supported || sources.Count != 0,
                    "mapping_actual_evidence_required", "An adequate supported assessment needs actual retained owner evidence.");
                var assessment = new QmsMappingEvidenceAssessment(c.Id, definition.Code, c.MappingRevision, 1, QmsAssessmentState.Draft,
                    c.Adequacy, c.Assessment, c.Interpretation, c.AssessmentMethod, c.ApplicabilityBasis, c.Gap, c.AuditorNotes, c.Limitations,
                    validation.Query, validation.Result, [validation.Result], sources, context.Actor, now, null, null, null,
                    Append([], "Create", 1, "", "Draft", "Qualified human assessment proposal; source linkage is separate", context, now));
                await uow.Store<IQmsReviewStore>().SaveMappingAssessmentAsync(assessment, null, ct);
                return definitions.Outcome(definition, "QmsMappingAssessment", c.Id.ToString("D"), 1, "Create", "Draft", null, assessment, context, MappingSources(assessment, revision));
            }, ct, async (uow, definition, now) =>
            {
                var revision = Revision(definition, c.MappingRevision); Effective(revision, now);
                await reviews.ResolveAsync(uow, context.Principal, c.Evidence, ct);
                await ValidateMappingAuditorAsync(uow, context, c.AuditorValidation, revision, now, ct);
            });
    public Task<CommandResult> TransitionMappingAsync(TransitionMappingEvidenceAssessment c, CommandContext context, CancellationToken ct) =>
        pipeline.ExecuteAsync(new("Quality", "MappingAssessment" + c.Action, Permission(c.Action), ResourceRef.Of("QmsMappingAssessment", c.Id.ToString("D")), true), c, context,
            async (uow, now) =>
            {
                var old = await MappingAsync(uow, c.Id, ct); Version(old.Version, c.ExpectedVersion); Text(c.Note);
                var definition = await definitions.DefinitionAsync(uow, old.MappingCode, ct); var revision = Revision(definition, old.MappingRevision);
                var validation = await ValidateMappingAuditorAsync(uow, context, c.AuditorValidation, revision, now, ct);
                var state = Transition(old.State, c.Action);
                if (c.Action is "Review" or "Approve") QmsReviewRules.Independent(context.Actor,
                    new[] { old.CreatedBy }.Concat(old.Evidence.SelectMany(source => source.Contributors)));
                QmsReviewRules.ValidateMappingAssessment(old.Adequacy, old.Assessment, revision.RequiresLicensedVerification
                    || old.Evidence.Any(source => source.RequiresLicensedVerification), old.Interpretation, old.AssessmentMethod,
                    old.ApplicabilityBasis, old.Gap, old.AuditorNotes, old.Limitations);
                var next = old with { Version = old.Version + 1, State = state,
                    ReviewedBy = c.Action == "Review" ? context.Actor : old.ReviewedBy,
                    ApprovedBy = c.Action == "Approve" ? context.Actor : old.ApprovedBy,
                    ApprovedAt = c.Action == "Approve" ? now : old.ApprovedAt,
                    Competence = validation.Result, CompetenceHistory = old.CompetenceHistory.Append(validation.Result).ToArray(),
                    History = Append(old.History, c.Action, old.Version + 1, old.State.ToString(), state.ToString(), c.Note, context, now) };
                await uow.Store<IQmsReviewStore>().SaveMappingAssessmentAsync(next, old.Version, ct);
                return definitions.Outcome(definition, "QmsMappingAssessment", c.Id.ToString("D"), next.Version, c.Action, state.ToString(), old, next, context, MappingSources(next, revision));
            }, ct, async (uow, now) =>
            {
                var preliminary = await MappingAsync(uow, c.Id, ct);
                await uow.Store<IQmsReviewStore>().LockAsync(preliminary.MappingCode, ct);
                await uow.Store<IQmsReviewStore>().LockAsync("MappingAssessment:" + c.Id.ToString("D"), ct);
                var old = await MappingAsync(uow, c.Id, ct);
                var definition = await definitions.DefinitionAsync(uow, old.MappingCode, ct); var revision = Revision(definition, old.MappingRevision);
                if (c.Action != "Withdraw") Effective(revision, now);
                await ValidateMappingAuditorAsync(uow, context, c.AuditorValidation, revision, now, ct);
                return await MappingAuthorityAsync(uow, context.Principal, old, ct, true);
            });
    public Task<CommandResult> CreateEditionAsync(CreateEditionMappingAssessment c, CommandContext context, CancellationToken ct)
    {
        var facility = BusinessCode.Require(c.FacilityCode, "Facility");
        var resource = new ResourceRef("QmsEditionAssessment", c.Id.ToString("D"), QmsPlanningService.Program(c.ProgramId), c.Markings, FacilityId: facility);
        return pipeline.ExecuteAsync(new("Quality", nameof(CreateEditionMappingAssessment), Permissions.QmsManage, resource, true), c, context,
            async (uow, now) =>
            {
                Require(c.Id != Guid.Empty && await uow.Store<IQmsReviewLookup>().FindEditionAssessmentAsync(c.Id, ct) is null,
                    "edition_assessment_exists", "Use a new opaque assessment identity.");
                await planning.HeaderAsync(uow, facility, c.ProgramId, c.Markings, ct);
                var sources = await EditionSourcesAsync(uow, context.Principal, c.FromCriteria, c.ToCriteria, c.Evidence, ct);
                var query = CurrentAuditor(context, c.AuditorValidation, now) with { RequiredStandardReferences = Documents(c.FromCriteria.Concat(c.ToCriteria)) };
                var qualified = await competence.ValidateAsync(uow, context.Principal, query, true, ct);
                var pending = c.FromCriteria.Concat(c.ToCriteria).Any(criterion => criterion.Verification != QmsStandardVerification.VerifiedAuthorizedCopy)
                    || sources.Any(source => source.RequiresLicensedVerification);
                var assessment = new QmsEditionMappingAssessment(c.Id, 1, options.OrganizationCode!, facility, c.ProgramId, c.Markings,
                    QmsAssessmentState.Draft, c.Standard, c.FromEdition, c.ToEdition, c.FromCriteria, c.ToCriteria, c.Relationship,
                    c.Basis, c.Changes, c.ApplicabilityImpact, c.RequiredActions, pending, query, qualified, [qualified], sources, context.Actor, now, null, null, null,
                    Append([], "Create", 1, "", "Draft", "Qualified human many-to-many mapping proposal", context, now));
                QmsReviewRules.Edition(assessment); await uow.Store<IQmsReviewStore>().SaveEditionAssessmentAsync(assessment, null, ct);
                return EditionOutcome(assessment, "Create", null, assessment, context);
            }, ct, async (uow, now) =>
            {
                await uow.Store<IQmsReviewStore>().LockAsync("EditionAssessment:" + c.Id.ToString("D"), ct);
                await planning.HeaderAsync(uow, facility, c.ProgramId, c.Markings, ct);
                var sources = await EditionSourcesAsync(uow, context.Principal, c.FromCriteria, c.ToCriteria, c.Evidence, ct);
                var query = CurrentAuditor(context, c.AuditorValidation, now) with { RequiredStandardReferences = Documents(c.FromCriteria.Concat(c.ToCriteria)) };
                await competence.ValidateAsync(uow, context.Principal, query, true, ct);
                if (await uow.Store<IQmsReviewLookup>().FindEditionAssessmentAsync(c.Id, ct) is { } existing)
                    return await EditionAuthorityAsync(uow, context.Principal, existing, ct, true);
                return new CommandAuthority(resource with { Markings = QmsPlanningService.Marks(c.Markings, sources) }, options.OrganizationCode + ":" + c.Id.ToString("D"));
            });
    }
    public Task<CommandResult> TransitionEditionAsync(TransitionEditionMappingAssessment c, CommandContext context, CancellationToken ct) =>
        pipeline.ExecuteAsync(new("Quality", "EditionAssessment" + c.Action, Permission(c.Action), ResourceRef.Of("QmsEditionAssessment", c.Id.ToString("D")), true), c, context,
            async (uow, now) =>
            {
                var old = await EditionAsync(uow, c.Id, ct); Version(old.Version, c.ExpectedVersion); Text(c.Note);
                var query = CurrentAuditor(context, c.AuditorValidation, now) with { RequiredStandardReferences = Documents(old.FromCriteria.Concat(old.ToCriteria)) };
                var qualified = await competence.ValidateAsync(uow, context.Principal, query, true, ct);
                if (c.Action is "Review" or "Approve") QmsReviewRules.Independent(context.Actor,
                    new[] { old.CreatedBy }.Concat(old.Evidence.SelectMany(source => source.Contributors)));
                var state = Transition(old.State, c.Action);
                var next = old with { Version = old.Version + 1, State = state, Competence = qualified,
                    CompetenceHistory = old.CompetenceHistory.Append(qualified).ToArray(),
                    ReviewedBy = c.Action == "Review" ? context.Actor : old.ReviewedBy,
                    ApprovedBy = c.Action == "Approve" ? context.Actor : old.ApprovedBy, ApprovedAt = c.Action == "Approve" ? now : old.ApprovedAt,
                    History = Append(old.History, c.Action, old.Version + 1, old.State.ToString(), state.ToString(), c.Note, context, now) };
                await uow.Store<IQmsReviewStore>().SaveEditionAssessmentAsync(next, old.Version, ct);
                return EditionOutcome(next, c.Action, old, next, context);
            }, ct, async (uow, now) =>
            {
                await uow.Store<IQmsReviewStore>().LockAsync("EditionAssessment:" + c.Id.ToString("D"), ct);
                var old = await EditionAsync(uow, c.Id, ct);
                var query = CurrentAuditor(context, c.AuditorValidation, now) with { RequiredStandardReferences = Documents(old.FromCriteria.Concat(old.ToCriteria)) };
                await competence.ValidateAsync(uow, context.Principal, query, true, ct);
                return await EditionAuthorityAsync(uow, context.Principal, old, ct, true);
            });
    public async Task<CommandAuthority> MappingAuthorityAsync(IKernelUnitOfWork uow, ForgePrincipal principal, QmsMappingEvidenceAssessment assessment, CancellationToken ct, bool lockSources = false)
    {
        var definition = await definitions.DefinitionAsync(uow, assessment.MappingCode, ct); var revision = Revision(definition, assessment.MappingRevision);
        var authority = await definitions.AuthorityAsync(uow, principal, definition, ct, lockSources);
        await planning.CurrentEvidenceAsync(uow, principal, MappingSources(assessment, revision), false, ct, lockSources);
        var resource = authority.Resource with { Type = "QmsMappingAssessment", Id = assessment.Id.ToString("D"), Markings = QmsPlanningService.Marks(authority.Resource.Markings, MappingSources(assessment, revision)) };
        definitions.Permit(principal, Permissions.QmsRead, resource); return authority with { Resource = resource };
    }
    public async Task<CommandAuthority> EditionAuthorityAsync(IKernelUnitOfWork uow, ForgePrincipal principal, QmsEditionMappingAssessment assessment, CancellationToken ct, bool lockSources = false)
    {
        Require(options.Configured && assessment.OrganizationCode == options.OrganizationCode, "review_not_found", "The assessment is unavailable.");
        var resource = new ResourceRef("QmsEditionAssessment", assessment.Id.ToString("D"), QmsPlanningService.Program(assessment.ProgramId),
            QmsPlanningService.Marks(assessment.Markings, assessment.Evidence.Concat(assessment.CompetenceHistory.SelectMany(item => item.Sources))), FacilityId: assessment.FacilityCode);
        definitions.Permit(principal, Permissions.QmsRead, resource);
        await planning.CurrentEvidenceAsync(uow, principal, assessment.Evidence.Concat(assessment.CompetenceHistory.SelectMany(item => item.Sources)), false, ct, lockSources);
        return new(resource, assessment.OrganizationCode + ":" + assessment.Id.ToString("D"));
    }
    private async Task<(QmsAuditorValidation Query, QmsAuditorValidationResult Result)> ValidateMappingAuditorAsync(IKernelUnitOfWork uow, CommandContext context, QmsAuditorValidation requested, QmsReviewDefinitionRevision revision, DateTimeOffset now, CancellationToken ct)
    {
        var mapping = revision.Content.Mapping ?? throw Unavailable();
        var query = CurrentAuditor(context, requested, now) with { RequiredCriteria = mapping.Requirements,
            RequiredStandardReferences = Documents(mapping.Criteria) };
        return (query, await competence.ValidateAsync(uow, context.Principal, query, true, ct));
    }
    private async Task<IReadOnlyList<QmsPlanningSourceSnapshot>> EditionSourcesAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        IReadOnlyList<QmsStandardReference> from, IReadOnlyList<QmsStandardReference> to, IReadOnlyList<QmsPlanningEvidenceReference> references, CancellationToken ct) =>
        await reviews.ResolveAsync(uow, principal, references.Concat(Documents(from.Concat(to)).Select(document =>
            new QmsPlanningEvidenceReference("Engineering", "DocumentRevision", document.DocumentNumber, document.Revision))).Distinct().ToArray(), ct);
    private CommandOutcome EditionOutcome(QmsEditionMappingAssessment assessment, string action, object? old, object next, CommandContext context) =>
        new(CommandResult.Ok(assessment.Id.ToString("D"), "Edition assessment: " + assessment.State + "."),
            [pipeline.Event(new QmsReviewChanged("QmsEditionAssessment", assessment.Id.ToString("D"), assessment.Version, action, assessment.State.ToString()), context, "Quality", "QmsEditionAssessment", assessment.Id.ToString("D"), QmsPlanningService.Program(assessment.ProgramId), QmsPlanningService.Marks(assessment.Markings, assessment.Evidence.Concat(assessment.Competence.Sources)))],
            new("QmsEditionAssessment", assessment.Id.ToString("D"), old is null ? null : Forge.Kernel.Serialization.CanonicalJson.Serialize(old), Forge.Kernel.Serialization.CanonicalJson.Serialize(next), QmsPlanningService.Program(assessment.ProgramId), QmsPlanningService.Marks(assessment.Markings, assessment.Evidence.Concat(assessment.Competence.Sources))));
    private static QmsAuditorValidation CurrentAuditor(CommandContext context, QmsAuditorValidation requested, DateTimeOffset now)
    {
        Require(requested is not null && requested.PersonId == context.Principal.PersonId.Value, "mapping_actual_auditor", "The actual authenticated assessor must hold current competence.");
        return requested with { AsOf = now };
    }
    private static IReadOnlyList<QmsDocumentReference> Documents(IEnumerable<QmsStandardReference> references) => references.Where(reference => reference.VerificationDocument is not null).Select(reference => reference.VerificationDocument!).Distinct().ToArray();
    private static IEnumerable<QmsPlanningSourceSnapshot> MappingSources(QmsMappingEvidenceAssessment assessment, QmsReviewDefinitionRevision revision) => revision.Sources.Concat(assessment.Evidence).Concat(assessment.CompetenceHistory.SelectMany(item => item.Sources));
    private static async Task<QmsMappingEvidenceAssessment> MappingAsync(IKernelUnitOfWork uow, Guid id, CancellationToken ct) => await uow.Store<IQmsReviewLookup>().FindMappingAssessmentAsync(id, ct) ?? throw Unavailable();
    private static async Task<QmsEditionMappingAssessment> EditionAsync(IKernelUnitOfWork uow, Guid id, CancellationToken ct) => await uow.Store<IQmsReviewLookup>().FindEditionAssessmentAsync(id, ct) ?? throw Unavailable();
    private static string Permission(string action) => action switch { "Submit" => Permissions.QmsManage, "Review" => Permissions.QmsReview, "Approve" => Permissions.QmsApprove, "Withdraw" => Permissions.QmsPublish, _ => throw new CommandRejectedException("mapping_action", "Unknown assessment action.") };
    private static QmsAssessmentState Transition(QmsAssessmentState state, string action) => (state, action) switch
    {
        (QmsAssessmentState.Draft, "Submit") => QmsAssessmentState.Submitted,
        (QmsAssessmentState.Submitted, "Review") => QmsAssessmentState.Reviewed,
        (QmsAssessmentState.Reviewed, "Approve") => QmsAssessmentState.Approved,
        (_, "Withdraw") when state != QmsAssessmentState.Withdrawn => QmsAssessmentState.Withdrawn,
        _ => throw new CommandRejectedException("mapping_transition", "The actual assessment is not in the required state.")
    };
}
