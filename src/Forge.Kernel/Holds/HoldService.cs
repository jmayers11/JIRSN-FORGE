using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Kernel.Holds;

/// <summary>A record a hold can be placed on, found through its owning context.</summary>
/// <param name="Id">The normalized target id (codes in upper case, operations as ORDER/SEQUENCE).</param>
public sealed record HoldTarget(HoldTargetType Type, string Id, string? Part, string Label, Guid? ProgramId);

/// <summary>Finds hold targets in the contexts that own them. The host implements it; the kernel never knows the contexts.</summary>
public interface IHoldTargetResolver
{
    Task<HoldTarget?> ResolveAsync(IKernelUnitOfWork uow, HoldTargetType type, string id, string? part, CancellationToken ct);
}

/// <summary>Place a hold. The command's reason is the hold's reason.</summary>
/// <param name="RequiredResolution">What must happen before it is released; a default per kind when empty.</param>
public sealed record PlaceHold(
    HoldKind Kind,
    HoldTargetType TargetType,
    string TargetId,
    string? TargetPart = null,
    string? Source = null,
    string? Evidence = null,
    IReadOnlyList<string>? AffectedRecords = null,
    string? RequiredResolution = null);

public sealed record ReleaseHold(long HoldId);

/// <summary>Change what a kind of hold blocks (V3.10: the configured downstream activities).</summary>
public sealed record SetHoldKindPolicy(HoldKind Kind, IReadOnlyList<HoldActivity> Blocks);

public sealed record HoldChanged(long HoldId, string Kind, string Target, bool Active) : IDomainEvent
{
    public static string EventType => nameof(HoldChanged);

    public static int SchemaVersion => 1;
}

public sealed record HoldKindPolicyChanged(string Kind, IReadOnlyList<string> Blocks) : IDomainEvent
{
    public static string EventType => nameof(HoldKindPolicyChanged);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Generalized holds (V3.10, V3 brief AS). Anyone with <c>holds.place</c> places a hold of any kind with a reason, on a
/// record that exists. Releasing needs the kind's release authority (quality holds: quality disposition; procurement
/// holds: purchasing approval; and so on): the person who placed a hold cannot release it without that authority.
/// </summary>
public sealed class HoldService(CommandPipeline pipeline, IKernelUnitOfWorkFactory unitOfWork, IHoldTargetResolver targets)
{
    public const string SourceContext = "Kernel";
    private const string Entity = "Hold";

    public Task<CommandResult> PlaceAsync(PlaceHold c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var spec = new CommandSpec(SourceContext, nameof(PlaceHold), Permissions.HoldsPlace, ResourceRef.Of(Entity, c.Kind.ToString()), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var target = await targets.ResolveAsync(uow, c.TargetType, c.TargetId ?? string.Empty, Optional(c.TargetPart), ct);
            if (target is null || (target.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p))))
            {
                throw new CommandRejectedException("hold_target_not_found", $"There is no {c.TargetType} {c.TargetId}.");
            }

