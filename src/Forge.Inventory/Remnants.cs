using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Inventory;

/// <summary>Someone in stores measures a lot (usually a leftover piece) so jobs that need a minimum size can use it.</summary>
public sealed record RecordLotDimension(string PartNumber, string LotNumber, decimal? Length, decimal? Width, decimal? Thickness, SizeUnit Unit);

/// <summary>An engineer or planner sets the smallest piece of a component a parent part's job can use; no sizes clears it.</summary>
public sealed record SetPieceSizeRule(string ParentPart, string ComponentPart, decimal? Length, decimal? Width, decimal? Thickness, SizeUnit Unit);

public sealed record PieceSizeChanged(string PartNumber, string Subject, string Change) : IDomainEvent
{
    public static string EventType => nameof(PieceSizeChanged);

    public static int SchemaVersion => 1;
}

/// <summary>Sizes of leftover pieces and minimum piece sizes for jobs (owner decision 2026-11-02: smallest piece that fits).</summary>
public sealed class RemnantService(CommandPipeline pipeline)
{
    public Task<CommandResult> RecordDimensionAsync(RecordLotDimension c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var lot = string.IsNullOrWhiteSpace(c.LotNumber) ? throw new CommandRejectedException("lot_required", "Give the lot to measure.") : c.LotNumber.Trim();
        var key = $"{part}/{lot}";
        var spec = new CommandSpec(InventoryService.SourceContext, nameof(RecordLotDimension), Permissions.InventoryTransact, ResourceRef.Of("Lot", key));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var size = Size(c.Length, c.Width, c.Thickness, c.Unit);
            if (await uow.Store<IInventoryLookup>().FindLotAsync(part, lot, ct) is null) throw new CommandRejectedException("lot_not_found", $"Lot {lot} of {part} does not exist.");
            var before = (await uow.Store<IAllocationLookup>().ListLotDimensionsAsync(part, ct)).FirstOrDefault(d => d.LotNumber == lot);
            await uow.Store<IAllocationStore>().UpsertLotDimensionAsync(new LotDimension(part, lot, size, context.Actor, now), ct);
            var remnant = await uow.Store<IInventoryLookup>().IsRecordedRemnantAsync(part, lot, ct);
            var message = $"{(remnant ? "Leftover piece" : "Lot")} {lot} of {part} is {size.Describe()}{(before is null ? string.Empty : $" (was {before.Size.Describe()})")}.";
            return new CommandOutcome(CommandResult.Ok(key, message), [pipeline.Event(new PieceSizeChanged(part, lot, "Measured"), context, InventoryService.SourceContext, "Lot", key)],
                new AuditChange("LotDimension", key, before is null ? null : CanonicalJson.Serialize(before.Size), CanonicalJson.Serialize(size)));
        }, ct);
    }

    public Task<CommandResult> SetRuleAsync(SetPieceSizeRule c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var parent = BusinessCode.Require(c.ParentPart, "Part");
        var component = BusinessCode.Require(c.ComponentPart, "Part");
        var key = $"{parent}/{component}";
        var spec = new CommandSpec(InventoryService.SourceContext, nameof(SetPieceSizeRule), Permissions.PieceSizesManage, ResourceRef.Of("PieceSizeRule", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var engineering = uow.Store<IEngineeringLookup>();
            if (await engineering.FindItemAsync(parent, ct) is null) throw new CommandRejectedException("item_not_found", $"Part {parent} does not exist.");
            if (await engineering.FindItemAsync(component, ct) is null) throw new CommandRejectedException("item_not_found", $"Part {component} does not exist.");
            var store = uow.Store<IAllocationStore>();
            var before = (await uow.Store<IAllocationLookup>().ListPieceSizeRulesAsync(ct)).FirstOrDefault(r => r.ParentPart == parent && r.ComponentPart == component);
            if (c.Length is null && c.Width is null && c.Thickness is null)
            {
                if (!await store.DeletePieceSizeRuleAsync(parent, component, ct)) return CommandOutcome.NoChange(CommandResult.NoChange(key, $"{parent} sets no piece size for {component}."));
                return new CommandOutcome(CommandResult.Ok(key, $"{parent} jobs take any piece of {component} again."),
                    [pipeline.Event(new PieceSizeChanged(component, parent, "Cleared"), context, InventoryService.SourceContext, "PieceSizeRule", key)],
                    new AuditChange("PieceSizeRule", key, CanonicalJson.Serialize(before!.Minimum), null));
            }

            var size = Size(c.Length, c.Width, c.Thickness, c.Unit);
            await store.UpsertPieceSizeRuleAsync(new PieceSizeRule(parent, component, size, context.Actor, now), ct);
            return new CommandOutcome(CommandResult.Ok(key, $"{parent} jobs need a piece of {component} at least {size.Describe()}; the smallest lot that fits is offered first."),
                [pipeline.Event(new PieceSizeChanged(component, parent, "Set"), context, InventoryService.SourceContext, "PieceSizeRule", key)],
                new AuditChange("PieceSizeRule", key, before is null ? null : CanonicalJson.Serialize(before.Minimum), CanonicalJson.Serialize(size)));
        }, ct);
    }

    private static PieceSize Size(decimal? length, decimal? width, decimal? thickness, SizeUnit unit)
    {
        if (!Enum.IsDefined(unit)) throw new CommandRejectedException("invalid_unit", "Give the size in mm, cm, m, in or ft.");
        foreach (var v in new[] { length, width, thickness })
            if (v is <= 0 or > 1_000_000) throw new CommandRejectedException("invalid_size", string.Create(CultureInfo.InvariantCulture, $"{v} is not a size; give a number above zero."));
        var size = new PieceSize(length, width, thickness, unit);
        return size.HasAny ? size : throw new CommandRejectedException("size_required", "Give at least a length, width or thickness.");
    }
}
