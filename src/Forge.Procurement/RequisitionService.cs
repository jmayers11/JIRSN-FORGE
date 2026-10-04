using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Procurement.Contracts;

namespace Forge.Procurement;

/// <param name="SourceKey">What the purchase is for, as kind:key (work-order:57, demand:104, order:10012, project:FAC-2027-014/3).</param>
/// <param name="SourceLabel">The same in words, shown to the buyer and kept on the purchase order line.</param>
public sealed record RaiseRequisition(
    string PartNumber,
    decimal Quantity,
    DateOnly NeedDate,
    PurchaseSourceKind SourceKind,
    string SourceKey,
    string SourceLabel,
    long? DemandId,
    string? PlannedOrder,
    string? RequiredRevision,
    string? SupplierCode,
    Guid? ProgramId);

public sealed record CancelRequisition(long RequisitionId);

public sealed record RequisitionChanged(long RequisitionId, string PartNumber, string SourceKind, string SourceKey, string Change) : IDomainEvent
{
    public static string EventType => nameof(RequisitionChanged);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Requisitions from every source in one queue (V2.7). Anyone who plans work that needs a purchase raises
/// one, always saying what it is for; a buyer turns it into a purchase order line, which keeps that source,
/// so every receipt and issue from it can answer "why was this purchased?".
/// </summary>
public sealed class RequisitionService(CommandPipeline pipeline)
{
    public const string SourceContext = PurchasingService.SourceContext;
    private const string Entity = "Requisition";

    public Task<CommandResult> RaiseAsync(RaiseRequisition c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var program = c.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
        var spec = new CommandSpec(SourceContext, nameof(RaiseRequisition), Permissions.RequisitionRaise, new ResourceRef(Entity, null, program));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var part = BusinessCode.Require(c.PartNumber, "Part");
            var engineering = uow.Store<IEngineeringLookup>();
            var item = await engineering.FindItemAsync(part, ct) ?? throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            if (item.MakeBuy != MakeBuy.Buy)
            {
                throw new CommandRejectedException("not_purchased", $"{part} is made, not bought.");
            }

            if (c.Quantity <= 0 || c.Quantity > 10_000_000)
            {
                throw new CommandRejectedException("invalid_quantity", "Quantity must be above zero.");
            }

            var key = SourceKey(c.SourceKey);
            var label = (c.SourceLabel ?? string.Empty).Trim();
            if (label.Length is 0 or > 500)
            {
                throw new CommandRejectedException("source_required", "Say what the purchase is for (at most 500 characters).");
            }

            string? revision = null;
            if (!string.IsNullOrWhiteSpace(c.RequiredRevision))
            {
                revision = c.RequiredRevision.Trim().ToUpperInvariant();
                if ((await engineering.ListRevisionsAsync(part, ct)).All(r => r.Revision != revision))
                {
                    throw new CommandRejectedException("revision_not_found", $"{part} has no revision {revision}.");
                }
            }

            string? supplier = null;
            if (!string.IsNullOrWhiteSpace(c.SupplierCode))
            {
                supplier = (await SupplierService.RequireOrderableAsync(uow, c.SupplierCode, ct)).Code;
            }

            var lookup = uow.Store<IRequisitionLookup>();
            if ((await lookup.ListRequisitionsAsync(true, ct)).FirstOrDefault(r => r.PartNumber == part && r.SourceKey == key) is { } open)
            {
                throw new CommandRejectedException("requisition_exists", $"Requisition {open.RequisitionId} already asks for {part} for {key}.");
            }

            var requisition = new RequisitionInfo(0, part, c.Quantity, c.NeedDate, c.SourceKind, key, label, c.DemandId, Optional(c.PlannedOrder, 100), revision, supplier, c.ProgramId,
                RequisitionStatus.Open, context.Actor, now, null, null, null, null, null, null, null);
            var number = await uow.Store<IProcurementStore>().InsertRequisitionAsync(requisition, ct);
            var id = number.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Requisition {id}: {c.Quantity} {item.UnitOfMeasure} {part} by {c.NeedDate:yyyy-MM-dd} for {label}. A buyer turns it into a purchase order."),
                [pipeline.Event(new RequisitionChanged(number, part, c.SourceKind.ToString(), key, "Raised"), context, SourceContext, Entity, id, program)],
                new AuditChange(Entity, id, null, CanonicalJson.Serialize(requisition with { RequisitionId = number }), program));
        }, ct);
    }

    public Task<CommandResult> CancelAsync(CancelRequisition c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var id = c.RequisitionId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(CancelRequisition), Permissions.RequisitionRaise, ResourceRef.Of(Entity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var requisition = await uow.Store<IRequisitionLookup>().FindRequisitionAsync(c.RequisitionId, ct);
            if (requisition is null || (requisition.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p))))
            {
                throw new CommandRejectedException("requisition_not_found", $"Requisition {id} does not exist.");
            }

            if (requisition.Status != RequisitionStatus.Open)
            {
                throw new CommandRejectedException("requisition_not_open", $"Requisition {id} is {requisition.Status}{(requisition.PoNumber is { } po ? $"; cancel PO {po} line {requisition.PoLine} first" : string.Empty)}.");
            }

            if (requisition.RequestedBy != context.Actor && !context.Principal.HasPermission(Permissions.PurchasingManage))
            {
                throw new CommandRejectedException("not_yours", "Only whoever raised a requisition, or a buyer, cancels it.");
            }

            await uow.Store<IProcurementStore>().CancelRequisitionAsync(requisition.RequisitionId, context.Actor, now, context.Reason!, ct);
            var program = requisition.ProgramId is { } pid ? new ProgramId(pid) : (ProgramId?)null;
            return new CommandOutcome(
                CommandResult.Ok(id, $"Requisition {id} cancelled."),
                [pipeline.Event(new RequisitionChanged(requisition.RequisitionId, requisition.PartNumber, requisition.SourceKind.ToString(), requisition.SourceKey, "Cancelled"), context, SourceContext, Entity, id, program)],
                new AuditChange(Entity, id, requisition.Status.ToString(), nameof(RequisitionStatus.Cancelled), program));
        }, ct);
    }

    private static string SourceKey(string? raw)
    {
        var key = (raw ?? string.Empty).Trim();
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == key.Length - 1 || key.Length > 120 || !key.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is ':' or '-' or '_' or '.' or '/' or '@'))
        {
            throw new CommandRejectedException("invalid_source", "A source is kind:key, for example work-order:57 or demand:104.");
        }

        return key[..colon].ToLowerInvariant() + key[colon..];
    }

    private static string? Optional(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }
}
