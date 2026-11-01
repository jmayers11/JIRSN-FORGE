using System.Globalization;
using System.Text;
using Forge.Host.Sales;
using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Sales;
using Forge.Sales.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record CustomerRequest(string Code, string Name, string? BillTo, string? ShipTo, int PaymentTermsDays, decimal TaxRatePercent, string? TaxExemptCertificate, string? Currency, string? Notes, string? Reason);

public sealed record CustomerChangeRequest(string Name, string? BillTo, string? ShipTo, int PaymentTermsDays, decimal TaxRatePercent, string? TaxExemptCertificate, string? Notes, string? Reason);

public sealed record CustomerStatusRequest(CustomerStatus Status, string? Reason);

public sealed record QuoteLineRequest(string PartNumber, decimal Quantity, decimal UnitPrice, int LeadTimeDays, string? Description);

public sealed record QuoteRequest(string Customer, DateOnly ValidUntil, IReadOnlyList<QuoteLineRequest>? Lines, string? Notes, string? Reason);

public sealed record QuoteLineAddRequest(string PartNumber, decimal Quantity, decimal UnitPrice, int LeadTimeDays, string? Description, string? Reason);

public sealed record QuoteConvertRequest(string? CustomerPo, string? ShipTo, string? Reason);

public sealed record SalesLineRequest(string PartNumber, decimal Quantity, decimal UnitPrice, DateOnly DueDate);

public sealed record SalesOrderRequest(string Customer, string? CustomerPo, string? ShipTo, IReadOnlyList<SalesLineRequest>? Lines, string? Reason);

public sealed record SalesLineAddRequest(string PartNumber, decimal Quantity, decimal UnitPrice, DateOnly DueDate, string? Reason);

public sealed record SalesLineDateRequest(DateOnly DueDate, string? Reason);

public sealed record InvoiceRequest(long OrderNumber, IReadOnlyList<long>? ShipmentIds, string? Reason);

/// <summary>A sales order line with what has shipped and what has been invoiced against it.</summary>
public sealed record SalesOrderLineView(int LineNumber, string PartNumber, decimal Quantity, string UnitOfMeasure, decimal UnitPrice, decimal Amount, DateOnly DueDate, SalesLineStatus Status,
    decimal Shipped, decimal Invoiced, decimal Remaining);

public sealed record SalesOrderView(long OrderNumber, string CustomerCode, string CustomerName, string? CustomerPo, long? QuoteNumber, string? ShipTo, string Currency, SalesOrderStatus Status,
    string CreatedBy, DateTimeOffset CreatedAt, string? ApprovedBy, DateTimeOffset? ApprovedAt, string? ClosedBy, DateTimeOffset? ClosedAt, decimal Total, decimal Invoiced,
    IReadOnlyList<SalesOrderLineView> Lines, IReadOnlyList<SalesShipment> UninvoicedShipments);

/// <summary>Quote to cash (owner decisions 2026-10-31) and the accounting export.</summary>
internal static class SalesEndpoints
{
    public static void MapSalesEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/sales").RequireAuthorization();

        api.MapGet("/customers", (HttpContext http, CancellationToken _) =>
            ReadStore<ISalesLookup>(http, Permissions.SalesRead, async (store, ct) => await store.ListCustomersAsync(ct)));
        api.MapPost("/customers", (HttpContext http, CustomerRequest b, CustomerService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateAsync(new CreateCustomer(b.Code, b.Name, b.BillTo, b.ShipTo, b.PaymentTermsDays, b.TaxRatePercent, b.TaxExemptCertificate, b.Currency, b.Notes), c, ct)));
        api.MapPost("/customers/{code}", (HttpContext http, string code, CustomerChangeRequest b, CustomerService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ChangeAsync(new ChangeCustomer(code, b.Name, b.BillTo, b.ShipTo, b.PaymentTermsDays, b.TaxRatePercent, b.TaxExemptCertificate, b.Notes), c, ct)));
        api.MapPost("/customers/{code}/status", (HttpContext http, string code, CustomerStatusRequest b, CustomerService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetStatusAsync(new SetCustomerStatus(code, b.Status), c, ct)));

