using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;
using Forge.Sales.Contracts;
using static Forge.Sales.QuoteService;

namespace Forge.Sales;

/// <summary>Bills confirmed shipments of one sales order: each named shipment whole, at the order line's price.</summary>
public sealed record IssueInvoice(long OrderNumber, IReadOnlyList<long> ShipmentIds);

public sealed record VoidInvoice(long InvoiceNumber);

/// <summary>
/// Invoices (owner decision 2026-10-31): a Billing Clerk bills only what has shipped. Every line is a confirmed shipment of the order,
/// billed once at the order line's unit price; tax is the customer's rate at issue, or none with a tax-exemption certificate; the due
/// date follows the customer's payment terms. A voided invoice keeps its number and frees its shipments to be billed again.
/// </summary>
public sealed class InvoiceService(CommandPipeline pipeline, ISalesShipmentResolver shipments)
{
    private const string Entity = "Invoice";

    public Task<CommandResult> IssueAsync(IssueInvoice c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(CustomerService.SourceContext, nameof(IssueInvoice), Permissions.BillingManage, ResourceRef.Of("SalesOrder", c.OrderNumber));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<ISalesStore>();
            await store.LockOrderAsync(c.OrderNumber, ct);
            var order = await uow.Store<ISalesLookup>().FindOrderAsync(c.OrderNumber, ct)
                ?? throw new CommandRejectedException("sales_order_not_found", $"Sales order {c.OrderNumber} does not exist.");
            if (order.Status is not (SalesOrderStatus.Approved or SalesOrderStatus.Closed))
                throw new CommandRejectedException("not_approved", $"Sales order {order.OrderNumber} is {order.Status}; only approved or closed orders are invoiced.");
            var requested = (c.ShipmentIds ?? []).Distinct().ToList();
            if (requested.Count is 0 or > 200)
                throw new CommandRejectedException("shipments_required", "Name 1 to 200 shipments to invoice.");

            var shipped = (await shipments.ShippedAsync(uow, order.OrderNumber, ct)).ToLookup(s => s.ShipmentId);
            var billed = await store.InvoicedShipmentsAsync(order.OrderNumber, ct);
            var lines = new List<InvoiceLineInfo>();
            foreach (var shipmentId in requested.Order())
            {
                if (!shipped.Contains(shipmentId))
                    throw new CommandRejectedException("not_shipped", $"Shipment {shipmentId} has not shipped against sales order {order.OrderNumber}; only shipped quantities are invoiced.");
                if (billed.Contains(shipmentId))
                    throw new CommandRejectedException("already_invoiced", $"Shipment {shipmentId} is already on an invoice.");
                foreach (var part in shipped[shipmentId].OrderBy(s => s.OrderLine))
                {
                    var line = order.Lines.Single(l => l.LineNumber == part.OrderLine);
                    lines.Add(new InvoiceLineInfo(lines.Count + 1, line.LineNumber, shipmentId, line.PartNumber, part.Quantity, line.UnitPrice, Money.Round(part.Quantity * line.UnitPrice)));
                }
            }

            var customer = await uow.Store<ISalesLookup>().FindCustomerAsync(order.CustomerCode, ct)
                ?? throw new CommandRejectedException("customer_not_found", $"Customer {order.CustomerCode} does not exist.");
            var subtotal = lines.Sum(l => l.Amount);
            var rate = customer.TaxExemptCertificate is null ? customer.TaxRatePercent : 0m;
            var tax = Money.Tax(subtotal, rate);
            var today = await uow.SiteDateAsync(now, ct);
            var invoice = new InvoiceInfo(0, order.OrderNumber, customer.Code, customer.Name, order.CustomerPo, InvoiceStatus.Issued, today, today.AddDays(customer.PaymentTermsDays),
                order.Currency, subtotal, rate, customer.TaxExemptCertificate, tax, subtotal + tax, context.Actor, now, null, null, null, lines);
            var number = await store.InsertInvoiceAsync(invoice, ct);
            var id = Id(number);
            var taxText = customer.TaxExemptCertificate is { } certificate ? $"no tax (exemption certificate {certificate})" : $"tax {Amount(tax, order.Currency)}";
            return new CommandOutcome(
                CommandResult.Ok(id, $"Invoice {id} issued to {customer.Name} for sales order {order.OrderNumber}: {lines.Count} line(s), {Amount(subtotal, order.Currency)} plus {taxText}, total {Amount(invoice.Total, order.Currency)}, due {invoice.DueDate:yyyy-MM-dd}."),
                [pipeline.Event(new InvoiceChanged(number, order.OrderNumber, "Issued"), context, CustomerService.SourceContext, Entity, id)],
                new AuditChange(Entity, id, null, CanonicalJson.Serialize(invoice with { InvoiceNumber = number })));
        }, ct);
    }

    public Task<CommandResult> VoidAsync(VoidInvoice c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = Id(c.InvoiceNumber);
        var spec = new CommandSpec(CustomerService.SourceContext, nameof(VoidInvoice), Permissions.BillingManage, ResourceRef.Of(Entity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var invoice = await uow.Store<ISalesLookup>().FindInvoiceAsync(c.InvoiceNumber, ct)
                ?? throw new CommandRejectedException("invoice_not_found", $"Invoice {id} does not exist.");
            await uow.Store<ISalesStore>().LockOrderAsync(invoice.OrderNumber, ct);
            if (invoice.Status == InvoiceStatus.Voided)
                return CommandOutcome.NoChange(CommandResult.NoChange(id, $"Invoice {id} is already void."));
            await uow.Store<ISalesStore>().VoidInvoiceAsync(invoice.InvoiceNumber, now, context.Actor, context.Reason!, ct);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Invoice {id} voided; its {invoice.Lines.Select(l => l.ShipmentId).Distinct().Count()} shipment(s) can be invoiced again."),
                [pipeline.Event(new InvoiceChanged(invoice.InvoiceNumber, invoice.OrderNumber, "Voided"), context, CustomerService.SourceContext, Entity, id)],
                new AuditChange(Entity, id, nameof(InvoiceStatus.Issued), nameof(InvoiceStatus.Voided)));
        }, ct);
    }
}
