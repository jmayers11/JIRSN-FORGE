namespace Forge.Interchange.Contracts;

// Owner decisions 2026-11-01 (API and EDI): ANSI X12 with customers, suppliers and other systems for orders, ship notices and
// invoices; files move through an inbox/outbox folder that a VAN, SFTP client or EDI provider serves; an order that arrives becomes a
// draft sales order a Sales Manager approves; other systems use partner API keys and receive HMAC-signed webhooks.

public enum PartnerKind
{
    /// <summary>Sends purchase orders (850) and receives acknowledgments (855), ship notices (856) and invoices (810).</summary>
    Customer,

    /// <summary>Receives purchase orders (850) and sends acknowledgments, ship notices and invoices back.</summary>
    Supplier,

    /// <summary>Another system of the company (accounting, a portal): API keys and webhooks, no EDI.</summary>
    System,
}

/// <param name="PartyCode">The customer code (customers) or supplier code (suppliers) the partner trades as; null for systems.</param>
/// <param name="EdiQualifier">The partner's ISA qualifier (01 DUNS, ZZ mutually defined …); null when the partner does not use EDI.</param>
/// <param name="OurQualifier">Our ISA qualifier and id as this partner knows us.</param>
/// <param name="Outbound">The transaction sets Forge sends this partner (855, 856, 810 for customers; 850 for suppliers).</param>
public sealed record TradingPartnerInfo(
    string Code,
    string Name,
    PartnerKind Kind,
    string? PartyCode,
    string? EdiQualifier,
    string? EdiId,
    string? OurQualifier,
    string? OurId,
    IReadOnlyList<string> Outbound,
    bool Active,
    bool Test,
    string UpdatedBy,
    DateTimeOffset UpdatedAt)
{
    public bool UsesEdi => EdiQualifier is not null && EdiId is not null && OurQualifier is not null && OurId is not null;

    public bool Sends(string transactionSet) => Active && UsesEdi && Outbound.Contains(transactionSet, StringComparer.Ordinal);
}

/// <summary>What an API key may do.</summary>
public static class ApiScopes
{
    /// <summary>Submit X12 documents.</summary>
    public const string Edi = "edi";

    /// <summary>Submit orders and read one's own orders, shipments and invoices (customers).</summary>
    public const string Orders = "orders";

    /// <summary>Read one's own approved purchase orders (suppliers).</summary>
    public const string PurchaseOrders = "purchase-orders";

    public static readonly IReadOnlyList<string> All = [Edi, Orders, PurchaseOrders];
}

/// <summary>An API key: only its hash is kept. The key itself is shown once, when issued.</summary>
public sealed record ApiKeyInfo(long KeyId, string PartnerCode, string Prefix, IReadOnlyList<string> Scopes, string IssuedBy, DateTimeOffset IssuedAt,
    string? RevokedBy, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt);

public enum DocumentDirection { Inbound, Outbound }

public enum DocumentStatus
{
    /// <summary>Inbound: read and waiting to be processed.</summary>
    Received,

    /// <summary>Inbound: done (a draft sales order made, a supplier notice recorded, an acknowledgment matched).</summary>
    Processed,

    /// <summary>Inbound: something could not be mapped; a person fixes it and reprocesses, or rejects it.</summary>
    NeedsReview,

    /// <summary>Inbound: a person rejected it.</summary>
    Rejected,

    /// <summary>Outbound: written, waiting to go out through the folder.</summary>
    Ready,

    /// <summary>Outbound: placed in the outbox.</summary>
    Sent,

    /// <summary>Outbound: the partner's functional acknowledgment (997) accepted it.</summary>
    Accepted,

    /// <summary>Outbound: the partner's functional acknowledgment rejected it.</summary>
    Refused,
}

/// <param name="BusinessRef">What the document is about: "sales-order:123", "shipment:7", "invoice:9", "purchase-order:5001".</param>
/// <param name="GroupControl">The functional group control number (GS06), used to match 997 acknowledgments.</param>
public sealed record InterchangeDocument(
    long DocumentId,
    DocumentDirection Direction,
    string? PartnerCode,
    string TransactionSet,
    string InterchangeControl,
    string GroupControl,
    string TransactionControl,
    DocumentStatus Status,
    string? BusinessRef,
    string? Summary,
    IReadOnlyList<string> Problems,
    string? FileName,
    string Content,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

/// <summary>The events a webhook may subscribe to, and the Forge events behind them.</summary>
public static class WebhookEvents
{
    public static readonly IReadOnlyDictionary<string, string> Catalog = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sales_order.changed"] = "SalesOrderChanged",
        ["shipment.changed"] = "ShipmentChanged",
        ["invoice.changed"] = "InvoiceChanged",
        ["purchase_order.changed"] = "PurchaseOrderChanged",
        ["production_order.status_changed"] = "ProductionOrderStatusChanged",
        ["edi_document.changed"] = "InterchangeDocumentChanged",
    };

    public static string? NameOf(string eventType) => Catalog.FirstOrDefault(p => p.Value == eventType).Key;
}

/// <param name="PartnerCode">When set, only events about that customer's or supplier's business are sent.</param>
public sealed record WebhookInfo(long WebhookId, string Name, string Url, IReadOnlyList<string> Events, string? PartnerCode, bool Active, string SecretPrefix,
    string UpdatedBy, DateTimeOffset UpdatedAt);

public enum DeliveryStatus { Pending, Delivered, Failed }

public sealed record WebhookDelivery(long DeliveryId, long WebhookId, long EventSequence, string EventName, string Payload, DeliveryStatus Status, int Attempts,
    DateTimeOffset NextAttemptAt, int? LastStatusCode, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt);

public interface IInterchangeLookup
{
    Task<IReadOnlyList<TradingPartnerInfo>> ListPartnersAsync(CancellationToken ct);

    Task<TradingPartnerInfo?> FindPartnerAsync(string code, CancellationToken ct);

    /// <summary>The partner that sends as this ISA qualifier and id.</summary>
    Task<TradingPartnerInfo?> FindPartnerByEdiAsync(string qualifier, string id, CancellationToken ct);

    /// <summary>The partner trading as this customer or supplier.</summary>
    Task<TradingPartnerInfo?> FindPartnerByPartyAsync(PartnerKind kind, string partyCode, CancellationToken ct);

    Task<IReadOnlyList<ApiKeyInfo>> ListApiKeysAsync(CancellationToken ct);

    /// <summary>The active key with this hash, with its partner.</summary>
    Task<(ApiKeyInfo Key, TradingPartnerInfo Partner)?> FindActiveKeyAsync(string hash, CancellationToken ct);

    Task<IReadOnlyList<InterchangeDocument>> ListDocumentsAsync(DocumentStatus? status, int take, CancellationToken ct);

    Task<InterchangeDocument?> FindDocumentAsync(long documentId, CancellationToken ct);

    /// <summary>Outbound documents written but not yet placed in the outbox.</summary>
    Task<IReadOnlyList<InterchangeDocument>> ListReadyAsync(CancellationToken ct);

    Task<IReadOnlyList<WebhookInfo>> ListWebhooksAsync(CancellationToken ct);

    /// <summary>The webhook's secret, for signing deliveries.</summary>
    Task<string?> WebhookSecretAsync(long webhookId, CancellationToken ct);

    Task<IReadOnlyList<WebhookDelivery>> ListDeliveriesAsync(long? webhookId, int take, CancellationToken ct);

    Task<IReadOnlyList<WebhookDelivery>> ListDueDeliveriesAsync(DateTimeOffset now, int take, CancellationToken ct);
}
