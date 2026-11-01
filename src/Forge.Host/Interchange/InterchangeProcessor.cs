using System.Globalization;
using System.Text.Json;
using Forge.Interchange;
using Forge.Interchange.Contracts;
using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Planning;
using Forge.Procurement;
using Forge.Procurement.Contracts;
using Forge.Resources.Contracts;
using Forge.Sales;
using Forge.Sales.Contracts;

namespace Forge.Host.Interchange;

/// <summary>The system identity EDI processing, the folder exchange, webhooks and the partner API act as.</summary>
public static class InterchangeSystem
{
    public static readonly ForgePrincipal Principal = new(
        new PersonId(Guid.Empty),
        "EDI and integrations",
        true,
        new HashSet<string>(StringComparer.Ordinal) { Roles.Interchange },
        new HashSet<ProgramId>(),
        DataMarking.None);

    public static CommandContext Context(Guid correlation, string key, string what, string actor = SystemActors.Interchange) =>
        new(Principal, correlation, key, CommandSource.Integration, what, actor);

    public static string Ticks(DateTimeOffset? at) => (at ?? DateTimeOffset.MinValue).UtcTicks.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Owner decisions 2026-11-01. Received documents are processed: a customer's 850 becomes a draft sales order a Sales Manager approves
/// (anything Forge cannot map is flagged for review, never guessed); a supplier's 855, 856 and 810 are recorded for people to act on; a
/// 997 marks what Forge sent as accepted or rejected; every received transaction is answered with a 997. Forge writes an 855 when a
/// customer partner's sales order is approved, an 856 when its shipment ships, an 810 when its invoice is issued, and an 850 when a
/// supplier partner's purchase order is approved. Delivered at least once; every step is idempotent.
/// </summary>
public sealed class InterchangeProcessor(InterchangeService interchange, SalesOrderService sales, IKernelUnitOfWorkFactory unitOfWork, ISiteClock siteClock,
    ILogger<InterchangeProcessor> logger) : IEventSubscriber
{
    public string Name => "interchange-edi";

    public bool Handles(string eventType) => eventType is nameof(InterchangeDocumentChanged) or nameof(SalesOrderChanged) or nameof(ShipmentChanged)
        or nameof(InvoiceChanged) or nameof(PurchaseOrderChanged);

    public async Task HandleAsync(long sequence, EventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        try
        {
            switch (envelope.EventType)
            {
                case nameof(InterchangeDocumentChanged):
                {
                    var e = JsonSerializer.Deserialize<InterchangeDocumentChanged>(envelope.Payload, CanonicalJson.Options)!;
                    if (e.Direction == nameof(DocumentDirection.Inbound) && e.Status == nameof(DocumentStatus.Received)) await ProcessAsync(e.DocumentId, envelope.CorrelationId, ct);
                    break;
                }
                case nameof(SalesOrderChanged):
                {
                    var e = JsonSerializer.Deserialize<SalesOrderChanged>(envelope.Payload, CanonicalJson.Options)!;
                    if (e.Change == "Approved") await AcknowledgeOrderAsync(e.OrderNumber, envelope.CorrelationId, ct);
                    break;
                }
                case nameof(ShipmentChanged):
                {
                    var e = JsonSerializer.Deserialize<ShipmentChanged>(envelope.Payload, CanonicalJson.Options)!;
                    if (e.Status == nameof(ShipmentStatus.Shipped)) await ShipNoticeAsync(e.ShipmentId, envelope.CorrelationId, ct);
                    break;
                }
                case nameof(InvoiceChanged):
                {
                    var e = JsonSerializer.Deserialize<InvoiceChanged>(envelope.Payload, CanonicalJson.Options)!;
                    if (e.Change == "Issued") await InvoiceAsync(e.InvoiceNumber, envelope.CorrelationId, ct);
                    break;
                }
                case nameof(PurchaseOrderChanged):
                {
                    var e = JsonSerializer.Deserialize<PurchaseOrderChanged>(envelope.Payload, CanonicalJson.Options)!;
                    if (e.Change == "Approved") await PurchaseOrderAsync(e.PoNumber, envelope.CorrelationId, ct);
                    break;
                }
            }
        }
        catch (CommandRejectedException ex)
        {
            logger.LogWarning("EDI handling of event {Sequence} ({Type}) was refused: {Code} {Message}", sequence, envelope.EventType, ex.Code, ex.Message);
        }
    }

    private async Task ProcessAsync(long documentId, Guid correlation, CancellationToken ct)
    {
        InterchangeDocument? doc;
        TradingPartnerInfo? partner;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            var lookup = read.Store<IInterchangeLookup>();
            doc = await lookup.FindDocumentAsync(documentId, ct);
            partner = doc?.PartnerCode is { } code ? await lookup.FindPartnerAsync(code, ct) : null;
        }

        if (doc is not { Direction: DocumentDirection.Inbound, Status: DocumentStatus.Received } || partner is null) return;
        var attempt = InterchangeSystem.Ticks(doc.UpdatedAt);
        var segments = InterchangeService.Segments(doc.Content);
        if (doc.TransactionSet != "997")
        {
            await interchange.QueueOutboundAsync(new QueueOutbound(partner.Code, "997", $"ack:{documentId}", EdiMaps.Build997(doc, true),
                    $"acknowledges {InterchangeService.Describe(doc.TransactionSet)} {doc.TransactionControl}"),
                InterchangeSystem.Context(correlation, $"system:edi:997:{documentId}", "Functional acknowledgment"), ct);
        }

        (DocumentStatus Status, string Summary, IReadOnlyList<string> Problems, string? Ref) outcome;
        switch (doc.TransactionSet)
        {
            case "850" when partner.Kind != PartnerKind.Customer:
                outcome = (DocumentStatus.NeedsReview, "A purchase order (850) from a partner that is not a customer.", ["Only customer partners send orders to Forge."], null);
                break;
            case "850":
                outcome = await OrderAsync(doc, partner, segments, attempt, correlation, ct);
                break;
            case "997":
            {
                var (group, accepted, note) = EdiMaps.Read997(segments);
                if (group is null)
                {
                    outcome = (DocumentStatus.NeedsReview, "A 997 without AK1.", ["The acknowledgment does not say which group it answers."], null);
                    break;
                }

                var r = await interchange.AcknowledgeOutboundAsync(new AcknowledgeOutbound(partner.Code, group, accepted, note),
                    InterchangeSystem.Context(correlation, $"system:edi:ack:{documentId}:{attempt}", "Partner acknowledgment"), ct);
                outcome = (DocumentStatus.Processed, r.Message, [], null);
                break;
            }
            case "855" or "856" or "810" when partner.Kind != PartnerKind.Supplier:
                outcome = (DocumentStatus.NeedsReview, $"{InterchangeService.Describe(doc.TransactionSet)} from a partner that is not a supplier.", ["Forge reads 855, 856 and 810 from suppliers."], null);
                break;
            default:
            {
                var (po, summary) = EdiMaps.ReadSupplierDocument(doc.TransactionSet, segments);
                outcome = (DocumentStatus.Processed, summary, [], po is null ? null : $"purchase-order:{po}");
                break;
            }
        }

        await interchange.CompleteInboundAsync(new CompleteInbound(documentId, outcome.Status, outcome.Summary, outcome.Problems, outcome.Ref),
            InterchangeSystem.Context(correlation, $"system:edi:complete:{documentId}:{attempt}", "EDI document processed"), ct);
    }