            var affected = (c.AffectedRecords ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.Ordinal).ToList();
            var hold = new Hold(0, c.Kind, target.Type, target.Id, target.Part, Optional(c.Source) ?? "Placed by hand", context.Reason!.Trim(), Optional(c.Evidence), affected,
                Optional(c.RequiredResolution) ?? DefaultResolution(c.Kind), now, context.Actor);
            var same = (await uow.Store<IHoldLookup>().ListHoldsAsync(false, ct)).FirstOrDefault(h => h.Kind == hold.Kind && h.TargetType == hold.TargetType && h.TargetId == hold.TargetId && h.TargetPart == hold.TargetPart);
            if (same is not null)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(Key(same.HoldId), $"{same.Target} already has a {same.Kind} hold ({same.HoldId})."));
            }

            var id = await uow.Store<IHoldStore>().InsertHoldAsync(hold, ct);
            hold = hold with { HoldId = id };
            var program = target.ProgramId is { } g ? new ProgramId(g) : (ProgramId?)null;
            return new CommandOutcome(
                CommandResult.Ok(Key(id), $"{c.Kind} hold {id} on {target.Label}. To release: {hold.RequiredResolution}"),
                [pipeline.Event(new HoldChanged(id, c.Kind.ToString(), hold.Target, true), context, SourceContext, Entity, Key(id), program)],
                new AuditChange(Entity, Key(id), null, CanonicalJson.Serialize(hold), program));
        }, ct);
    }

    /// <summary>Releases a hold. The permission checked is the release authority of the hold's kind.</summary>
    public async Task<CommandResult> ReleaseAsync(ReleaseHold c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var key = Key(c.HoldId);
        Hold? found;
        IReadOnlyList<HoldKindPolicy> policies;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            found = (await read.Store<IHoldLookup>().ListHoldsAsync(true, ct)).FirstOrDefault(h => h.HoldId == c.HoldId);
            policies = await read.Store<IHoldLookup>().ListKindPoliciesAsync(ct);
        }

        var permission = found is null ? Permissions.HoldsPlace : PolicyOf(policies, found.Kind).ReleasePermission;
        var spec = new CommandSpec(SourceContext, nameof(ReleaseHold), permission, ResourceRef.Of(Entity, key), ReasonRequired: true);
        return await pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var hold = (await uow.Store<IHoldLookup>().ListHoldsAsync(true, ct)).FirstOrDefault(h => h.HoldId == c.HoldId)
                ?? throw new CommandRejectedException("hold_not_found", $"Hold {key} does not exist.");
            var target = await targets.ResolveAsync(uow, hold.TargetType, hold.TargetId, hold.TargetPart, ct);
            if (target is null || (target.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p))))
            {
                throw new CommandRejectedException("hold_not_found", $"Hold {key} does not exist.");
            }

            var program = target.ProgramId is { } g ? new ProgramId(g) : (ProgramId?)null;
            if (!hold.Active)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(key, $"Hold {key} was already released."));
            }

            var reason = context.Reason!.Trim();
            await uow.Store<IHoldStore>().ReleaseHoldAsync(c.HoldId, now, context.Actor, reason, ct);
            var released = hold with { ReleasedAt = now, ReleasedBy = context.Actor, ReleaseReason = reason };
            return new CommandOutcome(
                CommandResult.Ok(key, $"{hold.Kind} hold {key} on {hold.Target} released."),
                [pipeline.Event(new HoldChanged(c.HoldId, hold.Kind.ToString(), hold.Target, false), context, SourceContext, Entity, key, program)],
                new AuditChange(Entity, key, CanonicalJson.Serialize(hold), CanonicalJson.Serialize(released), program));
        }, ct);
    }

    public Task<CommandResult> SetKindPolicyAsync(SetHoldKindPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(SetHoldKindPolicy), Permissions.AccessManage, ResourceRef.Of("HoldKind", c.Kind.ToString()), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var blocks = (c.Blocks ?? []).Distinct().Order().ToList();
            if (blocks.Count == 0)
            {
                throw new CommandRejectedException("invalid_policy", "A kind of hold blocks at least one activity.");
            }

            var before = PolicyOf(await uow.Store<IHoldLookup>().ListKindPoliciesAsync(ct), c.Kind);
            if (blocks.SequenceEqual(before.Blocks.Order()))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(c.Kind.ToString(), $"{c.Kind} holds already block {string.Join(", ", blocks)}."));
            }

            var policy = before with { Blocks = blocks, ChangedAt = now, ChangedBy = context.Actor };
            await uow.Store<IHoldStore>().SetKindPolicyAsync(policy, ct);
            var names = blocks.Select(b => b.ToString()).ToList();
            return new CommandOutcome(
                CommandResult.Ok(c.Kind.ToString(), $"{c.Kind} holds now block {string.Join(", ", names)}."),
                [pipeline.Event(new HoldKindPolicyChanged(c.Kind.ToString(), names), context, SourceContext, "HoldKind", c.Kind.ToString())],
                new AuditChange("HoldKind", c.Kind.ToString(), CanonicalJson.Serialize(before), CanonicalJson.Serialize(policy)));
        }, ct);
    }

    public static HoldKindPolicy PolicyOf(IReadOnlyCollection<HoldKindPolicy> policies, HoldKind kind) =>
        policies.FirstOrDefault(p => p.Kind == kind) ?? HoldKindPolicy.Defaults.First(p => p.Kind == kind);

    public static string DefaultResolution(HoldKind kind) => kind switch
    {
        HoldKind.Quality => "Quality dispositions the affected material or work.",
        HoldKind.Material => "Inventory control confirms the material is usable.",
        HoldKind.Production => "A supervisor or planner confirms production may continue.",
        HoldKind.Engineering => "Engineering releases the definition the work follows.",
        HoldKind.Customer => "The customer's direction is recorded by the program manager.",
        HoldKind.Shipping => "Shipping confirms the shipment may leave.",
        HoldKind.Procurement => "Purchasing approves buying from or receiving from the supplier again.",
        HoldKind.Configuration => "The change board approves the configuration.",
        HoldKind.Safety => "The safety approver confirms the work is safe.",
        _ => "A security administrator releases it.",
    };

    private static string? Optional(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);
}
