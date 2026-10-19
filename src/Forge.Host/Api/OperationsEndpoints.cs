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

public sealed record CodeNameRequest(string Code, string Name, string? Parent, string? Description, string? Reason, string? TimeZone = null, string? Picture = null);

/// <summary>Edits a plant record. Parent moves a work center (safety area) or machine (work center).</summary>
public sealed record ResourceEditRequest(string Name, string? Parent, string? Description, string? TimeZone, string? Picture, string? Reason);

public sealed record MachineActivityRequest(string ActivityCode, string[] OperatorQualifications, string? Reason);

public sealed record WorkCenterActivityRequest(string ActivityCode, string? Reason);

public sealed record QualificationTypeRequest(string Code, string Name, QualificationKind Kind, int? ValidityDays, string? Reason);

public sealed record PrerequisiteRequest(string PrerequisiteCode, string? Reason);

public sealed record PersonQualificationRequest(string Code, string? Evidence, string? Reason);

public sealed record WorkerRequest(string HomeWorkCenterCode, string? Reason);

public sealed record AssignmentRequest(string? SafetyAreaCode, string? WorkCenterCode, string? MachineCode, string ActivityCode, bool AsTrainee, string? Reason);

public sealed record PresenceRequest(PresenceKind Kind, DateTimeOffset? ValidUntil, string? Reason);

public sealed record UnavailabilityRequest(UnavailabilityKind Kind, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Reason);

/// <summary>A temporary move (ADR 0057 §5): the estimated end date is required.</summary>
public sealed record TemporaryMoveRequest(string WorkCenterCode, DateOnly StartsOn, DateOnly EstimatedEndOn, string? Reason);

public sealed record MoveEndRequest(DateOnly NewEndOn, string? Reason);

