using Forge.Engineering;
using Forge.Engineering.Contracts;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning;
using Forge.Workforce;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record DocumentTrainingRequest(Guid PersonId, string DocumentNumber, string Revision, DateOnly? TrainedOn, string? Reason);

public sealed record TrainingPolicyRequest(TrainingPolicy Policy, DocumentType[]? DocumentTypes, string? Reason);

public sealed record ReallocationPlanRequest(ReallocationPlanProposal[] Moves);

public sealed record WorkforceRecoveryPreviewRequest(string? SafetyAreaCode, string? WorkCenterCode, string? MachineCode, string ActivityCode);

public sealed record MaterialReceiptImpactPreviewRequest(long TaskId);

/// <summary>
/// V3.9: executable capacity and presence. Every open operation answers "can this happen now, and why not?"; each
/// worker's presence level; and training on controlled documents with the plant's policy for it.
/// </summary>
internal static class ExecutableEndpoints
{
    public static void MapExecutableEndpoints(this IEndpointRouteBuilder app)
    {
        var readiness = app.MapGroup("/api/readiness").RequireAuthorization();
        readiness.MapGet("/operations", async (HttpContext http, string? workCenter, ReadinessService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.EvaluateOperationsAsync(principal, workCenter, ct));
        });
        readiness.MapGet("/presence", async (HttpContext http, string? workCenter, ReadinessService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.EvaluatePresenceAsync(principal, workCenter, ct));
        });
        readiness.MapPost("/reallocation-plan-preview", async (HttpContext http, ReallocationPlanRequest request, ReadinessService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null
                ? NotRegistered()
                : Results.Ok(await s.PreviewReallocationPlanAsync(principal, request.Moves, ct));
        });
        readiness.MapPost("/workforce-recovery-preview", async (HttpContext http, WorkforceRecoveryPreviewRequest request, ReadinessService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null
                ? NotRegistered()
                : Results.Ok(await s.RecommendWorkforceRecoveryAsync(principal,
                    new AssignWork(new PersonId(Guid.Empty), request.SafetyAreaCode, request.WorkCenterCode,
                        request.MachineCode, request.ActivityCode, false), ct));
        });
        readiness.MapPost("/material-receipt-impact-preview", async (HttpContext http, MaterialReceiptImpactPreviewRequest request, MaterialReceiptImpactPreviewService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.PreviewAsync(principal, request.TaskId, ct));
        });

        var training = app.MapGroup("/api/engineering/document-training").RequireAuthorization();
        training.MapGet("/", (HttpContext http, Guid? personId, CancellationToken _) =>
            ReadStore<IDocumentTrainingLookup>(http, Permissions.CompetencyRead, async (l, ct) => new
            {
                policy = await l.GetTrainingPolicyAsync(ct),
                records = await l.ListTrainingAsync(personId is { } id ? new[] { id } : null, ct),
            }));
        training.MapPost("/", (HttpContext http, DocumentTrainingRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordTrainingAsync(new RecordDocumentTraining(new PersonId(b.PersonId), b.DocumentNumber, b.Revision, b.TrainedOn), c, ct)));
        training.MapPost("/policy", (HttpContext http, TrainingPolicyRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetTrainingPolicyAsync(new SetDocumentTrainingPolicy(b.Policy, b.DocumentTypes), c, ct)));
    }
}
