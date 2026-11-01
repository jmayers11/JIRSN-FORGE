using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Forge.Host.Interchange;
using Forge.Interchange;
using Forge.Interchange.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Procurement.Contracts;
using Forge.Sales;
using Forge.Sales.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record PartnerRequest(string Code, string Name, PartnerKind Kind, string? PartyCode, string? EdiQualifier, string? EdiId, string? OurQualifier, string? OurId,
    string[]? Outbound, bool Active, bool Test, string? Reason);

public sealed record ApiKeyRequest(string PartnerCode, string[]? Scopes, string? Reason);

public sealed record WebhookRequest(string Name, string Url, string[]? Events, string? PartnerCode, bool Active, bool RotateSecret, string? Reason);

public sealed record UploadRequest(string? FileName, string Content, string? Reason);

public sealed record PartnerOrderLine(string PartNumber, decimal Quantity, decimal UnitPrice, DateOnly DueDate);

public sealed record PartnerOrderRequest(string? CustomerPo, string? ShipTo, PartnerOrderLine[]? Lines);

/// <summary>API and EDI (owner decisions 2026-11-01): administration for people, and the partner API for API keys.</summary>
internal static class InterchangeEndpoints
{
    public const string KeyHeader = "X-Forge-Api-Key";

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>A new key: "fk_" and 8 hex characters people can recognise, then 32 random bytes. Only the hash is stored.</summary>
    public static (string Key, string Prefix) NewKey()
    {
        var prefix = "fk_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        return ($"{prefix}_{Base64Url(RandomNumberGenerator.GetBytes(32))}", prefix);
    }

    public static string NewSecret() => "whsec_" + Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static void MapInterchangeEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/interchange").RequireAuthorization();
        api.MapGet("/partners", (HttpContext http, CancellationToken _) =>
            ReadStore<IInterchangeLookup>(http, Permissions.InterchangeRead, async (s, ct) => await s.ListPartnersAsync(ct)));
        api.MapPost("/partners", (HttpContext http, PartnerRequest b, InterchangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SavePartnerAsync(new SavePartner(b.Code, b.Name, b.Kind, b.PartyCode, b.EdiQualifier, b.EdiId, b.OurQualifier, b.OurId, b.Outbound ?? [], b.Active, b.Test), c, ct)));
        api.MapGet("/keys", (HttpContext http, CancellationToken _) =>
            ReadStore<IInterchangeLookup>(http, Permissions.InterchangeRead, async (s, ct) => await s.ListApiKeysAsync(ct)));
        api.MapPost("/keys", async (HttpContext http, ApiKeyRequest b, InterchangeService s, CancellationToken ct) =>
        {
            var (key, prefix) = NewKey();
            CommandResult? issued = null;
            var result = await Command(http, b.Reason, async c => issued = await s.IssueApiKeyAsync(new IssueApiKey(b.PartnerCode, prefix, Hash(key), b.Scopes ?? []), c, ct));
            // The key is returned once, only when this request created it (a replay of the same request cannot show it again).
            return issued is { Succeeded: true, Code: "ok" } ? Results.Ok(new { issued.EntityId, issued.Message, key, prefix }) : result;
        });
        api.MapPost("/keys/{id:long}/revoke", (HttpContext http, long id, ReasonRequest b, InterchangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RevokeApiKeyAsync(new RevokeApiKey(id), c, ct)));
        api.MapGet("/documents", (HttpContext http, DocumentStatus? status, int? take, CancellationToken _) =>
            ReadStore<IInterchangeLookup>(http, Permissions.InterchangeRead, async (s, ct) =>
                (await s.ListDocumentsAsync(status, take ?? 200, ct)).Select(d => d with { Content = string.Empty }).ToList()));
        api.MapGet("/documents/{id:long}", (HttpContext http, long id, CancellationToken _) =>
            ReadStore<IInterchangeLookup>(http, Permissions.InterchangeRead, async (s, ct) => await s.FindDocumentAsync(id, ct)));
        api.MapPost("/documents/upload", (HttpContext http, UploadRequest b, InterchangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReceiveAsync(new ReceiveDocuments(null, b.FileName, b.Content), c, ct)));
        api.MapPost("/documents/{id:long}/reprocess", (HttpContext http, long id, ReasonRequest b, InterchangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReprocessAsync(new ReprocessDocument(id), c, ct)));
        api.MapPost("/documents/{id:long}/reject", (HttpContext http, long id, ReasonRequest b, InterchangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RejectAsync(new RejectDocument(id), c, ct)));
        api.MapPost("/folder/run", (HttpContext http, FolderExchange folder, CancellationToken _) =>
            Read(http, Permissions.InterchangeManage, async principal =>
            {
                if (!folder.Enabled) return new { enabled = false, received = 0, failed = 0, sent = 0 };
                var (received, failed, sent) = await folder.RunOnceAsync(http.RequestAborted);
                return (object)new { enabled = true, received, failed, sent };
            }));
        api.MapGet("/webhooks", (HttpContext http, CancellationToken _) =>
            ReadStore<IInterchangeLookup>(http, Permissions.InterchangeRead, async (s, ct) => new { webhooks = await s.ListWebhooksAsync(ct), events = WebhookEvents.Catalog.Keys }));
        api.MapPost("/webhooks", (HttpContext http, WebhookRequest b, InterchangeService s, CancellationToken ct) => SaveWebhookAsync(http, null, b, s, ct));
        api.MapPost("/webhooks/{id:long}", (HttpContext http, long id, WebhookRequest b, InterchangeService s, CancellationToken ct) => SaveWebhookAsync(http, id, b, s, ct));
        api.MapGet("/webhooks/deliveries", (HttpContext http, long? webhook, CancellationToken _) =>
            ReadStore<IInterchangeLookup>(http, Permissions.InterchangeRead, async (s, ct) => await s.ListDeliveriesAsync(webhook, 200, ct)));