public sealed record WorkerTransferRequest(string WorkCenterCode, DateOnly EffectiveFrom, string? Reason);

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
        // Retired records are left out unless asked for (plant setup shows them so they can be reactivated).
        api.MapGet("/", (HttpContext http, bool? includeRetired, CancellationToken _) => ReadStore<IResourcesLookup>(http, Permissions.ResourcesRead, async (r, ct) =>
        {
            var all = includeRetired == true;
            var areas = (await r.ListSafetyAreasAsync(ct)).Where(a => all || a.Active).ToList();
            var workCenters = (await r.ListWorkCentersAsync(ct)).Where(w => all || w.Active).ToList();
            var machines = (await r.ListMachinesAsync(ct)).Where(m => all || m.Active).ToList();
            var machineCodes = machines.Select(m => m.Code).ToHashSet(StringComparer.Ordinal);
            var workCenterCodes = workCenters.Select(w => w.Code).ToHashSet(StringComparer.Ordinal);
            return new
            {
                sites = await r.ListSitesAsync(ct),
                safetyAreas = areas,
                workCenters,
                machines,
                activityTypes = (await r.ListActivityTypesAsync(ct)).Where(a => all || a.Active).ToList(),
                machineActivities = (await r.ListMachineActivitiesAsync(ct)).Where(a => machineCodes.Contains(a.MachineCode)).ToList(),
                shifts = await r.ListShiftsAsync(ct),
                workCenterShifts = (await r.ListWorkCenterShiftsAsync(ct)).Where(s => workCenterCodes.Contains(s.WorkCenterCode)).ToList(),
                closures = await r.ListSiteClosuresAsync(ct),
                workCenterActivities = (await r.ListWorkCenterActivitiesAsync(ct)).Where(a => workCenterCodes.Contains(a.WorkCenterCode)).ToList(),
            };
        }));

        api.MapPost("/sites", (HttpContext http, CodeNameRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateSiteAsync(new CreateSite(b.Code, b.Name, b.TimeZone), c, ct)));
        api.MapPost("/safety-areas", (HttpContext http, CodeNameRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateSafetyAreaAsync(new CreateSafetyArea(b.Code, b.Parent ?? string.Empty, b.Name), c, ct)));
        api.MapPost("/work-centers", (HttpContext http, CodeNameRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateWorkCenterAsync(new CreateWorkCenter(b.Code, b.Parent ?? string.Empty, b.Name, b.Picture), c, ct)));
        api.MapPost("/machines", (HttpContext http, CodeNameRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateMachineAsync(new CreateMachine(b.Code, b.Parent ?? string.Empty, b.Name), c, ct)));
        api.MapPost("/activity-types", (HttpContext http, CodeNameRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DefineActivityTypeAsync(new DefineActivityType(b.Code, b.Name, b.Description), c, ct)));
        api.MapPost("/machines/{machine}/activities", (HttpContext http, string machine, MachineActivityRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetMachineActivityAsync(new SetMachineActivity(machine, b.ActivityCode, b.OperatorQualifications), c, ct)));
        api.MapPost("/work-centers/{workCenter}/activities", (HttpContext http, string workCenter, WorkCenterActivityRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AllowWorkCenterActivityAsync(new AllowWorkCenterActivity(workCenter, b.ActivityCode), c, ct)));

        // Plant setup: edit, retire and reactivate. Codes never change; every edit carries a reason.
        api.MapPost("/sites/{code}/edit", (HttpContext http, string code, ResourceEditRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.UpdateSiteAsync(new UpdateSite(code, b.Name, b.TimeZone), c, ct)));
        api.MapPost("/safety-areas/{code}/edit", (HttpContext http, string code, ResourceEditRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.UpdateSafetyAreaAsync(new UpdateSafetyArea(code, b.Name), c, ct)));
        api.MapPost("/work-centers/{code}/edit", (HttpContext http, string code, ResourceEditRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.UpdateWorkCenterAsync(new UpdateWorkCenter(code, b.Name, b.Parent ?? string.Empty, b.Picture), c, ct)));
        api.MapPost("/machines/{code}/edit", (HttpContext http, string code, ResourceEditRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.UpdateMachineAsync(new UpdateMachine(code, b.Name, b.Parent ?? string.Empty), c, ct)));
        api.MapPost("/activity-types/{code}/edit", (HttpContext http, string code, ResourceEditRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.UpdateActivityTypeAsync(new UpdateActivityType(code, b.Name, b.Description), c, ct)));

        foreach (var (path, kind) in new[] { ("safety-areas", "SafetyArea"), ("work-centers", "WorkCenter"), ("machines", "Machine"), ("activity-types", "ActivityType") })
        {
            api.MapPost($"/{path}/{{code}}/retire", (HttpContext http, string code, ReasonRequest b, ResourcesService s, CancellationToken ct) =>
                Command(http, b.Reason, c => s.SetResourceActiveAsync(new SetResourceActive(kind, code, false), c, ct)));
            api.MapPost($"/{path}/{{code}}/reactivate", (HttpContext http, string code, ReasonRequest b, ResourcesService s, CancellationToken ct) =>
                Command(http, b.Reason, c => s.SetResourceActiveAsync(new SetResourceActive(kind, code, true), c, ct)));
        }

        api.MapPost("/machines/{machine}/activities/{activity}/remove", (HttpContext http, string machine, string activity, ReasonRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RemoveMachineActivityAsync(new RemoveMachineActivity(machine, activity), c, ct)));
        api.MapPost("/work-centers/{workCenter}/activities/{activity}/remove", (HttpContext http, string workCenter, string activity, ReasonRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RemoveWorkCenterActivityAsync(new RemoveWorkCenterActivity(workCenter, activity), c, ct)));
        api.MapPost("/work-centers/{workCenter}/shifts/{shift}/remove", (HttpContext http, string workCenter, string shift, ReasonRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.UnassignWorkCenterShiftAsync(new UnassignWorkCenterShift(workCenter, shift), c, ct)));
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

        // ADR 0057 §5: temporary moves (with an estimated end, extended or renewed) and transfers are separate actions.
        api.MapGet("/workers", (HttpContext http, TimeProvider time, CancellationToken _) =>
            ReadStore<IWorkforceLookup>(http, Permissions.WorkforceRead, async (l, ct) => await l.GetWorkerStatesAsync(time.GetUtcNow(), ct)));
        api.MapGet("/moves", (HttpContext http, Guid? personId, bool? includeClosed, CancellationToken _) =>
            ReadStore<IWorkforceLookup>(http, Permissions.WorkforceRead, async (l, ct) => await l.ListTemporaryMovesAsync(personId, includeClosed ?? false, ct)));
        api.MapPost("/workers/{personId:guid}/moves", (HttpContext http, Guid personId, TemporaryMoveRequest b, WorkforceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.StartTemporaryMoveAsync(new StartTemporaryMove(new PersonId(personId), b.WorkCenterCode, b.StartsOn, b.EstimatedEndOn), c, ct)));
        api.MapPost("/moves/{moveId:long}/extend", (HttpContext http, long moveId, MoveEndRequest b, WorkforceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ExtendTemporaryMoveAsync(new ExtendTemporaryMove(moveId, b.NewEndOn), c, ct)));
        api.MapPost("/moves/{moveId:long}/renew", (HttpContext http, long moveId, MoveEndRequest b, WorkforceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ExtendTemporaryMoveAsync(new ExtendTemporaryMove(moveId, b.NewEndOn, Renew: true), c, ct)));
        api.MapPost("/moves/{moveId:long}/end", (HttpContext http, long moveId, ReasonRequest b, WorkforceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.EndTemporaryMoveAsync(new EndTemporaryMove(moveId), c, ct)));
        api.MapGet("/transfers", (HttpContext http, Guid? personId, CancellationToken _) =>
            ReadStore<IWorkforceLookup>(http, Permissions.WorkforceRead, async (l, ct) => await l.ListTransfersAsync(personId, ct)));
        api.MapPost("/workers/{personId:guid}/transfer", (HttpContext http, Guid personId, WorkerTransferRequest b, WorkforceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.TransferWorkerAsync(new TransferWorker(new PersonId(personId), b.WorkCenterCode, b.EffectiveFrom), c, ct)));
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
