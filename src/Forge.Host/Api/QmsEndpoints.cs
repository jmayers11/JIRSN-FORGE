using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record CreateQmsRequest(string Code, QmsRecordKind Kind, string FacilityCode, Guid? ProgramId,
    DataMarking Markings, QmsContent Content, string? Reason);
public sealed record QmsContentRequest(int ExpectedVersion, QmsContent Content, string? Reason);
public sealed record QmsTransitionRequest(int ExpectedVersion, string Note, string? Reason);

public static class QmsEndpoints
{
    public static void MapQmsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/quality/qms").RequireAuthorization();
        // Neutral deployment configuration contains no governed record content. Scoped readers need this
        // before selecting a real facility/program record; current registration remains mandatory.
        api.MapGet("/status", async (HttpContext http, QmsService service) =>
            await PrincipalAsync(http) is null ? NotRegistered() : Results.Ok(service.Status));
        api.MapGet("/records", (HttpContext http, QmsService service, QmsRecordKind? kind, CancellationToken ct) =>
            Query(http, principal => service.ListAsync(principal, kind, ct)));
        api.MapGet("/records/{code}", (HttpContext http, QmsService service, string code, CancellationToken ct) =>
            Query(http, async principal => { var record = await service.ReadAsync(principal, code, ct); return new { record, revisions = record.Revisions }; }));
        api.MapGet("/records/{code}/revisions/{revision:int}", (HttpContext http, QmsService service, string code, int revision, CancellationToken ct) =>
            Query(http, principal => service.ReadRevisionAsync(principal, code, revision, null, ct)));
        api.MapGet("/records/{code}/effective", (HttpContext http, QmsService service, string code, DateTimeOffset? at, CancellationToken ct) =>
            Query(http, async principal =>
            {
                var instant = at ?? http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                return await service.ReadRevisionAsync(principal, code, null, instant, ct);
            }));
        api.MapPost("/records", (HttpContext http, QmsService service, CreateQmsRequest c, CancellationToken ct) =>
            Execute(() => Command(http, c.Reason, context => service.CreateAsync(new(c.Code, c.Kind, c.FacilityCode, c.ProgramId, c.Markings, c.Content), context, ct))));
        api.MapPost("/records/{code}/revisions", (HttpContext http, QmsService service, string code, QmsContentRequest c, CancellationToken ct) =>
            Execute(() => Command(http, c.Reason, context => service.CreateRevisionAsync(new(code, c.ExpectedVersion, c.Content), context, ct))));
        api.MapPost("/records/{code}/revisions/{revision:int}/edit", (HttpContext http, QmsService service, string code, int revision, QmsContentRequest c, CancellationToken ct) =>
            Execute(() => Command(http, c.Reason, context => service.EditAsync(new(code, revision, c.ExpectedVersion, c.Content), context, ct))));
        foreach (var action in new[] { "Submit", "Review", "Approve", "Effective", "Withdraw" })
        {
            var captured = action;
            api.MapPost("/records/{code}/revisions/{revision:int}/" + action.ToLowerInvariant(),
                (HttpContext http, QmsService service, string code, int revision, QmsTransitionRequest c, CancellationToken ct) =>
                    Execute(() => Command(http, c.Reason, context => service.TransitionAsync(new(code, revision, c.ExpectedVersion, captured, c.Note), context, ct))));
        }
    }

    private static async Task<IResult> Query<T>(HttpContext http, Func<ForgePrincipal, Task<T>> read)
    {
        var principal = await PrincipalAsync(http);
        return principal is null ? NotRegistered() : await Execute(async () => Results.Ok(await read(principal)));
    }
    private static async Task<IResult> Execute(Func<Task<IResult>> execute)
    {
        try { return await execute(); }
        catch (CommandRejectedException rejected) when (rejected.Code == "qms_not_configured")
        {
            return Results.Problem(title: "QMS organization configuration required", detail: rejected.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable, extensions: new Dictionary<string, object?> { ["code"] = rejected.Code });
        }
    }
}
