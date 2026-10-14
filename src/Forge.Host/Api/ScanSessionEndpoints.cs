using Forge.Host.Identification;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record CreateScanSessionRequest(string? OperationCode, long? OrderNumber, int? Sequence, string? Badge,
    string? MachineCode, Guid? PreviousSessionId, string? Reason);
public sealed record StageScanRequest(int ExpectedVersion, string Code, ScanInputMode InputMode,
    string? AdapterId, string? Symbology, string? Reason);
public sealed record InvalidateScanSessionRequest(int ExpectedVersion, string? Reason);

internal static class ScanSessionEndpoints
{
    public static void MapScanSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/identification").RequireAuthorization();
        api.MapGet("/scanners/config", (HttpContext http, ScannerOptions options) =>
            Read(http, "identification.read", _ => Task.FromResult<object?>(options.View())));
        api.MapGet("/scan-sessions/requirements", async (HttpContext http, ScanSessionService service,
            string? operationCode, long? orderNumber, int? sequence, string? machineCode, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            return Results.Ok(await service.RequirementsAsync(principal, operationCode, orderNumber, sequence, machineCode, ct));
        });
        api.MapGet("/scan-sessions/current", async (HttpContext http, IKernelUnitOfWorkFactory unitOfWork, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var channel = ScanSessionRules.Channel(principal, AuthenticationEvidence(http));
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            return Results.Ok(new { id = await uow.Store<IScanSessionStore>().CurrentAsync(channel, ct) });
        });
        api.MapGet("/scan-acceptances/{id:guid}", async (HttpContext http, Guid id, ScanSessionService service, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var accepted = await service.ReadAcceptedAsync(principal, id, ct);
            return accepted is null ? Results.NotFound() : Results.Ok(accepted);
        });
        api.MapGet("/scan-sessions/{id:guid}", async (HttpContext http, Guid id, ScanSessionService service, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var session = await service.ReadAsync(principal, id, ct);
            return session is null ? Results.NotFound() : Results.Ok(new
            {
                session.Id, session.Version, session.State, session.OperatorPersonId, session.OperationCode,
                session.OrderNumber, session.Sequence, machineCode = session.SelectedMachineCode,
                session.Selections, session.RequiredKinds, session.ManualAllowed, session.ExpiresAt,
                policy = new { code = session.Policy.PolicyCode, revision = session.Policy.PolicyRevision,
                    adoptionId = session.Policy.AdoptionCode, fingerprint = session.Policy.Fingerprint }, session.Accepted,
            });
        });
        api.MapPost("/scan-sessions", (HttpContext http, CreateScanSessionRequest body, ScanSessionService service, CancellationToken ct) =>
            Command(http, body.Reason, context => service.CreateAsync(new(body.OperationCode, body.OrderNumber, body.Sequence,
                string.IsNullOrWhiteSpace(body.Badge) ? null : new Secret(body.Badge), body.MachineCode, body.PreviousSessionId), context, ct)));
        api.MapPost("/scan-sessions/{id:guid}/stages", (HttpContext http, Guid id, StageScanRequest body, ScanSessionService service, CancellationToken ct) =>
            Command(http, body.Reason, context => service.StageAsync(id, new(body.ExpectedVersion, body.Code, body.InputMode,
                body.AdapterId, body.Symbology), context, ct)));
        api.MapPost("/scan-sessions/{id:guid}/invalidate", (HttpContext http, Guid id, InvalidateScanSessionRequest body,
            ScanSessionService service, CancellationToken ct) =>
            Command(http, body.Reason, context => service.InvalidateAsync(id, new(body.ExpectedVersion), context, ct)));
    }
}
