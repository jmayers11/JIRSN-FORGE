using Forge.Competency;
using Forge.Competency.Contracts;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning;
using Forge.Resources;
using Forge.Resources.Contracts;
using Forge.SafetyRules;
using Forge.SafetyRules.Contracts;
using Forge.Workforce;
using Forge.Workforce.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record CodeNameRequest(string Code, string Name, string? Parent, string? Description, string? Reason);

public sealed record MachineActivityRequest(string ActivityCode, string[] OperatorQualifications, string? Reason);

public sealed record WorkCenterActivityRequest(string ActivityCode, string? Reason);

public sealed record QualificationTypeRequest(string Code, string Name, QualificationKind Kind, int? ValidityDays, string? Reason);

public sealed record PrerequisiteRequest(string PrerequisiteCode, string? Reason);

public sealed record PersonQualificationRequest(string Code, string? Evidence, string? Reason);

public sealed record WorkerRequest(string HomeWorkCenterCode, string? Reason);

public sealed record AssignmentRequest(string? SafetyAreaCode, string? WorkCenterCode, string? MachineCode, string ActivityCode, bool AsTrainee, string? Reason);

public sealed record PresenceRequest(PresenceKind Kind, DateTimeOffset? ValidUntil, string? Reason);

public sealed record UnavailabilityRequest(UnavailabilityKind Kind, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Reason);

public sealed record SafetyRuleRequest(
    string Code,
    string Title,
    RuleScopeKind ScopeKind,
    string ScopeCode,
    string[]? ActivityCodes,
    int MinimumPhysicalPeople,
    bool ProhibitLoneWork,
    RoleSlot[]? Slots,
    string? Reason);

