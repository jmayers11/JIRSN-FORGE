using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Holds;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Procurement.Contracts;
using Forge.Quality.Contracts;

namespace Forge.Inventory;

/// <summary>Inspect (part of) a receipt into inspection: accept some, reject some, the rest waits.</summary>
public sealed record InspectReceipt(long ReceiptTransactionId, decimal Accepted, decimal Rejected, string? Defect, IReadOnlyList<ReceivingCharacteristicResult>? Results);

/// <summary>
/// Decide what happens to a rejected quantity. The reason given is why. A quantity below the rejected quantity decides
/// only that part; the rest stays on hold as a new pending rejection (ADR 0055). Omitted, it decides the whole rejection.
/// </summary>
public sealed record DispositionRejection(long RejectionId, SupplierDisposition Disposition,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? Quantity = null);

/// <summary>A second person approves using rejected material as is.</summary>
public sealed record ApproveUseAsIs(long RejectionId);

public sealed record ShipReturnToVendor(long RtvId, string? Carrier);

/// <summary>
/// Return less than an open return to vendor holds (ADR 0055). The quantity only goes down; the rest stays on hold as a
/// new pending rejection that needs its own disposition, so nothing leaves quality hold without a decision.
/// </summary>
public sealed record ReduceReturnToVendor(long RtvId, decimal Quantity);

/// <summary>Receive replacement material for a return to vendor: a new instance, linked to the one it replaces.</summary>
public sealed record ReceiveReplacement(long RtvId, decimal Quantity, string? LotNumber, string LocationCode, bool Inspect = true);

/// <summary>Close a shipped return to vendor with a credit instead of replacement material.</summary>
public sealed record CreditReturnToVendor(long RtvId);

/// <summary>Material a customer sent back against a shipment; received onto hold. The reason is the customer's.</summary>
public sealed record ReceiveCustomerReturn(long ShipmentId, string PartNumber, string? LotNumber, decimal Quantity, string LocationCode);

public sealed record DispositionCustomerReturn(long RmaId, CustomerReturnDisposition Disposition,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? DecisionId=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? ExpectedDecisionVersion=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? Quantity=null);

public sealed record ReceivingRecorded(string Record, long Id, string Change) : IDomainEvent
{
    public static string EventType => nameof(ReceivingRecorded);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Receiving inspection, supplier dispositions, returns to vendor and customer returns (V3.11, V3 brief O, P, R, AW).
/// A receipt is accepted in part; first article inspection pending determination never lets material be accepted;
/// replacement material from a supplier is a new instance and the returned one keeps its history.
/// </summary>
public sealed partial class InventoryService
{
    private const string ReceivingEntity = "Receiving";

    public Task<CommandResult> InspectReceiptAsync(InspectReceipt c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var key = Key(c.ReceiptTransactionId);
        var spec = new CommandSpec(SourceContext, nameof(InspectReceipt), Permissions.QualityInspect, ResourceRef.Of(ReceivingEntity, key));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var receiving = uow.Store<IReceivingLookup>();
            var receipt = await receiving.FindTransactionAsync(c.ReceiptTransactionId, ct);
            var outsideReturn = await uow.Store<IOutsideProcessLookup>().FindReturnAsync(c.ReceiptTransactionId, ct);
            if (receipt is null || (receipt.Kind != InventoryTransactionKind.Receipt && outsideReturn is null) || receipt.ToStatus != StockStatus.Inspection)
            {
                throw new CommandRejectedException("receipt_not_found", $"Transaction {key} is not a receipt into inspection.");
            }
            if (outsideReturn is not null)
            {
                var sent = await uow.Store<IOutsideProcessLookup>().ListDispatchesAsync(outsideReturn.Key, ct);
                RequireOutsideProcessAccess(sent.Count == 0 ? null : sent[0], context.Principal);
            }

            var store = uow.Store<IInventoryStore>();
            await store.LockPartAsync(receipt.PartNumber, ct);
            if (c.Accepted < 0 || c.Rejected < 0 || c.Accepted + c.Rejected <= 0)
            {
                throw new CommandRejectedException("invalid_quantity", "Accept or reject a quantity above zero.");
            }

            var state = await ReceivingQueries.ReceiptStateAsync(uow, receipt, ct);
            if (c.Accepted + c.Rejected > state.Remaining)
            {
                throw new CommandRejectedException("over_inspection", $"{state.Remaining} of receipt {key} is still to inspect ({state.Accepted} accepted, {state.Rejected} rejected so far).");
            }

            var location = receipt.ToLocation!;
            await RequireStockAsync(uow, receipt.PartNumber, location, receipt.LotNumber, StockStatus.Inspection, c.Accepted + c.Rejected, ct);
            if (receipt.Actor == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You received this material; a different person inspects it.");
            }

            var results = (c.Results ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Characteristic)).ToList();
            if (outsideReturn is not null && c.Accepted > 0)
            {
                if (results.Count == 0)
                    throw new CommandRejectedException("process_inspection_results_required", "Record the outside-process inspection characteristics before accepting the returned material.");
                await RequireOutsideProcessCertificateAsync(uow, outsideReturn.Key, context.Principal, ct);
            }
            if (c.Rejected == 0 && results.Any(r => !r.Pass))
            {
                throw new CommandRejectedException("results_disagree", "A characteristic failed; reject the quantity it applies to.");
            }

