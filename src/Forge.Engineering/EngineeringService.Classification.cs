using Forge.Engineering.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Engineering;

/// <summary>Classifies an item (V2 §10). A reason is required: the class decides purchasing, documentation and substitution rules.</summary>
public sealed record ClassifyItem(string PartNumber, ItemClass Class);

public sealed record ProposeManufacturerPart(string PartNumber, string Manufacturer, string Mpn, string? ManufacturerRevision, ManufacturerLifecycle Lifecycle, string? Datasheet, string? Note);

public sealed record ApproveManufacturerPart(string PartNumber, string Manufacturer, string Mpn);

public sealed record WithdrawManufacturerPart(string PartNumber, string Manufacturer, string Mpn);

/// <summary>Records what the manufacturer now says about the part's lifecycle (end of life, last-time buy).</summary>
public sealed record SetManufacturerLifecycle(string PartNumber, string Manufacturer, string Mpn, ManufacturerLifecycle Lifecycle);

public sealed record ItemClassified(string PartNumber, string From, string To) : IDomainEvent
{
    public static string EventType => nameof(ItemClassified);

    public static int SchemaVersion => 1;
}

public sealed record ManufacturerPartChanged(string PartNumber, string Manufacturer, string Mpn, string Change) : IDomainEvent
{
    public static string EventType => nameof(ManufacturerPartChanged);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Item classification and manufacturer parts (V2.1). A manufacturer part is proposed by one person and
/// approved by a different person with COTS authority; only approved manufacturer parts let a COTS item be
/// bought. Forge records what people state about a manufacturer's lifecycle; it never infers it.
/// </summary>
public sealed partial class EngineeringService
{
    private const string ManufacturerPartEntity = "ManufacturerPart";

    public Task<CommandResult> ClassifyItemAsync(ClassifyItem c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(ClassifyItem), Permissions.EngineeringManage, ResourceRef.Of(ItemEntity, part), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var item = await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct)
                ?? throw new CommandRejectedException("item_not_found", $"Part {part} does not exist.");
            if (c.Class == ItemClass.Unclassified)
            {
                throw new CommandRejectedException("invalid_class", "Choose a class; an item cannot be made unclassified again.");
            }