/// <summary>Phase 2 endpoints: resources, competency, workforce, safety rules and readiness.</summary>
public static class OperationsEndpoints
{
    public static void MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        MapResources(app.MapGroup("/api/resources").RequireAuthorization());
        MapCompetency(app.MapGroup("/api/competency").RequireAuthorization());
        MapWorkforce(app.MapGroup("/api/workforce").RequireAuthorization());
        MapSafetyRules(app.MapGroup("/api/safety-rules").RequireAuthorization());
        MapReadiness(app.MapGroup("/api/readiness").RequireAuthorization());
    }

    private static void MapResources(RouteGroupBuilder api)
    {
        api.MapGet("/", (HttpContext http, CancellationToken _) => ReadStore<IResourcesLookup>(http, Permissions.ResourcesRead, async (r, ct) => new
        {
            sites = await r.ListSitesAsync(ct),
            safetyAreas = await r.ListSafetyAreasAsync(ct),
            workCenters = await r.ListWorkCentersAsync(ct),
            machines = await r.ListMachinesAsync(ct),
            activityTypes = await r.ListActivityTypesAsync(ct),
            machineActivities = await r.ListMachineActivitiesAsync(ct),
            workCenterActivities = await r.ListWorkCenterActivitiesAsync(ct),
        }));

        api.MapPost("/sites", (HttpContext http, CodeNameRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateSiteAsync(new CreateSite(b.Code, b.Name), c, ct)));
        api.MapPost("/safety-areas", (HttpContext http, CodeNameRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateSafetyAreaAsync(new CreateSafetyArea(b.Code, b.Parent ?? string.Empty, b.Name), c, ct)));
        api.MapPost("/work-centers", (HttpContext http, CodeNameRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateWorkCenterAsync(new CreateWorkCenter(b.Code, b.Parent ?? string.Empty, b.Name), c, ct)));
        api.MapPost("/machines", (HttpContext http, CodeNameRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateMachineAsync(new CreateMachine(b.Code, b.Parent ?? string.Empty, b.Name), c, ct)));
        api.MapPost("/activity-types", (HttpContext http, CodeNameRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DefineActivityTypeAsync(new DefineActivityType(b.Code, b.Name, b.Description), c, ct)));
        api.MapPost("/machines/{machine}/activities", (HttpContext http, string machine, MachineActivityRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetMachineActivityAsync(new SetMachineActivity(machine, b.ActivityCode, b.OperatorQualifications), c, ct)));
        api.MapPost("/work-centers/{workCenter}/activities", (HttpContext http, string workCenter, WorkCenterActivityRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AllowWorkCenterActivityAsync(new AllowWorkCenterActivity(workCenter, b.ActivityCode), c, ct)));
    }

    private static void MapCompetency(RouteGroupBuilder api)
    {
        api.MapGet("/qualification-types", (HttpContext http, CancellationToken _) =>
            ReadStore<ICompetencyLookup>(http, Permissions.CompetencyRead, async (l, ct) => await l.ListTypesAsync(ct)));
        api.MapGet("/people/{personId:guid}", (HttpContext http, Guid personId) =>
            ReadStore<ICompetencyLookup>(http, Permissions.CompetencyRead, async (l, ct) => await l.GetQualificationsAsync([new PersonId(personId)], ct)));

        api.MapPost("/qualification-types", (HttpContext http, QualificationTypeRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DefineTypeAsync(new DefineQualificationType(b.Code, b.Name, b.Kind, b.ValidityDays), c, ct)));
        api.MapPost("/qualification-types/{code}/prerequisites", (HttpContext http, string code, PrerequisiteRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddPrerequisiteAsync(new AddPrerequisite(code, b.PrerequisiteCode), c, ct)));
        api.MapPost("/people/{personId:guid}/training", (HttpContext http, Guid personId, PersonQualificationRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.StartTrainingAsync(new StartTraining(new PersonId(personId), b.Code), c, ct)));
        api.MapPost("/people/{personId:guid}/certifications", (HttpContext http, Guid personId, PersonQualificationRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CertifyAsync(new CertifyQualification(new PersonId(personId), b.Code, b.Evidence ?? string.Empty), c, ct)));
        api.MapPost("/people/{personId:guid}/suspensions", (HttpContext http, Guid personId, PersonQualificationRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SuspendAsync(new SuspendQualification(new PersonId(personId), b.Code), c, ct)));
        api.MapPost("/people/{personId:guid}/revocations", (HttpContext http, Guid personId, PersonQualificationRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RevokeAsync(new RevokeQualification(new PersonId(personId), b.Code), c, ct)));
    }

    private static void MapWorkforce(RouteGroupBuilder api)
    {
        api.MapPost("/workers/{personId:guid}", (HttpContext http, Guid personId, WorkerRequest b, WorkforceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RegisterWorkerAsync(new RegisterWorker(new PersonId(personId), b.HomeWorkCenterCode), c, ct)));
        api.MapPost("/workers/{personId:guid}/assignment", (HttpContext http, Guid personId, AssignmentRequest b, WorkforceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AssignWorkAsync(new AssignWork(new PersonId(personId), b.SafetyAreaCode, b.WorkCenterCode, b.MachineCode, b.ActivityCode, b.AsTrainee), c, ct)));
        api.MapPost("/workers/{personId:guid}/assignment/end", (HttpContext http, Guid personId, ReasonRequest b, WorkforceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.EndAssignmentAsync(new EndAssignment(new PersonId(personId)), c, ct)));
        api.MapPost("/workers/{personId:guid}/presence", (HttpContext http, Guid personId, PresenceRequest b, WorkforceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordPresenceAsync(new RecordPresence(new PersonId(personId), b.Kind, b.ValidUntil), c, ct)));
        api.MapPost("/workers/{personId:guid}/departure", (HttpContext http, Guid personId, ReasonRequest b, WorkforceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordDepartureAsync(new RecordDeparture(new PersonId(personId)), c, ct)));
        api.MapPost("/workers/{personId:guid}/unavailability", (HttpContext http, Guid personId, UnavailabilityRequest b, WorkforceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordUnavailabilityAsync(new RecordUnavailability(new PersonId(personId), b.Kind, b.StartsAt, b.EndsAt), c, ct)));
    }

    private static void MapSafetyRules(RouteGroupBuilder api)
    {
        api.MapGet("/", (HttpContext http, CancellationToken _) =>
            ReadStore<ISafetyRuleLookup>(http, Permissions.SafetyRulesRead, async (l, ct) => await l.ListAllAsync(ct)));
        api.MapPost("/drafts", (HttpContext http, SafetyRuleRequest b, SafetyRuleService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DraftAsync(new DraftSafetyRule(b.Code, b.Title, b.ScopeKind, b.ScopeCode, b.ActivityCodes, b.MinimumPhysicalPeople, b.ProhibitLoneWork, b.Slots), c, ct)));
        api.MapPost("/{code}/versions/{version:int}/publish", (HttpContext http, string code, int version, ReasonRequest b, SafetyRuleService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.PublishAsync(new PublishSafetyRule(code, version), c, ct)));
        api.MapPost("/{code}/draft/discard", (HttpContext http, string code, ReasonRequest b, SafetyRuleService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DiscardDraftAsync(new DiscardSafetyRuleDraft(code), c, ct)));
        api.MapPost("/{code}/retire", (HttpContext http, string code, ReasonRequest b, SafetyRuleService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RetireAsync(new RetireSafetyRule(code), c, ct)));
    }

    private static void MapReadiness(RouteGroupBuilder api)
    {
        api.MapGet("/machines/{machine}", async (HttpContext http, string machine, string activity, ReadinessService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.EvaluateMachineAsync(principal, machine, activity, ct));
        });

        api.MapGet("/active-work", async (HttpContext http, string? safetyArea, ReadinessService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.EvaluateActiveWorkAsync(principal, safetyArea, ct));
        });

        // Read-only: evaluates a proposed move without committing it. Commit with POST /api/workforce/workers/{id}/assignment.
        api.MapPost("/reallocation-preview/{personId:guid}", async (HttpContext http, Guid personId, AssignmentRequest b, ReadinessService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null
                ? NotRegistered()
                : Results.Ok(await s.PreviewReallocationAsync(principal, new AssignWork(new PersonId(personId), b.SafetyAreaCode, b.WorkCenterCode, b.MachineCode, b.ActivityCode, b.AsTrainee), ct));
        });
    }
}