    private async Task<(DocumentStatus, string, IReadOnlyList<string>, string?)> OrderAsync(InterchangeDocument doc, TradingPartnerInfo partner, IReadOnlyList<X12Segment> segments,
        string attempt, Guid correlation, CancellationToken ct)
    {
        var order = EdiMaps.Read850(segments);
        if (order.Problems.Count > 0) return (DocumentStatus.NeedsReview, $"Purchase order {order.CustomerPo} from {partner.Name} needs review.", order.Problems, null);
        try
        {
            var lines = order.Lines.Select(l => new NewSalesLine(l.PartNumber!, l.Quantity!.Value, l.UnitPrice!.Value, l.DueDate!.Value)).ToList();
            var result = await sales.CreateAsync(new CreateSalesOrder(partner.PartyCode!, order.CustomerPo, order.ShipTo, lines),
                InterchangeSystem.Context(correlation, $"system:edi:850:{doc.DocumentId}:{attempt}", $"EDI purchase order {order.CustomerPo} from {partner.Name}"), ct);
            return (DocumentStatus.Processed, $"Draft sales order {result.EntityId} for {partner.Name}, PO {order.CustomerPo}: {lines.Count} line(s). A Sales Manager approves it.", [],
                $"sales-order:{result.EntityId}");
        }
        catch (CommandRejectedException ex)
        {
            return (DocumentStatus.NeedsReview, $"Purchase order {order.CustomerPo} from {partner.Name} could not become a sales order.", [ex.Message], null);
        }
    }

    private async Task AcknowledgeOrderAsync(long orderNumber, Guid correlation, CancellationToken ct)
    {
        SalesOrderInfo? order;
        TradingPartnerInfo? partner;
        DateOnly today;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            order = await read.Store<ISalesLookup>().FindOrderAsync(orderNumber, ct);
            partner = order is null ? null : await read.Store<IInterchangeLookup>().FindPartnerByPartyAsync(PartnerKind.Customer, order.CustomerCode, ct);
            today = siteClock.DateAt(order?.ApprovedAt ?? DateTimeOffset.UnixEpoch);
        }