            RequireClassFits(c.Class, item.MakeBuy, part);
            if (item.Class == c.Class)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(part, $"{part} is already {c.Class}."));
            }

            if (c.Class != ItemClass.Cots && item.Class == ItemClass.Cots
                && (await uow.Store<IEngineeringLookup>().ListManufacturerPartsAsync(part, ct)).Any(m => m.Status == ManufacturerPartStatus.Approved))
            {
                throw new CommandRejectedException("has_manufacturer_parts", $"{part} has approved manufacturer parts; withdraw them before it stops being COTS.");
            }

            await uow.Store<IEngineeringStore>().SetClassAsync(part, c.Class, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(part, $"{part} classified {c.Class} (was {item.Class})."),
                [pipeline.Event(new ItemClassified(part, item.Class.ToString(), c.Class.ToString()), context, SourceContext, ItemEntity, part)],
                new AuditChange(ItemEntity, part, CanonicalJson.Serialize(new { Class = item.Class.ToString() }), CanonicalJson.Serialize(new { Class = c.Class.ToString() })));
        }, ct);
    }

    public Task<CommandResult> ProposeManufacturerPartAsync(ProposeManufacturerPart c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(ProposeManufacturerPart), Permissions.CotsPropose, ResourceRef.Of(ManufacturerPartEntity, part));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var item = await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct)
                ?? throw new CommandRejectedException("item_not_found", $"Part {part} does not exist.");
            if (item.Class is ItemClass.Unclassified)
            {
                throw new CommandRejectedException("item_unclassified", $"Classify {part} first; manufacturer parts belong to bought, off-the-shelf classes.");
            }

            if (item.Class is ItemClass.Make or ItemClass.BuildToPrint or ItemClass.Service)
            {
                throw new CommandRejectedException("not_manufacturer_item", $"{part} is {item.Class}: our drawing or specification defines it, not a manufacturer's part number.");
            }

            var manufacturer = BusinessCode.RequireName(c.Manufacturer, "Manufacturer");
            var mpn = Mpn(c.Mpn);
            var record = new ManufacturerPartInfo(
                part, manufacturer, mpn, Optional(c.ManufacturerRevision, 40), c.Lifecycle, ManufacturerPartStatus.Proposed,
                Optional(c.Datasheet, 500), Optional(c.Note, 1000), context.Actor, now, null, null);
            if (!await uow.Store<IEngineeringStore>().InsertManufacturerPartAsync(record, ct))
            {
                throw new CommandRejectedException("manufacturer_part_exists", $"{manufacturer} {mpn} is already recorded for {part}.");
            }

            var id = Key(part, manufacturer, mpn);
            return new CommandOutcome(
                CommandResult.Ok(id, $"{manufacturer} {mpn} proposed for {part}. A different person with COTS authority must approve it before it can be bought."),
                [pipeline.Event(new ManufacturerPartChanged(part, manufacturer, mpn, "Proposed"), context, SourceContext, ManufacturerPartEntity, id)],
                new AuditChange(ManufacturerPartEntity, id, null, CanonicalJson.Serialize(record)));
        }, ct);
    }

    public Task<CommandResult> ApproveManufacturerPartAsync(ApproveManufacturerPart c, CommandContext context, CancellationToken ct) =>
        ChangeManufacturerPartAsync(nameof(ApproveManufacturerPart), c.PartNumber, c.Manufacturer, c.Mpn, c, context, Permissions.CotsManage, false, (current, now) =>
        {
            if (current.Status != ManufacturerPartStatus.Proposed)
            {
                throw new CommandRejectedException("not_proposed", $"It is {current.Status}; only a proposed manufacturer part is approved.");
            }

            if (current.ProposedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You proposed this manufacturer part; a different person must approve it.");
            }

            return (current with { Status = ManufacturerPartStatus.Approved, ApprovedBy = context.Actor, ApprovedAt = now }, "Approved", "approved; the item may now be bought as this part");
        }, ct);

    public Task<CommandResult> WithdrawManufacturerPartAsync(WithdrawManufacturerPart c, CommandContext context, CancellationToken ct) =>
        ChangeManufacturerPartAsync(nameof(WithdrawManufacturerPart), c.PartNumber, c.Manufacturer, c.Mpn, c, context, Permissions.CotsManage, true, (current, _) =>
            current.Status == ManufacturerPartStatus.Withdrawn
                ? null
                : (current with { Status = ManufacturerPartStatus.Withdrawn }, "Withdrawn", "withdrawn; it is no longer an approved source of the item"), ct);

    public Task<CommandResult> SetManufacturerLifecycleAsync(SetManufacturerLifecycle c, CommandContext context, CancellationToken ct) =>
        ChangeManufacturerPartAsync(nameof(SetManufacturerLifecycle), c.PartNumber, c.Manufacturer, c.Mpn, c, context, Permissions.CotsPropose, true, (current, _) =>
            current.Lifecycle == c.Lifecycle
                ? null
                : (current with { Lifecycle = c.Lifecycle }, "Lifecycle", $"lifecycle now {c.Lifecycle} (was {current.Lifecycle})"), ct);

    private static void RequireClassFits(ItemClass itemClass, MakeBuy makeBuy, string part)
    {
        if (!ItemClasses.Allows(itemClass, makeBuy))
        {
            throw new CommandRejectedException("class_conflict", itemClass == ItemClass.Make
                ? $"{part} is bought; a Make item is made here."
                : $"{part} is made here; {itemClass} items are always bought.");
        }
    }

    private Task<CommandResult> ChangeManufacturerPartAsync<TCommand>(
        string commandType,
        string rawPart,
        string rawManufacturer,
        string rawMpn,
        TCommand command,
        CommandContext context,
        string permission,
        bool reasonRequired,
        Func<ManufacturerPartInfo, DateTimeOffset, (ManufacturerPartInfo Updated, string Change, string Message)?> change,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var part = BusinessCode.Require(rawPart, "Part");
        var manufacturer = BusinessCode.RequireName(rawManufacturer, "Manufacturer");
        var mpn = Mpn(rawMpn);
        var id = Key(part, manufacturer, mpn);
        var spec = new CommandSpec(SourceContext, commandType, permission, ResourceRef.Of(ManufacturerPartEntity, id), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var current = (await uow.Store<IEngineeringLookup>().ListManufacturerPartsAsync(part, ct))
                .FirstOrDefault(m => string.Equals(m.Manufacturer, manufacturer, StringComparison.OrdinalIgnoreCase) && m.Mpn == mpn)
                ?? throw new CommandRejectedException("manufacturer_part_not_found", $"{manufacturer} {mpn} is not recorded for {part}.");
            var result = change(current, now);
            if (result is not { } r)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Nothing changed."));
            }

            await uow.Store<IEngineeringStore>().UpdateManufacturerPartAsync(r.Updated, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(id, $"{current.Manufacturer} {mpn} for {part} {r.Message}."),
                [pipeline.Event(new ManufacturerPartChanged(part, current.Manufacturer, mpn, r.Change), context, SourceContext, ManufacturerPartEntity, id)],
                new AuditChange(ManufacturerPartEntity, id, CanonicalJson.Serialize(current), CanonicalJson.Serialize(r.Updated)));
        }, ct);
    }

    /// <summary>A manufacturer part number as the manufacturer writes it: trimmed, case kept, 1-60 characters.</summary>
    private static string Mpn(string? raw)
    {
        var mpn = (raw ?? string.Empty).Trim();
        return mpn.Length is 0 or > 60 || mpn.Any(char.IsControl)
            ? throw new CommandRejectedException("invalid_mpn", "Manufacturer part number is required (at most 60 characters).")
            : mpn;
    }

    private static string? Optional(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }

    private static string Key(string part, string manufacturer, string mpn) => $"{part}/{manufacturer}/{mpn}";
}
