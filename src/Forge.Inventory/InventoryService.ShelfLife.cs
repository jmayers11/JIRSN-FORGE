using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Inventory;

public sealed record SetShelfLifeRule(
    string PartNumber,
    int? ShelfLifeDays,
    ShelfLifeBasis Basis,
    int? RetestDays,
    int? OpenLifeHours,
    decimal? StorageMinC,
    decimal? StorageMaxC,
    string? StorageNote,
    SelectionRule Selection);

public sealed record OpenContainer(string PartNumber, string LotNumber);

public sealed record RequestShelfLifeExtension(string PartNumber, string LotNumber, DateOnly NewExpires, string Evidence);

public sealed record DecideShelfLifeExtension(long ExtensionId, bool Approve);

public sealed record RecordRetest(string PartNumber, string LotNumber, bool Passed, DateOnly? NextDue, string Evidence);

public sealed record RecordStorageExcursion(string LocationCode, string Condition, decimal? MeasuredC, DateTimeOffset StartedAt, DateTimeOffset EndedAt, string? Note);

public sealed record ShelfLifeChanged(string PartNumber, string? LotNumber, string Change) : IDomainEvent
{
    public static string EventType => nameof(ShelfLifeChanged);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Aging, shelf life and selection (V3.6, V3 brief M, N). Age, expiry, open-container life, retest and storage are
/// separate facts. A lot keeps the dates it was given; an extension (requested by one person, approved by another)
/// and a retest are their own records. Expired, open-life-expired, retest-overdue or failed lots, and lots that sat
/// through a storage excursion without a retest since, are never issued or shipped. FIFO or FEFO then orders what is
/// left; it never brings back what the rules left out.
/// </summary>
public sealed partial class InventoryService
{
    private const string LotEntity = "Lot";

    public Task<CommandResult> SetShelfLifeRuleAsync(SetShelfLifeRule c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(SetShelfLifeRule), Permissions.QualityPlan, ResourceRef.Of(StockEntity, part), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var item = await ItemAsync(uow, part, ct);
            if (!item.LotTracked && !item.Serialized)
            {
                throw new CommandRejectedException("not_lot_tracked", $"{part} is not lot-tracked or serialized; shelf life belongs to identified lots.");
            }

            if (c.ShelfLifeDays is <= 0 or > 36_500 || c.RetestDays is <= 0 or > 36_500 || c.OpenLifeHours is <= 0 or > 87_600)
            {
                throw new CommandRejectedException("invalid_shelf_life", "Shelf life and retest are days above zero, open life is hours above zero, or empty.");
            }

            if (c.StorageMinC is { } min && c.StorageMaxC is { } max && min >= max)
            {
                throw new CommandRejectedException("invalid_storage", "The storage minimum is below the maximum.");
            }

            var lookup = uow.Store<IShelfLifeLookup>();
            var current = await lookup.FindRuleAsync(part, ct);
            var rule = new ShelfLifeRule(part, c.ShelfLifeDays, c.Basis, c.RetestDays, c.OpenLifeHours, c.StorageMinC, c.StorageMaxC,
                string.IsNullOrWhiteSpace(c.StorageNote) ? null : c.StorageNote.Trim(), c.Selection, context.Reason!, context.Actor, now);
            if (current is not null && current with { Reason = rule.Reason, SetBy = rule.SetBy, SetAt = rule.SetAt } == rule)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(part, "The shelf-life rule already says this."));
            }

