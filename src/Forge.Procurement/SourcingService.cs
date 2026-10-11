using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Holds;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Procurement.Contracts;

namespace Forge.Procurement;

public sealed record CreateRfq(long RequisitionId, string Title, IReadOnlyList<string> SupplierCodes);
public sealed record ReleaseRfq(long RfqId);
public sealed record CancelRfq(long RfqId);
public sealed record RecordSupplierQuote(long RfqId, string SupplierCode, decimal Quantity, decimal UnitPrice, string Currency,
    DateOnly PromisedDate, DateOnly ValidThrough, string? ProposedAlternatePart, string OriginalDocumentNumber, string OriginalDocumentRevision, string? Note);
public sealed record SelectSupplierQuote(long RfqId, long QuoteId);
public sealed record SourcingChanged(long RfqId, string Change, long? QuoteId) : IDomainEvent
{
    public static string EventType => nameof(SourcingChanged);
    public static int SchemaVersion => 1;
}

/// <summary>RFQ and immutable supplier responses extend requisitions, technical applicability, evidence and the shared command pipeline.</summary>
public sealed class SourcingService(CommandPipeline pipeline, ISourcingPackageResolver packages)
{
    private const string Source = "Procurement";

    public Task<CommandResult> CreateAsync(CreateRfq c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(Source, nameof(CreateRfq), Permissions.PurchasingManage, ResourceRef.Of("Requisition", c.RequisitionId));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await uow.Store<ISourcingStore>().LockRfqAsync(-c.RequisitionId, ct);
            var need = await NeedAsync(uow, c.RequisitionId, context, ct);
            var title = (c.Title ?? string.Empty).Trim();
            if (title.Length is < 1 or > 200) throw new CommandRejectedException("title_required", "Give the RFQ a title of at most 200 characters.");
            var suppliers = (c.SupplierCodes ?? []).Select(s => BusinessCode.Require(s, "Supplier")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (suppliers.Count is < 1 or > 100) throw new CommandRejectedException("suppliers_required", "Invite between one and 100 suppliers.");
            foreach (var supplier in suppliers) await SupplierService.RequireOrderableAsync(uow, supplier, ct);
            if ((await uow.Store<ISourcingLookup>().ListRfqsAsync(ct)).Any(r => r.RequisitionId == need.RequisitionId && r.Status is RfqStatus.Draft or RfqStatus.Released))
                throw new CommandRejectedException("rfq_exists", "This need already has an active RFQ.");
            var rfq = new RfqInfo(0, need.RequisitionId, title, suppliers, RfqStatus.Draft, context.Actor, now, null, null, null, null, null, null, null);
            var id = await uow.Store<ISourcingStore>().InsertRfqAsync(rfq, ct);
            return Outcome(rfq with { RfqId = id }, null, need, context, "Created", $"RFQ {id} created for {need.SourceLabel}; it is not supply.");
        }, ct);
    }

    public Task<CommandResult> ReleaseAsync(ReleaseRfq c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(ReleaseRfq), c.RfqId, c, context, Permissions.PurchasingApprove, async (uow, rfq, need, now) =>
        {
            if (rfq.Status != RfqStatus.Draft) throw new CommandRejectedException("rfq_not_draft", "Only a draft RFQ can be released.");
            if (rfq.CreatedBy == context.Actor) throw new CommandRejectedException("separation_of_duties", "A different person releases the RFQ you created.");
            foreach (var supplier in rfq.SupplierCodes)
            {
                await SupplierService.RequireOrderableAsync(uow, supplier, ct);
                await HoldGate.RequireNoneAsync(uow, new HoldCheck(HoldActivity.Purchase, Parts: [need.PartNumber], SupplierCode: supplier), ct);
            }

            var package = await packages.ResolveAsync(uow, rfq.RfqId, need, now, context.Actor, ct);
            foreach (var sent in package.Documents)
            {
                var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(sent.DocumentNumber, ct);
                if (document is null || !DocumentAccess.CanSee(context.Principal, document)) throw new CommandRejectedException("package_access_required", "You must be cleared for every document being sent.");
                if (sent.Revision is null || sent.State is not ("Current" or "Pinned" or "LegitimatelySuperseded"))
                    throw new CommandRejectedException("package_not_ready", $"{sent.DocumentNumber}: {sent.State}. Resolve the applicable configuration before release.");
            }

            return rfq with { Status = RfqStatus.Released, ReleasedBy = context.Actor, ReleasedAt = now, Package = package };
        }, ct);
    }

    public Task<CommandResult> RecordQuoteAsync(RecordSupplierQuote c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(Source, nameof(RecordSupplierQuote), Permissions.PurchasingManage, ResourceRef.Of("Rfq", c.RfqId));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var (rfq, need) = await RfqAsync(uow, c.RfqId, context, ct);
            if (rfq.Status != RfqStatus.Released) throw new CommandRejectedException("rfq_not_released", "Supplier responses are recorded against a released RFQ.");
            var supplier = await SupplierService.RequireOrderableAsync(uow, c.SupplierCode, ct);
            if (!rfq.SupplierCodes.Contains(supplier.Code, StringComparer.Ordinal)) throw new CommandRejectedException("supplier_not_invited", "This supplier was not sent the RFQ.");
            var currency = (c.Currency ?? string.Empty).Trim().ToUpperInvariant();
            if (c.Quantity <= 0 || c.Quantity > 100_000_000 || c.UnitPrice < 0 || c.UnitPrice > 100_000_000 || currency.Length != 3 || !currency.All(char.IsAsciiLetter))
                throw new CommandRejectedException("invalid_quote", "Record a positive quantity, nonnegative unit price and three-letter currency.");
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            if (c.PromisedDate < today || c.ValidThrough < today) throw new CommandRejectedException("quote_dates", "The response must have a current validity date and a future or current promised date.");
            var number = BusinessCode.Require(c.OriginalDocumentNumber, "Document");
            var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(number, ct);
            if (document is null || !DocumentAccess.CanSee(context.Principal, document)) throw new CommandRejectedException("document_not_found", "The original response does not exist.");
            var revision = document.Revisions.FirstOrDefault(r => string.Equals(r.Revision, (c.OriginalDocumentRevision ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
            if (document.Authority != DocumentAuthority.Reference || revision?.Status != DocumentRevisionStatus.Uncontrolled || revision.FileSha256 is null)
                throw new CommandRejectedException("original_quote_required", "Attach the original supplier response as a reference file.");
            if (document.ProgramId is { } p && p != need.ProgramId) throw new CommandRejectedException("program_mismatch", "The supplier response belongs to a different program.");
            string? alternate = null;
            if (!string.IsNullOrWhiteSpace(c.ProposedAlternatePart))
            {
                alternate = BusinessCode.Require(c.ProposedAlternatePart, "Alternate part");
                if (await uow.Store<IEngineeringLookup>().FindItemAsync(alternate, ct) is null) throw new CommandRejectedException("alternate_not_found", "Record the proposed part before quoting it.");
            }

            var quote = new SupplierQuote(0, rfq.RfqId, supplier.Code, c.Quantity, c.UnitPrice, currency, c.PromisedDate, c.ValidThrough, alternate, number, revision.Revision, context.Actor, now, Optional(c.Note));
            var id = await uow.Store<ISourcingStore>().InsertQuoteAsync(quote, ct);
            var program = Program(need);
            return new CommandOutcome(CommandResult.Ok(Key(id), $"Quote {id} recorded; it is neither supply nor alternate approval."),
                [pipeline.Event(new SourcingChanged(rfq.RfqId, "QuoteRecorded", id), context, Source, "SupplierQuote", Key(id), program, document.Markings)],
                new AuditChange("SupplierQuote", Key(id), null, CanonicalJson.Serialize(quote with { QuoteId = id }), program));
        }, ct);
    }

    public Task<CommandResult> SelectAsync(SelectSupplierQuote c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(SelectSupplierQuote), c.RfqId, c, context, Permissions.PurchasingApprove, async (uow, rfq, need, now) =>
        {
            if (rfq.Status != RfqStatus.Released) throw new CommandRejectedException("rfq_not_released", "Select a source from a released RFQ.");
            var quote = (await uow.Store<ISourcingLookup>().ListQuotesAsync(rfq.RfqId, ct)).FirstOrDefault(q => q.QuoteId == c.QuoteId)
                ?? throw new CommandRejectedException("quote_not_found", "That response is not part of this RFQ.");
            if (quote.RecordedBy == context.Actor) throw new CommandRejectedException("separation_of_duties", "A different person selects the supplier response you recorded.");
            if (quote.ValidThrough < DateOnly.FromDateTime(now.UtcDateTime)) throw new CommandRejectedException("quote_expired", "The supplier must renew the response before selection.");
            if (quote.Quantity < need.Quantity) throw new CommandRejectedException("quote_quantity", "This response does not cover the requested quantity; reconcile the need before selecting it.");
            if (quote.ProposedAlternatePart is not null && quote.ProposedAlternatePart != need.PartNumber)
                throw new CommandRejectedException("alternate_requires_engineering", "A proposed alternate is not approved configuration. Resolve engineering authority and the requisition before purchasing it.");
            var original = await uow.Store<IDocumentLookup>().FindDocumentAsync(quote.OriginalDocumentNumber, ct);
            if (original is null || !DocumentAccess.CanSee(context.Principal, original)) throw new CommandRejectedException("quote_not_found", "The supplier response is not available to you.");
            await SupplierService.RequireOrderableAsync(uow, quote.SupplierCode, ct);
            await HoldGate.RequireNoneAsync(uow, new HoldCheck(HoldActivity.Purchase, Parts: [need.PartNumber], SupplierCode: quote.SupplierCode), ct);
            return rfq with { Status = RfqStatus.Selected, SelectedQuoteId = quote.QuoteId, SelectedBy = context.Actor, SelectedAt = now, SelectionReason = context.Reason };
        }, ct);
    }

    public Task<CommandResult> CancelAsync(CancelRfq c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(CancelRfq), c.RfqId, c, context, Permissions.PurchasingManage, (_, rfq, _, _) =>
            rfq.Status is RfqStatus.Selected or RfqStatus.Cancelled ? throw new CommandRejectedException("rfq_closed", "This RFQ is already closed.")
                : Task.FromResult(rfq with { Status = RfqStatus.Cancelled }), ct);
    }

    private Task<CommandResult> ChangeAsync<T>(string commandType, long id, T command, CommandContext context, string permission,
        Func<IKernelUnitOfWork, RfqInfo, RequisitionInfo, DateTimeOffset, Task<RfqInfo>> change, CancellationToken ct)
    {
        var spec = new CommandSpec(Source, commandType, permission, ResourceRef.Of("Rfq", id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var (rfq, need) = await RfqAsync(uow, id, context, ct);
            var changed = await change(uow, rfq, need, now);
            await uow.Store<ISourcingStore>().UpdateRfqAsync(changed, ct);
            return Outcome(changed, rfq, need, context, changed.Status.ToString(), $"RFQ {id}: {changed.Status}; production state is unchanged.");
        }, ct);
    }

    private static async Task<(RfqInfo Rfq, RequisitionInfo Need)> RfqAsync(IKernelUnitOfWork uow, long id, CommandContext context, CancellationToken ct)
    {
        await uow.Store<ISourcingStore>().LockRfqAsync(id, ct);
        var rfq = await uow.Store<ISourcingLookup>().FindRfqAsync(id, ct) ?? throw new CommandRejectedException("rfq_not_found", "RFQ does not exist.");
        return (rfq, await NeedAsync(uow, rfq.RequisitionId, context, ct));
    }

    private static async Task<RequisitionInfo> NeedAsync(IKernelUnitOfWork uow, long id, CommandContext context, CancellationToken ct)
    {
        var need = await uow.Store<IRequisitionLookup>().FindRequisitionAsync(id, ct);
        if (need is null || need.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p))) throw new CommandRejectedException("requisition_not_found", "The sourcing need does not exist.");
        if (need.Status != RequisitionStatus.Open) throw new CommandRejectedException("requisition_not_open", "The sourcing need must be open.");
        return need;
    }

    private CommandOutcome Outcome(RfqInfo rfq, RfqInfo? before, RequisitionInfo need, CommandContext context, string change, string message) =>
        new(CommandResult.Ok(Key(rfq.RfqId), message),
            [pipeline.Event(new SourcingChanged(rfq.RfqId, change, rfq.SelectedQuoteId), context, Source, "Rfq", Key(rfq.RfqId), Program(need))],
            new AuditChange("Rfq", Key(rfq.RfqId), before is null ? null : CanonicalJson.Serialize(before), CanonicalJson.Serialize(rfq), Program(need)));

    private static ProgramId? Program(RequisitionInfo need) => need.ProgramId is { } p ? new ProgramId(p) : null;
    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= 2000 ? value.Trim() : throw new CommandRejectedException("note_too_long", "Notes are at most 2000 characters.");
}
