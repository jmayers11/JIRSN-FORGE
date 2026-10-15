using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Review;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record CreateReviewDefinitionRequest(string Code, QmsReviewDefinitionKind Kind, string FacilityCode,
    Guid? ProgramId, DataMarking Markings, QmsReviewDefinitionContent Content, string? Reason);
public sealed record ReviewDefinitionContentRequest(int ExpectedVersion, QmsReviewDefinitionContent Content, string? Reason);
public sealed record CreateManagementReviewRequest(Guid Id, string ProgramCode, int ProgramRevision,
    DateTimeOffset PeriodFrom, DateTimeOffset PeriodTo, string? Reason);
public sealed record ReviewVersionRequest(int ExpectedVersion, string? Reason);
public sealed record ReviewMeetingRequest(int ExpectedVersion, string MeetingRecord, IReadOnlyList<Guid> Participants,
    IReadOnlyList<QmsReviewInputAssessment> Assessments, IReadOnlyList<QmsManagementReviewDecision> Decisions,
    string Limitations, string? Reason);
public sealed record ReviewTransitionRequest(int ExpectedVersion, string Note, string? Reason);
public sealed record ReviewActionRequest(int ExpectedVersion, string Result, IReadOnlyList<QmsPlanningEvidenceReference> Evidence, string? Reason);
public sealed record MappingAssessmentRequest(Guid Id, string MappingCode, int MappingRevision,
    QmsEvidenceAdequacy Adequacy, QmsConformityAssessment Assessment, string Interpretation,
    string AssessmentMethod, string ApplicabilityBasis, string Gap, string AuditorNotes, string Limitations,
    QmsAuditorValidation AuditorValidation, IReadOnlyList<QmsPlanningEvidenceReference> Evidence, string? Reason);
public sealed record MappingTransitionRequest(int ExpectedVersion, string Note, QmsAuditorValidation AuditorValidation, string? Reason);
public sealed record EditionAssessmentRequest(Guid Id, string FacilityCode, Guid? ProgramId, DataMarking Markings,
    string Standard, string FromEdition, string ToEdition, IReadOnlyList<QmsStandardReference> FromCriteria,
    IReadOnlyList<QmsStandardReference> ToCriteria, QmsEditionRelationship Relationship, string Basis, string Changes,
    string ApplicabilityImpact, string RequiredActions, QmsAuditorValidation AuditorValidation,
    IReadOnlyList<QmsPlanningEvidenceReference> Evidence, string? Reason);

