using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Planning;
using Forge.Quality.Review;
using Forge.Quality.Qms;
using static Forge.Quality.Planning.QmsPlanningRules;

namespace Forge.Host.Qms;

/// <summary>Resolves current review-owner access in the caller's unit of work without introducing an evidence-reader DI cycle.</summary>
public sealed class QmsReviewOwnerEvidenceResolver(IForgeAuthorizer authorizer, QmsOptions options, TimeProvider time) : IQmsPlanningOwnerEvidenceResolver
{
    private static readonly string[] Types = ["QmsReviewRevision", "QmsReviewRun", "QmsReviewAction", "QmsMappingAssessment", "QmsEditionAssessment"];
    public bool Owns(string context, string type) => context == "Quality" && Types.Contains(type, StringComparer.Ordinal);

    public async Task<QmsPlanningSourceSnapshot> ResolveAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        QmsPlanningEvidenceReference reference, bool lockSource,
        Func<QmsPlanningEvidenceReference, bool, Task<QmsPlanningSourceSnapshot>> resolveSource, CancellationToken ct)
    {
        Require(options.Configured, "review_not_found", "Configure the explicit organization before reading review evidence.");
        var lookup = uow.Store<IQmsReviewLookup>();
        if (lockSource)
        {
            var pg = uow as PostgresUnitOfWork ?? throw Missing();
            var (storageType, storageId) = Storage(reference);
            await using var row = pg.Command("SELECT entity_id FROM quality.review_head WHERE entity_type=$1 AND entity_id=$2 FOR SHARE", storageType, storageId);
            if (await row.ExecuteScalarAsync(ct) is null) throw Missing();
        }

        object content; QmsReviewDefinition definition; IReadOnlyList<QmsPlanningSourceSnapshot> saved;
        var authors = new List<string>(); DateTimeOffset? created = null, approved = null, closed = null;
        switch (reference.Type)
        {
            case "QmsReviewRevision":
            {
                var revisionNumber = Positive(reference.Revision, "review_revision");
                definition = await DefinitionAsync(lookup, reference.Key, ct);
                var revision = definition.Revisions.SingleOrDefault(item => item.Revision == revisionNumber) ?? throw Missing();
                content = revision; saved = revision.Sources;
                authors.AddRange([revision.CreatedBy, revision.LastEditedBy]);
                authors.AddRange(revision.History.Where(item => item.Action is "Create" or "Edit").Select(item => item.Actor));
                created = definition.CreatedAt; approved = revision.History.LastOrDefault(item => item.Action == "Approve")?.At;
                break;
            }
            case "QmsReviewRun":
            {
                var run = await lookup.FindRunAsync(Uuid(reference.Key), ct) ?? throw Missing();
                definition = await DefinitionAsync(lookup, run.ProgramCode, ct); content = run;
                saved = RunSources(run).ToArray();
                authors.Add(run.CreatedBy); authors.AddRange(run.History.Select(item => item.Actor));
                created = run.CreatedAt; approved = run.ApprovedAt; closed = run.State == QmsReviewRunState.Closed ? run.History.LastOrDefault(item => item.Action == "Close")?.At : null;
                break;
            }
            case "QmsReviewAction":
            {
                var action = await lookup.FindActionAsync(Uuid(reference.Key), ct) ?? throw Missing();
                var run = await lookup.FindRunAsync(action.ReviewId, ct) ?? throw Missing();
                definition = await DefinitionAsync(lookup, run.ProgramCode, ct); content = action;
                saved = RunSources(run).Concat(action.Evidence).DistinctBy(item => CanonicalJson.Fingerprint(item)).ToArray();
                authors.AddRange([action.CreatedBy, "person:" + action.OwnerPersonId, action.CompletedBy ?? "", action.VerifiedBy ?? ""]);
                authors.AddRange(action.History.Select(item => item.Actor)); created = action.CreatedAt; approved = action.VerifiedAt;
                closed = action.State == QmsReviewActionState.Verified ? action.VerifiedAt : null;
                break;
            }
            case "QmsMappingAssessment":
            {
                var assessment = await lookup.FindMappingAssessmentAsync(Uuid(reference.Key), ct) ?? throw Missing();
                definition = await DefinitionAsync(lookup, assessment.MappingCode, ct); content = assessment;
                var revision = definition.Revisions.SingleOrDefault(item => item.Revision == assessment.MappingRevision) ?? throw Missing();
                saved = revision.Sources.Concat(assessment.Evidence).Concat(assessment.CompetenceHistory.SelectMany(item => item.Sources)).ToArray();
                authors.Add(assessment.CreatedBy); authors.AddRange(assessment.History.Select(item => item.Actor));
                created = assessment.CreatedAt; approved = assessment.ApprovedAt;
                break;
            }
            case "QmsEditionAssessment":
            {
                var assessment = await lookup.FindEditionAssessmentAsync(Uuid(reference.Key), ct) ?? throw Missing();
                Require(assessment.OrganizationCode == options.OrganizationCode, "review_not_found", "The edition assessment is unavailable.");
                definition = null!; content = assessment;
                saved = assessment.Evidence.Concat(assessment.CompetenceHistory.SelectMany(item => item.Sources)).ToArray();
                authors.Add(assessment.CreatedBy); authors.AddRange(assessment.History.Select(item => item.Actor));
                created = assessment.CreatedAt; approved = assessment.ApprovedAt;
                break;
            }
            default: throw Missing();
        }

        ResourceRef resource;
        IEnumerable<QmsPlanningSourceSnapshot> scopeSources = saved;
        if (reference.Type == "QmsEditionAssessment")
        {
            var assessment = (QmsEditionMappingAssessment)content;
            resource = new("QmsEditionAssessment", assessment.Id.ToString("D"), QmsPlanningService.Program(assessment.ProgramId),
                Marks(assessment.Markings, saved), FacilityId: assessment.FacilityCode);
        }
        else
        {
            resource = QmsReviewService.Resource(definition) with { Type = reference.Type, Id = reference.Key + (reference.Type == "QmsReviewRevision" ? "/" + reference.Revision : ""),
                Markings = Marks(definition.Markings, saved) };
        }

        var sources = new List<QmsPlanningReadSource> { new(Permissions.QmsRead, resource) };
        foreach (var snapshot in scopeSources.DistinctBy(item => CanonicalJson.Serialize(item.Reference)))
        {
            foreach (var read in snapshot.Sources) Permit(principal, read);
            sources.AddRange(snapshot.Sources);
            var current = await resolveSource(snapshot.Reference, lockSource);
            sources.AddRange(current.Sources);
        }
        foreach (var source in sources.Distinct()) Permit(principal, source);
        var projected = new { content, sources = sources.Distinct().OrderBy(item => item.Permission, StringComparer.Ordinal)
            .ThenBy(item => item.Resource.Type, StringComparer.Ordinal).ThenBy(item => item.Resource.Id, StringComparer.Ordinal).ToArray() };
        return new(reference, "Quality", reference.Type, reference.Key, reference.Revision,
            CanonicalJson.Fingerprint(projected), time.GetUtcNow(), created, approved, closed, null,
            authors.Where(item => !string.IsNullOrEmpty(item)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            sources.Distinct().ToArray(), saved.Any(item => item.RequiresLicensedVerification), CanonicalJson.Serialize(projected));
    }

    private async Task<QmsReviewDefinition> DefinitionAsync(IQmsReviewLookup lookup, string code, CancellationToken ct)
    {
        Require(code == Forge.Kernel.Identity.BusinessCode.Require(code, "Review definition"), "review_not_found", "The review definition is unavailable.");
        var definition = await lookup.FindDefinitionAsync(code, ct) ?? throw Missing();
        Require(definition.OrganizationCode == options.OrganizationCode, "review_not_found", "The review definition is unavailable.");
        return definition;
    }
    private void Permit(ForgePrincipal principal, QmsPlanningReadSource source)
    {
        var result = authorizer.Authorize(principal, source.Permission, source.Resource);
        if (!result.IsPermitted) throw new ForgeAuthorizationException(result);
    }
    private static (string Type, string Id) Storage(QmsPlanningEvidenceReference reference) => reference.Type switch
    {
        "QmsReviewRevision" => ("Revision", reference.Key + "/" + Positive(reference.Revision, "review_revision").ToString(CultureInfo.InvariantCulture)),
        "QmsReviewRun" => ("Run", Uuid(reference.Key).ToString("D")),
        "QmsReviewAction" => ("Action", Uuid(reference.Key).ToString("D")),
        "QmsMappingAssessment" => ("MappingAssessment", Uuid(reference.Key).ToString("D")),
        "QmsEditionAssessment" => ("EditionAssessment", Uuid(reference.Key).ToString("D")),
        _ => throw Missing()
    };
    private static async Task LockAsync(PostgresUnitOfWork pg, string type, string id, CancellationToken ct)
    {
        await using var row = pg.Command("SELECT entity_id FROM quality.review_head WHERE entity_type=$1 AND entity_id=$2 FOR SHARE", type, id);
        if (await row.ExecuteScalarAsync(ct) is null) throw Missing();
    }
    private static int Positive(string? value, string code) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 && value == number.ToString(CultureInfo.InvariantCulture)
        ? number : throw new CommandRejectedException(code, "Use the exact positive review revision.");
    private static IEnumerable<QmsPlanningSourceSnapshot> RunSources(QmsManagementReviewRun run) =>
        (run.Manifest?.Inputs.SelectMany(input => input.Sources) ?? [])
        .Concat(run.Assessments.SelectMany(assessment => assessment.CapturedEvidence ?? []))
        .Concat(run.Decisions.SelectMany(decision => decision.CapturedSources ?? []));
    private static Forge.Kernel.Security.DataMarking Marks(Forge.Kernel.Security.DataMarking start, IEnumerable<QmsPlanningSourceSnapshot> sources) =>
        sources.SelectMany(item => item.Sources).Aggregate(start, (markings, source) => markings | source.Resource.Markings);
    private static Guid Uuid(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty && value == id.ToString("D")
        ? id : throw Missing();
    private static CommandRejectedException Missing() => new("review_not_found", "The current authorized review evidence is unavailable.");
}
