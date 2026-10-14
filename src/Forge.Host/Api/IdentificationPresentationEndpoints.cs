using Forge.Host.Identification;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record LabelTemplateRequest(string Code, string FacilityCode, int Width, int Height, IReadOnlyList<string> AllowedKinds,
    IReadOnlyList<LabelSymbology> AllowedSymbologies, IReadOnlyList<QmsDocumentReference> Documents, string? Reason);
public sealed record LabelTemplateRevisionRequest(int ExpectedVersion, int Width, int Height, IReadOnlyList<string> AllowedKinds,
    IReadOnlyList<LabelSymbology> AllowedSymbologies, IReadOnlyList<QmsDocumentReference> Documents, string? Reason);
public sealed record PresentationVersionRequest(int ExpectedVersion, string? Reason);
public sealed record LabelIssuanceRequest(string SubjectCode, string TemplateCode, int TemplateVersion, LabelSymbology Symbology, int Copies, string FacilityCode, string? Reason);
public sealed record LabelPrintRequest(int ExpectedVersion, string? PrinterCode, string? Reason);
public sealed record LabelPrintObservationRequest(int ExpectedVersion, LabelPrintState Outcome, string Observation, bool ObservedPrinted, bool ObservedAttached, string? Reason);
public sealed record IdentificationAliasRequest(string Domain, string Alias, string SubjectCode, string FacilityCode, string? Reason);
public sealed record IdentificationAliasReplacementRequest(int ExpectedVersion, string SubjectCode, string FacilityCode, string? Reason);
public sealed record ResolveIdentificationAliasRequest(string Domain, string Alias);

