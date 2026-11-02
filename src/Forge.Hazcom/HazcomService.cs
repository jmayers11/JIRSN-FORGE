using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Hazcom.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Hazcom;

public sealed record SetChemical(string PartNumber, bool Hazardous, SignalWord SignalWord, IReadOnlyList<string>? Pictograms, string? HazardStatements, string? Ppe, string? Storage);

public sealed record SubmitSds(string PartNumber, string Manufacturer, string ProductName, DateOnly RevisionDate, string FileSha256, string? FileName);

public sealed record DecideSds(long SdsId, bool Approve, string? Note);

/// <summary>The EHS Coordinator confirms an SDS is still the supplier's latest; its next review is three years on.</summary>
public sealed record ReviewSds(long SdsId, string Note);

/// <summary>Marks SDS whose review falls due within 30 days (the system, daily).</summary>
public sealed record FlagSdsReviews(DateOnly Today);

/// <summary>
/// The EHS Coordinator decides whether site HazCom training covers a newly hazardous chemical (OSHA 1910.1200(h): train when a new
/// hazard is introduced). Refresher training is then requested by a person from the HazCom training view; nothing is assigned here.
/// </summary>
public sealed record ReviewHazcomTraining(string PartNumber, bool RefresherNeeded, string Note);

/// <param name="NewlyHazardous">The material became hazardous with this change: the EHS and training coordinators are asked to review training.</param>
public sealed record ChemicalChanged(string PartNumber, bool Hazardous, bool NewlyHazardous = false, string Message = "") : IDomainEvent
{
    public static string EventType => nameof(ChemicalChanged);

    public static int SchemaVersion => 1;
}

public sealed record SdsChanged(long SdsId, string PartNumber, string Status, string Message) : IDomainEvent
{
    public static string EventType => nameof(SdsChanged);

    public static int SchemaVersion => 1;
}

/// <summary>The EHS Coordinator decided on training for a newly hazardous chemical: clears the training-review notice.</summary>
public sealed record HazcomTrainingReviewed(string PartNumber, bool RefresherNeeded, string Message) : IDomainEvent
{
    public static string EventType => nameof(HazcomTrainingReviewed);

    public static int SchemaVersion => 1;
}

/// <summary>The SDS left review (approved, rejected or reviewed): clears the waiting notices.</summary>
public sealed record SdsReviewed(long SdsId, string PartNumber) : IDomainEvent
{
    public static string EventType => nameof(SdsReviewed);

    public static int SchemaVersion => 1;
}

public sealed record SdsReviewDue(long SdsId, string PartNumber, DateOnly Due, string Message) : IDomainEvent
{
    public static string EventType => nameof(SdsReviewDue);

    public static int SchemaVersion => 1;
}

public interface IHazcomStore
{
    Task UpsertChemicalAsync(ChemicalInfo chemical, CancellationToken ct);

    Task<long> InsertSdsAsync(SdsInfo sds, CancellationToken ct);

    Task ReviewTrainingAsync(string partNumber, string actor, DateTimeOffset at, string note, CancellationToken ct);

    Task DecideSdsAsync(long sdsId, SdsStatus status, string actor, DateTimeOffset at, string? note, DateOnly? reviewDue, CancellationToken ct);

    Task SupersedeAsync(string partNumber, long exceptSdsId, CancellationToken ct);

    Task ReviewSdsAsync(long sdsId, string actor, DateTimeOffset at, string note, DateOnly reviewDue, CancellationToken ct);

    /// <summary>Approved SDS due by <paramref name="by"/> not yet flagged for that due date; marks them flagged.</summary>
    Task<IReadOnlyList<SdsInfo>> TakeDueAsync(DateOnly by, CancellationToken ct);
}

/// <summary>
/// The chemical register and safety data sheets (owner decisions 2026-11-01). The EHS Coordinator keeps the register and approves or
/// rejects each SDS; buyers, engineers and inventory controllers may upload a supplier's SDS for review. Approving a newer revision
/// supersedes the old one. Nothing expires: a review falls due three years after the SDS's revision date (or its last review).
/// </summary>
public sealed class HazcomService(CommandPipeline pipeline)
{
    public const string SourceContext = "Hazcom";
    private const string Sds = "SafetyDataSheet";
    private const string Chemical = "Chemical";