            await uow.Store<IShelfLifeStore>().InsertRuleAsync(rule, ct);
            return new CommandOutcome(
                CommandResult.Ok(part, $"{part}: {(c.ShelfLifeDays is { } d ? $"{d} days from {c.Basis.ToString().ToLowerInvariant()}" : "no shelf life")}{(c.RetestDays is { } r ? $", retest every {r} days" : string.Empty)}{(c.OpenLifeHours is { } h ? $", {h} h once opened" : string.Empty)}; {c.Selection.ToString().ToUpperInvariant()}. Lots already received keep their dates."),
                [pipeline.Event(new ShelfLifeChanged(part, null, "Rule"), context, SourceContext, StockEntity, part)],
                new AuditChange("ShelfLifeRule", part, current is null ? null : CanonicalJson.Serialize(current), CanonicalJson.Serialize(rule)));
        }, ct);
    }

    public Task<CommandResult> OpenContainerAsync(OpenContainer c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(OpenContainer), Permissions.InventoryTransact, ResourceRef.Of(StockEntity, part));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var item = await ItemAsync(uow, part, ct);
            var lot = await ExistingLotAsync(uow, item, c.LotNumber, ct);
            var rule = await uow.Store<IShelfLifeLookup>().FindRuleAsync(part, ct);
            if (rule?.OpenLifeHours is not { } hours)
            {
                throw new CommandRejectedException("no_open_life", $"{part} has no open-container life to start.");
            }

            if (lot.OpenedAt is { } opened)
            {
                throw new CommandRejectedException("already_open", $"{part} {lot.LotNumber} was opened {opened:u}.");
            }

            await uow.Store<IShelfLifeStore>().OpenLotAsync(part, lot.LotNumber, now, context.Actor, ct);
            var key = $"{part}/{lot.LotNumber}";
            return new CommandOutcome(
                CommandResult.Ok(key, $"{key} opened; usable until {now.AddHours(hours):u}."),
                [pipeline.Event(new ShelfLifeChanged(part, lot.LotNumber, "Opened"), context, SourceContext, LotEntity, key)],
                new AuditChange(LotEntity, key, null, CanonicalJson.Serialize(new { openedAt = now, openLifeHours = hours })));
        }, ct);
    }

    public Task<CommandResult> RequestShelfLifeExtensionAsync(RequestShelfLifeExtension c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(RequestShelfLifeExtension), Permissions.QualityPlan, ResourceRef.Of(StockEntity, part), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var item = await ItemAsync(uow, part, ct);
            var lot = await ExistingLotAsync(uow, item, c.LotNumber, ct);
            var life = await ShelfLifeAsync(uow, lot, now, ct);
            if (life.Expires is not { } current)
            {
                throw new CommandRejectedException("no_expiry", $"{part} {lot.LotNumber} has no expiry to extend.");
            }

            if (c.NewExpires <= current)
            {
                throw new CommandRejectedException("invalid_extension", $"{part} {lot.LotNumber} expires {current:yyyy-MM-dd}; an extension moves it later.");
            }

            var store = uow.Store<IShelfLifeStore>();
            if ((await uow.Store<IShelfLifeLookup>().ListExtensionsAsync(part, ct)).Any(e => e.LotNumber == lot.LotNumber && e.Approved is null))
            {
                throw new CommandRejectedException("extension_pending", $"{part} {lot.LotNumber} already has an extension waiting for a decision.");
            }

            var extension = new ShelfLifeExtension(0, part, lot.LotNumber, current, c.NewExpires, BusinessCode.RequireName(c.Evidence, "Evidence"), context.Actor, now);
            var id = await store.InsertExtensionAsync(extension, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Extension {key} of {part} {lot.LotNumber} from {current:yyyy-MM-dd} to {c.NewExpires:yyyy-MM-dd} requested; someone else with quality approval authority decides it."),
                [pipeline.Event(new ShelfLifeChanged(part, lot.LotNumber, "ExtensionRequested"), context, SourceContext, LotEntity, $"{part}/{lot.LotNumber}")],
                new AuditChange("ShelfLifeExtension", key, null, CanonicalJson.Serialize(extension with { ExtensionId = id })));
        }, ct);
    }

    public Task<CommandResult> DecideShelfLifeExtensionAsync(DecideShelfLifeExtension c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.ExtensionId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(DecideShelfLifeExtension), Permissions.QualityApprove, ResourceRef.Of("ShelfLifeExtension", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var extension = (await uow.Store<IShelfLifeLookup>().ListExtensionsAsync(null, ct)).FirstOrDefault(e => e.ExtensionId == c.ExtensionId)
                ?? throw new CommandRejectedException("extension_not_found", $"Extension {key} does not exist.");
            if (extension.Approved is not null)
            {
                throw new CommandRejectedException("extension_decided", $"Extension {key} was already {(extension.Approved.Value ? "approved" : "rejected")}.");
            }

            if (extension.RequestedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You requested this extension; a different person must decide it.");
            }

            await uow.Store<IShelfLifeStore>().DecideExtensionAsync(c.ExtensionId, c.Approve, now, context.Actor, ct);
            var decided = extension with { Approved = c.Approve, DecidedBy = context.Actor, DecidedAt = now };
            return new CommandOutcome(
                CommandResult.Ok(key, c.Approve
                    ? $"{extension.PartNumber} {extension.LotNumber} now expires {extension.NewExpires:yyyy-MM-dd}; its original date {extension.PreviousExpires:yyyy-MM-dd} stays on record."
                    : $"Extension {key} rejected; {extension.PartNumber} {extension.LotNumber} still expires {extension.PreviousExpires:yyyy-MM-dd}."),
                [pipeline.Event(new ShelfLifeChanged(extension.PartNumber, extension.LotNumber, c.Approve ? "ExtensionApproved" : "ExtensionRejected"), context, SourceContext, LotEntity, $"{extension.PartNumber}/{extension.LotNumber}")],
                new AuditChange("ShelfLifeExtension", key, CanonicalJson.Serialize(extension), CanonicalJson.Serialize(decided)));
        }, ct);
    }

    public Task<CommandResult> RecordRetestAsync(RecordRetest c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(RecordRetest), Permissions.QualityInspect, ResourceRef.Of(StockEntity, part));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var item = await ItemAsync(uow, part, ct);
            var lot = await ExistingLotAsync(uow, item, c.LotNumber, ct);
            var rule = await uow.Store<IShelfLifeLookup>().FindRuleAsync(part, ct);
            var today = (await uow.SiteDateAsync(now, ct));
            DateOnly? next = null;
            if (c.Passed)
            {
                next = c.NextDue ?? (rule?.RetestDays is { } days ? today.AddDays(days) : null);
                if (next is { } n && n <= today)
                {
                    throw new CommandRejectedException("invalid_retest_date", "The next retest is after today.");
                }
            }
            else if (c.NextDue is not null)
            {
                throw new CommandRejectedException("invalid_retest_date", "A failed retest has no next date; the lot is not used.");
            }

            var retest = new RetestInfo(0, part, lot.LotNumber, c.Passed, next, BusinessCode.RequireName(c.Evidence, "Evidence"), context.Actor, now);
            var id = await uow.Store<IShelfLifeStore>().InsertRetestAsync(retest, ct);
            var key = $"{part}/{lot.LotNumber}";
            return new CommandOutcome(
                CommandResult.Ok(id.ToString(CultureInfo.InvariantCulture), c.Passed
                    ? $"{key} passed retest{(next is { } due ? $"; next due {due:yyyy-MM-dd}" : string.Empty)}."
                    : $"{key} failed retest; it is not used, issued or shipped."),
                [pipeline.Event(new ShelfLifeChanged(part, lot.LotNumber, c.Passed ? "RetestPassed" : "RetestFailed"), context, SourceContext, LotEntity, key)],
                new AuditChange("Retest", id.ToString(CultureInfo.InvariantCulture), null, CanonicalJson.Serialize(retest with { RetestId = id })));
        }, ct);
    }

    public Task<CommandResult> RecordStorageExcursionAsync(RecordStorageExcursion c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.LocationCode, "Location");
        var spec = new CommandSpec(SourceContext, nameof(RecordStorageExcursion), Permissions.InventoryTransact, ResourceRef.Of("Location", code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var location = await LocationAsync(uow, code, ct);
            if (c.EndedAt < c.StartedAt || c.EndedAt > now)
            {
                throw new CommandRejectedException("invalid_time", "An excursion is recorded once it is over: it ends after it started and not in the future.");
            }

            var excursion = new StorageExcursion(0, location.Code, BusinessCode.RequireName(c.Condition, "Condition"), c.MeasuredC, c.StartedAt, c.EndedAt,
                string.IsNullOrWhiteSpace(c.Note) ? null : c.Note.Trim(), context.Actor, now);
            var id = await uow.Store<IShelfLifeStore>().InsertExcursionAsync(excursion, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Storage excursion at {location.Code} recorded. Lots there with storage limits it broke need a retest before use."),
                [pipeline.Event(new ShelfLifeChanged(location.Code, null, "Excursion"), context, SourceContext, "Location", location.Code)],
                new AuditChange("StorageExcursion", key, null, CanonicalJson.Serialize(excursion with { ExcursionId = id })));
        }, ct);
    }

    /// <summary>One lot's shelf-life state now (V3.6).</summary>
    internal static async Task<LotShelfLife> ShelfLifeAsync(IKernelUnitOfWork uow, LotInfo lot, DateTimeOffset now, CancellationToken ct)
    {
        var lookup = uow.Store<IShelfLifeLookup>();
        var residence = (await lookup.ListLotResidenceAsync(lot.PartNumber, ct)).Where(r => r.LotNumber == lot.LotNumber).Select(r => (r.Location, r.From, r.Until));
        var zone = await uow.SiteTimeZoneAsync(ct);
        return LotShelfLife.For(lot, await lookup.FindRuleAsync(lot.PartNumber, ct), await lookup.ListExtensionsAsync(lot.PartNumber, ct), await lookup.ListRetestsAsync(lot.PartNumber, ct),
            await lookup.ListExcursionsAsync(ct), residence, SiteTime.DateAt(now, zone), now, zone);
    }

    /// <summary>The hard rule (V3.6): a lot that is not usable by shelf life is never issued or shipped.</summary>
    private static async Task RequireUsableAsync(IKernelUnitOfWork uow, ItemInfo item, string? lotNumber, DateTimeOffset now, CancellationToken ct)
    {
        if (lotNumber is null)
        {
            return;
        }

        var lot = (await uow.Store<IInventoryLookup>().ListLotsAsync(item.PartNumber, ct)).FirstOrDefault(l => l.LotNumber == lotNumber);
        if (lot is not null && await ShelfLifeAsync(uow, lot, now, ct) is { Usable: false } life)
        {
            throw new CommandRejectedException("shelf_life_blocked", $"{item.PartNumber} {lotNumber} cannot be used: {life.Explanation}");
        }
    }

    /// <summary>The dates a newly received lot gets from the part's rule and its paperwork (V3.6). Dates it already has stay.</summary>
    private static async Task ReceiptDatesAsync(IKernelUnitOfWork uow, ItemInfo item, string lot, DateOnly? manufacturedOn, DateOnly? expiresOn, DateTimeOffset now, CancellationToken ct)
    {
        var today = (await uow.SiteDateAsync(now, ct));
        if (manufacturedOn > today || expiresOn < manufacturedOn)
        {
            throw new CommandRejectedException("invalid_dates", "Manufacture is not in the future and expiry is after manufacture.");
        }

        var rule = await uow.Store<IShelfLifeLookup>().FindRuleAsync(item.PartNumber, ct);
        DateOnly? expires = expiresOn;
        if (rule?.ShelfLifeDays is { } days)
        {
            var from = rule.Basis == ShelfLifeBasis.Manufacture
                ? manufacturedOn ?? throw new CommandRejectedException("manufactured_on_required", $"{item.PartNumber} shelf life counts from manufacture; give the manufacture date from the certificate.")
                : today;
            var computed = from.AddDays(days);
            expires = expiresOn is { } stated && stated < computed ? stated : computed;
        }

        var retest = rule?.RetestDays is { } every ? today.AddDays(every) : (DateOnly?)null;
        if (manufacturedOn is not null || expires is not null || retest is not null)
        {
            await uow.Store<IShelfLifeStore>().SetLotDatesAsync(item.PartNumber, lot, manufacturedOn, expires, retest, ct);
        }
    }
}