public static class QmsReviewEndpoints
{
    public static void MapQmsReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/quality/qms/review").RequireAuthorization();
        api.MapGet("/definitions", (HttpContext http, QmsReviewService service, QmsReviewDefinitionKind? kind, int? take, string? after, CancellationToken ct) =>
            Read(http, principal => service.ListAsync(principal, kind, take ?? 50, after, ct)));
        api.MapGet("/definitions/{code}", (HttpContext http, QmsReviewService service, string code, CancellationToken ct) =>
            Read(http, principal => service.ReadAsync(principal, code, ct)));
        api.MapPost("/definitions", (HttpContext http, QmsReviewService service, CreateReviewDefinitionRequest body, CancellationToken ct) =>
            Command(http, body.Reason, context => service.CreateAsync(new(body.Code, body.Kind, body.FacilityCode, body.ProgramId, body.Markings, body.Content), context, ct)));
        api.MapPost("/definitions/{code}/revisions", (HttpContext http, QmsReviewService service, string code, ReviewDefinitionContentRequest body, CancellationToken ct) =>
            Command(http, body.Reason, context => service.ReviseAsync(new(code, null, body.ExpectedVersion, body.Content), context, ct)));
        api.MapPost("/definitions/{code}/revisions/{revision:int}/edit", (HttpContext http, QmsReviewService service, string code, int revision, ReviewDefinitionContentRequest body, CancellationToken ct) =>
            Command(http, body.Reason, context => service.ReviseAsync(new(code, revision, body.ExpectedVersion, body.Content), context, ct)));
        foreach (var action in new[] { "Submit", "Review", "Approve", "Effective", "Withdraw" })
        {
            var captured = action;
            api.MapPost("/definitions/{code}/revisions/{revision:int}/" + action.ToLowerInvariant(),
                (HttpContext http, QmsReviewService service, string code, int revision, ReviewTransitionRequest body, CancellationToken ct) =>
                    Command(http, body.Reason, context => service.TransitionAsync(new(code, revision, body.ExpectedVersion, captured, body.Note), context, ct)));
        }
        api.MapPost("/runs", (HttpContext http, ManagementReviewService service, CreateManagementReviewRequest body, CancellationToken ct) =>
            Command(http, body.Reason, context => service.CreateAsync(new(body.Id, body.ProgramCode, body.ProgramRevision, body.PeriodFrom, body.PeriodTo), context, ct)));
        api.MapGet("/runs", (HttpContext http, ManagementReviewService service, string? programCode, int? take, string? after, CancellationToken ct) =>
            Read(http, principal => service.ListAsync(principal, programCode, take ?? 50, after, ct)));
        api.MapGet("/runs/{id:guid}", (HttpContext http, ManagementReviewService service, Guid id, CancellationToken ct) => Read(http, principal => service.ReadAsync(principal, id, ct)));
        api.MapGet("/runs/{id:guid}/actions", (HttpContext http, ManagementReviewService service, Guid id, CancellationToken ct) => Read(http, principal => service.ActionsAsync(principal, id, ct)));
        api.MapPost("/runs/{id:guid}/freeze", (HttpContext http, ManagementReviewService service, Guid id, ReviewVersionRequest body, CancellationToken ct) =>
            Command(http, body.Reason, context => service.FreezeAsync(new(id, body.ExpectedVersion), context, ct)));
        api.MapPost("/runs/{id:guid}/meeting", (HttpContext http, ManagementReviewService service, Guid id, ReviewMeetingRequest body, CancellationToken ct) =>
            Command(http, body.Reason, context => service.RecordMeetingAsync(new(id, body.ExpectedVersion, body.MeetingRecord, body.Participants, body.Assessments, body.Decisions, body.Limitations), context, ct)));
        foreach (var action in new[] { "Submit", "Review", "Approve", "Close", "Withdraw" })
        {
            var captured = action;
            api.MapPost("/runs/{id:guid}/" + action.ToLowerInvariant(), (HttpContext http, ManagementReviewService service, Guid id, ReviewTransitionRequest body, CancellationToken ct) =>
                Command(http, body.Reason, context => service.TransitionAsync(new(id, body.ExpectedVersion, captured, body.Note), context, ct)));
        }
        foreach (var action in new[] { "Complete", "Verify", "Reopen", "Withdraw" })
        {
            var captured = action;
            api.MapPost("/actions/{id:guid}/" + action.ToLowerInvariant(), (HttpContext http, ManagementReviewService service, Guid id, ReviewActionRequest body, CancellationToken ct) =>
                Command(http, body.Reason, context => service.TransitionActionAsync(new(id, body.ExpectedVersion, captured, body.Result, body.Evidence), context, ct)));
        }
        api.MapGet("/mappings/assessments", (HttpContext http, EvidenceMappingService service, string? code, int? take, string? after, CancellationToken ct) =>
            Read(http, principal => service.ListMappingsAsync(principal, code, take ?? 50, after, ct)));
        api.MapGet("/mappings/assessments/{id:guid}", (HttpContext http, EvidenceMappingService service, Guid id, CancellationToken ct) => Read(http, principal => service.ReadMappingAsync(principal, id, ct)));
        api.MapPost("/mappings/assessments", (HttpContext http, EvidenceMappingService service, MappingAssessmentRequest body, CancellationToken ct) =>
            Command(http, body.Reason, context => service.CreateMappingAsync(new(body.Id, body.MappingCode, body.MappingRevision, body.Adequacy, body.Assessment, body.Interpretation, body.AssessmentMethod, body.ApplicabilityBasis, body.Gap, body.AuditorNotes, body.Limitations, body.AuditorValidation, body.Evidence), context, ct)));
        api.MapGet("/editions/assessments", (HttpContext http, EvidenceMappingService service, int? take, string? after, CancellationToken ct) =>
            Read(http, principal => service.ListEditionsAsync(principal, take ?? 50, after, ct)));
        api.MapGet("/editions/assessments/{id:guid}", (HttpContext http, EvidenceMappingService service, Guid id, CancellationToken ct) => Read(http, principal => service.ReadEditionAsync(principal, id, ct)));
        api.MapPost("/editions/assessments", (HttpContext http, EvidenceMappingService service, EditionAssessmentRequest body, CancellationToken ct) =>
            Command(http, body.Reason, context => service.CreateEditionAsync(new(body.Id, body.FacilityCode, body.ProgramId, body.Markings, body.Standard, body.FromEdition, body.ToEdition, body.FromCriteria, body.ToCriteria, body.Relationship, body.Basis, body.Changes, body.ApplicabilityImpact, body.RequiredActions, body.AuditorValidation, body.Evidence), context, ct)));
        foreach (var action in new[] { "Submit", "Review", "Approve", "Withdraw" })
        {
            var captured = action;
            api.MapPost("/mappings/assessments/{id:guid}/" + action.ToLowerInvariant(), (HttpContext http, EvidenceMappingService service, Guid id, MappingTransitionRequest body, CancellationToken ct) =>
                Command(http, body.Reason, context => service.TransitionMappingAsync(new(id, body.ExpectedVersion, captured, body.Note, body.AuditorValidation), context, ct)));
            api.MapPost("/editions/assessments/{id:guid}/" + action.ToLowerInvariant(), (HttpContext http, EvidenceMappingService service, Guid id, MappingTransitionRequest body, CancellationToken ct) =>
                Command(http, body.Reason, context => service.TransitionEditionAsync(new(id, body.ExpectedVersion, captured, body.Note, body.AuditorValidation), context, ct)));
        }
    }
    private static async Task<IResult> Read<T>(HttpContext http, Func<Forge.Kernel.Security.ForgePrincipal, Task<T>> query)
    {
        var principal = await PrincipalAsync(http);
        return principal is null ? NotRegistered() : Results.Ok(await query(principal));
    }
}