    public Task<CommandResult> SetChemicalAsync(SetChemical c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(SetChemical), Permissions.HazcomManage, ResourceRef.Of(Chemical, part), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct) is null) throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            var pictograms = (c.Pictograms ?? []).Select(p => p.Trim().ToUpperInvariant()).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (pictograms.FirstOrDefault(p => !GhsPictograms.All.ContainsKey(p)) is { } bad)
                throw new CommandRejectedException("invalid_pictogram", $"{bad} is not a GHS pictogram (GHS01 to GHS09).");
            if (!c.Hazardous && (pictograms.Count > 0 || c.SignalWord != SignalWord.None))
                throw new CommandRejectedException("not_hazardous", "A material with pictograms or a signal word is hazardous.");
            static string? Text(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().Length > 4000 ? v.Trim()[..4000] : v.Trim();
            var before = await uow.Store<IHazcomLookup>().FindChemicalAsync(part, ct);
            var info = new ChemicalInfo(part, c.Hazardous, c.SignalWord, pictograms, Text(c.HazardStatements), Text(c.Ppe), Text(c.Storage), context.Actor, now);
            await uow.Store<IHazcomStore>().UpsertChemicalAsync(info, ct);
            var newly = c.Hazardous && before is not { Hazardous: true };
            var message = newly
                ? $"{part} was added as a hazardous chemical ({c.SignalWord}). Decide whether site HazCom training covers it or employees who use it need refresher training."
                : $"{part} is {(c.Hazardous ? $"a hazardous chemical ({c.SignalWord})" : "not hazardous")}.";
            return new CommandOutcome(CommandResult.Ok(part, newly ? $"{part} is a hazardous chemical ({c.SignalWord}). The EHS and training coordinators were asked to review HazCom training." : message),
                [pipeline.Event(new ChemicalChanged(part, c.Hazardous, newly, message), context, SourceContext, Chemical, part)],
                new AuditChange(Chemical, part, before is null ? null : CanonicalJson.Serialize(before), CanonicalJson.Serialize(info)));
        }, ct);
    }

    public Task<CommandResult> ReviewTrainingAsync(ReviewHazcomTraining c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(ReviewHazcomTraining), Permissions.HazcomManage, ResourceRef.Of(Chemical, part));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var chemical = await uow.Store<IHazcomLookup>().FindChemicalAsync(part, ct);
            if (chemical is not { Hazardous: true }) throw new CommandRejectedException("not_hazardous", $"{part} is not a hazardous chemical in the register.");
            var note = BusinessCode.RequireName(c.Note, "Note");
            var recorded = (c.RefresherNeeded ? "Refresher training needed: " : "Covered by site HazCom training: ") + note;
            await uow.Store<IHazcomStore>().ReviewTrainingAsync(part, context.Actor, now, recorded, ct);
            var message = c.RefresherNeeded
                ? $"HazCom refresher training is needed for {part}. Request it for the people who use it from the HazCom training view."
                : $"Site HazCom training covers {part}.";
            return new CommandOutcome(CommandResult.Ok(part, message),
                [pipeline.Event(new HazcomTrainingReviewed(part, c.RefresherNeeded, message), context, SourceContext, Chemical, part)],
                new AuditChange(Chemical, part, CanonicalJson.Serialize(chemical), CanonicalJson.Serialize(chemical with { TrainingReviewDue = false, TrainingReviewedBy = context.Actor, TrainingReviewedAt = now, TrainingReviewNote = recorded })));
        }, ct);
    }

    public Task<CommandResult> SubmitAsync(SubmitSds c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(SubmitSds), Permissions.HazcomSubmit, ResourceRef.Of(Sds, part));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct) is null) throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            var sha = (c.FileSha256 ?? string.Empty).Trim().ToLowerInvariant();
            if (sha.Length != 64 || await uow.Store<IDocumentLookup>().FindFileAsync(sha, ct) is null)
                throw new CommandRejectedException("file_required", "Upload the SDS file first.");
            var today = await uow.SiteDateAsync(now, ct);
            if (c.RevisionDate > today) throw new CommandRejectedException("invalid_revision_date", "The SDS revision date cannot be in the future.");
            var manufacturer = BusinessCode.RequireName(c.Manufacturer, "Manufacturer");
            var product = BusinessCode.RequireName(c.ProductName, "Product name");
            var sds = new SdsInfo(0, part, manufacturer, product, c.RevisionDate, sha, string.IsNullOrWhiteSpace(c.FileName) ? null : c.FileName.Trim(), SdsStatus.Submitted,
                context.Actor, now, null, null, null, null, null, null);
            var id = await uow.Store<IHazcomStore>().InsertSdsAsync(sds, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            var message = $"New safety data sheet for {part} ({product}, {manufacturer}, revised {c.RevisionDate:yyyy-MM-dd}) waits for EHS review.";
            return new CommandOutcome(CommandResult.Ok(key, message),
                [pipeline.Event(new SdsChanged(id, part, nameof(SdsStatus.Submitted), message), context, SourceContext, Sds, key)],
                new AuditChange(Sds, key, null, CanonicalJson.Serialize(sds with { SdsId = id })));
        }, ct);
    }

    public Task<CommandResult> DecideAsync(DecideSds c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.SdsId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(DecideSds), Permissions.HazcomManage, ResourceRef.Of(Sds, key), ReasonRequired: !c.Approve);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var sds = await uow.Store<IHazcomLookup>().FindSdsAsync(c.SdsId, ct) ?? throw new CommandRejectedException("sds_not_found", $"SDS {key} does not exist.");
            if (sds.Status != SdsStatus.Submitted) throw new CommandRejectedException("not_submitted", $"SDS {key} is {sds.Status}.");
            var store = uow.Store<IHazcomStore>();
            var current = await uow.Store<IHazcomLookup>().CurrentSdsAsync(sds.PartNumber, ct);
            if (c.Approve && current is not null && current.RevisionDate > sds.RevisionDate)
                throw new CommandRejectedException("older_revision", $"The approved SDS for {sds.PartNumber} is newer ({current.RevisionDate:yyyy-MM-dd}); reject this one.");
            var today = await uow.SiteDateAsync(now, ct);
            var due = c.Approve ? (sds.RevisionDate.AddYears(3) > today ? sds.RevisionDate.AddYears(3) : today) : (DateOnly?)null;
            var note = string.IsNullOrWhiteSpace(c.Note) ? context.Reason : c.Note.Trim();
            await store.DecideSdsAsync(c.SdsId, c.Approve ? SdsStatus.Approved : SdsStatus.Rejected, context.Actor, now, note, due, ct);
            if (c.Approve) await store.SupersedeAsync(sds.PartNumber, c.SdsId, ct);
            var message = c.Approve
                ? $"SDS for {sds.PartNumber} ({sds.ProductName}, revised {sds.RevisionDate:yyyy-MM-dd}) approved; review due {due:yyyy-MM-dd}."
                : $"SDS for {sds.PartNumber} ({sds.ProductName}) rejected: {note}";
            return new CommandOutcome(CommandResult.Ok(key, message),
                [pipeline.Event(new SdsChanged(c.SdsId, sds.PartNumber, c.Approve ? nameof(SdsStatus.Approved) : nameof(SdsStatus.Rejected), message), context, SourceContext, Sds, key),
                 pipeline.Event(new SdsReviewed(c.SdsId, sds.PartNumber), context, SourceContext, Sds, key)],
                new AuditChange(Sds, key, nameof(SdsStatus.Submitted), c.Approve ? nameof(SdsStatus.Approved) : nameof(SdsStatus.Rejected)));
        }, ct);
    }

    public Task<CommandResult> ReviewAsync(ReviewSds c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.SdsId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(ReviewSds), Permissions.HazcomManage, ResourceRef.Of(Sds, key));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var sds = await uow.Store<IHazcomLookup>().FindSdsAsync(c.SdsId, ct) ?? throw new CommandRejectedException("sds_not_found", $"SDS {key} does not exist.");
            if (sds.Status != SdsStatus.Approved) throw new CommandRejectedException("not_approved", $"Only the approved SDS is reviewed; SDS {key} is {sds.Status}.");
            var note = (c.Note ?? string.Empty).Trim();
            if (note.Length is 0 or > 1000) throw new CommandRejectedException("note_required", "Say how you confirmed it (for example: checked the supplier's site, no newer revision).");
            var due = (await uow.SiteDateAsync(now, ct)).AddYears(3);
            await uow.Store<IHazcomStore>().ReviewSdsAsync(c.SdsId, context.Actor, now, note, due, ct);
            return new CommandOutcome(CommandResult.Ok(key, $"SDS for {sds.PartNumber} confirmed current; next review {due:yyyy-MM-dd}."),
                [pipeline.Event(new SdsReviewed(c.SdsId, sds.PartNumber), context, SourceContext, Sds, key)],
                new AuditChange(Sds, key, sds.ReviewDue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }, ct);
    }

    public Task<CommandResult> FlagReviewsAsync(FlagSdsReviews c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(FlagSdsReviews), Permissions.HazcomFlag, ResourceRef.Of(Sds));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var due = await uow.Store<IHazcomStore>().TakeDueAsync(c.Today.AddDays(30), ct);
            if (due.Count == 0) return CommandOutcome.NoChange(CommandResult.NoChange("sds", "No SDS reviews fall due."));
            var events = due.Select(s => pipeline.Event(new SdsReviewDue(s.SdsId, s.PartNumber, s.ReviewDue!.Value,
                    $"The SDS for {s.PartNumber} ({s.ProductName}, {s.Manufacturer}) is due for review on {s.ReviewDue:yyyy-MM-dd}: check the supplier still publishes this revision."),
                context, SourceContext, Sds, s.SdsId.ToString(CultureInfo.InvariantCulture))).ToList();
            return new CommandOutcome(CommandResult.Ok("sds", $"{due.Count} SDS review(s) due."), events, null);
        }, ct);
    }
}
