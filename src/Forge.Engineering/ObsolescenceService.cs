using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Engineering;

/// <param name="MinQuantity">A critical spare below this is reported.</param>
/// <param name="ReorderPoint">At or below this (on hand plus on order, less what is needed), MRP plans a purchase.</param>
/// <param name="MaxQuantity">The purchase brings stock up to this.</param>
public sealed record SetMroPolicy(string PartNumber, decimal MinQuantity, decimal ReorderPoint, decimal MaxQuantity, bool CriticalSpare, int? ShelfLifeDays);

public sealed record ClearMroPolicy(string PartNumber);

public sealed record RecordObsolescence(string PartNumber, ObsolescenceStatus Status, DateOnly? LastTimeBuy, DateOnly? EndOfLife, DateOnly? EndOfSupport, string Evidence, string? ReplacementPart);

public sealed record ProposeReplacement(long RecordId, string ReplacementPart);

public sealed record ApproveReplacement(long RecordId);

public sealed record RejectReplacement(long RecordId);

public sealed record MroPolicyChanged(string PartNumber, decimal ReorderPoint, decimal MaxQuantity, bool Cleared) : IDomainEvent
{
    public static string EventType => nameof(MroPolicyChanged);

    public static int SchemaVersion => 1;
}

public sealed record ObsolescenceIdentified(long RecordId, string PartNumber, string Status, DateOnly? EndOfSupport) : IDomainEvent
{
    public static string EventType => nameof(ObsolescenceIdentified);

    public static int SchemaVersion => 1;
}

public sealed record ReplacementChanged(long RecordId, string PartNumber, string ReplacementPart, string Change) : IDomainEvent
{
    public static string EventType => nameof(ReplacementChanged);

    public static int SchemaVersion => 1;
}

public interface IObsolescenceStore
{
    Task InsertMroPolicyAsync(MroPolicyInfo policy, CancellationToken ct);

    Task<long> InsertObsolescenceAsync(ObsolescenceInfo record, CancellationToken ct);

    Task UpdateReplacementAsync(ObsolescenceInfo record, CancellationToken ct);
}

/// <summary>
/// MRO stocking parameters and obsolescence records (V2.8). Both are history: a new setting or record is
/// added, the old one stays. An obsolescence record states what is known and from what evidence; a
/// replacement is only what a person names and a different person with engineering authority approves —
/// Forge never proposes one.
/// </summary>
public sealed class ObsolescenceService(CommandPipeline pipeline)
{
    public const string SourceContext = EngineeringService.SourceContext;
    private const string PolicyEntity = "MroPolicy";
    private const string Entity = "Obsolescence";

    public Task<CommandResult> SetMroPolicyAsync(SetMroPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(SetMroPolicy), Permissions.MroManage, ResourceRef.Of(PolicyEntity, part), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var item = await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct) ?? throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            if (item.MakeBuy != MakeBuy.Buy)
            {
                throw new CommandRejectedException("not_purchased", $"{part} is made, not bought; MRO stock is bought.");
            }

            if (c.MinQuantity < 0 || c.ReorderPoint < c.MinQuantity || c.MaxQuantity <= c.ReorderPoint || c.MaxQuantity > 10_000_000)
            {
                throw new CommandRejectedException("invalid_levels", "Levels must be 0 ≤ minimum ≤ reorder point < maximum.");
            }

            if (c.ShelfLifeDays is <= 0 or > 36_500)
            {
                throw new CommandRejectedException("invalid_shelf_life", "Shelf life is a number of days above zero, or empty.");
            }

