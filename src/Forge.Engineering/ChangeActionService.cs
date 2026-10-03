using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Engineering;

/// <summary>Raised by propagation (system actor): one action per change, kind and target.</summary>
public sealed record RaiseChangeAction(string ChangeNumber, ChangeActionKind Kind, string Target, string? PartNumber, string? DocumentNumber, string? Revision, Guid? PersonId, Guid? ProgramId, string Detail);

/// <summary>A person's decision on an action: the resolution is one of the kind's resolutions (WipResolution, PurchaseResolution, AcknowledgmentResolution).</summary>
public sealed record ResolveChangeAction(long ActionId, string Resolution, string? Notes);

public sealed record ChangeActionRaised(long ActionId, string ChangeNumber, string Kind, string Target) : IDomainEvent
{
    public static string EventType => nameof(ChangeActionRaised);

    public static int SchemaVersion => 1;
}

public sealed record ChangeActionResolved(long ActionId, string ChangeNumber, string Kind, string Resolution) : IDomainEvent
{
    public static string EventType => nameof(ChangeActionResolved);

    public static int SchemaVersion => 1;
}

public sealed record EngineeringChangeReleased(string Number, DateOnly EffectiveFrom) : IDomainEvent
{
    public static string EventType => nameof(EngineeringChangeReleased);

    public static int SchemaVersion => 1;
}

public sealed record PropagationFinished(string Number, string Status, int Steps, int Unresolved) : IDomainEvent
{
    public static string EventType => nameof(PropagationFinished);

    public static int SchemaVersion => 1;
}

public interface IPropagationStore
{
    Task StartAsync(string changeNumber, Guid correlationId, DateTimeOffset at, CancellationToken ct);

    Task PlanStepAsync(PropagationStepInfo step, CancellationToken ct);

    Task SetStepAsync(string changeNumber, string stepKey, PropagationStepStatus status, string detail, string? error, DateTimeOffset at, CancellationToken ct);

    Task FinishRunAsync(string changeNumber, PropagationStatus status, string? error, DateTimeOffset at, CancellationToken ct);

    /// <summary>Inserts the action unless one exists for the same change, kind and target; returns the new id or null.</summary>
    Task<long?> InsertActionAsync(ChangeActionInfo action, CancellationToken ct);

    Task ResolveActionAsync(long actionId, string resolution, string? notes, string actor, DateTimeOffset at, CancellationToken ct);
}

/// <summary>
/// What people decide because a change was released (V2.5): quality dispositions work in progress, a buyer
/// reviews each open purchase order line, and people who work where a changed instruction is used
/// acknowledge it (recorded by a training coordinator). Actions are raised once, decided once, never edited.
/// </summary>
public sealed class ChangeActionService(CommandPipeline pipeline)
{
    public const string SourceContext = EngineeringService.SourceContext;
    private const string ActionEntity = "ChangeAction";

    /// <summary>The permission that decides each kind of action.</summary>
    public static string PermissionFor(ChangeActionKind kind) => kind switch
    {
        ChangeActionKind.WipDisposition => Permissions.QualityDisposition,
        ChangeActionKind.PurchaseReview => Permissions.PurchasingManage,
        _ => Permissions.CompetencyManage,
    };

    /// <summary>The resolutions a kind of action accepts.</summary>
    public static IReadOnlyList<string> ResolutionsFor(ChangeActionKind kind) => kind switch
    {
        ChangeActionKind.WipDisposition => Enum.GetNames<WipResolution>(),
        ChangeActionKind.PurchaseReview => Enum.GetNames<PurchaseResolution>(),
        _ => Enum.GetNames<AcknowledgmentResolution>(),
    };

