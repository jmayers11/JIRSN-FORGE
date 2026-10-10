using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Inventory;

public sealed record InstanceShare(string LotNumber, decimal Quantity);

/// <summary>Divides part of one instance into new instances (V3.2). What is not divided off stays in the source.</summary>
public sealed record SplitInstance(string PartNumber, string LotNumber, string LocationCode, StockStatus Status, IReadOnlyList<InstanceShare> Results);

/// <summary>Combines quantities of instances into one, new or existing (V3.2), only where the part's merge policy allows.</summary>
public sealed record MergeInstances(string PartNumber, IReadOnlyList<InstanceShare> Sources, string ResultLot, string LocationCode, StockStatus Status);

public sealed record SetMergePolicy(string PartNumber, bool Allowed, bool SameRevisionRequired, bool SameSupplierLotRequired);

public sealed record InstancesSplit(string PartNumber, string SourceLot, IReadOnlyList<string> ResultLots, decimal Quantity) : IDomainEvent
{
    public static string EventType => nameof(InstancesSplit);

    public static int SchemaVersion => 1;
}

public sealed record InstancesMerged(string PartNumber, IReadOnlyList<string> SourceLots, string ResultLot, decimal Quantity) : IDomainEvent
{
    public static string EventType => nameof(InstancesMerged);

    public static int SchemaVersion => 1;
}

public sealed record MergePolicySet(string PartNumber, bool Allowed) : IDomainEvent
{
    public static string EventType => nameof(MergePolicySet);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Material identity (V3.2, V3 brief D, E, K). An instance keeps its identity through every move; its quantity is
/// the ledger sum and may shrink as it is used. Dividing it creates new instances whose quantities add up exactly
/// to what left it; combining instances is allowed only where the part's merge policy says so, because the
/// sources stop being separately traceable from then on. Each split and merge is a recorded link, never changed.
/// </summary>
public sealed partial class InventoryService
{
    private const int MaxShares = 50;