            var defect = c.Rejected > 0 ? Optional(c.Defect) ?? throw new CommandRejectedException("defect_required", "Say what is wrong with the rejected quantity.") : null;
            var fai = await ReceivingQueries.FaiStatusAsync(uow, receipt, state.SupplierCode, ct);
            if (c.Accepted > 0 && !Fai.AllowsAcceptance(fai.Status))
            {
                throw new CommandRejectedException("fai_not_complete", Fai.Explain(fai.Status, fai.Subject) + " Material is not accepted until it is; it may be rejected.");
            }

            if (c.Accepted > 0)
            {
                await RequireLegacyReleaseUnadoptedAsync(uow, context, receipt.PartNumber, receipt.LotNumber, location, now, ct);
                await store.InsertTransactionAsync(new NewTransaction(InventoryTransactionKind.QualityRelease, receipt.PartNumber, c.Accepted, location, StockStatus.Inspection, location, StockStatus.Released,
                    receipt.LotNumber, PoNumber: receipt.PoNumber, Reason: $"Receiving inspection of receipt {key}"), now, context.Actor, ct);
            }

            if (c.Rejected > 0)
            {
                await store.InsertTransactionAsync(new NewTransaction(InventoryTransactionKind.QualityHold, receipt.PartNumber, c.Rejected, location, StockStatus.Inspection, location, StockStatus.Hold,
                    receipt.LotNumber, PoNumber: receipt.PoNumber, Reason: $"Rejected at receiving inspection: {defect}"), now, context.Actor, ct);
            }

            var inspection = new ReceivingInspection(0, receipt.TransactionId, receipt.PartNumber, receipt.LotNumber, location, receipt.PoNumber, state.SupplierCode, c.Accepted, c.Rejected,
                results, fai.Status, context.Actor, now);
            var id = await uow.Store<IReceivingStore>().InsertReceivingInspectionAsync(inspection, ct);
            inspection = inspection with { InspectionId = id };
            long? rejectionId = null;
            if (c.Rejected > 0)
            {
                rejectionId = await uow.Store<IReceivingStore>().InsertRejectionAsync(
                    new SupplierRejection(0, id, receipt.PartNumber, receipt.LotNumber, location, state.SupplierCode, receipt.PoNumber, c.Rejected, defect!, SupplierDisposition.Pending), ct);
            }