        api.MapGet("/quotes", (HttpContext http, CancellationToken _) =>
            ReadStore<ISalesLookup>(http, Permissions.SalesRead, async (store, ct) => (await store.ListQuotesAsync(ct)).Select(q => new { quote = q, total = q.Total })));
        api.MapPost("/quotes", (HttpContext http, QuoteRequest b, QuoteService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateAsync(new CreateQuote(b.Customer, b.ValidUntil, (b.Lines ?? []).Select(l => new NewQuoteLine(l.PartNumber, l.Quantity, l.UnitPrice, l.LeadTimeDays, l.Description)).ToList(), b.Notes), c, ct)));
        api.MapPost("/quotes/{quote:long}/lines", (HttpContext http, long quote, QuoteLineAddRequest b, QuoteService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddLineAsync(new AddQuoteLine(quote, b.PartNumber, b.Quantity, b.UnitPrice, b.LeadTimeDays, b.Description), c, ct)));
        api.MapPost("/quotes/{quote:long}/lines/{line:int}/remove", (HttpContext http, long quote, int line, ReasonRequest b, QuoteService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RemoveLineAsync(new RemoveQuoteLine(quote, line), c, ct)));
        api.MapPost("/quotes/{quote:long}/approve", (HttpContext http, long quote, ReasonRequest b, QuoteService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveAsync(new ApproveQuote(quote), c, ct)));
        api.MapPost("/quotes/{quote:long}/decline", (HttpContext http, long quote, ReasonRequest b, QuoteService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DeclineAsync(new DeclineQuote(quote), c, ct)));
        api.MapPost("/quotes/{quote:long}/cancel", (HttpContext http, long quote, ReasonRequest b, QuoteService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelAsync(new CancelQuote(quote), c, ct)));
        api.MapPost("/quotes/{quote:long}/order", (HttpContext http, long quote, QuoteConvertRequest b, SalesOrderService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateFromQuoteAsync(new CreateSalesOrderFromQuote(quote, b.CustomerPo, b.ShipTo), c, ct)));

