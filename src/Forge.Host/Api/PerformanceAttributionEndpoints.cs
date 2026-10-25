using Forge.Host.Workforce;
using Forge.Kernel.Security;
using Forge.Quality;
using Forge.Quality.Contracts;
using Forge.Workforce;
using Forge.Workforce.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record CodedTimeBody(Guid PersonId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, CodedTimeCode Code, bool Paid, string Note, string? Reason,
    string? WorkCenterCode = null, string? MachineCode = null, long? OrderNumber = null, int? Sequence = null, Guid? SupersedesEntryId = null);

public sealed record CodedTimeReviewBody(bool Approve, string? Reason);

public sealed record AttributionBody(DefectCause Cause, bool EmployeeOpportunity, string Evidence, string? Reason, decimal? FailedQuantity = null, long? SupersedesAttributionId = null);

public sealed record AttributionReviewBody(bool Verify, string? Reason);

public sealed record CoverageBody(Guid PersonId, string DestinationWorkCenter, string? MachineCode, string ActivityCode, DateTimeOffset PlannedStart, DateTimeOffset PlannedEnd,
    string CoverageReason, decimal GoalGoodUnits, int GoalProductiveMinutes, string GoalRationale, string? Reason, long? OrderNumber = null, int? Sequence = null,
    decimal? ForecastConservative = null, decimal? ForecastExpected = null, decimal? ForecastOptimistic = null, string? ForecastConfidence = null, string? ForecastBasis = null);

public sealed record CoverageGoalBody(decimal GoodUnits, int ProductiveMinutes, string Rationale, string? Reason, bool TimeAdjusted = false);

public sealed record CoverageComparabilityBody(bool Comparable, string? ExclusionReason, string? Reason);

public sealed record CoverageStepBody(string? Reason);

public sealed record OnboardingRequirementBody(string? WorkCenterCode, string QualificationCode, int DueDays, string? Note, string? Reason);

public sealed record TrainingRequestBody(Guid PersonId, string? QualificationCode, string? Title, DateOnly? DueOn, string? Reason);

public sealed record TrainingScheduleBody(Guid? TrainerPersonId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Location, string? Reason);

public sealed record TrainingNoteBody(string Note);

public sealed record TrainingScanBody(int? WithinDays, string? Reason);

