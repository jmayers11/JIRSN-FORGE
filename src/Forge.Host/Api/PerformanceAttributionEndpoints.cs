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

        var quality = app.MapGroup("/api/quality").RequireAuthorization();
        quality.MapGet("/attributions", (HttpContext http, long[]? inspection, CancellationToken _) =>
            ReadStore<IFirstPassAttributionLookup>(http, Permissions.QualityRead, async (l, ct) => await l.ListAttributionsAsync((inspection ?? []).Take(500).ToArray(), ct)));
        quality.MapPost("/inspections/{id:long}/attribution", (HttpContext http, long id, AttributionBody b, FirstPassAttributionService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ProposeAsync(new ProposeFirstPassAttribution(id, b.Cause, b.EmployeeOpportunity, b.Evidence, b.FailedQuantity, b.SupersedesAttributionId), c, ct)));
        quality.MapPost("/attributions/{id:long}/verify", (HttpContext http, long id, AttributionReviewBody b, FirstPassAttributionService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.VerifyAsync(new VerifyFirstPassAttribution(id, b.Verify), c, ct)));
    }
}