        if (order is null || partner?.Sends("855") != true) return;
        await interchange.QueueOutboundAsync(new QueueOutbound(partner.Code, "855", $"sales-order:{orderNumber}", EdiMaps.Build855(order, today),
                $"sales order {orderNumber} (PO {order.CustomerPo}) accepted"),
            InterchangeSystem.Context(correlation, $"system:edi:855:{orderNumber}", "Order acknowledgment"), ct);
    }

    private async Task ShipNoticeAsync(long shipmentId, Guid correlation, CancellationToken ct)
    {
        ShipmentInfo? shipment;
        SalesOrderInfo? order = null;
        TradingPartnerInfo? partner = null;
        var line = 0;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            shipment = await read.Store<IInventoryLookup>().FindShipmentAsync(shipmentId, ct);
            if (shipment?.DemandId is { } demandId && await read.Store<IDemandStore>().FindAsync(demandId, ct) is { } demand
                && SalesOrderInfo.ParseDemandSource(demand.SourceKey) is { } source)
            {
                order = await read.Store<ISalesLookup>().FindOrderAsync(source.OrderNumber, ct);
                line = source.LineNumber;
                partner = order is null ? null : await read.Store<IInterchangeLookup>().FindPartnerByPartyAsync(PartnerKind.Customer, order.CustomerCode, ct);
            }
        }

        if (shipment is null || order is null || partner?.Sends("856") != true) return;
        var unit = order.Lines.FirstOrDefault(l => l.LineNumber == line)?.UnitOfMeasure ?? "EA";
        var shipped = siteClock.DateAt(shipment.ShippedAt ?? shipment.CreatedAt);
        await interchange.QueueOutboundAsync(new QueueOutbound(partner.Code, "856", $"shipment:{shipmentId}", EdiMaps.Build856(shipment, order, line, unit, shipped),
                $"shipment {shipmentId} for sales order {order.OrderNumber} (PO {order.CustomerPo})"),
            InterchangeSystem.Context(correlation, $"system:edi:856:{shipmentId}", "Ship notice"), ct);
    }

    private async Task InvoiceAsync(long invoiceNumber, Guid correlation, CancellationToken ct)
    {
        InvoiceInfo? invoice;
        SalesOrderInfo? order = null;
        CustomerInfo? customer = null;
        TradingPartnerInfo? partner = null;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            var lookup = read.Store<ISalesLookup>();
            invoice = await lookup.FindInvoiceAsync(invoiceNumber, ct);
            if (invoice is not null)
            {
                order = await lookup.FindOrderAsync(invoice.OrderNumber, ct);
                customer = await lookup.FindCustomerAsync(invoice.CustomerCode, ct);
                partner = await read.Store<IInterchangeLookup>().FindPartnerByPartyAsync(PartnerKind.Customer, invoice.CustomerCode, ct);
            }
        }

        if (invoice is null || partner?.Sends("810") != true) return;
        var units = (order?.Lines ?? []).ToDictionary(l => l.LineNumber, l => l.UnitOfMeasure);
        await interchange.QueueOutboundAsync(new QueueOutbound(partner.Code, "810", $"invoice:{invoiceNumber}", EdiMaps.Build810(invoice, units, customer?.PaymentTermsDays ?? 30),
                $"invoice {invoiceNumber} for sales order {invoice.OrderNumber}, {invoice.Total.ToString("0.00", CultureInfo.InvariantCulture)} {invoice.Currency}"),
            InterchangeSystem.Context(correlation, $"system:edi:810:{invoiceNumber}", "Invoice"), ct);
    }

    private async Task PurchaseOrderAsync(long poNumber, Guid correlation, CancellationToken ct)
    {
        PurchaseOrderInfo? po;
        SupplierInfo? supplier = null;
        TradingPartnerInfo? partner = null;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            po = await read.Store<IProcurementLookup>().FindPurchaseOrderAsync(poNumber, ct);
            if (po?.SupplierCode is { } code)
            {
                supplier = await read.Store<IProcurementLookup>().FindSupplierAsync(code, ct);
                partner = await read.Store<IInterchangeLookup>().FindPartnerByPartyAsync(PartnerKind.Supplier, code, ct);
            }
        }

        if (po is null || partner?.Sends("850") != true) return;
        await interchange.QueueOutboundAsync(new QueueOutbound(partner.Code, "850", $"purchase-order:{poNumber}",
                EdiMaps.Build850(po, supplier?.Name ?? po.Supplier, siteClock.DateAt(po.ApprovedAt ?? po.CreatedAt)), $"purchase order {poNumber}"),
            InterchangeSystem.Context(correlation, $"system:edi:850-out:{poNumber}", "Purchase order"), ct);
    }
}

/// <summary>Customers and suppliers for partner records: Sales and Procurement own them.</summary>
public sealed class InterchangePartyResolver : IInterchangePartyResolver
{
    public async Task<string?> PartyNameAsync(IKernelUnitOfWork uow, PartnerKind kind, string code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return kind switch
        {
            PartnerKind.Customer => (await uow.Store<ISalesLookup>().FindCustomerAsync(code, ct))?.Name,
            PartnerKind.Supplier => (await uow.Store<IProcurementLookup>().FindSupplierAsync(code, ct))?.Name,
            _ => null,
        };
    }
}