/// <summary>Cause-adjusted performance (Sprint 10 §16): coded time, verified first-pass attribution and the factual report built from them.</summary>
public static class PerformanceAttributionEndpoints
{
    public static void MapPerformanceAttribution(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var workforce = app.MapGroup("/api/workforce").RequireAuthorization();
        workforce.MapGet("/cause-adjusted-performance", async (HttpContext http, Guid personId, DateTimeOffset? from, DateTimeOffset? to, decimal? minCoverage,
            CauseAdjustedPerformanceService service, TimeProvider time, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var end = to ?? time.GetUtcNow();
            return Results.Ok(await service.BuildAsync(principal, personId, from ?? end.AddDays(-7), end, minCoverage ?? 80m, ct));
        });
        // Review cards for a quarter or a year: every employee's verified evidence in one read (the Command Center's Employees tab and its printout).
        workforce.MapGet("/performance-cards", async (HttpContext http, DateTimeOffset? from, DateTimeOffset? to, Guid[]? personIds, bool? includeInactive,
            EmployeePerformanceCardService service, TimeProvider time, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var end = to ?? time.GetUtcNow();
            return Results.Ok(await service.BuildAsync(principal, from ?? end.AddDays(-91), end, personIds, includeInactive ?? false, ct));
        });
        workforce.MapGet("/coded-time", (HttpContext http, Guid? personId, DateTimeOffset? from, DateTimeOffset? to, TimeProvider time, CancellationToken _) =>
            ReadStore<ICodedTimeLookup>(http, Permissions.WorkforceRead, async (l, ct) =>
            {
                var end = to ?? time.GetUtcNow();
                return await l.ListCodedTimeAsync(personId, from ?? end.AddDays(-31), end, ct);
            }));
        workforce.MapPost("/coded-time", (HttpContext http, CodedTimeBody b, CodedTimeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordAsync(new RecordCodedTime(b.PersonId, b.StartsAt, b.EndsAt, b.Code, b.Paid, b.Note, b.WorkCenterCode, b.MachineCode,
                b.OrderNumber, b.Sequence, b.SupersedesEntryId), c, ct)));
        workforce.MapPost("/coded-time/{id:guid}/review", (HttpContext http, Guid id, CodedTimeReviewBody b, CodedTimeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReviewAsync(new ReviewCodedTime(id, b.Approve), c, ct)));

        workforce.MapGet("/coverage", async (HttpContext http, Guid personId, DateTimeOffset? from, DateTimeOffset? to, CoverageContributionService service, TimeProvider time, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var end = to ?? time.GetUtcNow();
            return Results.Ok(await service.BuildAsync(principal, personId, from ?? end.AddDays(-92), end, ct));
        });
        workforce.MapPost("/coverage", (HttpContext http, CoverageBody b, CoverageService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.PlanAsync(new PlanCoverageAssignment(b.PersonId, b.DestinationWorkCenter, b.MachineCode, b.ActivityCode, b.PlannedStart, b.PlannedEnd,
                b.CoverageReason, b.GoalGoodUnits, b.GoalProductiveMinutes, b.GoalRationale, b.OrderNumber, b.Sequence, b.ForecastConservative, b.ForecastExpected,
                b.ForecastOptimistic, b.ForecastConfidence, b.ForecastBasis), c, ct)));
        workforce.MapPost("/coverage/{id:guid}/start", (HttpContext http, Guid id, CoverageStepBody b, CoverageService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.StartAsync(new StartCoverage(id), c, ct)));
        workforce.MapPost("/coverage/{id:guid}/complete", (HttpContext http, Guid id, CoverageStepBody b, CoverageService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CompleteAsync(new CompleteCoverage(id), c, ct)));
        workforce.MapPost("/coverage/{id:guid}/cancel", (HttpContext http, Guid id, CoverageStepBody b, CoverageService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelAsync(new CancelCoverage(id), c, ct)));
        workforce.MapPost("/coverage/{id:guid}/goal", (HttpContext http, Guid id, CoverageGoalBody b, CoverageService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReviseGoalAsync(new ReviseCoverageGoal(id, b.GoodUnits, b.ProductiveMinutes, b.Rationale, b.TimeAdjusted), c, ct)));
        workforce.MapPost("/coverage/{id:guid}/comparability", (HttpContext http, Guid id, CoverageComparabilityBody b, CoverageService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReviewComparabilityAsync(new ReviewCoverageComparability(id, b.Comparable, b.ExclusionReason), c, ct)));

        // New-employee training and training requests: supervisors ask, technical trainers are notified and schedule.
        workforce.MapGet("/onboarding-requirements", (HttpContext http, CancellationToken _) =>
            ReadStore<ITrainingRequestStore>(http, Permissions.TrainingRequestsRead, async (s, ct) => await s.ListRequirementsAsync(true, ct)));
        workforce.MapPost("/onboarding-requirements", (HttpContext http, OnboardingRequirementBody b, TrainingRequestService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DefineRequirementAsync(new DefineOnboardingRequirement(b.WorkCenterCode, b.QualificationCode, b.DueDays, b.Note), c, ct)));
        workforce.MapPost("/onboarding-requirements/{id:guid}/retire", (HttpContext http, Guid id, CoverageStepBody b, TrainingRequestService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RetireRequirementAsync(new RetireOnboardingRequirement(id), c, ct)));
        workforce.MapPost("/workers/{personId:guid}/onboarding", (HttpContext http, Guid personId, CoverageStepBody b, TrainingRequestService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.StartOnboardingAsync(new StartOnboarding(personId), c, ct)));
        workforce.MapGet("/training-requests", (HttpContext http, Guid? personId, bool? open, CancellationToken _) =>
            ReadStore<ITrainingRequestStore>(http, Permissions.TrainingRequestsRead, async (s, ct) => await s.ListRequestsAsync(personId, open ?? false, ct)));
        workforce.MapGet("/training-requests/{id:guid}/history", (HttpContext http, Guid id, CancellationToken _) =>
            ReadStore<ITrainingRequestStore>(http, Permissions.TrainingRequestsRead, async (s, ct) => await s.ListRequestEventsAsync(id, ct)));
        workforce.MapPost("/training-requests", (HttpContext http, TrainingRequestBody b, TrainingRequestService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RequestAsync(new RequestTraining(b.PersonId, b.QualificationCode, b.Title, b.DueOn), c, ct)));
        workforce.MapPost("/training-requests/{id:guid}/schedule", (HttpContext http, Guid id, TrainingScheduleBody b, TrainingRequestService s, CancellationToken ct) =>
            Command(http, b.Reason ?? "Training scheduled", c => s.ScheduleAsync(new ScheduleTraining(id, b.TrainerPersonId, b.StartsAt, b.EndsAt, b.Location), c, ct)));
        workforce.MapPost("/training-requests/{id:guid}/complete", (HttpContext http, Guid id, TrainingNoteBody b, TrainingRequestService s, CancellationToken ct) =>
            Command(http, b.Note, c => s.CompleteAsync(new CompleteTraining(id, b.Note), c, ct)));
        workforce.MapPost("/training-requests/{id:guid}/cancel", (HttpContext http, Guid id, TrainingNoteBody b, TrainingRequestService s, CancellationToken ct) =>
            Command(http, b.Note, c => s.CancelAsync(new CancelTraining(id, b.Note), c, ct)));
        workforce.MapPost("/training-requests/expiry-scan", (HttpContext http, TrainingScanBody b, TrainingRequestService s, CancellationToken ct) =>
            Command(http, b.Reason ?? "Check for expiring qualifications", c => s.OpenExpiryRequestsAsync(new OpenExpiryTrainingRequests(b.WithinDays ?? TrainingRequestService.DefaultExpiryWindowDays), c, ct)));

        var quality = app.MapGroup("/api/quality").RequireAuthorization();
        quality.MapGet("/attributions", (HttpContext http, long[]? inspection, CancellationToken _) =>
            ReadStore<IFirstPassAttributionLookup>(http, Permissions.QualityRead, async (l, ct) => await l.ListAttributionsAsync((inspection ?? []).Take(500).ToArray(), ct)));
        quality.MapPost("/inspections/{id:long}/attribution", (HttpContext http, long id, AttributionBody b, FirstPassAttributionService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ProposeAsync(new ProposeFirstPassAttribution(id, b.Cause, b.EmployeeOpportunity, b.Evidence, b.FailedQuantity, b.SupersedesAttributionId), c, ct)));
        quality.MapPost("/attributions/{id:long}/verify", (HttpContext http, long id, AttributionReviewBody b, FirstPassAttributionService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.VerifyAsync(new VerifyFirstPassAttribution(id, b.Verify), c, ct)));
    }
}