        api.MapGet("/orders", (HttpContext http, ISalesShipmentResolver shipments, CancellationToken _) =>
            Read(http, Permissions.SalesRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var lookup = uow.Store<ISalesLookup>();
                var invoices = await lookup.ListInvoicesAsync(http.RequestAborted);
                var views = new List<SalesOrderView>();
                foreach (var order in await lookup.ListOrdersAsync(http.RequestAborted))
                    views.Add(View(order, order.Status == SalesOrderStatus.Draft ? [] : await shipments.ShippedAsync(uow, order.OrderNumber, http.RequestAborted), invoices));
                return views;
            }));
        api.MapPost("/orders", (HttpContext http, SalesOrderRequest b, SalesOrderService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateAsync(new CreateSalesOrder(b.Customer, b.CustomerPo, b.ShipTo, (b.Lines ?? []).Select(l => new NewSalesLine(l.PartNumber, l.Quantity, l.UnitPrice, l.DueDate)).ToList()), c, ct)));
        api.MapPost("/orders/{order:long}/lines", (HttpContext http, long order, SalesLineAddRequest b, SalesOrderService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddLineAsync(new AddSalesLine(order, b.PartNumber, b.Quantity, b.UnitPrice, b.DueDate), c, ct)));
        api.MapPost("/orders/{order:long}/lines/{line:int}/date", (HttpContext http, long order, int line, SalesLineDateRequest b, SalesOrderService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ChangeLineDateAsync(new ChangeSalesLineDate(order, line, b.DueDate), c, ct)));
        api.MapPost("/orders/{order:long}/lines/{line:int}/cancel", (HttpContext http, long order, int line, ReasonRequest b, SalesOrderService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelLineAsync(new CancelSalesLine(order, line), c, ct)));
        api.MapPost("/orders/{order:long}/approve", (HttpContext http, long order, ReasonRequest b, SalesOrderService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveAsync(new ApproveSalesOrder(order), c, ct)));
        api.MapPost("/orders/{order:long}/close", (HttpContext http, long order, ReasonRequest b, SalesOrderService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CloseAsync(new CloseSalesOrder(order), c, ct)));
        api.MapPost("/orders/{order:long}/cancel", (HttpContext http, long order, ReasonRequest b, SalesOrderService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelAsync(new CancelSalesOrder(order), c, ct)));

        api.MapGet("/invoices", (HttpContext http, CancellationToken _) =>
            ReadStore<ISalesLookup>(http, Permissions.SalesRead, async (store, ct) => await store.ListInvoicesAsync(ct)));
        api.MapPost("/invoices", (HttpContext http, InvoiceRequest b, InvoiceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.IssueAsync(new IssueInvoice(b.OrderNumber, b.ShipmentIds ?? []), c, ct)));
        api.MapPost("/invoices/{invoice:long}/void", (HttpContext http, long invoice, ReasonRequest b, InvoiceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.VoidAsync(new VoidInvoice(invoice), c, ct)));

        var accounting = app.MapGroup("/api/accounting").RequireAuthorization();
        accounting.MapGet("/export", (HttpContext http, DateOnly from, DateOnly to, AccountingExportService s, CancellationToken _) =>
            Read(http, Permissions.AccountingExport, async principal =>
            {
                var (start, end) = Period(from, to);
                return (await s.BuildAsync(principal, start, end, http.RequestAborted)).Summary;
            }));
        accounting.MapGet("/export/{file}", async (HttpContext http, string file, DateOnly from, DateOnly to, AccountingExportService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var decision = http.RequestServices.GetRequiredService<IForgeAuthorizer>().Authorize(principal, Permissions.AccountingExport, ResourceRef.Of(Permissions.AccountingExport));
            if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision);
            var (start, end) = Period(from, to);
            var export = await s.BuildAsync(principal, start, end, ct);
            var csv = file switch
            {
                "invoices" => AccountingExportService.InvoicesCsv(export.Invoices),
                "invoice-lines" => AccountingExportService.InvoiceLinesCsv(export.Invoices),
                "sales-journal" => AccountingExportService.JournalCsv(export.SalesJournal),
                "inventory-journal" when export.Summary.InventoryIncluded => AccountingExportService.JournalCsv(export.InventoryJournal),
                "inventory-journal" => null,
                _ => throw new Forge.Kernel.Commands.CommandRejectedException("unknown_export", "Export invoices, invoice-lines, sales-journal or inventory-journal."),
            };
            if (csv is null)
                throw new ForgeAuthorizationException(http.RequestServices.GetRequiredService<IForgeAuthorizer>().Authorize(principal, Permissions.CostingRead, ResourceRef.Of(Permissions.CostingRead)));
            var name = string.Create(CultureInfo.InvariantCulture, $"forge-{file}-{start:yyyyMMdd}-{end:yyyyMMdd}.csv");
            return Results.File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", name);
        });
    }

    private static (DateOnly From, DateOnly To) Period(DateOnly from, DateOnly to) =>
        to < from || to.DayNumber - from.DayNumber > 366
            ? throw new Forge.Kernel.Commands.CommandRejectedException("invalid_period", "Export a period of at most a year, from a date to the same or a later date.")
            : (from, to);

    private static SalesOrderView View(SalesOrderInfo order, IReadOnlyList<SalesShipment> shipped, IReadOnlyList<InvoiceInfo> invoices)
    {
        var billed = invoices.Where(i => i.OrderNumber == order.OrderNumber && i.Status == InvoiceStatus.Issued).ToList();
        var billedShipments = billed.SelectMany(i => i.Lines.Select(l => l.ShipmentId)).ToHashSet();
        var lines = order.Lines.Select(l =>
        {
            var shippedQty = shipped.Where(s => s.OrderLine == l.LineNumber).Sum(s => s.Quantity);
            var invoicedQty = billed.SelectMany(i => i.Lines).Where(x => x.OrderLine == l.LineNumber).Sum(x => x.Quantity);
            var remaining = l.Status == SalesLineStatus.Open && order.Status == SalesOrderStatus.Approved ? Math.Max(0, l.Quantity - shippedQty) : 0;
            return new SalesOrderLineView(l.LineNumber, l.PartNumber, l.Quantity, l.UnitOfMeasure, l.UnitPrice, l.Amount, l.DueDate, l.Status, shippedQty, invoicedQty, remaining);
        }).ToList();
        return new SalesOrderView(order.OrderNumber, order.CustomerCode, order.CustomerName, order.CustomerPo, order.QuoteNumber, order.ShipTo, order.Currency, order.Status,
            order.CreatedBy, order.CreatedAt, order.ApprovedBy, order.ApprovedAt, order.ClosedBy, order.ClosedAt, order.Total, billed.Sum(i => i.Total), lines,
            shipped.Where(s => !billedShipments.Contains(s.ShipmentId)).ToList());
    }
}