internal static class IdentificationPresentationEndpoints
{
    public static void MapIdentificationPresentationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/identification").RequireAuthorization();
        group.MapGet("/labels/config", (HttpContext h, string subjectCode, string facilityCode, LabelService s, CancellationToken ct) =>
            ReadPresentation(h, p => s.ConfigurationAsync(p, subjectCode, facilityCode, ct)));
        group.MapGet("/labels", (HttpContext h, string subjectCode, int? take, string? after, LabelService s, CancellationToken ct) =>
            ReadPresentation(h, async p => await s.ListAsync(p, subjectCode, take ?? 50, after, ct)));
        group.MapGet("/labels/issuances/{id:guid}", (HttpContext h, Guid id, LabelService s, CancellationToken ct) => ReadPresentation(h, async p =>
        { var result = await s.IssuanceAsync(p, id, ct); return new { issuance = result.Issuance, copies = result.Copies }; }));
        group.MapGet("/labels/{id:guid}", (HttpContext h, Guid id, LabelService s, CancellationToken ct) => ReadPresentation(h, async p => await s.DetailAsync(p, id, ct)));
        group.MapPost("/labels", (HttpContext h, LabelIssuanceRequest b, LabelService s, CancellationToken ct) =>
            Command(h, b.Reason, c => s.IssueAsync(new(b.SubjectCode, b.TemplateCode, b.TemplateVersion, b.Symbology, b.Copies, b.FacilityCode), c, ct)));
        foreach (var action in new[] { "Approve", "Reprint", "Replace", "Void" })
        {
            var captured = action;
            group.MapPost("/labels/{id:guid}/" + action.ToLowerInvariant(), (HttpContext h, Guid id, PresentationVersionRequest b, LabelService s, CancellationToken ct) =>
                Command(h, b.Reason, c => s.ChangeAsync(new(id, b.ExpectedVersion, captured), c, ct)));
        }
        group.MapPost("/labels/{id:guid}/print-attempts", (HttpContext h, Guid id, LabelPrintRequest b, LabelService s, CancellationToken ct) =>
            Command(h, b.Reason, c => s.RequestPrintAsync(new(id, b.ExpectedVersion, b.PrinterCode), c, ct)));
        group.MapPost("/print-attempts/{id:guid}/outcome", (HttpContext h, Guid id, LabelPrintObservationRequest b, LabelService s, CancellationToken ct) =>
            Command(h, b.Reason, c => s.ObservePrintAsync(new(id, b.ExpectedVersion, b.Outcome, b.Observation, b.ObservedPrinted, b.ObservedAttached), c, ct)));
        group.MapGet("/print-attempts/{id:guid}/artifact", async (HttpContext h, Guid id, LabelService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(h); if (principal is null) return NotRegistered();
            try
            {
                var artifact = await s.ArtifactAsync(principal, id, ct);
                h.Response.Headers["X-Content-Type-Options"] = "nosniff";
                h.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'; style-src 'none'";
                h.Response.Headers["Cache-Control"] = "no-store";
                return Results.File(artifact.Bytes, artifact.MediaType, artifact.FileName);
            }
            catch (ForgeAuthorizationException) { return NotFound(); }
            catch (CommandRejectedException) { return NotFound(); }
        });
        group.MapGet("/templates", (HttpContext h, int? take, string? after, LabelService s, CancellationToken ct) =>
            ReadPresentation(h, async p => await s.TemplatesAsync(p, take ?? 50, after, ct)));
        group.MapGet("/templates/{code}/revisions/{revision:int}", (HttpContext h, string code, int revision, LabelService s, CancellationToken ct) =>
            ReadPresentation(h, async p => await s.ReadTemplateAsync(p, code, revision, ct)));
        group.MapPost("/templates", (HttpContext h, LabelTemplateRequest b, LabelService s, CancellationToken ct) =>
            Command(h, b.Reason, c => s.CreateTemplateAsync(new(b.Code, b.FacilityCode, b.Width, b.Height, b.AllowedKinds, b.AllowedSymbologies, b.Documents), c, ct)));
        group.MapPost("/templates/{code}/revisions", (HttpContext h, string code, LabelTemplateRevisionRequest b, LabelService s, CancellationToken ct) =>
            Command(h, b.Reason, c => s.ReviseTemplateAsync(new(code, b.ExpectedVersion, b.Width, b.Height, b.AllowedKinds, b.AllowedSymbologies, b.Documents), c, ct)));
        foreach (var withdraw in new[] { false, true })
        {
            var captured = withdraw;
            group.MapPost("/templates/{code}/revisions/{revision:int}/" + (withdraw ? "withdraw" : "approve"),
                (HttpContext h, string code, int revision, PresentationVersionRequest b, LabelService s, CancellationToken ct) =>
                    Command(h, b.Reason, c => s.TransitionTemplateAsync(new(code, revision, b.ExpectedVersion, captured), c, ct)));
        }
        group.MapGet("/aliases", (HttpContext h, string subjectCode, int? take, string? after, LabelService s, CancellationToken ct) =>
            ReadPresentation(h, async p => await s.AliasesAsync(p, subjectCode, take ?? 50, after, ct)));
        group.MapPost("/aliases/resolve", async (HttpContext h, ResolveIdentificationAliasRequest b, IdentificationResolver resolver, CancellationToken ct) =>
        {
            var principal=await PrincipalAsync(h);if(principal is null)return NotRegistered();
            try { h.Response.Headers["Cache-Control"]="no-store";return Results.Ok(await resolver.ResolveAliasAsync(principal,b.Domain,b.Alias,ct)); }
            catch(IdentificationException error)
            {return Results.Problem(statusCode:error.Code=="identification_not_configured"?503:404,extensions:new Dictionary<string,object?>{["code"]=error.Code});}
        });
        group.MapGet("/aliases/{id:guid}", (HttpContext h, Guid id, LabelService s, CancellationToken ct) =>
            ReadPresentation(h, async p => await s.AliasAsync(p, id, ct)));
        group.MapPost("/aliases", (HttpContext h, IdentificationAliasRequest b, LabelService s, CancellationToken ct) =>
            Command(h, b.Reason, c => s.CreateAliasAsync(new(b.Domain, b.Alias, b.SubjectCode, b.FacilityCode), c, ct)));
        group.MapPost("/aliases/{id:guid}/replace", (HttpContext h, Guid id, IdentificationAliasReplacementRequest b, LabelService s, CancellationToken ct) =>
            Command(h, b.Reason, c => s.ReplaceAliasAsync(new(id, b.ExpectedVersion, b.SubjectCode, b.FacilityCode), c, ct)));
        group.MapPost("/aliases/{id:guid}/void", (HttpContext h, Guid id, PresentationVersionRequest b, LabelService s, CancellationToken ct) =>
            Command(h, b.Reason, c => s.VoidAliasAsync(new(id, b.ExpectedVersion), c, ct)));
    }

    private static async Task<IResult> ReadPresentation(HttpContext http, Func<ForgePrincipal, Task<object>> read)
    {
        var principal = await PrincipalAsync(http); if (principal is null) return NotRegistered();
        try { http.Response.Headers["Cache-Control"] = "no-store"; return Results.Ok(await read(principal)); }
        catch (ForgeAuthorizationException) { return NotFound(); }
        catch (CommandRejectedException error) when (error.Code == "identification_not_configured")
        { return Results.Problem(statusCode: 503, extensions: new Dictionary<string, object?> { ["code"] = error.Code }); }
        catch (CommandRejectedException) { return NotFound(); }
    }
    private static IResult NotFound() => Results.Problem(title: "Presentation unavailable", statusCode: 404,
        extensions: new Dictionary<string, object?> { ["code"] = "presentation_not_found" });
}
