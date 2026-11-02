using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
using Forge.Sales;
using Forge.Sales.Contracts;

namespace Forge.Host.Interchange;

/// <summary>Sends one webhook request; replaced in tests.</summary>
public interface IWebhookTransport
{
    /// <returns>The HTTP status code, or null with an error when nothing came back.</returns>
    Task<(int? StatusCode, string? Error)> PostAsync(Uri url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken ct);
}

public sealed class HttpWebhookTransport(IHttpClientFactory clients) : IWebhookTransport
{
    public async Task<(int? StatusCode, string? Error)> PostAsync(Uri url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(headers);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await clients.CreateClient("forge-webhooks").SendAsync(request, timeout.Token);
            return ((int)response.StatusCode, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "No answer within 10 seconds.");
        }
    }
}

/// <summary>
/// Owner decision 2026-11-01: other systems receive signed webhooks. Each subscribed event is queued once per webhook; a webhook limited
/// to a partner only gets events about that customer's or supplier's business. Delivered at least once.
/// </summary>
public sealed class WebhookSubscriber(InterchangeService interchange, IKernelUnitOfWorkFactory unitOfWork, ILogger<WebhookSubscriber> logger) : IEventSubscriber
{
    public string Name => "webhooks";

    public bool Handles(string eventType) => WebhookEvents.NameOf(eventType) is not null;

    public async Task HandleAsync(long sequence, EventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var name = WebhookEvents.NameOf(envelope.EventType)!;
        List<long> targets;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            var hooks = (await read.Store<IInterchangeLookup>().ListWebhooksAsync(ct)).Where(w => w.Active && w.Events.Contains(name)).ToList();
            if (hooks.Count == 0) return;
            var (customer, supplier, partnerCode) = await PartyAsync(read, envelope, ct);
            var partners = (await read.Store<IInterchangeLookup>().ListPartnersAsync(ct)).ToDictionary(p => p.Code, StringComparer.Ordinal);
            targets = hooks.Where(w => w.PartnerCode is null || (partners.TryGetValue(w.PartnerCode, out var p) && (
                    p.Code == partnerCode
                    || (p.Kind == PartnerKind.Customer && p.PartyCode == customer)
                    || (p.Kind == PartnerKind.Supplier && p.PartyCode == supplier))))
                .Select(w => w.WebhookId).ToList();
        }

        if (targets.Count == 0) return;
        using var data = JsonDocument.Parse(envelope.Payload);
        var payload = JsonSerializer.Serialize(new
        {
            id = string.Create(CultureInfo.InvariantCulture, $"evt_{sequence}"),
            type = name,
            occurredAt = envelope.RecordedAt,
            entityType = envelope.EntityType,
            entityId = envelope.EntityId,
            data = data.RootElement,
        }, CanonicalJson.Options);
        try
        {
            await interchange.EnqueueDeliveriesAsync(new EnqueueDeliveries(sequence, name, payload, targets),
                InterchangeSystem.Context(envelope.CorrelationId, string.Create(CultureInfo.InvariantCulture, $"system:webhooks:{sequence}"), "Webhook deliveries", SystemActors.Webhooks), ct);
        }
        catch (CommandRejectedException ex)
        {
            logger.LogWarning("Queuing webhooks for event {Sequence} was refused: {Code} {Message}", sequence, ex.Code, ex.Message);
        }
    }

    /// <summary>Which customer, supplier or partner an event is about, when it is about one.</summary>
    private static async Task<(string? Customer, string? Supplier, string? Partner)> PartyAsync(IKernelUnitOfWork read, EventEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.EventType)
        {
            case nameof(SalesOrderChanged):
            {
                var e = JsonSerializer.Deserialize<SalesOrderChanged>(envelope.Payload, CanonicalJson.Options)!;
                return ((await read.Store<ISalesLookup>().FindOrderAsync(e.OrderNumber, ct))?.CustomerCode, null, null);
            }
            case nameof(InvoiceChanged):
            {
                var e = JsonSerializer.Deserialize<InvoiceChanged>(envelope.Payload, CanonicalJson.Options)!;
                return ((await read.Store<ISalesLookup>().FindInvoiceAsync(e.InvoiceNumber, ct))?.CustomerCode, null, null);
            }
            case nameof(ShipmentChanged):
            {
                var e = JsonSerializer.Deserialize<ShipmentChanged>(envelope.Payload, CanonicalJson.Options)!;
                var shipment = await read.Store<IInventoryLookup>().FindShipmentAsync(e.ShipmentId, ct);
                if (shipment?.DemandId is { } demandId && await read.Store<IDemandStore>().FindAsync(demandId, ct) is { } demand
                    && SalesOrderInfo.ParseDemandSource(demand.SourceKey) is { } source)
                    return ((await read.Store<ISalesLookup>().FindOrderAsync(source.OrderNumber, ct))?.CustomerCode, null, null);
                return (null, null, null);
            }
            case nameof(PurchaseOrderChanged):
            {
                var e = JsonSerializer.Deserialize<PurchaseOrderChanged>(envelope.Payload, CanonicalJson.Options)!;
                return (null, (await read.Store<IProcurementLookup>().FindPurchaseOrderAsync(e.PoNumber, ct))?.SupplierCode, null);
            }
            case nameof(InterchangeDocumentChanged):
            {
                var e = JsonSerializer.Deserialize<InterchangeDocumentChanged>(envelope.Payload, CanonicalJson.Options)!;
                return (null, null, e.PartnerCode);
            }
            default:
                return (null, null, null);
        }
    }
}