        MapPartnerApi(app.MapGroup("/api/partner/v1"));
    }

    private static async Task<IResult> SaveWebhookAsync(HttpContext http, long? id, WebhookRequest b, InterchangeService s, CancellationToken ct)
    {
        var secret = id is null || b.RotateSecret ? NewSecret() : null;
        CommandResult? saved = null;
        var result = await Command(http, b.Reason, async c => saved = await s.SaveWebhookAsync(new SaveWebhook(id, b.Name, b.Url, b.Events ?? [], b.PartnerCode, b.Active, secret), c, ct));
        return saved is { Succeeded: true, Code: "ok" } && secret is not null ? Results.Ok(new { saved.EntityId, saved.Message, secret }) : result;
    }

    /// <summary>The partner API: an API key in X-Forge-Api-Key (or Authorization: Bearer) names the partner; each route needs a scope.</summary>
    private static void MapPartnerApi(RouteGroupBuilder partner)
    {
        partner.MapPost("/edi", async (HttpContext http, InterchangeService s, TimeProvider time, CancellationToken ct) =>
        {
            if (await PartnerAsync(http, ApiScopes.Edi) is not { } caller) return Denied(http);
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(ct);
            if (text.Length > 5_000_000) return Results.Problem(title: "Too large", statusCode: StatusCodes.Status413PayloadTooLarge);
            return await PartnerCommandAsync(http, caller.Partner, time, "EDI submitted through the API", c => s.ReceiveAsync(new ReceiveDocuments(caller.Partner.Code, "api", text), c, ct));
        });
        partner.MapPost("/orders", async (HttpContext http, PartnerOrderRequest b, SalesOrderService sales, TimeProvider time, CancellationToken ct) =>
        {
            if (await PartnerAsync(http, ApiScopes.Orders) is not { } caller) return Denied(http);
            var lines = (b.Lines ?? []).Select(l => new NewSalesLine(l.PartNumber, l.Quantity, l.UnitPrice, l.DueDate)).ToList();
            // Owner decision 2026-11-01: an order from a partner is a draft a Sales Manager approves, like one from EDI.
            return await PartnerCommandAsync(http, caller.Partner, time, $"Order {b.CustomerPo} submitted through the API",
                c => sales.CreateAsync(new CreateSalesOrder(caller.Partner.PartyCode!, b.CustomerPo, b.ShipTo, lines), c, ct));
        });
        partner.MapGet("/orders", async (HttpContext http, string? customerPo, IKernelUnitOfWorkFactory unitOfWork, ISalesShipmentResolver shipments, CancellationToken ct) =>
        {
            if (await PartnerAsync(http, ApiScopes.Orders) is not { } caller) return Denied(http);
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            var sales = uow.Store<ISalesLookup>();
            var orders = (await sales.ListOrdersAsync(ct)).Where(o => o.CustomerCode == caller.Partner.PartyCode
                && (customerPo is null || string.Equals(o.CustomerPo, customerPo, StringComparison.OrdinalIgnoreCase))).Take(200).ToList();
            var invoices = (await sales.ListInvoicesAsync(ct)).Where(i => i.CustomerCode == caller.Partner.PartyCode && i.Status == InvoiceStatus.Issued).ToList();
            var view = new List<object>();
            foreach (var o in orders)
            {
                view.Add(new
                {
                    o.OrderNumber, o.CustomerPo, status = o.Status.ToString(), o.Currency, o.Total,
                    lines = o.Lines.Select(l => new { l.LineNumber, l.PartNumber, l.Quantity, l.UnitOfMeasure, l.UnitPrice, l.DueDate, status = l.Status.ToString() }),
                    shipments = (await shipments.ShippedAsync(uow, o.OrderNumber, ct)).Select(s => new { s.ShipmentId, s.OrderLine, s.PartNumber, s.Quantity, s.ShippedAt, s.Carrier, s.Tracking }),
                    invoices = invoices.Where(i => i.OrderNumber == o.OrderNumber).Select(i => new { i.InvoiceNumber, i.InvoiceDate, i.DueDate, i.Total, i.Currency }),
                });
            }

            return Results.Ok(view);
        });
        partner.MapGet("/purchase-orders", async (HttpContext http, IKernelUnitOfWorkFactory unitOfWork, CancellationToken ct) =>
        {
            if (await PartnerAsync(http, ApiScopes.PurchaseOrders) is not { } caller) return Denied(http);
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            var orders = (await uow.Store<IProcurementLookup>().ListPurchaseOrdersAsync(ct))
                .Where(o => o.SupplierCode == caller.Partner.PartyCode && o.Status is PurchaseOrderStatus.Approved or PurchaseOrderStatus.Closed).Take(200)
                .Select(o => new
                {
                    o.PoNumber, status = o.Status.ToString(), o.ApprovedAt,
                    lines = o.Lines.Where(l => l.Status == PurchaseLineStatus.Open).Select(l => new { l.LineNumber, l.PartNumber, l.Quantity, l.UnitOfMeasure, l.PromisedDate, l.UnitPrice, l.Currency }),
                }).ToList();
            return Results.Ok(orders);
        });
    }

    private static IResult Denied(HttpContext http) => http.Items.ContainsKey("forge.partner.known")
        ? Results.Problem(title: "Not allowed", detail: "This API key does not have the scope this request needs.", statusCode: StatusCodes.Status403Forbidden)
        : Results.Problem(title: "API key required", detail: $"Send a valid API key in {KeyHeader}.", statusCode: StatusCodes.Status401Unauthorized);

    private static async Task<(ApiKeyInfo Key, TradingPartnerInfo Partner)?> PartnerAsync(HttpContext http, string scope)
    {
        var key = http.Request.Headers[KeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(key) && http.Request.Headers.Authorization.ToString() is { } auth && auth.StartsWith("Bearer fk_", StringComparison.Ordinal)) key = auth[7..];
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) return null;
        await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
        var found = await uow.Store<IInterchangeLookup>().FindActiveKeyAsync(Hash(key.Trim()), http.RequestAborted);
        if (found is null) return null;
        http.Items["forge.partner.known"] = true;
        return found.Value.Key.Scopes.Contains(scope) ? found : null;
    }

    /// <summary>Runs a command for a partner: as the integration identity, recorded as "partner:CODE", with the caller's idempotency key.</summary>
    private static async Task<IResult> PartnerCommandAsync(HttpContext http, TradingPartnerInfo partner, TimeProvider time, string what, Func<CommandContext, Task<CommandResult>> run)
    {
        var supplied = http.Request.Headers[IdempotencyHeader].ToString();
        if (string.IsNullOrWhiteSpace(supplied) || supplied.Length > 150)
            return Results.Problem(title: "Idempotency key required", detail: $"Send a unique {IdempotencyHeader} with every request.", statusCode: StatusCodes.Status400BadRequest);
        var correlation = Guid.TryParse(http.Request.Headers[CorrelationHeader], out var c) ? c : ForgeIds.New(time);
        var context = InterchangeSystem.Context(correlation, $"partner:{partner.Code}:{supplied}", what, $"partner:{partner.Code}");
        try
        {
            var result = await run(context);
            return Results.Ok(result);
        }
        catch (CommandRejectedException ex)
        {
            return Results.Problem(title: "Command rejected", detail: ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity, extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
    }
}
