using Forge.Competency;
using Forge.Competency.Contracts;
using Forge.Kernel.Identity;
using Forge.Kernel.Access;
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

public sealed record MachineActivityRequest(string ActivityCode, string[] OperatorQualifications, string? Reason, bool RequiresMachineAuthorization = false);

public sealed record WorkCenterActivityRequest(string ActivityCode, string? Reason);

public sealed record QualificationTypeRequest(string Code, string Name, QualificationKind Kind, int? ValidityDays, string? Reason);

public sealed record PrerequisiteRequest(string PrerequisiteCode, string? Reason);
public sealed record PrerequisiteAlternativesRequest(string GroupCode, string Name, IReadOnlyList<string> PrerequisiteCodes, string? Reason);

public sealed record CourseCompletionRequest(string CourseCode, string CourseVersion, DateTimeOffset? ExpiresAt, string Evidence, string? Reason);
public sealed record PracticalAssessmentRequest(string QualificationCode, string MachineCode, string ActivityCode, bool Passed, string AssessmentVersion, string Evidence, string? Restrictions, string? Reason);
public sealed record MachineAuthorizationRequest(string MachineCode, string ActivityCode, string QualificationCode, Guid AssessmentId, DateTimeOffset? ExpiresAt, string Evidence, string? Restrictions, bool IndependentOperationAllowed, string? Reason);
public sealed record MachineAuthorizationStatusRequest(MachineAuthorizationStatus Status, string? Reason);

public sealed record PersonQualificationRequest(string Code, string? Evidence, string? Reason, DateTimeOffset? ExpiresAt = null);

public sealed record WorkerRequest(string HomeWorkCenterCode, string? Reason);

public sealed record WorkerJobTitleRequest(string? JobTitle, string? Reason);
public sealed record WorkerDepartmentRequest(string? Department, string? Reason);

public sealed record WorkCenterLayoutsRequest(IReadOnlyList<WorkCenterPlacement> Placements, string? Reason);

public sealed record WorkCenterMapLayoutRequest(IReadOnlyList<WorkCenterMapPlacement> Placements, string? Reason);

public sealed record AssignmentRequest(string? SafetyAreaCode, string? WorkCenterCode, string? MachineCode, string ActivityCode, bool AsTrainee, string? Reason);

public sealed record PresenceRequest(PresenceKind Kind, DateTimeOffset? ValidUntil, string? Reason);

public sealed record UnavailabilityRequest(UnavailabilityKind Kind, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Reason);

public sealed record AttendancePolicyRequest(string PolicyCode, int Revision, DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveThrough,
    int GraceMinutes, bool IncludeCallOutsInArrivalDenominator, string? Reason);

public sealed record GenerateAttendanceScheduleRequest(DateTimeOffset From, DateTimeOffset Through, string? Reason);

public sealed record AttendanceClassificationRequest(AttendanceEventKind Kind, DateTimeOffset? ObservedAt, string? ReasonCode, string? Reason);
public sealed record AttendanceSourceImportRequest(Guid OpportunityId, AttendanceEventKind Kind, DateTimeOffset? ObservedAt,
    AttendanceEventSource Source, string SourceRecordId, string? Reason);

public sealed record AttendanceEventReviewRequest(AttendanceEventReviewState Decision, string? ReasonCode, string? Reason);

public sealed record AttendanceEventCorrectionRequest(AttendanceEventKind Kind, DateTimeOffset? ObservedAt, string? ReasonCode, string? Reason);

public sealed record DevelopmentNominationRequest(Guid PersonId, DevelopmentNominationKind Kind, string Pathway, string Rationale,
    string ExpectedPlantBenefit, IReadOnlyList<string> EvidenceReferences, decimal? EstimatedTrainingHours, decimal? EstimatedCost, string? Currency, string? Reason);

