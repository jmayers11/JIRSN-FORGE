using Forge.Competency;
using Forge.Competency.Contracts;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning;
using Forge.Workforce;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record WorkforceRecoveryCapacityRequest(string? SafetyAreaCode, string? WorkCenterCode, string? MachineCode, string ActivityCode, int ScheduledMinutes = 480,
    DateOnly? ShiftDate = null, string? ShiftCode = null);

public sealed record RefresherPolicyBody(int? RefresherAfterDays, int? ReacclimationMinutes, string? Reason);

/// <summary>Skill recency and proficiency (Sprint 10 §21): refresher rules and capacity forecasts on the existing recovery ranking.</summary>
public static class SkillCapacityEndpoints
{
    public static void MapSkillCapacity(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/readiness/workforce-recovery-capacity", async (HttpContext http, WorkforceRecoveryCapacityRequest request, SkillCapacityService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null
                ? NotRegistered()
                : Results.Ok(await s.RecoveryWithCapacityAsync(principal,
                    new AssignWork(new PersonId(Guid.Empty), request.SafetyAreaCode, request.WorkCenterCode, request.MachineCode, request.ActivityCode, false),
                    request.ScheduledMinutes, ct, request.ShiftDate, request.ShiftCode));
        }).RequireAuthorization();

        var competency = app.MapGroup("/api/competency").RequireAuthorization();
        competency.MapGet("/refresher-policies", (HttpContext http, CancellationToken _) =>
            ReadStore<IRefresherPolicyLookup>(http, Permissions.CompetencyRead, async (l, ct) => await l.ListRefresherPoliciesAsync(ct)));
        competency.MapPost("/qualification-types/{code}/refresher-policy", (HttpContext http, string code, RefresherPolicyBody b, RefresherPolicyService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetAsync(new SetRefresherPolicy(code, b.RefresherAfterDays, b.ReacclimationMinutes), c, ct)));
    }
}
