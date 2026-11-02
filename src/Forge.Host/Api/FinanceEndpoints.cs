using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Procurement;
using Forge.Procurement.Contracts;
using Forge.Resources;
using Forge.Resources.Contracts;
using Forge.Sales;
using Forge.Sales.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record SiteFeatureRequest(bool Enabled, string? Reason);

public sealed record PaymentRequest(string CustomerCode, DateOnly ReceivedOn, decimal Amount, PaymentMethod Method, string? Reference, string? Note, PaymentApplication[]? Applications, string? Reason);

public sealed record CreditLimitRequest(decimal? CreditLimit, string? Reason);

public sealed record CreditPolicyRequest(int? PastDueDays, bool IncludeOpenOrders, string? Reason);

public sealed record CreditReleaseRequest(string CustomerCode, long? SalesOrderNumber, long? ShipmentId, string? Reason);

public sealed record SupplierInvoiceRequest(long PoNumber, string SupplierInvoiceNumber, DateOnly InvoiceDate, DateOnly? DueDate, decimal Freight, decimal Tax, SupplierInvoiceLineEntry[]? Lines,
    string? Reason);

public sealed record SupplierInvoiceDecisionRequest(bool Approve, string Note, string? Reason);

public sealed record SupplierInvoicePaidRequest(DateOnly PaidOn, string? Reference, string? Reason);

public sealed record MatchPolicyRequest(decimal TolerancePercent, decimal ToleranceAmount, string? Reason);

public sealed record BackflushRequest(bool Enabled, string? LocationCode, string? Reason);

public sealed record BackflushResolveRequest(string Note, string? Reason);

/// <summary>Where a customer stands, with their open invoices and recent payments.</summary>
public sealed record ReceivablesView(IReadOnlyList<CustomerCreditStatus> Customers, IReadOnlyList<InvoiceBalance> OpenInvoices, IReadOnlyList<PaymentInfo> Payments, CreditPolicy Policy,
    IReadOnlyList<CreditRelease> Releases, IReadOnlyList<CreditHoldItem> Waiting);

/// <summary>A draft sales order or a packed shipment that waits on a customer's credit hold.</summary>
public sealed record CreditHoldItem(string CustomerCode, string CustomerName, string Kind, long Number, decimal? Value, IReadOnlyList<string> Reasons);