    public Task<CommandResult> SplitAsync(SplitInstance c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(SplitInstance), Permissions.InventoryTransact, ResourceRef.Of(StockEntity, part), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var item = await ItemAsync(uow, part, ct);
            RequireDivisible(item);
            var store = uow.Store<IInventoryStore>();
            await store.LockPartAsync(part, ct);
            var location = await CompanyLocationAsync(uow, c.LocationCode, ct);
            var source = await ExistingLotAsync(uow, item, c.LotNumber, ct);
            var shares = await NewSharesAsync(uow, item, c.Results, [source.LotNumber], ct);
            var total = shares.Sum(s => s.Quantity);
            await RequireStockAsync(uow, part, location.Code, source.LotNumber, c.Status, total, ct);

            var outId = await store.InsertTransactionAsync(
                new NewTransaction(InventoryTransactionKind.Split, part, total, location.Code, c.Status, null, null, source.LotNumber, Reason: context.Reason, PartRevision: source.PartRevision), now, context.Actor, ct);
            foreach (var share in shares)
            {
                await store.EnsureLotAsync(part, share.LotNumber, source.SupplierLot, source.IdentityKind, now, context.Actor, ct);
                if (source.PartRevision is { } revision)
                {
                    await store.SetLotRevisionAsync(part, share.LotNumber, revision, ct);
                }

                var inId = await store.InsertTransactionAsync(
                    new NewTransaction(InventoryTransactionKind.Split, part, share.Quantity, null, null, location.Code, c.Status, share.LotNumber, Reason: context.Reason, PartRevision: source.PartRevision), now, context.Actor, ct);
                await store.InsertInstanceLinkAsync(new InstanceLink(0, InstanceRelation.Split, part, source.LotNumber, part, share.LotNumber, share.Quantity, outId, inId, context.Reason, now, context.Actor), ct);
            }

            var left = await store.BalanceAsync(part, location.Code, source.LotNumber, c.Status, ct);
            var key = outId.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Split {Qty(total)} {item.UnitOfMeasure} off {part} {source.LotNumber} into {string.Join(", ", shares.Select(s => $"{s.LotNumber} ({Qty(s.Quantity)})"))}; {Qty(left)} left in {source.LotNumber}."),
                [pipeline.Event(new InstancesSplit(part, source.LotNumber, shares.Select(s => s.LotNumber).ToList(), total), context, SourceContext, StockEntity, part)],
                new AuditChange("Instance", $"{part}/{source.LotNumber}", null, CanonicalJson.Serialize(new { location = location.Code, status = c.Status, results = shares })));
        }, ct);
    }

    public Task<CommandResult> MergeAsync(MergeInstances c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(MergeInstances), Permissions.InventoryTransact, ResourceRef.Of(StockEntity, part), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var item = await ItemAsync(uow, part, ct);
            RequireDivisible(item);
            var lookup = uow.Store<IInventoryLookup>();
            var policy = await lookup.FindMergePolicyAsync(part, ct);
            if (policy is not { Allowed: true })
            {
                throw new CommandRejectedException("merge_not_allowed", $"Lots of {part} are never merged: no merge policy allows it. Someone with inventory approval authority sets one.");
            }

            var store = uow.Store<IInventoryStore>();
            await store.LockPartAsync(part, ct);
            var location = await CompanyLocationAsync(uow, c.LocationCode, ct);
            var resultLot = RequireLot(item, c.ResultLot)!;
            if ((c.Sources ?? []).Count is 0 or > MaxShares)
            {
                throw new CommandRejectedException("invalid_sources", $"Merge 1 to {MaxShares} sources.");
            }

            var lots = (await lookup.ListLotsAsync(part, ct)).ToDictionary(l => l.LotNumber, StringComparer.Ordinal);
            var sources = new List<(LotInfo Lot, decimal Quantity)>();
            foreach (var share in c.Sources!)
            {
                var lot = await ExistingLotAsync(uow, item, share.LotNumber, ct);
                if (lot.LotNumber == resultLot || sources.Any(s => s.Lot.LotNumber == lot.LotNumber))
                {
                    throw new CommandRejectedException("invalid_sources", $"Lot {lot.LotNumber} is listed twice or is the result.");
                }

                var quantity = RequireQuantity(item, share.Quantity);
                await RequireStockAsync(uow, part, location.Code, lot.LotNumber, c.Status, quantity, ct);
                sources.Add((lot, quantity));
            }

            var existing = lots.GetValueOrDefault(resultLot);
            var members = sources.Select(s => s.Lot).Concat(existing is null ? [] : [existing]).ToList();
            if (policy.SameRevisionRequired && members.Select(m => m.PartRevision ?? "(not recorded)").Distinct(StringComparer.Ordinal).Count() > 1)
            {
                throw new CommandRejectedException("revision_mismatch", $"{part} lots are merged only within one recorded revision: {string.Join(", ", members.Select(m => $"{m.LotNumber} rev {m.PartRevision ?? "not recorded"}"))}.");
            }

            if (policy.SameSupplierLotRequired && members.Select(m => m.SupplierLot ?? "(none)").Distinct(StringComparer.Ordinal).Count() > 1)
            {
                throw new CommandRejectedException("supplier_lot_mismatch", $"{part} lots are merged only within one supplier lot: {string.Join(", ", members.Select(m => $"{m.LotNumber} {m.SupplierLot ?? "none"}"))}.");
            }

            if (existing is null)
            {
                var revisions = members.Select(m => m.PartRevision).Distinct().ToList();
                var supplierLots = members.Select(m => m.SupplierLot).Distinct().ToList();
                await store.EnsureLotAsync(part, resultLot, supplierLots.Count == 1 ? supplierLots[0] : null, sources[0].Lot.IdentityKind, now, context.Actor, ct);
                if (revisions is [{ } revision])
                {
                    await store.SetLotRevisionAsync(part, resultLot, revision, ct);
                }
            }

            var resultRevision = existing?.PartRevision ?? (members.Select(m => m.PartRevision).Distinct().ToList() is [{ } only] ? only : null);
            long firstOut = 0;
            foreach (var (lot, quantity) in sources)
            {
                var outId = await store.InsertTransactionAsync(
                    new NewTransaction(InventoryTransactionKind.Merge, part, quantity, location.Code, c.Status, null, null, lot.LotNumber, Reason: context.Reason, PartRevision: lot.PartRevision), now, context.Actor, ct);
                var inId = await store.InsertTransactionAsync(
                    new NewTransaction(InventoryTransactionKind.Merge, part, quantity, null, null, location.Code, c.Status, resultLot, Reason: context.Reason, PartRevision: resultRevision), now, context.Actor, ct);
                await store.InsertInstanceLinkAsync(new InstanceLink(0, InstanceRelation.Merge, part, lot.LotNumber, part, resultLot, quantity, outId, inId, context.Reason, now, context.Actor), ct);
                firstOut = firstOut == 0 ? outId : firstOut;
            }

            var total = sources.Sum(s => s.Quantity);
            var key = firstOut.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Merged {string.Join(", ", sources.Select(s => $"{Qty(s.Quantity)} of {s.Lot.LotNumber}"))} into {part} {resultLot} ({Qty(total)} {item.UnitOfMeasure})."),
                [pipeline.Event(new InstancesMerged(part, sources.Select(s => s.Lot.LotNumber).ToList(), resultLot, total), context, SourceContext, StockEntity, part)],
                new AuditChange("Instance", $"{part}/{resultLot}", existing is null ? null : "existing", CanonicalJson.Serialize(new { location = location.Code, status = c.Status, sources = sources.Select(s => new { lot = s.Lot.LotNumber, s.Quantity }) })));
        }, ct);
    }

    public Task<CommandResult> SetMergePolicyAsync(SetMergePolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(SetMergePolicy), Permissions.InventoryApprove, ResourceRef.Of(StockEntity, part), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var item = await ItemAsync(uow, part, ct);
            RequireDivisible(item);
            var current = await uow.Store<IInventoryLookup>().FindMergePolicyAsync(part, ct);
            if (current is not null && (current.Allowed, current.SameRevisionRequired, current.SameSupplierLotRequired) == (c.Allowed, c.SameRevisionRequired, c.SameSupplierLotRequired))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(part, "The merge policy already says this."));
            }

            var policy = new MergePolicy(part, c.Allowed, c.SameRevisionRequired, c.SameSupplierLotRequired, context.Reason!, context.Actor, now);
            await uow.Store<IInventoryStore>().InsertMergePolicyAsync(policy, ct);
            return new CommandOutcome(
                CommandResult.Ok(part, c.Allowed
                    ? $"Lots of {part} may be merged{(c.SameRevisionRequired ? ", within one revision" : string.Empty)}{(c.SameSupplierLotRequired ? ", within one supplier lot" : string.Empty)}."
                    : $"Lots of {part} are never merged."),
                [pipeline.Event(new MergePolicySet(part, c.Allowed), context, SourceContext, StockEntity, part)],
                new AuditChange("MergePolicy", part, current is null ? null : CanonicalJson.Serialize(current), CanonicalJson.Serialize(policy)));
        }, ct);
    }

    /// <summary>Only lot-tracked parts have instances to divide or combine; a serial is one unit.</summary>
    private static void RequireDivisible(ItemInfo item)
    {
        if (item.Serialized || !item.LotTracked)
        {
            throw new CommandRejectedException("not_divisible", item.Serialized
                ? $"{item.PartNumber} is serialized: each serial is one unit and is never split or merged."
                : $"{item.PartNumber} is not lot-tracked, so it has no instances to split or merge.");
        }
    }

    private static async Task<LotInfo> ExistingLotAsync(IKernelUnitOfWork uow, ItemInfo item, string raw, CancellationToken ct)
    {
        var lot = RequireLot(item, raw)!;
        return (await uow.Store<IInventoryLookup>().ListLotsAsync(item.PartNumber, ct)).FirstOrDefault(l => l.LotNumber == lot)
            ?? throw new CommandRejectedException("lot_not_found", $"{item.PartNumber} lot {lot} does not exist.");
    }

    /// <summary>New instance numbers, each new for the part, distinct, with a positive quantity.</summary>
    private static async Task<List<InstanceShare>> NewSharesAsync(IKernelUnitOfWork uow, ItemInfo item, IReadOnlyList<InstanceShare>? raw, IReadOnlyCollection<string> taken, CancellationToken ct)
    {
        if ((raw ?? []).Count is 0 or > MaxShares)
        {
            throw new CommandRejectedException("invalid_results", $"Split into 1 to {MaxShares} new instances.");
        }

        var existing = (await uow.Store<IInventoryLookup>().ListLotsAsync(item.PartNumber, ct)).Select(l => l.LotNumber).ToHashSet(StringComparer.Ordinal);
        var shares = new List<InstanceShare>();
        foreach (var share in raw!)
        {
            var lot = RequireLot(item, share.LotNumber)!;
            if (existing.Contains(lot) || taken.Contains(lot) || shares.Any(s => s.LotNumber == lot))
            {
                throw new CommandRejectedException("lot_exists", $"{item.PartNumber} {lot} already exists or is listed twice; a split always creates new instances.");
            }

            shares.Add(new InstanceShare(lot, RequireQuantity(item, share.Quantity)));
        }

        return shares;
    }

    private static string Qty(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