            var left = state.Remaining - c.Accepted - c.Rejected;
            var message = $"Receipt {key}: {c.Accepted} accepted, {c.Rejected} rejected" + (rejectionId is { } r ? $" (rejection {r}, disposition pending)" : string.Empty)
                + (left > 0 ? $"; {left} still to inspect." : ".") + (fai.Status == FaiStatus.NotApplicable ? string.Empty : $" {Fai.Explain(fai.Status, fai.Subject)}");
            return new CommandOutcome(
                CommandResult.Ok(Key(id), message),
                [pipeline.Event(new ReceivingRecorded("ReceivingInspection", id, "Inspected"), context, SourceContext, ReceivingEntity, Key(id))],
                new AuditChange("ReceivingInspection", Key(id), null, CanonicalJson.Serialize(inspection)));
        }, ct, (uow, now) => ReceivingAcceptanceAuthorityAsync(uow, spec, c, context, now, ct));
    }

    public Task<CommandResult> DispositionRejectionAsync(DispositionRejection c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = Key(c.RejectionId);
        var spec = new CommandSpec(SourceContext, nameof(DispositionRejection), Permissions.QualityDisposition, ResourceRef.Of("SupplierRejection", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var rejection = (await uow.Store<IReceivingLookup>().ListRejectionsAsync(false, ct)).FirstOrDefault(r => r.RejectionId == c.RejectionId)
                ?? throw new CommandRejectedException("rejection_not_found", $"Rejection {key} does not exist.");
            await uow.Store<IInventoryStore>().LockPartAsync(rejection.PartNumber, ct);
            rejection = (await uow.Store<IReceivingLookup>().ListRejectionsAsync(false, ct)).Single(r => r.RejectionId == c.RejectionId);
            if (rejection.Disposition != SupplierDisposition.Pending)
            {
                throw new CommandRejectedException("already_dispositioned", $"Rejection {key} is already dispositioned ({rejection.Disposition}).");
            }

            if (c.Disposition == SupplierDisposition.Pending)
            {
                throw new CommandRejectedException("invalid_disposition", "Choose return to vendor, rework by supplier, scrap or use as is.");
            }

            var store = uow.Store<IInventoryStore>();
            var (part, remainder) = await SplitRejectionAsync(uow, rejection, c.Quantity, ct);
            var decided = part with { Disposition = c.Disposition, DispositionedBy = context.Actor, DispositionedAt = now };
            await uow.Store<IReceivingStore>().UpdateRejectionAsync(decided, ct);
            string message;
            switch (c.Disposition)
            {
                case SupplierDisposition.Scrap:
                    await RequireStockAsync(uow, decided.PartNumber, decided.LocationCode, decided.LotNumber, StockStatus.Hold, decided.Quantity, ct);
                    await store.InsertTransactionAsync(new NewTransaction(InventoryTransactionKind.Scrap, decided.PartNumber, decided.Quantity, decided.LocationCode, StockStatus.Hold, null, null,
                        decided.LotNumber, PoNumber: decided.PoNumber, Reason: $"Rejection {key} scrapped: {context.Reason}"), now, context.Actor, ct);
                    message = $"Rejection {key}: {decided.Quantity} {decided.PartNumber} scrapped.";
                    break;
                case SupplierDisposition.UseAsIs:
                    message = $"Rejection {key}: use as is for {decided.Quantity}, waiting for a second person to approve.";
                    break;
                default:
                    var rtvId = await uow.Store<IReceivingStore>().InsertRtvAsync(new RtvInfo(0, decided.RejectionId, decided.SupplierCode, decided.PartNumber, decided.LotNumber,
                        decided.LocationCode, decided.Quantity, c.Disposition == SupplierDisposition.ReworkBySupplier, RtvStatus.Open), ct);
                    message = $"Rejection {key}: return to vendor {rtvId} opened for {decided.Quantity}{(c.Disposition == SupplierDisposition.ReworkBySupplier ? " for rework by the supplier" : string.Empty)}.";
                    break;
            }

            List<EventEnvelope> events = [pipeline.Event(new ReceivingRecorded("SupplierRejection", c.RejectionId, c.Disposition.ToString()), context, SourceContext, "SupplierRejection", key)];
            if (remainder is { } rest)
            {
                message += $" {rejection.Quantity - decided.Quantity} stays on hold as pending rejection {Key(rest)}.";
                events.Add(pipeline.Event(new ReceivingRecorded("SupplierRejection", rest, "SplitFrom" + key), context, SourceContext, "SupplierRejection", Key(rest)));
            }

            return new CommandOutcome(
                CommandResult.Ok(key, message),
                events,
                new AuditChange("SupplierRejection", key, CanonicalJson.Serialize(rejection), CanonicalJson.Serialize(decided)));
        }, ct);
    }

    public Task<CommandResult> ApproveUseAsIsAsync(ApproveUseAsIs c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = Key(c.RejectionId);
        var spec = new CommandSpec(SourceContext, nameof(ApproveUseAsIs), Permissions.QualityApprove, ResourceRef.Of("SupplierRejection", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var rejection = (await uow.Store<IReceivingLookup>().ListRejectionsAsync(false, ct)).FirstOrDefault(r => r.RejectionId == c.RejectionId)
                ?? throw new CommandRejectedException("rejection_not_found", $"Rejection {key} does not exist.");
            await uow.Store<IInventoryStore>().LockPartAsync(rejection.PartNumber, ct);
            rejection = (await uow.Store<IReceivingLookup>().ListRejectionsAsync(false, ct)).Single(r => r.RejectionId == c.RejectionId);
            if (rejection.Disposition != SupplierDisposition.UseAsIs || rejection.ApprovedBy is not null)
            {
                throw new CommandRejectedException("not_awaiting_approval", $"Rejection {key} is not waiting for a use-as-is approval.");
            }

            if (rejection.DispositionedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You decided use as is; a different person approves it.");
            }

            var store = uow.Store<IInventoryStore>();
            await RequireStockAsync(uow, rejection.PartNumber, rejection.LocationCode, rejection.LotNumber, StockStatus.Hold, rejection.Quantity, ct);
            await RequireLegacyReleaseUnadoptedAsync(uow, context, rejection.PartNumber, rejection.LotNumber, rejection.LocationCode, now, ct);
            await store.InsertTransactionAsync(new NewTransaction(InventoryTransactionKind.QualityRelease, rejection.PartNumber, rejection.Quantity, rejection.LocationCode, StockStatus.Hold,
                rejection.LocationCode, StockStatus.Released, rejection.LotNumber, PoNumber: rejection.PoNumber, Reason: $"Use as is (rejection {key}): {context.Reason}", ApprovedBy: context.Actor), now, rejection.DispositionedBy!, ct);
            var approved = rejection with { ApprovedBy = context.Actor, ApprovedAt = now };
            await uow.Store<IReceivingStore>().UpdateRejectionAsync(approved, ct);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Rejection {key}: use as is approved; {rejection.Quantity} {rejection.PartNumber} released."),
                [pipeline.Event(new ReceivingRecorded("SupplierRejection", c.RejectionId, "UseAsIsApproved"), context, SourceContext, "SupplierRejection", key)],
                new AuditChange("SupplierRejection", key, CanonicalJson.Serialize(rejection), CanonicalJson.Serialize(approved)));
        }, ct, (uow, now) => ReceivingAcceptanceAuthorityAsync(uow, spec, c, context, now, ct));
    }

    public Task<CommandResult> ShipReturnToVendorAsync(ShipReturnToVendor c, CommandContext context, CancellationToken ct) =>
        RtvAsync(nameof(ShipReturnToVendor), c?.RtvId ?? 0, c!, context, Permissions.InventoryTransact, false, async (uow, rtv, now) =>
        {
            if (rtv.Status != RtvStatus.Open)
            {
                throw new CommandRejectedException("rtv_not_open", $"Return to vendor {rtv.RtvId} is {rtv.Status}.");
            }

            await RequireStockAsync(uow, rtv.PartNumber, rtv.LocationCode, rtv.LotNumber, StockStatus.Hold, rtv.Quantity, ct);
            var txn = await uow.Store<IInventoryStore>().InsertTransactionAsync(new NewTransaction(InventoryTransactionKind.ReturnToVendor, rtv.PartNumber, rtv.Quantity, rtv.LocationCode, StockStatus.Hold,
                null, null, rtv.LotNumber, Reason: $"Return to vendor {rtv.RtvId}{(rtv.SupplierCode is null ? string.Empty : " to " + rtv.SupplierCode)}{(Optional(c!.Carrier) is { } carrier ? " by " + carrier : string.Empty)}"),
                now, context.Actor, ct);
            return (rtv with { Status = RtvStatus.Shipped, ShippedTransactionId = txn, ShippedBy = context.Actor, ShippedAt = now }, $"Return to vendor {rtv.RtvId}: {rtv.Quantity} {rtv.PartNumber} shipped back.");
        }, ct);

    public Task<CommandResult> ReduceReturnToVendorAsync(ReduceReturnToVendor c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = Key(c.RtvId);
        var spec = new CommandSpec(SourceContext, nameof(ReduceReturnToVendor), Permissions.QualityDisposition, ResourceRef.Of("ReturnToVendor", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var rtv = (await uow.Store<IReceivingLookup>().ListRtvsAsync(false, ct)).FirstOrDefault(r => r.RtvId == c.RtvId)
                ?? throw new CommandRejectedException("rtv_not_found", $"Return to vendor {key} does not exist.");
            await uow.Store<IInventoryStore>().LockPartAsync(rtv.PartNumber, ct);
            rtv = (await uow.Store<IReceivingLookup>().ListRtvsAsync(false, ct)).Single(r => r.RtvId == c.RtvId);
            if (rtv.Status != RtvStatus.Open)
            {
                throw new CommandRejectedException("rtv_not_open", $"Return to vendor {key} is {rtv.Status}; only an open return can be reduced.");
            }

            var quantity = RequireQuantity(await ItemAsync(uow, rtv.PartNumber, ct), c.Quantity);
            if (quantity >= rtv.Quantity)
            {
                throw new CommandRejectedException("rtv_quantity_not_reduced", $"Return to vendor {key} is for {rtv.Quantity}; it can be reduced, never increased or kept the same.");
            }

            var rejection = (await uow.Store<IReceivingLookup>().ListRejectionsAsync(false, ct)).Single(r => r.RejectionId == rtv.RejectionId);
            var (part, remainder) = await SplitRejectionAsync(uow, rejection, quantity, ct);
            await uow.Store<IReceivingStore>().UpdateRejectionAsync(part, ct);
            var reduced = rtv with { Quantity = quantity };
            await uow.Store<IReceivingStore>().UpdateRtvAsync(reduced, ct);
            var rest = remainder!.Value;
            return new CommandOutcome(
                CommandResult.Ok(key, $"Return to vendor {key}: reduced from {rtv.Quantity} to {quantity} {rtv.PartNumber}; {rtv.Quantity - quantity} stays on hold as pending rejection {Key(rest)}."),
                [
                    pipeline.Event(new ReceivingRecorded("ReturnToVendor", c.RtvId, "Reduced"), context, SourceContext, "ReturnToVendor", key),
                    pipeline.Event(new ReceivingRecorded("SupplierRejection", rest, "SplitFrom" + Key(rejection.RejectionId)), context, SourceContext, "SupplierRejection", Key(rest)),
                ],
                new AuditChange("ReturnToVendor", key, CanonicalJson.Serialize(rtv), CanonicalJson.Serialize(reduced)));
        }, ct);
    }

    public Task<CommandResult> ReceiveReplacementAsync(ReceiveReplacement c, CommandContext context, CancellationToken ct) =>
        RtvAsync(nameof(ReceiveReplacement), c?.RtvId ?? 0, c!, context, Permissions.InventoryTransact, false, async (uow, rtv, now) =>
        {
            if (rtv.Status != RtvStatus.Shipped)
            {
                throw new CommandRejectedException("rtv_not_shipped", $"Return to vendor {rtv.RtvId} is {rtv.Status}; replacement comes after it is shipped back.");
            }

            var item = await ItemAsync(uow, rtv.PartNumber, ct);
            var quantity = RequireQuantity(item, c!.Quantity);
            if (quantity != rtv.Quantity)
            {
                throw new CommandRejectedException("replacement_quantity_mismatch", $"This return expects {rtv.Quantity}; do not close it with an unexplained replacement quantity of {quantity}.");
            }
            var lot = RequireLot(item, c.LotNumber);
            if (lot is not null && (await uow.Store<IInventoryLookup>().ListLotsAsync(rtv.PartNumber, ct)).Any(l => l.LotNumber == lot))
            {
                throw new CommandRejectedException("replacement_is_new_instance", $"Replacement material is a new instance; lot {lot} already exists and keeps its history.");
            }

            await HoldGate.RequireNoneAsync(uow, new HoldCheck(HoldActivity.Receive, Parts: [rtv.PartNumber], SupplierCode: rtv.SupplierCode), ct);
            var location = await CompanyLocationAsync(uow, c.LocationCode, ct);
            var revision = rtv.LotNumber is { } old ? (await uow.Store<IInventoryLookup>().ListLotsAsync(rtv.PartNumber, ct)).FirstOrDefault(l => l.LotNumber == old)?.PartRevision : null;
            if (lot is not null)
            {
                await LotAsync(uow, rtv.PartNumber, lot, null, revision, item.Serialized ? IdentityKind.Serial : IdentityKind.Lot, now, context.Actor, ct);
            }

            var store = uow.Store<IInventoryStore>();
            var rejection = (await uow.Store<IReceivingLookup>().ListRejectionsAsync(false, ct)).Single(r => r.RejectionId == rtv.RejectionId);
            var governed = await productRelease.OutputPolicyAsync(uow, context.Principal, item.PartNumber, revision, location.Code, null, rejection.PoNumber, now, ct);
            var status = c.Inspect || governed is not null ? StockStatus.Inspection : StockStatus.Released;
            var txn = await store.InsertTransactionAsync(new NewTransaction(InventoryTransactionKind.Receipt, rtv.PartNumber, quantity, null, null, location.Code, status, lot,
                PoNumber: rejection.PoNumber, Reason: $"Replacement for return to vendor {rtv.RtvId}", PartRevision: revision), now, context.Actor, ct);
            if (lot is not null && rtv.LotNumber is { } returned)
            {
                await store.InsertInstanceLinkAsync(new InstanceLink(0, InstanceRelation.Replaces, rtv.PartNumber, returned, rtv.PartNumber, lot, quantity, rtv.ShippedTransactionId!.Value, txn,
                    $"Replacement for return to vendor {rtv.RtvId}", now, context.Actor), ct);
            }

            return (rtv with { Status = RtvStatus.Replaced, ReplacementLot = lot, ReplacementTransactionId = txn, ClosedBy = context.Actor, ClosedAt = now },
                $"Return to vendor {rtv.RtvId}: replacement {quantity} {rtv.PartNumber}{(lot is null ? string.Empty : " lot " + lot)} received into {location.Code} ({status}).");
        }, ct);

    public Task<CommandResult> CreditReturnToVendorAsync(CreditReturnToVendor c, CommandContext context, CancellationToken ct) =>
        RtvAsync(nameof(CreditReturnToVendor), c?.RtvId ?? 0, c!, context, Permissions.PurchasingApprove, true, (_, rtv, now) => rtv.Status != RtvStatus.Shipped
            ? throw new CommandRejectedException("rtv_not_shipped", $"Return to vendor {rtv.RtvId} is {rtv.Status}; it is credited once shipped back.")
            : Task.FromResult((rtv with { Status = RtvStatus.Credited, ClosedBy = context.Actor, ClosedAt = now, Note = context.Reason }, $"Return to vendor {rtv.RtvId} credited.")), ct);

    public Task<CommandResult> ReceiveCustomerReturnAsync(ReceiveCustomerReturn c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(ReceiveCustomerReturn), Permissions.InventoryTransact, ResourceRef.Of("Shipment", Key(c.ShipmentId)), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var shipment = await uow.Store<IInventoryLookup>().FindShipmentAsync(c.ShipmentId, ct);
            if (shipment is null || shipment.Status != ShipmentStatus.Shipped)
            {
                throw new CommandRejectedException("shipment_not_found", $"Shipment {c.ShipmentId} was not shipped.");
            }

            var item = await ItemAsync(uow, BusinessCode.Require(c.PartNumber, "Part"), ct);
            var lot = RequireLot(item, c.LotNumber);
            var quantity = RequireQuantity(item, c.Quantity);
            var store = uow.Store<IInventoryStore>();
            await store.LockPartAsync(item.PartNumber, ct);
            var shipped = shipment.Lines.Where(l => l.PartNumber == item.PartNumber && (lot is null || l.LotNumber == lot)).Sum(l => l.Quantity);
            var returned = (await uow.Store<IReceivingLookup>().ListCustomerReturnsAsync(c.ShipmentId, ct)).Where(r => r.PartNumber == item.PartNumber && (lot is null || r.LotNumber == lot)).Sum(r => r.Quantity);
            if (shipped == 0)
            {
                throw new CommandRejectedException("not_on_shipment", $"Shipment {c.ShipmentId} did not carry {item.PartNumber}{(lot is null ? string.Empty : " lot " + lot)}.");
            }

            if (returned + quantity > shipped)
            {
                throw new CommandRejectedException("over_return", $"Shipment {c.ShipmentId} carried {shipped}; {returned} already returned, so at most {shipped - returned} more.");
            }

            var location = await CompanyLocationAsync(uow, c.LocationCode, ct);
            var txn = await store.InsertTransactionAsync(new NewTransaction(InventoryTransactionKind.CustomerReturn, item.PartNumber, quantity, null, null, location.Code, StockStatus.Hold, lot,
                Reason: $"Returned by {shipment.Customer} against shipment {c.ShipmentId}: {context.Reason}", ShipmentId: c.ShipmentId), now, context.Actor, ct);
            var rma = new CustomerReturnInfo(0, c.ShipmentId, shipment.Customer, item.PartNumber, lot, quantity, location.Code, context.Reason!.Trim(), txn, context.Actor, now);
            var id = await uow.Store<IReceivingStore>().InsertCustomerReturnAsync(rma, ct);
            rma = rma with { RmaId = id };
            return new CommandOutcome(
                CommandResult.Ok(Key(id), $"Return {id}: {quantity} {item.PartNumber}{(lot is null ? string.Empty : " lot " + lot)} back from {shipment.Customer}, on hold at {location.Code}."),
                [pipeline.Event(new ReceivingRecorded("CustomerReturn", id, "Received"), context, SourceContext, "CustomerReturn", Key(id))],
                new AuditChange("CustomerReturn", Key(id), null, CanonicalJson.Serialize(rma)));
        }, ct);
    }

    public Task<CommandResult> DispositionCustomerReturnAsync(DispositionCustomerReturn c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = Key(c.RmaId);
        var spec = new CommandSpec(SourceContext, nameof(DispositionCustomerReturn), Permissions.QualityDisposition, ResourceRef.Of("CustomerReturn", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var rma = await uow.Store<IReceivingLookup>().FindCustomerReturnAsync(c.RmaId, ct)
                ?? throw new CommandRejectedException("return_not_found", $"Return {key} does not exist.");
            await uow.Store<IReceivingStore>().LockCustomerReturnAsync(c.RmaId, ct);
            await uow.Store<IInventoryStore>().LockPartAsync(rma.PartNumber, ct);
            rma = await uow.Store<IReceivingLookup>().FindCustomerReturnAsync(c.RmaId, ct)
                ?? throw new CommandRejectedException("return_not_found", "The actual return is unavailable.");
            var current = returnAcceptance is null ? null : await returnAcceptance.AuthorityAsync(uow, context.Principal, c.RmaId, c.DecisionId, c.ExpectedDecisionVersion, now, ct);
            if (c.Disposition != CustomerReturnDisposition.Restock && returnAcceptance is not null
                && await uow.Store<IReturnAcceptanceInventoryLookup>().PreparedForRmaAsync(c.RmaId, ct) > 0)
                throw new CommandRejectedException("return_cohort_disposition_required", "Prepared return cohorts cannot be consumed or completed by the legacy whole-return disposition.");
            if (c.Disposition == CustomerReturnDisposition.Restock && current is not null)
            {
                if(c.DecisionId is not{} decision || c.ExpectedDecisionVersion is not{} version)
                    throw new CommandRejectedException("return_release_required", "Use the exact independently approved release for this returned cohort.");
                var applied = await returnAcceptance!.ConsumeReturnReleaseAsync(uow, context, c.RmaId, decision, version, c.Quantity ?? rma.Quantity, now, ct);
                var next = applied.ReturnComplete ? rma with { Disposition=CustomerReturnDisposition.Restock, DispositionedBy=context.Actor, DispositionedAt=now, DispositionTransactionId=applied.TransactionId } : rma;
                if(applied.ReturnComplete)await uow.Store<IReceivingStore>().UpdateCustomerReturnAsync(next,ct);
                return new CommandOutcome(CommandResult.Ok(key, $"Return {key}: independently restocked {applied.Quantity}; cumulative {applied.CumulativeRmaRestockedQuantity} of {applied.RmaRecordedQuantity}."),
                    [pipeline.Event(new ReceivingRecorded("CustomerReturn", c.RmaId, applied.ReturnComplete ? "Restocked" : "PartiallyRestocked"), context, SourceContext, "CustomerReturn", key)],
                    new AuditChange("CustomerReturn", key, CanonicalJson.Serialize(rma), CanonicalJson.Serialize(new { returnRecord=next, applied, current.Sources, current.Resources }),
                        current.Resources.Select(r=>r.Program).FirstOrDefault(p=>p is not null), current.Resources.Aggregate(DataMarking.None,(m,r)=>m|r.Markings)));
            }
            if(c.DecisionId is not null || c.ExpectedDecisionVersion is not null || c.Quantity is not null)
                throw new CommandRejectedException("return_release_unexpected", "Typed cohort release inputs require a governed returned cohort.");
            if (rma.Disposition is not null)
            {
                throw new CommandRejectedException("already_dispositioned", $"Return {key} is already dispositioned ({rma.Disposition}).");
            }

            var store = uow.Store<IInventoryStore>();
            long? txn = null;
            if (c.Disposition == CustomerReturnDisposition.Restock)
                await RequireLegacyReleaseUnadoptedAsync(uow, context, rma.PartNumber, rma.LotNumber, rma.LocationCode, now, ct);
            if (c.Disposition != CustomerReturnDisposition.Rework)
            {
                await RequireStockAsync(uow, rma.PartNumber, rma.LocationCode, rma.LotNumber, StockStatus.Hold, rma.Quantity, ct);
                txn = await store.InsertTransactionAsync(c.Disposition == CustomerReturnDisposition.Restock
                    ? new NewTransaction(InventoryTransactionKind.QualityRelease, rma.PartNumber, rma.Quantity, rma.LocationCode, StockStatus.Hold, rma.LocationCode, StockStatus.Released, rma.LotNumber,
                        Reason: $"Customer return {key} restocked: {context.Reason}", ShipmentId: rma.ShipmentId)
                    : new NewTransaction(InventoryTransactionKind.Scrap, rma.PartNumber, rma.Quantity, rma.LocationCode, StockStatus.Hold, null, null, rma.LotNumber,
                        Reason: $"Customer return {key} scrapped: {context.Reason}", ShipmentId: rma.ShipmentId), now, context.Actor, ct);
            }

            var decided = rma with { Disposition = c.Disposition, DispositionedBy = context.Actor, DispositionedAt = now, DispositionTransactionId = txn };
            await uow.Store<IReceivingStore>().UpdateCustomerReturnAsync(decided, ct);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Return {key}: {c.Disposition.ToString().ToLowerInvariant()}" + (c.Disposition == CustomerReturnDisposition.Rework ? "; it stays on hold until reworked and released." : ".")),
                [pipeline.Event(new ReceivingRecorded("CustomerReturn", c.RmaId, c.Disposition.ToString()), context, SourceContext, "CustomerReturn", key)],
                new AuditChange("CustomerReturn", key, CanonicalJson.Serialize(rma), CanonicalJson.Serialize(decided)));
        }, ct, (uow, now) => ReturnDispositionAuthorityAsync(uow, spec, c, context, now, ct));
    }

    private Task<CommandResult> RtvAsync<TCommand>(
        string commandType,
        long rtvId,
        TCommand command,
        CommandContext context,
        string permission,
        bool reasonRequired,
        Func<IKernelUnitOfWork, RtvInfo, DateTimeOffset, Task<(RtvInfo Rtv, string Message)>> change,
        CancellationToken ct)
    {
        var key = Key(rtvId);
        var spec = new CommandSpec(SourceContext, commandType, permission, ResourceRef.Of("ReturnToVendor", key), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var rtv = (await uow.Store<IReceivingLookup>().ListRtvsAsync(false, ct)).FirstOrDefault(r => r.RtvId == rtvId)
                ?? throw new CommandRejectedException("rtv_not_found", $"Return to vendor {key} does not exist.");
            await uow.Store<IInventoryStore>().LockPartAsync(rtv.PartNumber, ct);
            rtv = (await uow.Store<IReceivingLookup>().ListRtvsAsync(false, ct)).Single(r => r.RtvId == rtvId);
            var (changed, message) = await change(uow, rtv, now);
            await uow.Store<IReceivingStore>().UpdateRtvAsync(changed, ct);
            return new CommandOutcome(
                CommandResult.Ok(key, message),
                [pipeline.Event(new ReceivingRecorded("ReturnToVendor", rtvId, changed.Status.ToString()), context, SourceContext, "ReturnToVendor", key)],
                new AuditChange("ReturnToVendor", key, CanonicalJson.Serialize(rtv), CanonicalJson.Serialize(changed)));
        }, ct, (uow, now) => ReceivingAcceptanceAuthorityAsync(uow, spec, command, context, now, ct));
    }

    /// <summary>
    /// ADR 0055: decide part of a rejection. Returns the part to decide (the rejection itself, reduced to the quantity) and
    /// the new pending rejection holding the rest, or the whole rejection when no smaller quantity is given.
    /// </summary>
    private static async Task<(SupplierRejection Part, long? Remainder)> SplitRejectionAsync(IKernelUnitOfWork uow, SupplierRejection rejection, decimal? quantity, CancellationToken ct)
    {
        if (quantity is not { } requested)
        {
            return (rejection, null);
        }

        var decided = RequireQuantity(await ItemAsync(uow, rejection.PartNumber, ct), requested);
        if (decided > rejection.Quantity)
        {
            throw new CommandRejectedException("over_rejected_quantity", $"Rejection {Key(rejection.RejectionId)} is for {rejection.Quantity}; a disposition cannot cover {decided}.");
        }

        if (decided == rejection.Quantity)
        {
            return (rejection, null);
        }

        var remainder = await uow.Store<IReceivingStore>().InsertRejectionAsync(
            rejection with
            {
                RejectionId = 0, Quantity = rejection.Quantity - decided, Disposition = SupplierDisposition.Pending, DispositionedBy = null, DispositionedAt = null,
                ApprovedBy = null, ApprovedAt = null, SplitFrom = rejection.RejectionId,
            },
            ct);
        return (rejection with { Quantity = decided }, remainder);
    }

    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);
}