/// <summary>
/// Owner decisions 2026-11-01: the optional parts of Forge a company can turn off; customer payments and credit holds; supplier invoices
/// and the three-way match; backflush settings and exceptions.
/// </summary>
internal static class FinanceEndpoints
{
    public static void MapFinanceEndpoints(this IEndpointRouteBuilder app)
    {
        var site = app.MapGroup("/api/site").RequireAuthorization();
        site.MapGet("/features", async (HttpContext http, IKernelUnitOfWorkFactory unitOfWork, CancellationToken ct) =>
        {
            if (await PrincipalAsync(http) is null) return NotRegistered();
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            return Results.Ok(await uow.Store<ISiteFeatureLookup>().ListFeaturesAsync(ct));
        });
        site.MapPost("/features/{code}", (HttpContext http, string code, SiteFeatureRequest b, SiteFeatureService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetAsync(new SetSiteFeature(code, b.Enabled), c, ct)));

        var ar = app.MapGroup("/api/receivables").RequireAuthorization();
        ar.MapGet("/", (HttpContext http, string? customer, TimeProvider time, CancellationToken _) =>
            Read(http, Permissions.SalesRead, async principal =>
            {
                var ct = http.RequestAborted;
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(ct);
                var today = await uow.SiteDateAsync(time.GetUtcNow(), ct);
                var code = string.IsNullOrWhiteSpace(customer) ? null : customer.Trim().ToUpperInvariant();
                var status = await Receivables.StatusAsync(uow, today, code, ct);
                var receivables = uow.Store<IReceivablesLookup>();
                var waiting = new List<CreditHoldItem>();
                var held = status.Where(s => s.OnHold).ToDictionary(s => s.CustomerCode);
                var releases = await receivables.ListCreditReleasesAsync(code, ct);
                foreach (var order in (await uow.Store<ISalesLookup>().ListOrdersAsync(ct)).Where(o => o.Status == SalesOrderStatus.Draft && held.ContainsKey(o.CustomerCode)))
                    if (!releases.Any(r => r.SalesOrderNumber == order.OrderNumber))
                        waiting.Add(new CreditHoldItem(order.CustomerCode, order.CustomerName, "SalesOrder", order.OrderNumber, order.Total, held[order.CustomerCode].Reasons));
                foreach (var shipment in (await uow.Store<IInventoryLookup>().ListShipmentsAsync(false, ct)).Where(s => s.Status == ShipmentStatus.Packed))
                {
                    var check = http.RequestServices.GetServices<IShipmentReleaseCheck>();
                    foreach (var gate in check)
                    {
                        try
                        {
                            await gate.CheckAsync(uow, shipment, time.GetUtcNow(), ct);
                        }
                        catch (CommandRejectedException ex) when (ex.Code == "credit_hold")
                        {
                            waiting.Add(new CreditHoldItem(shipment.Customer, shipment.Customer, "Shipment", shipment.ShipmentId, null, [ex.Message]));
                        }
                    }
                }

                return new ReceivablesView(status, (await Receivables.BalancesAsync(uow, today, code, ct)).Where(b => b.Balance != 0).ToList(),
                    (await receivables.ListPaymentsAsync(code, ct)).Take(200).ToList(), await receivables.CreditPolicyAsync(ct), releases.Take(100).ToList(), waiting);
            }));
        ar.MapPost("/payments", (HttpContext http, PaymentRequest b, ReceivablesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordPaymentAsync(new RecordPayment(b.CustomerCode, b.ReceivedOn, b.Amount, b.Method, b.Reference, b.Note, b.Applications), c, ct)));
        ar.MapPost("/payments/{id:long}/void", (HttpContext http, long id, ReasonRequest b, ReceivablesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.VoidPaymentAsync(new VoidPayment(id), c, ct)));
        ar.MapPost("/customers/{code}/credit-limit", (HttpContext http, string code, CreditLimitRequest b, ReceivablesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetCreditLimitAsync(new SetCreditLimit(code, b.CreditLimit), c, ct)));
        ar.MapPost("/policy", (HttpContext http, CreditPolicyRequest b, ReceivablesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetCreditPolicyAsync(new SetCreditPolicy(b.PastDueDays, b.IncludeOpenOrders), c, ct)));
        ar.MapPost("/releases", (HttpContext http, CreditReleaseRequest b, ReceivablesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReleaseAsync(new ReleaseCreditHold(b.CustomerCode, b.SalesOrderNumber, b.ShipmentId), c, ct)));

        var ap = app.MapGroup("/api/payables").RequireAuthorization();
        ap.MapGet("/invoices", (HttpContext http, CancellationToken _) =>
            ReadStore<ISupplierInvoiceLookup>(http, Permissions.PurchasingRead, async (l, ct) => await l.ListSupplierInvoicesAsync(ct)));
        ap.MapGet("/policy", (HttpContext http, CancellationToken _) =>
            ReadStore<ISupplierInvoiceLookup>(http, Permissions.PurchasingRead, async (l, ct) => await l.MatchPolicyAsync(ct)));
        ap.MapPost("/invoices", (HttpContext http, SupplierInvoiceRequest b, SupplierInvoiceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.EnterAsync(new EnterSupplierInvoice(b.PoNumber, b.SupplierInvoiceNumber, b.InvoiceDate, b.DueDate, b.Freight, b.Tax, b.Lines ?? []), c, ct)));
        ap.MapPost("/invoices/{id:long}/decide", (HttpContext http, long id, SupplierInvoiceDecisionRequest b, SupplierInvoiceService s, CancellationToken ct) =>
            Command(http, b.Reason ?? b.Note, c => s.DecideAsync(new DecideSupplierInvoice(id, b.Approve, b.Note), c, ct)));
        ap.MapPost("/invoices/{id:long}/void", (HttpContext http, long id, ReasonRequest b, SupplierInvoiceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.VoidAsync(new VoidSupplierInvoice(id), c, ct)));
        ap.MapPost("/invoices/{id:long}/paid", (HttpContext http, long id, SupplierInvoicePaidRequest b, SupplierInvoiceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.MarkPaidAsync(new MarkSupplierInvoicePaid(id, b.PaidOn, b.Reference), c, ct)));
        ap.MapPost("/policy", (HttpContext http, MatchPolicyRequest b, SupplierInvoiceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetPolicyAsync(new SetInvoiceMatchPolicy(b.TolerancePercent, b.ToleranceAmount), c, ct)));

        var backflush = app.MapGroup("/api/inventory/backflush").RequireAuthorization();
        backflush.MapGet("/settings", (HttpContext http, CancellationToken _) =>
            ReadStore<IBackflushLookup>(http, Permissions.InventoryRead, async (l, ct) => await l.ListBackflushSettingsAsync(ct)));
        backflush.MapGet("/exceptions", (HttpContext http, bool? open, CancellationToken _) =>
            ReadStore<IBackflushLookup>(http, Permissions.InventoryRead, async (l, ct) => await l.ListBackflushExceptionsAsync(open ?? true, ct)));
        backflush.MapPost("/settings/{part}", (HttpContext http, string part, BackflushRequest b, BackflushService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetAsync(new SetBackflush(part, b.Enabled, b.LocationCode), c, ct)));
        backflush.MapPost("/exceptions/{id:long}/resolve", (HttpContext http, long id, BackflushResolveRequest b, BackflushService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ResolveExceptionAsync(new ResolveBackflushException(id, b.Note), c, ct)));
    }
}
