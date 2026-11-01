using Forge.Host.Procurement;
using Forge.Host.Quality;
using Forge.Kernel.Security;
using Forge.Procurement;
using Forge.Quality;
using Forge.Quality.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record SpcPolicyRequest(decimal MinimumCpk, decimal CriticalMinimumCpk, int MinimumPoints, bool NotifyOnSignal, string? Reason);

public sealed record SpcCriticalRequest(string PartNumber, int Sequence, int Number, bool Critical, string? Reason);

public sealed record SpcAcknowledgeRequest(string? Note, string? Reason);

public sealed record ScorecardPolicyRequest(int LateToleranceDays, int? EarlyLimitDays, decimal GradeAOnTimePercent, decimal GradeAQualityPercent,
    decimal GradeBOnTimePercent, decimal GradeBQualityPercent, int PeriodDays, string? Reason);

/// <summary>Statistical process control and supplier scorecards (owner decisions 2026-11-01). Neither holds anything; people decide.</summary>
internal static class SpcEndpoints
{
    public static void MapSpcEndpoints(this IEndpointRouteBuilder app)
    {
        var spc = app.MapGroup("/api/quality/spc").RequireAuthorization();
        spc.MapGet("/charts", (HttpContext http, string? part, SpcChartService s, CancellationToken _) =>
            Read(http, Permissions.QualityRead, async principal => await s.SummariesAsync(string.IsNullOrWhiteSpace(part) ? null : part, http.RequestAborted)));
        spc.MapGet("/chart", (HttpContext http, string part, int sequence, int number, SpcChartService s, CancellationToken _) =>
            Read(http, Permissions.QualityRead, async principal => await s.ChartAsync(new SpcCharacteristicKey(part.Trim().ToUpperInvariant(), sequence, number), http.RequestAborted)));
        spc.MapGet("/policy", (HttpContext http, SpcChartService s, CancellationToken _) =>
            Read(http, Permissions.QualityRead, async principal =>
            {
                var (policy, critical) = await s.PolicyAsync(http.RequestAborted);
                return new { policy, critical };
            }));
        spc.MapPost("/policy", (HttpContext http, SpcPolicyRequest b, SpcService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetPolicyAsync(new SetSpcPolicy(b.MinimumCpk, b.CriticalMinimumCpk, b.MinimumPoints, b.NotifyOnSignal), c, ct)));
        spc.MapPost("/critical", (HttpContext http, SpcCriticalRequest b, SpcService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.MarkCriticalAsync(new MarkCriticalCharacteristic(b.PartNumber, b.Sequence, b.Number, b.Critical), c, ct)));
        spc.MapGet("/signals", (HttpContext http, bool? open, SpcChartService s, CancellationToken _) =>
            Read(http, Permissions.QualityRead, async principal => await s.SignalsAsync(open ?? true, http.RequestAborted)));
        spc.MapPost("/signals/{id:long}/acknowledge", (HttpContext http, long id, SpcAcknowledgeRequest b, SpcService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AcknowledgeAsync(new AcknowledgeSpcSignal(id, b.Note ?? string.Empty), c, ct)));

        var scorecard = app.MapGroup("/api/procurement/scorecard").RequireAuthorization();
        scorecard.MapGet(string.Empty, (HttpContext http, int? days, SupplierScorecardService s, CancellationToken _) =>
            Read(http, Permissions.PurchasingRead, async principal => await s.ReportAsync(days, http.RequestAborted)));
        scorecard.MapPost("/policy", (HttpContext http, ScorecardPolicyRequest b, ScorecardPolicyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetPolicyAsync(new SetScorecardPolicy(b.LateToleranceDays, b.EarlyLimitDays, b.GradeAOnTimePercent, b.GradeAQualityPercent,
                b.GradeBOnTimePercent, b.GradeBQualityPercent, b.PeriodDays), c, ct)));
    }
}