    public Task<CommandResult> RaiseAsync(RaiseChangeAction c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var program = c.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
        var spec = new CommandSpec(SourceContext, nameof(RaiseChangeAction), Permissions.ChangesManage, new ResourceRef(ActionEntity, null, program));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var change = await uow.Store<IChangeLookup>().FindChangeAsync(c.ChangeNumber, ct)
                ?? throw new CommandRejectedException("change_not_found", $"Engineering change {c.ChangeNumber} does not exist.");
            if (change.Status != ChangeStatus.Released)
            {
                throw new CommandRejectedException("invalid_status", $"{change.Number} is {change.Status}; actions follow a released change.");
            }

            var action = new ChangeActionInfo(0, change.Number, c.Kind, c.Target, c.PartNumber, c.DocumentNumber, c.Revision, c.PersonId, c.ProgramId, c.Detail, ChangeActionStatus.Open,
                null, null, context.Actor, now, null, null);
            if (await uow.Store<IPropagationStore>().InsertActionAsync(action, ct) is not { } id)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(c.Target, $"{change.Number} already has a {c.Kind} action for {c.Target}."));
            }

            var key = id.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(key, $"{c.Kind} action {id} raised for {c.Target} ({change.Number})."),
                [pipeline.Event(new ChangeActionRaised(id, change.Number, c.Kind.ToString(), c.Target), context, SourceContext, ActionEntity, key, program)],
                new AuditChange(ActionEntity, key, null, CanonicalJson.Serialize(action with { ActionId = id }), program));
        }, ct);
    }

    public Task<CommandResult> ResolveAsync(ResolveChangeAction c, ChangeActionKind kind, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var key = c.ActionId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(ResolveChangeAction), PermissionFor(kind), ResourceRef.Of(ActionEntity, key));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var action = await uow.Store<IPropagationLookup>().FindActionAsync(c.ActionId, ct);
            if (action is null || action.Kind != kind || (action.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p))))
            {
                throw new CommandRejectedException("action_not_found", $"Action {c.ActionId} does not exist.");
            }

            if (action.Status == ChangeActionStatus.Resolved)
            {
                throw new CommandRejectedException("action_resolved", $"Action {c.ActionId} was already resolved by {action.ResolvedBy}.");
            }

            var resolution = ResolutionsFor(kind).FirstOrDefault(r => string.Equals(r, (c.Resolution ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new CommandRejectedException("invalid_resolution", $"A {kind} action is resolved as {string.Join(", ", ResolutionsFor(kind))}.");
            var notes = string.IsNullOrWhiteSpace(c.Notes) ? null : c.Notes.Trim();
            if (notes is { Length: > 2000 })
            {
                notes = notes[..2000];
            }

            var notesNeeded = kind == ChangeActionKind.WipDisposition || (kind == ChangeActionKind.PurchaseReview && resolution != nameof(PurchaseResolution.NoImpact));
            if (notesNeeded && notes is null)
            {
                throw new CommandRejectedException("notes_required", kind == ChangeActionKind.WipDisposition
                    ? "Record the disposition's basis: the deviation, concession or rework instruction."
                    : "Record the reference: who at the supplier was told, or the amended line.");
            }

            if (kind == ChangeActionKind.TrainingAcknowledgment && action.PersonId is { } person && context.Actor == "person:" + person)
            {
                throw new CommandRejectedException("separation_of_duties", "A training coordinator records your acknowledgment, not you.");
            }

            await uow.Store<IPropagationStore>().ResolveActionAsync(action.ActionId, resolution, notes, context.Actor, now, ct);
            var program = action.ProgramId is { } pid ? new ProgramId(pid) : (ProgramId?)null;
            return new CommandOutcome(
                CommandResult.Ok(key, $"Action {action.ActionId} ({action.Target}) resolved: {resolution}."),
                [pipeline.Event(new ChangeActionResolved(action.ActionId, action.ChangeNumber, kind.ToString(), resolution), context, SourceContext, ActionEntity, key, program)],
                new AuditChange(ActionEntity, key, CanonicalJson.Serialize(action), CanonicalJson.Serialize(action with { Status = ChangeActionStatus.Resolved, Resolution = resolution, Notes = notes, ResolvedBy = context.Actor, ResolvedAt = now }), program));
        }, ct);
    }
}
