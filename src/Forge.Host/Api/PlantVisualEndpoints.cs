using Forge.Engineering.Contracts;
using Forge.Host.Workforce;
using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public static class PlantVisualEndpoints
{
    public static void MapPlantVisualEndpoints(this IEndpointRouteBuilder app)
    {
        var resources = app.MapGroup("/api/resources").RequireAuthorization();
        resources.MapPost("/sites/{siteCode}/map", async (HttpContext http, string siteCode, PlantVisualService service, CancellationToken ct) =>
        {
            var (bytes, contentType, error) = await ReadImage(http, ct);
            if (error is not null) return error;
            return await Command(http, http.Request.Headers["X-Change-Reason"].ToString(), context => service.SetSiteMapAsync(siteCode, bytes!, contentType!, context, ct));
        });
        resources.MapDelete("/sites/{siteCode}/map", (HttpContext http, string siteCode, PlantVisualService service, CancellationToken ct) =>
            Command(http, http.Request.Headers["X-Change-Reason"].ToString(), context => service.RemoveSiteMapAsync(siteCode, context, ct)));
        resources.MapGet("/sites/{siteCode}/map/image", (HttpContext http, string siteCode, CancellationToken ct) => ProtectedImage(http, Permissions.ResourcesRead,
            async (uow, token) => await uow.Store<IResourcesLookup>().FindSiteMapAsync(siteCode, token), ct));

        var workforce = app.MapGroup("/api/workforce").RequireAuthorization();
        workforce.MapPost("/workers/{personId:guid}/photo", async (HttpContext http, Guid personId, PlantVisualService service, CancellationToken ct) =>
        {
            var (bytes, contentType, error) = await ReadImage(http, ct);
            if (error is not null) return error;
            return await Command(http, http.Request.Headers["X-Change-Reason"].ToString(), context => service.SetWorkerPhotoAsync(new PersonId(personId), bytes!, contentType!, context, ct));
        });
        workforce.MapDelete("/workers/{personId:guid}/photo", (HttpContext http, Guid personId, PlantVisualService service, CancellationToken ct) =>
            Command(http, http.Request.Headers["X-Change-Reason"].ToString(), context => service.RemoveWorkerPhotoAsync(new PersonId(personId), context, ct)));
        workforce.MapGet("/workers/{personId:guid}/photo", (HttpContext http, Guid personId, CancellationToken ct) => ProtectedImage(http, Permissions.WorkforceRead,
            async (uow, token) => await uow.Store<IWorkforceLookup>().FindWorkerPhotoAsync(personId, token), ct));
    }

    private static async Task<(byte[]? Bytes, string? ContentType, IResult? Error)> ReadImage(HttpContext http, CancellationToken ct)
    {
        var contentType = http.Request.ContentType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (contentType is null || !PlantVisualService.SupportedImageTypes.Contains(contentType, StringComparer.Ordinal))
            return (null, null, Results.Problem(title: "Unsupported image type", detail: "Upload a PNG, JPEG or WebP image.", statusCode: StatusCodes.Status415UnsupportedMediaType));
        if (http.Request.ContentLength is > PlantVisualService.MaximumImageBytes)
            return (null, null, Results.Problem(title: "Image is too large", detail: "Plant images are limited to 8 MiB.", statusCode: StatusCodes.Status413PayloadTooLarge));

        await using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await http.Request.Body.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (output.Length + count > PlantVisualService.MaximumImageBytes)
                return (null, null, Results.Problem(title: "Image is too large", detail: "Plant images are limited to 8 MiB.", statusCode: StatusCodes.Status413PayloadTooLarge));
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        var bytes = output.ToArray();
        try { PlantVisualService.ValidateImage(bytes, contentType); }
        catch (Forge.Kernel.Commands.CommandRejectedException)
        {
            return (null, null, Results.Problem(title: "Invalid image", detail: "Choose a valid PNG, JPEG or WebP image.", statusCode: StatusCodes.Status400BadRequest));
        }
        return (bytes, contentType, null);
    }

    private static async Task<IResult> ProtectedImage<TImage>(HttpContext http, string permission,
        Func<IKernelUnitOfWork, CancellationToken, Task<TImage?>> find, CancellationToken ct)
        where TImage : class
    {
        var principal = await PrincipalAsync(http);
        if (principal is null) return NotRegistered();
        var authorizer = http.RequestServices.GetRequiredService<IForgeAuthorizer>();
        var decision = authorizer.Authorize(principal, permission, ResourceRef.Of(permission));
        if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision);

        await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(ct);
        var reference = await find(uow, ct);
        if (reference is null) return Results.NotFound();
        var (sha, contentType) = reference switch
        {
            SiteMapImageInfo map => (map.Sha256, map.ContentType),
            WorkerPhotoInfo photo => (photo.Sha256, photo.ContentType),
            _ => throw new InvalidOperationException("Unsupported protected image reference."),
        };
        var bytes = await uow.Store<IDocumentLookup>().ReadFileAsync(sha, ct);
        if (bytes is null) return Results.NotFound();
        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(bytes, contentType);
    }
}