public sealed record DevelopmentNominationAdvanceRequest(DevelopmentNominationStatus Status, string Note, string? Reason);

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
            Command(http, b.Reason, c => s.SetMachineActivityAsync(new SetMachineActivity(machine, b.ActivityCode, b.OperatorQualifications, b.RequiresMachineAuthorization), c, ct)));
        api.MapPost("/work-centers/{workCenter}/activities", (HttpContext http, string workCenter, WorkCenterActivityRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AllowWorkCenterActivityAsync(new AllowWorkCenterActivity(workCenter, b.ActivityCode), c, ct)));

        // Plant setup: edit, retire and reactivate. Codes never change; every edit carries a reason.
        api.MapPost("/sites/{code}/edit", (HttpContext http, string code, ResourceEditRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.UpdateSiteAsync(new UpdateSite(code, b.Name, b.TimeZone), c, ct)));
        api.MapPost("/safety-areas/{code}/edit", (HttpContext http, string code, ResourceEditRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.UpdateSafetyAreaAsync(new UpdateSafetyArea(code, b.Name), c, ct)));
        api.MapPost("/work-centers/{code}/edit", (HttpContext http, string code, ResourceEditRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.UpdateWorkCenterAsync(new UpdateWorkCenter(code, b.Name, b.Parent ?? string.Empty, b.Picture), c, ct)));
        api.MapPost("/work-centers/layout", (HttpContext http, WorkCenterLayoutsRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.UpdateWorkCenterLayoutsAsync(new UpdateWorkCenterLayouts(b.Placements), c, ct)));
        api.MapPost("/work-centers/map-layout", (HttpContext http, WorkCenterMapLayoutRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.UpdateWorkCenterMapLayoutAsync(new UpdateWorkCenterMapLayout(b.Placements), c, ct)));
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
        api.MapGet("/people/qualifications", (HttpContext http, Guid[]? personIds) =>
            ReadStore<ICompetencyLookup>(http, Permissions.CompetencyRead, async (l, ct) =>
            {
                var ids = (personIds ?? []).Distinct().Take(501).Select(id => new PersonId(id)).ToArray();
                if (ids.Length > 500)
                    throw new Forge.Kernel.Commands.CommandRejectedException("too_many_people", "Read at most 500 people at a time.");
                return await l.GetQualificationsAsync(ids, ct);
            }));
        api.MapGet("/machine-authorizations", (HttpContext http, Guid[]? personIds) =>
            ReadStore<ICompetencyLookup>(http, Permissions.CompetencyRead, async (l, ct) =>
            {
                var ids = (personIds ?? []).Distinct().Take(501).Select(id => new PersonId(id)).ToArray();
                if (ids.Length > 500)
                    throw new Forge.Kernel.Commands.CommandRejectedException("too_many_people", "Read at most 500 people at a time.");
                return await l.GetMachineAuthorizationsAsync(ids, ct);
            }));
        api.MapGet("/training-course-completions", (HttpContext http, Guid[]? personIds) =>
            ReadStore<ICompetencyLookup>(http, Permissions.CompetencyRead, async (l, ct) =>
            {
                var ids = (personIds ?? []).Distinct().Take(501).Select(id => new PersonId(id)).ToArray();
                if (ids.Length > 500)
                    throw new Forge.Kernel.Commands.CommandRejectedException("too_many_people", "Read at most 500 people at a time.");
                return await l.GetTrainingCourseCompletionsAsync(ids, ct);
            }));
        api.MapGet("/practical-assessments", (HttpContext http, Guid[]? personIds) =>
            ReadStore<ICompetencyLookup>(http, Permissions.CompetencyRead, async (l, ct) =>
            {
                var ids = (personIds ?? []).Distinct().Take(501).Select(id => new PersonId(id)).ToArray();
                if (ids.Length > 500)
                    throw new Forge.Kernel.Commands.CommandRejectedException("too_many_people", "Read at most 500 people at a time.");
                return await l.GetPracticalAssessmentsAsync(ids, ct);
            }));

        api.MapPost("/qualification-types", (HttpContext http, QualificationTypeRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DefineTypeAsync(new DefineQualificationType(b.Code, b.Name, b.Kind, b.ValidityDays), c, ct)));
        api.MapPost("/qualification-types/{code}/prerequisites", (HttpContext http, string code, PrerequisiteRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddPrerequisiteAsync(new AddPrerequisite(code, b.PrerequisiteCode), c, ct)));
        api.MapPost("/qualification-types/{code}/prerequisite-alternatives", (HttpContext http, string code, PrerequisiteAlternativesRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DefinePrerequisiteAlternativesAsync(new DefinePrerequisiteAlternatives(code, b.GroupCode, b.Name, b.PrerequisiteCodes), c, ct)));
        api.MapPost("/people/{personId:guid}/training", (HttpContext http, Guid personId, PersonQualificationRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.StartTrainingAsync(new StartTraining(new PersonId(personId), b.Code), c, ct)));
        api.MapPost("/people/{personId:guid}/certifications", (HttpContext http, Guid personId, PersonQualificationRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CertifyAsync(new CertifyQualification(new PersonId(personId), b.Code, b.Evidence ?? string.Empty, b.ExpiresAt), c, ct)));
        api.MapPost("/people/{personId:guid}/suspensions", (HttpContext http, Guid personId, PersonQualificationRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SuspendAsync(new SuspendQualification(new PersonId(personId), b.Code), c, ct)));
        api.MapPost("/people/{personId:guid}/revocations", (HttpContext http, Guid personId, PersonQualificationRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RevokeAsync(new RevokeQualification(new PersonId(personId), b.Code), c, ct)));
        api.MapPost("/people/{personId:guid}/training-course-completions", (HttpContext http, Guid personId, CourseCompletionRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordTrainingCourseCompletionAsync(new RecordTrainingCourseCompletion(new PersonId(personId), b.CourseCode, b.CourseVersion, b.ExpiresAt, b.Evidence), c, ct)));
        api.MapPost("/people/{personId:guid}/practical-assessments", (HttpContext http, Guid personId, PracticalAssessmentRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordPracticalAssessmentAsync(new RecordPracticalAssessment(new PersonId(personId), b.QualificationCode, b.MachineCode, b.ActivityCode, b.Passed, b.AssessmentVersion, b.Evidence, b.Restrictions), c, ct)));
        api.MapPost("/people/{personId:guid}/machine-authorizations", (HttpContext http, Guid personId, MachineAuthorizationRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AuthorizeMachineOperationAsync(new AuthorizeMachineOperation(new PersonId(personId), b.MachineCode, b.ActivityCode, b.QualificationCode, b.AssessmentId, b.ExpiresAt, b.Evidence, b.Restrictions, b.IndependentOperationAllowed), c, ct)));
        api.MapPost("/people/{personId:guid}/machine-authorizations/{machine}/{activity}/{qualification}/status", (HttpContext http, Guid personId, string machine, string activity, string qualification, MachineAuthorizationStatusRequest b, CompetencyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ChangeMachineAuthorizationAsync(new ChangeMachineAuthorization(new PersonId(personId), machine, activity, qualification, b.Status), c, ct)));
    }

    private static void MapWorkforce(RouteGroupBuilder api)
    {
        api.MapPost("/workers/{personId:guid}", (HttpContext http, Guid personId, WorkerRequest b, WorkforceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RegisterWorkerAsync(new RegisterWorker(new PersonId(personId), b.HomeWorkCenterCode), c, ct)));
        api.MapPost("/workers/{personId:guid}/job-title", (HttpContext http, Guid personId, WorkerJobTitleRequest b, WorkforceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetWorkerJobTitleAsync(new SetWorkerJobTitle(new PersonId(personId), b.JobTitle), c, ct)));
        api.MapPost("/workers/{personId:guid}/department", (HttpContext http, Guid personId, WorkerDepartmentRequest b, WorkforceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetWorkerDepartmentAsync(new SetWorkerDepartment(new PersonId(personId), b.Department), c, ct)));
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
        api.MapGet("/unavailability", async (HttpContext http, DateTimeOffset? from, DateTimeOffset? to, TimeProvider time, CancellationToken _) =>
        {
            var startsAt = from ?? time.GetUtcNow();
            var endsAt = to ?? startsAt.AddDays(30);
            if (endsAt <= startsAt || endsAt - startsAt > TimeSpan.FromDays(90))
                return Results.BadRequest(new { error = "invalid_date_range", message = "Choose an end time after the start, within a 90-day window." });
            return await ReadStore<IWorkforceLookup>(http, Permissions.WorkforceRead, async (l, ct) => await l.ListUnavailabilityAsync(startsAt, endsAt, ct));
        });
        // Without dates the summary covers a calendar quarter at the site: the current one to date by default, the previous one, or any
        // earlier quarter by name (?quarter=2026-Q3). Explicit from/to dates still choose any period up to a year.
        api.MapGet("/attendance-intelligence", async (HttpContext http, DateTimeOffset? from, DateTimeOffset? to, string? period, string? quarter, TimeProvider time,
            IKernelUnitOfWorkFactory unitOfWork, CancellationToken _) =>
        {
            var now = time.GetUtcNow();
            DateTimeOffset startsAt, endsAt;
            string? label = null;
            if (from is null && to is null)
            {
                string zone;
                await using (var uow = await unitOfWork.BeginSnapshotAsync(http.RequestAborted))
                    zone = await uow.SiteTimeZoneAsync(http.RequestAborted);
                var named = AttendancePeriods.Resolve(period, quarter, SiteTime.DateAt(now, zone));
                if (named is null)
                    return Results.BadRequest(new { error = "invalid_period", message = "Choose quarter, previous-quarter or a started quarter such as 2026-Q3." });
                startsAt = SiteTime.StartOf(named.FirstDay, zone);
                var periodEnd = SiteTime.StartOf(named.EndBefore, zone);
                endsAt = periodEnd < now ? periodEnd : now;
                label = periodEnd < now ? named.Label : $"{named.Label} to date";
            }
            else
            {
                endsAt = to ?? now;
                startsAt = from ?? endsAt.AddDays(-90);
            }

            if (endsAt <= startsAt || endsAt - startsAt > TimeSpan.FromDays(366))
                return Results.BadRequest(new { error = "invalid_date_range", message = "Choose a period up to one year long." });
            return await ReadStore<IAttendanceReliabilityStore>(http, Permissions.AttendanceIntelligenceRead, async (store, ct) =>
            {
                var opportunities = await store.ListAttendanceOpportunitiesAsync(startsAt, endsAt, ct);
                var events = await store.ListAttendanceIntelligenceEventsAsync(startsAt, endsAt, ct);
                var policies = await store.ListAttendancePoliciesAsync(ct);
                return AttendanceReliabilityCalculator.Calculate(opportunities, events, policies, startsAt, endsAt, now)
                    with { PeriodLabel = label, PeriodStart = startsAt, PeriodEnd = endsAt };
            });
        });
        api.MapGet("/attendance-policies", (HttpContext http, CancellationToken _) =>
            ReadStore<IAttendanceReliabilityStore>(http, Permissions.AttendancePolicyManage,
                async (store, ct) => await store.ListAttendancePoliciesAsync(ct)));
        api.MapGet("/attendance-opportunities", async (HttpContext http, DateTimeOffset? from, DateTimeOffset? to, TimeProvider time, CancellationToken _) =>
        {
            var startsAt = from ?? time.GetUtcNow();
            var endsAt = to ?? startsAt.AddDays(14);
            if (endsAt <= startsAt || endsAt - startsAt > TimeSpan.FromDays(90))
                return Results.BadRequest(new { error = "invalid_date_range", message = "Choose a schedule period up to 90 days long." });
            return await ReadStore<IAttendanceReliabilityStore>(http, Permissions.AttendanceScheduleManage,
                async (store, ct) => await store.ListAttendanceOpportunitiesAsync(startsAt, endsAt, ct));
        });
        api.MapGet("/my-attendance-events", async (HttpContext http, DateTimeOffset? from, DateTimeOffset? to, TimeProvider time) =>
        {
            var endsAt = to ?? time.GetUtcNow().AddDays(1);
            var startsAt = from ?? endsAt.AddDays(-366);
            if (endsAt <= startsAt || endsAt - startsAt > TimeSpan.FromDays(366))
                return Results.BadRequest(new { error = "invalid_date_range", message = "Choose an event period up to one year long." });
            return await Read(http, Permissions.AttendanceCorrectionRequest, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                return (await uow.Store<IAttendanceReliabilityStore>().ListAttendanceIntelligenceEventsAsync(startsAt, endsAt, http.RequestAborted))
                    .Where(item => item.PersonId == principal.PersonId.Value)
                    .OrderByDescending(item => item.RecordedAt)
                    .Select(item => new { item.EventId, item.OpportunityId, item.Kind, item.ObservedAt, item.ReviewState, item.Source, item.RecordedAt, item.SupersedesEventId })
                    .ToArray();
            });
        });
        api.MapGet("/attendance-intelligence/events", async (HttpContext http, DateTimeOffset? from, DateTimeOffset? to, TimeProvider time, CancellationToken _) =>
        {
            var endsAt = to ?? time.GetUtcNow().AddDays(1);
            var startsAt = from ?? endsAt.AddDays(-30);
            if (endsAt <= startsAt || endsAt - startsAt > TimeSpan.FromDays(366))
                return Results.BadRequest(new { error = "invalid_date_range", message = "Choose an event period up to one year long." });
            return await ReadStore<IAttendanceReliabilityStore>(http, Permissions.AttendanceClassificationReview, async (store, ct) =>
                (await store.ListAttendanceIntelligenceEventsAsync(startsAt, endsAt, ct))
                    .Select(item => new { item.EventId, item.PersonId, item.OpportunityId, item.Kind, item.ObservedAt, item.ReviewState, item.Source, item.RecordedAt, item.RecordedBy, item.SupersedesEventId })
                    .ToArray());
        });
        api.MapPost("/attendance-schedule/generate", (HttpContext http, GenerateAttendanceScheduleRequest b, AttendanceScheduleService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.GenerateAsync(new GenerateAttendanceSchedule(b.From, b.Through), c, ct)));
        api.MapPost("/attendance-intelligence/imports", (HttpContext http, AttendanceSourceImportRequest b, AttendanceIntelligenceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ImportSourceEventAsync(new ImportAttendanceSourceEvent(b.OpportunityId, b.Kind, b.ObservedAt, b.Source, b.SourceRecordId), c, ct)));
        api.MapPost("/attendance-policies", (HttpContext http, AttendancePolicyRequest b, AttendanceIntelligenceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetPolicyAsync(new SetAttendancePolicy(new AttendancePolicyVersion(b.PolicyCode, b.Revision, b.EffectiveFrom, b.EffectiveThrough,
                b.GraceMinutes, b.IncludeCallOutsInArrivalDenominator)), c, ct)));
        api.MapPost("/attendance-opportunities/{opportunityId:guid}/classifications", (HttpContext http, Guid opportunityId, AttendanceClassificationRequest b, AttendanceIntelligenceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordClassificationAsync(new RecordAttendanceClassification(opportunityId, b.Kind, b.ObservedAt, b.ReasonCode), c, ct)));
        api.MapPost("/attendance-intelligence/events/{eventId:guid}/review", (HttpContext http, Guid eventId, AttendanceEventReviewRequest b, AttendanceIntelligenceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReviewEventAsync(new ReviewAttendanceIntelligenceEvent(eventId, b.Decision, b.ReasonCode), c, ct)));
        api.MapPost("/attendance-intelligence/events/{eventId:guid}/correction", (HttpContext http, Guid eventId, AttendanceEventCorrectionRequest b, AttendanceIntelligenceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CorrectEventAsync(new CorrectAttendanceIntelligenceEvent(eventId, b.Kind, b.ObservedAt, b.ReasonCode), c, ct)));
        api.MapPost("/my-attendance-events/{eventId:guid}/correction", (HttpContext http, Guid eventId, AttendanceEventCorrectionRequest b, AttendanceIntelligenceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RequestOwnCorrectionAsync(new RequestOwnAttendanceCorrection(eventId, b.Kind, b.ObservedAt, b.ReasonCode), c, ct)));
        api.MapGet("/development-nominations", (HttpContext http, Guid? personId, CancellationToken _) =>
            ReadStore<IWorkforceDevelopmentStore>(http, Permissions.WorkforceNominationRead, async (store, ct) =>
            {
                var nominations = await store.ListNominationsAsync(personId, ct);
                var principal = await PrincipalAsync(http);
                return principal?.HasPermission(Permissions.WorkforceCompensationRead) == true
                    ? nominations
                    : nominations.Where(item => item.Kind != DevelopmentNominationKind.CompensationConsideration).ToArray();
            }));
        api.MapGet("/development-nominations/{nominationId:guid}/events", (HttpContext http, Guid nominationId, CancellationToken _) =>
            ReadStore<IWorkforceDevelopmentStore>(http, Permissions.WorkforceNominationRead, async (store, ct) =>
            {
                var nomination = await store.FindNominationAsync(nominationId, ct);
                if (nomination is null) return Array.Empty<DevelopmentNominationEvent>();
                var principal = await PrincipalAsync(http);
                if (nomination.Kind == DevelopmentNominationKind.CompensationConsideration && principal?.HasPermission(Permissions.WorkforceCompensationRead) != true)
                    throw new ForgeAuthorizationException(http.RequestServices.GetRequiredService<IForgeAuthorizer>()
                        .Authorize(principal!, Permissions.WorkforceCompensationRead, ResourceRef.Of("DevelopmentNomination", nominationId.ToString("D"))));
                return await store.ListNominationEventsAsync(nominationId, ct);
            }));
        api.MapPost("/development-nominations", (HttpContext http, DevelopmentNominationRequest b, DevelopmentNominationService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateAsync(new CreateDevelopmentNomination(b.PersonId, b.Kind, b.Pathway, b.Rationale,
                b.ExpectedPlantBenefit, b.EvidenceReferences, b.EstimatedTrainingHours, b.EstimatedCost, b.Currency), c, ct)));
        api.MapPost("/development-nominations/{nominationId:guid}/advance", (HttpContext http, Guid nominationId, DevelopmentNominationAdvanceRequest b,
            DevelopmentNominationService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AdvanceAsync(new AdvanceDevelopmentNomination(nominationId, b.Status, b.Note), c, ct)));
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
