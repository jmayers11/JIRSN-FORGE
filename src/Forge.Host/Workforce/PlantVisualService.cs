using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources;
using Forge.Resources.Contracts;
using Forge.Workforce;
using Forge.Workforce.Contracts;

namespace Forge.Host.Workforce;

public sealed record WorkerPhotoChanged(Guid PersonId, string? PreviousSha256, string? CurrentSha256) : IDomainEvent
{
    public static string EventType => nameof(WorkerPhotoChanged);
    public static int SchemaVersion => 1;
}

public sealed record SitePlantMapChanged(string SiteCode, string? PreviousSha256, string? CurrentSha256) : IDomainEvent
{
    public static string EventType => nameof(SitePlantMapChanged);
    public static int SchemaVersion => 1;
}

/// <summary>Audited owner commands for worker photos and site maps; image bytes use Engineering's bounded content store.</summary>
public sealed class PlantVisualService(CommandPipeline pipeline)
{
    public const int MaximumImageBytes = 8 * 1024 * 1024;
    public static readonly string[] SupportedImageTypes = ["image/png", "image/jpeg", "image/webp"];

    public static void ValidateImage(byte[] bytes, string contentType)
    {
        if (bytes is null || bytes.Length is < 1 or > MaximumImageBytes)
            throw new CommandRejectedException("image_size", "Plant images must be between 1 byte and 8 MiB.");
        var valid = contentType switch
        {
            "image/png" => bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
            "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };
        if (!valid) throw new CommandRejectedException("invalid_image", "Choose a valid PNG, JPEG or WebP image.");
    }

    public Task<CommandResult> SetWorkerPhotoAsync(PersonId personId, byte[] bytes, string contentType, CommandContext context, CancellationToken ct)
    {
        ValidateImage(bytes, contentType);
        var spec = new CommandSpec("Workforce", "SetWorkerPhoto", Permissions.WorkforcePhotosManage, ResourceRef.Of("Worker", personId), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, new { personId, sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), contentType }, context, async (uow, now) =>
        {
            var people = uow.Store<IWorkforceLookup>();
            if (!await people.WorkerExistsAsync(personId.Value, ct)) throw new CommandRejectedException("worker_not_found", "This person is not a registered worker.");
            var previous = await people.FindWorkerPhotoAsync(personId.Value, ct);
            var file = await uow.Store<IGeneratedDocumentContentStore>().StoreAsync(bytes, context.Actor, now, ct);
            if (previous?.Sha256 == file.Sha256 && previous.ContentType == contentType)
                return CommandOutcome.NoChange(CommandResult.NoChange(personId.ToString(), "Worker photo unchanged."));

            await uow.Store<IWorkforceStore>().SetWorkerPhotoAsync(personId, file.Sha256, contentType, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(personId.ToString(), "Worker photo saved."),
                [pipeline.Event(new WorkerPhotoChanged(personId.Value, previous?.Sha256, file.Sha256), context, "Workforce", "Worker", personId.ToString())],
                new AuditChange("WorkerPhoto", personId.ToString(), CanonicalJson.Serialize(previous), CanonicalJson.Serialize(new WorkerPhotoInfo(file.Sha256, contentType))));
        }, ct);
    }

    public Task<CommandResult> RemoveWorkerPhotoAsync(PersonId personId, CommandContext context, CancellationToken ct)
    {
        var spec = new CommandSpec("Workforce", "RemoveWorkerPhoto", Permissions.WorkforcePhotosManage, ResourceRef.Of("Worker", personId), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, new { personId }, context, async (uow, now) =>
        {
            var lookup = uow.Store<IWorkforceLookup>();
            if (!await lookup.WorkerExistsAsync(personId.Value, ct)) throw new CommandRejectedException("worker_not_found", "This person is not a registered worker.");
            var previous = await lookup.FindWorkerPhotoAsync(personId.Value, ct);
            if (previous is null) return CommandOutcome.NoChange(CommandResult.NoChange(personId.ToString(), "Worker photo is already clear."));
            await uow.Store<IWorkforceStore>().SetWorkerPhotoAsync(personId, null, null, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(personId.ToString(), "Worker photo removed."),
                [pipeline.Event(new WorkerPhotoChanged(personId.Value, previous.Sha256, null), context, "Workforce", "Worker", personId.ToString())],
                new AuditChange("WorkerPhoto", personId.ToString(), CanonicalJson.Serialize(previous), null));
        }, ct);
    }

    public Task<CommandResult> SetSiteMapAsync(string siteCode, byte[] bytes, string contentType, CommandContext context, CancellationToken ct)
    {
        ValidateImage(bytes, contentType);
        var spec = new CommandSpec("Resources", "SetSitePlantMap", Permissions.ResourcesFloorMapManage, ResourceRef.Of("Site", siteCode), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, new { siteCode, sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), contentType }, context, async (uow, now) =>
        {
            var lookup = uow.Store<IResourcesLookup>();
            if (!(await lookup.ListSitesAsync(ct)).Any(site => string.Equals(site.Code, siteCode, StringComparison.OrdinalIgnoreCase)))
                throw new CommandRejectedException("site_not_found", $"Site {siteCode} does not exist.");
            var previous = await lookup.FindSiteMapAsync(siteCode, ct);
            var file = await uow.Store<IGeneratedDocumentContentStore>().StoreAsync(bytes, context.Actor, now, ct);
            if (previous?.Sha256 == file.Sha256 && previous.ContentType == contentType)
                return CommandOutcome.NoChange(CommandResult.NoChange(siteCode, "Plant map unchanged."));
            await uow.Store<IResourcesStore>().SetSiteMapAsync(siteCode, file.Sha256, contentType, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(siteCode, "Plant map saved."),
                [pipeline.Event(new SitePlantMapChanged(siteCode, previous?.Sha256, file.Sha256), context, "Resources", "Site", siteCode)],
                new AuditChange("SitePlantMap", siteCode, CanonicalJson.Serialize(previous), CanonicalJson.Serialize(new SiteMapImageInfo(file.Sha256, contentType))));
        }, ct);
    }

    public Task<CommandResult> RemoveSiteMapAsync(string siteCode, CommandContext context, CancellationToken ct)
    {
        var spec = new CommandSpec("Resources", "RemoveSitePlantMap", Permissions.ResourcesFloorMapManage, ResourceRef.Of("Site", siteCode), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, new { siteCode }, context, async (uow, now) =>
        {
            var lookup = uow.Store<IResourcesLookup>();
            if (!(await lookup.ListSitesAsync(ct)).Any(site => string.Equals(site.Code, siteCode, StringComparison.OrdinalIgnoreCase)))
                throw new CommandRejectedException("site_not_found", $"Site {siteCode} does not exist.");
            var previous = await lookup.FindSiteMapAsync(siteCode, ct);
            if (previous is null) return CommandOutcome.NoChange(CommandResult.NoChange(siteCode, "The default plant map is already in use."));
            await uow.Store<IResourcesStore>().SetSiteMapAsync(siteCode, null, null, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(siteCode, "Custom plant map removed; the reference map will be shown."),
                [pipeline.Event(new SitePlantMapChanged(siteCode, previous.Sha256, null), context, "Resources", "Site", siteCode)],
                new AuditChange("SitePlantMap", siteCode, CanonicalJson.Serialize(previous), null));
        }, ct);
    }
}