            var history = await uow.Store<IMroLookup>().MroHistoryAsync(part, ct);
            var current = history.Count > 0 ? history[0] : null;
            var policy = new MroPolicyInfo(0, part, c.MinQuantity, c.ReorderPoint, c.MaxQuantity, c.CriticalSpare, c.ShelfLifeDays, false, context.Actor, now, context.Reason!);
            if (current is { Cleared: false } && (current.MinQuantity, current.ReorderPoint, current.MaxQuantity, current.CriticalSpare, current.ShelfLifeDays) ==
                (c.MinQuantity, c.ReorderPoint, c.MaxQuantity, c.CriticalSpare, c.ShelfLifeDays))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(part, $"{part} already has these levels."));
            }

            await uow.Store<IObsolescenceStore>().InsertMroPolicyAsync(policy, ct);
            return new CommandOutcome(
                CommandResult.Ok(part, $"{part}: reorder at {Q(c.ReorderPoint)} {item.UnitOfMeasure}, up to {Q(c.MaxQuantity)}{(c.CriticalSpare ? $"; critical spare, minimum {Q(c.MinQuantity)}" : string.Empty)}. MRP plans the purchase."),
                [pipeline.Event(new MroPolicyChanged(part, c.ReorderPoint, c.MaxQuantity, false), context, SourceContext, PolicyEntity, part)],
                new AuditChange(PolicyEntity, part, current is null or { Cleared: true } ? null : Levels(current), Levels(policy)));
        }, ct);
    }

    public Task<CommandResult> ClearMroPolicyAsync(ClearMroPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(ClearMroPolicy), Permissions.MroManage, ResourceRef.Of(PolicyEntity, part), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var history = await uow.Store<IMroLookup>().MroHistoryAsync(part, ct);
            var current = history.Count > 0 ? history[0] : null;
            if (current is null or { Cleared: true })
            {
                throw new CommandRejectedException("no_policy", $"{part} has no MRO policy in force.");
            }

            await uow.Store<IObsolescenceStore>().InsertMroPolicyAsync(current with { Cleared = true, SetBy = context.Actor, SetAt = now, Reason = context.Reason! }, ct);
            return new CommandOutcome(
                CommandResult.Ok(part, $"{part}: MRO policy cleared; MRP no longer replenishes it to a maximum."),
                [pipeline.Event(new MroPolicyChanged(part, current.ReorderPoint, current.MaxQuantity, true), context, SourceContext, PolicyEntity, part)],
                new AuditChange(PolicyEntity, part, Levels(current), "Cleared"));
        }, ct);
    }

    public Task<CommandResult> RecordAsync(RecordObsolescence c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(RecordObsolescence), Permissions.ObsolescenceRecord, ResourceRef.Of("Item", part));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var engineering = uow.Store<IEngineeringLookup>();
            _ = await engineering.FindItemAsync(part, ct) ?? throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            var evidence = (c.Evidence ?? string.Empty).Trim();
            if (evidence.Length is 0 or > 1000)
            {
                throw new CommandRejectedException("evidence_required", "Say where this comes from: the manufacturer notice, distributor statement or other evidence (at most 1000 characters).");
            }

            if (c.Status is ObsolescenceStatus.LastTimeBuy && c.LastTimeBuy is null)
            {
                throw new CommandRejectedException("date_required", "A last-time-buy record needs the last-time-buy date.");
            }

            if ((c.LastTimeBuy is { } ltb && c.EndOfLife is { } eol && ltb > eol) || (c.EndOfLife is { } e && c.EndOfSupport is { } s && s < e))
            {
                throw new CommandRejectedException("invalid_dates", "Last-time-buy comes before end of life, and end of support is not before end of life.");
            }

            var replacement = await ReplacementAsync(engineering, part, c.ReplacementPart, ct);
            var record = new ObsolescenceInfo(0, part, c.Status, c.LastTimeBuy, c.EndOfLife, c.EndOfSupport, evidence, context.Actor, now,
                replacement, replacement is null ? ReplacementStatus.None : ReplacementStatus.Proposed, replacement is null ? null : context.Actor, replacement is null ? null : now, null, null, null);
            var id = await uow.Store<IObsolescenceStore>().InsertObsolescenceAsync(record, ct);
            var key = Key(id);
            var events = new List<EventEnvelope> { pipeline.Event(new ObsolescenceIdentified(id, part, c.Status.ToString(), c.EndOfSupport), context, SourceContext, Entity, key) };
            if (replacement is not null)
            {
                events.Add(pipeline.Event(new ReplacementChanged(id, part, replacement, "Proposed"), context, SourceContext, Entity, key));
            }

            return new CommandOutcome(
                CommandResult.Ok(key, $"Obsolescence {key}: {part} {Label(c.Status)}{(c.EndOfSupport is { } eos ? $", end of support {eos:yyyy-MM-dd}" : string.Empty)}. "
                    + (replacement is null ? "No replacement recorded; the item stays flagged." : $"Replacement {replacement} proposed; someone else with engineering authority approves it.")),
                events,
                new AuditChange(Entity, key, null, CanonicalJson.Serialize(record with { RecordId = id })));
        }, ct);
    }

    public Task<CommandResult> ProposeReplacementAsync(ProposeReplacement c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(ProposeReplacement), c.RecordId, c, Permissions.ObsolescenceRecord, false, context, async (uow, r, now) =>
        {
            if (r.Replacement is not (ReplacementStatus.None or ReplacementStatus.Rejected))
            {
                throw new CommandRejectedException("invalid_status", $"Obsolescence {r.RecordId} already has a {r.Replacement.ToString().ToLowerInvariant()} replacement ({r.ReplacementPart}).");
            }

            var replacement = await ReplacementAsync(uow.Store<IEngineeringLookup>(), r.PartNumber, c.ReplacementPart, ct)
                ?? throw new CommandRejectedException("replacement_required", "Name the replacement part.");
            return (r with { ReplacementPart = replacement, Replacement = ReplacementStatus.Proposed, ReplacementProposedBy = context.Actor, ReplacementProposedAt = now, ReplacementDecidedBy = null, ReplacementDecidedAt = null, ReplacementReason = null },
                "Proposed", $"Replacement {replacement} proposed for {r.PartNumber}; someone else with engineering authority approves it.");
        }, ct);
    }

    public Task<CommandResult> ApproveReplacementAsync(ApproveReplacement c, CommandContext context, CancellationToken ct) =>
        DecideAsync(nameof(ApproveReplacement), c?.RecordId ?? 0, c!, ReplacementStatus.Approved, context, ct);

    public Task<CommandResult> RejectReplacementAsync(RejectReplacement c, CommandContext context, CancellationToken ct) =>
        DecideAsync(nameof(RejectReplacement), c?.RecordId ?? 0, c!, ReplacementStatus.Rejected, context, ct);

    public static string Label(ObsolescenceStatus status) => status switch
    {
        ObsolescenceStatus.NotRecommended => "not recommended for new designs",
        ObsolescenceStatus.LastTimeBuy => "last-time buy",
        ObsolescenceStatus.EndOfLife => "end of life",
        _ => "obsolete",
    };

    private Task<CommandResult> DecideAsync<TCommand>(string type, long recordId, TCommand command, ReplacementStatus decision, CommandContext context, CancellationToken ct) =>
        ChangeAsync(type, recordId, command, Permissions.AlternatesApprove, true, context, (_, r, now) =>
        {
            if (r.Replacement != ReplacementStatus.Proposed)
            {
                throw new CommandRejectedException("invalid_status", $"Obsolescence {r.RecordId} has no proposed replacement to decide.");
            }

            if (r.ReplacementProposedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You proposed this replacement; a different person with engineering authority decides it.");
            }

            var updated = r with { Replacement = decision, ReplacementDecidedBy = context.Actor, ReplacementDecidedAt = now, ReplacementReason = context.Reason };
            return Task.FromResult(decision == ReplacementStatus.Approved
                ? (updated, "Approved", $"Replacement {r.ReplacementPart} approved for {r.PartNumber}. Alternates, BOMs and assets still change only through their own records.")
                : (updated, "Rejected", $"Replacement {r.ReplacementPart} rejected; {r.PartNumber} stays flagged."));
        }, ct);

    private Task<CommandResult> ChangeAsync<TCommand>(
        string type,
        long recordId,
        TCommand command,
        string permission,
        bool reasonRequired,
        CommandContext context,
        Func<Kernel.Access.IKernelUnitOfWork, ObsolescenceInfo, DateTimeOffset, Task<(ObsolescenceInfo Updated, string Change, string Message)>> change,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var key = Key(recordId);
        var spec = new CommandSpec(SourceContext, type, permission, ResourceRef.Of(Entity, key), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var record = await uow.Store<IObsolescenceLookup>().FindObsolescenceAsync(recordId, ct)
                ?? throw new CommandRejectedException("obsolescence_not_found", $"Obsolescence record {key} does not exist.");
            var (updated, what, message) = await change(uow, record, now);
            await uow.Store<IObsolescenceStore>().UpdateReplacementAsync(updated, ct);
            return new CommandOutcome(
                CommandResult.Ok(key, message),
                [pipeline.Event(new ReplacementChanged(recordId, record.PartNumber, updated.ReplacementPart!, what), context, SourceContext, Entity, key)],
                new AuditChange(Entity, key, $"{record.Replacement} {record.ReplacementPart}".Trim(), $"{updated.Replacement} {updated.ReplacementPart}"));
        }, ct);
    }

    private static async Task<string?> ReplacementAsync(IEngineeringLookup engineering, string part, string? raw, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var replacement = BusinessCode.Require(raw, "Replacement part");
        if (replacement == part)
        {
            throw new CommandRejectedException("same_part", "An item does not replace itself.");
        }

        return await engineering.FindItemAsync(replacement, ct) is null
            ? throw new CommandRejectedException("unknown_part", $"Replacement {replacement} does not exist; create the item first.")
            : replacement;
    }

    private static string Levels(MroPolicyInfo p) =>
        $"min {Q(p.MinQuantity)}, reorder {Q(p.ReorderPoint)}, max {Q(p.MaxQuantity)}{(p.CriticalSpare ? ", critical" : string.Empty)}{(p.ShelfLifeDays is { } d ? $", shelf life {d} d" : string.Empty)}";

    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static string Q(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