/// <summary>
/// Sends due webhook deliveries, signed: X-Forge-Signature is "sha256=" and the hex HMAC-SHA256 of "{timestamp}.{body}" with the
/// webhook's secret, X-Forge-Timestamp the Unix seconds it was sent. A 2xx answer delivers; otherwise it is retried after 1, 5 and 30
/// minutes, 2 and 12 hours, then marked failed and the integration administrator is told.
/// </summary>
public sealed class WebhookDispatcher(InterchangeService interchange, IKernelUnitOfWorkFactory unitOfWork, IWebhookTransport transport, TimeProvider time, Forge.Host.Security.ISecretProtector secrets)
{
    public static string Signature(string secret, long timestamp, string body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{timestamp}.{body}")));
        return "sha256=" + Convert.ToHexStringLower(mac);
    }

    public async Task<int> DispatchDueAsync(int take, CancellationToken ct)
    {
        IReadOnlyList<WebhookDelivery> due;
        Dictionary<long, (WebhookInfo Hook, string? Secret)> hooks = [];
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            var lookup = read.Store<IInterchangeLookup>();
            due = await lookup.ListDueDeliveriesAsync(time.GetUtcNow(), take, ct);
            foreach (var hook in (await lookup.ListWebhooksAsync(ct)).Where(h => due.Any(d => d.WebhookId == h.WebhookId)))
                hooks[hook.WebhookId] = (hook, await lookup.WebhookSecretAsync(hook.WebhookId, ct) is { } stored ? secrets.Unprotect(stored) : null);
        }

        foreach (var d in due)
        {
            int? status = null;
            string? error;
            var (hook, secret) = hooks[d.WebhookId];
            if (!hook.Active || secret is null)
            {
                error = "The webhook is inactive.";
            }
            else
            {
                var stamp = time.GetUtcNow().ToUnixTimeSeconds();
                var headers = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["X-Forge-Event"] = d.EventName,
                    ["X-Forge-Delivery"] = d.DeliveryId.ToString(CultureInfo.InvariantCulture),
                    ["X-Forge-Timestamp"] = stamp.ToString(CultureInfo.InvariantCulture),
                    ["X-Forge-Signature"] = Signature(secret, stamp, d.Payload),
                };
                (status, error) = await transport.PostAsync(new Uri(hook.Url), d.Payload, headers, ct);
                if (status is not null && error is null && status is < 200 or >= 300) error = string.Create(CultureInfo.InvariantCulture, $"HTTP {status}");
            }

            var delivered = status is >= 200 and < 300;
            await interchange.RecordDeliveryAttemptAsync(new RecordDeliveryAttempt(d.DeliveryId, delivered, status, delivered ? null : error),
                InterchangeSystem.Context(ForgeIds.New(time), string.Create(CultureInfo.InvariantCulture, $"system:webhooks:attempt:{d.DeliveryId}:{d.Attempts + 1}"), "Webhook delivery", SystemActors.Webhooks), ct);
        }

        return due.Count;
    }
}

/// <summary>Background loop: webhook deliveries every 30 seconds, the EDI folder every minute when one is configured.</summary>
public sealed class InterchangeWorker(WebhookDispatcher webhooks, FolderExchange folder, TimeProvider time, ILogger<InterchangeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken);
                while (await webhooks.DispatchDueAsync(50, stoppingToken) == 50)
                {
                }

                if (tick++ % 2 == 0 && folder.Enabled) await folder.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Integration work failed; trying again shortly.");
            }
        }
    }
}
