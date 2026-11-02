namespace Forge.Inventory.Contracts;

// Shipping carriers (owner decisions 2026-11-01): UPS, FedEx and USPS through their own APIs, ShipEngine and EasyPost as multi-carrier
// services, and freight or any other carrier by hand (a bill of lading printed from Forge and a PRO or tracking number typed in). Each
// carrier account's credentials are encrypted and never shown again. Nothing here ships anything: a shipper still releases the shipment.

public enum CarrierKind
{
    Ups,
    FedEx,
    Usps,
    ShipEngine,
    EasyPost,

    /// <summary>Freight (LTL) or any carrier without a connection: bill of lading and tracking numbers entered by hand.</summary>
    Manual,
}

/// <param name="Settings">Non-secret settings: the ship-from address (fromName, fromStreet1, fromCity, fromState, fromPostal, fromCountry, fromPhone),
/// baseUrl to point at a sandbox or simulator, and connector specifics (ShipEngine carrierIds; USPS crid, mid).</param>
/// <param name="HasCredentials">Whether credentials are stored (they are never returned).</param>
public sealed record CarrierAccountInfo(string Code, CarrierKind Kind, string Name, string? AccountNumber, bool Sandbox, bool Active, IReadOnlyDictionary<string, string> Settings,
    bool HasCredentials, string UpdatedBy, DateTimeOffset UpdatedAt);

public sealed record CarrierAddress(string Name, string? Company, string Street1, string? Street2, string City, string State, string PostalCode, string Country, string? Phone);

public sealed record CarrierPackage(decimal WeightLb, decimal? LengthIn, decimal? WidthIn, decimal? HeightIn);

public enum CarrierShipmentStatus
{
    /// <summary>A label was bought; not yet handed to the carrier.</summary>
    Labeled,

    /// <summary>Tracking or a PRO number was entered by hand (freight or a carrier without a connection).</summary>
    Manual,
    InTransit,
    Delivered,

    /// <summary>The carrier reports a problem (exception, return, undeliverable).</summary>
    Exception,
    Voided,
}

/// <summary>The carrier side of one shipment: who carries it, its label or bill of lading, and where it is.</summary>
public sealed record ShipmentCarrierInfo(
    long ShipmentId,
    string? AccountCode,
    string Carrier,
    string? Service,
    string? Tracking,
    string? ProNumber,
    string? BolNumber,
    decimal? Cost,
    string? Currency,
    CarrierAddress? ShipTo,
    IReadOnlyList<CarrierPackage> Packages,
    bool HasLabel,
    string? LabelReference,
    CarrierShipmentStatus Status,
    string? TrackingSummary,
    DateTimeOffset? LastTrackedAt,
    DateTimeOffset? DeliveredAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public interface ICarrierLookup
{
    Task<IReadOnlyList<CarrierAccountInfo>> ListCarrierAccountsAsync(CancellationToken ct);

    /// <summary>The account's encrypted credentials (for the host's connectors only).</summary>
    Task<string?> CarrierCredentialsAsync(string code, CancellationToken ct);

    Task<IReadOnlyList<ShipmentCarrierInfo>> ListShipmentCarriersAsync(CancellationToken ct);

    Task<ShipmentCarrierInfo?> FindShipmentCarrierAsync(long shipmentId, CancellationToken ct);

    Task<(byte[] Content, string ContentType)?> ReadLabelAsync(long shipmentId, CancellationToken ct);
}

/// <summary>A check the host adds to releasing a shipment (credit holds, owner decision 2026-11-01). It throws to refuse.</summary>
public interface IShipmentReleaseCheck
{
    Task CheckAsync(Forge.Kernel.Access.IKernelUnitOfWork uow, ShipmentInfo shipment, DateTimeOffset now, CancellationToken ct);
}
