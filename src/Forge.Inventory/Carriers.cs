using System.Globalization;
using Forge.Inventory.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Inventory;

/// <param name="EncryptedCredentials">Already encrypted by the host; null keeps what is stored.</param>
public sealed record SetCarrierAccount(string Code, CarrierKind Kind, string Name, string? AccountNumber, bool Sandbox, bool Active, IReadOnlyDictionary<string, string>? Settings,
    string? EncryptedCredentials);

/// <summary>Records a label bought through a carrier connection (the host buys it, then records it here).</summary>
public sealed record RecordCarrierLabel(long ShipmentId, string AccountCode, string Service, string Tracking, decimal? Cost, string? Currency, CarrierAddress ShipTo,
    IReadOnlyList<CarrierPackage> Packages, byte[]? Label, string? LabelContentType, string? LabelReference);

/// <summary>Freight or a carrier without a connection: the carrier's name and the tracking or PRO number, typed in.</summary>
public sealed record RecordManualCarrier(long ShipmentId, string Carrier, string? Tracking, string? ProNumber, string? BolNumber, CarrierAddress? ShipTo, IReadOnlyList<CarrierPackage>? Packages);

/// <summary>The system records what a carrier's tracking says.</summary>
public sealed record RecordCarrierTracking(long ShipmentId, CarrierShipmentStatus Status, string Summary, DateTimeOffset? DeliveredAt);

public sealed record VoidCarrierLabel(long ShipmentId);

public sealed record CarrierAccountChanged(string Code, string Kind, bool Active) : IDomainEvent
{
    public static string EventType => nameof(CarrierAccountChanged);

    public static int SchemaVersion => 1;
}

public sealed record ShipmentCarrierChanged(long ShipmentId, string Carrier, string? Tracking, string Status, string Message) : IDomainEvent
{
    public static string EventType => nameof(ShipmentCarrierChanged);

    public static int SchemaVersion => 1;
}

/// <summary>The carrier reports a delivery problem; shippers are told. Cleared when it moves again or is delivered.</summary>
public sealed record CarrierException(long ShipmentId, string Carrier, string Tracking, string Message) : IDomainEvent
{
    public static string EventType => nameof(CarrierException);

    public static int SchemaVersion => 1;
}

public sealed record CarrierExceptionCleared(long ShipmentId, string Status) : IDomainEvent
{
    public static string EventType => nameof(CarrierExceptionCleared);

    public static int SchemaVersion => 1;
}

public interface ICarrierStore
{
    Task UpsertCarrierAccountAsync(CarrierAccountInfo account, string? encryptedCredentials, CancellationToken ct);

    Task UpsertShipmentCarrierAsync(ShipmentCarrierInfo carrier, CancellationToken ct);

    Task SaveLabelAsync(long shipmentId, byte[]? content, string? contentType, CancellationToken ct);
}

/// <summary>Carrier accounts, labels, bills of lading and tracking (owner decisions 2026-11-01).</summary>
public sealed class CarrierService(CommandPipeline pipeline)
{
    private const string AccountEntity = "CarrierAccount";
    private const string ShipmentEntity = "Shipment";
    private static readonly string[] SettingKeys =
        ["fromName", "fromCompany", "fromStreet1", "fromStreet2", "fromCity", "fromState", "fromPostal", "fromCountry", "fromPhone", "baseUrl", "carrierIds", "crid", "mid", "labelFormat"];

    public Task<CommandResult> SetAccountAsync(SetCarrierAccount c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Carrier account");
        var spec = new CommandSpec(InventoryService.SourceContext, nameof(SetCarrierAccount), Permissions.CarriersManage, ResourceRef.Of(AccountEntity, code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c with { EncryptedCredentials = c.EncryptedCredentials is null ? null : "(encrypted)" }, context, async (uow, now) =>
        {
            await uow.RequireOnAsync(SiteFeatures.ShippingCarriers, ct);
            var name = BusinessCode.RequireName(c.Name, "Carrier account name");
            var settings = (c.Settings ?? new Dictionary<string, string>()).Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim(), StringComparer.Ordinal);
            if (settings.Keys.FirstOrDefault(k => !SettingKeys.Contains(k, StringComparer.Ordinal)) is { } unknown)
                throw new CommandRejectedException("unknown_setting", $"'{unknown}' is not a carrier setting. Use {string.Join(", ", SettingKeys)}.");
            if (settings.TryGetValue("baseUrl", out var url) && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
                throw new CommandRejectedException("invalid_url", "The base URL must be an https address.");
            var lookup = uow.Store<ICarrierLookup>();
            var before = (await lookup.ListCarrierAccountsAsync(ct)).FirstOrDefault(a => a.Code == code);
            var hasCredentials = c.EncryptedCredentials is not null || before?.HasCredentials == true;
            if (c.Kind != CarrierKind.Manual && c.Active && !hasCredentials)
                throw new CommandRejectedException("credentials_required", $"A {c.Kind} account needs its API credentials before it is used.");
            var account = new CarrierAccountInfo(code, c.Kind, name, string.IsNullOrWhiteSpace(c.AccountNumber) ? null : c.AccountNumber.Trim(), c.Sandbox, c.Active, settings,
                hasCredentials, context.Actor, now);
            await uow.Store<ICarrierStore>().UpsertCarrierAccountAsync(account, c.EncryptedCredentials, ct);
            return new CommandOutcome(CommandResult.Ok(code, $"Carrier account {code} ({c.Kind}{(c.Sandbox ? ", sandbox" : string.Empty)}) {(c.Active ? "is ready" : "is not in use")}."),
                [pipeline.Event(new CarrierAccountChanged(code, c.Kind.ToString(), c.Active), context, InventoryService.SourceContext, AccountEntity, code)],
                new AuditChange(AccountEntity, code, before is null ? null : CanonicalJson.Serialize(before), CanonicalJson.Serialize(account)));
        }, ct);
    }

    public Task<CommandResult> RecordLabelAsync(RecordCarrierLabel c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ShipmentAsync(nameof(RecordCarrierLabel), c.ShipmentId, c with { Label = null }, context, Permissions.ShippingRelease, async (uow, shipment, before, now) =>
        {
            if (before is { Status: CarrierShipmentStatus.Labeled or CarrierShipmentStatus.InTransit or CarrierShipmentStatus.Delivered })
                throw new CommandRejectedException("already_labeled", $"Shipment {c.ShipmentId} already has a label ({before.Carrier} {before.Tracking}). Void it first.");
            var account = (await uow.Store<ICarrierLookup>().ListCarrierAccountsAsync(ct)).FirstOrDefault(a => a.Code == c.AccountCode)
                ?? throw new CommandRejectedException("unknown_carrier_account", $"Carrier account {c.AccountCode} does not exist.");
            var info = new ShipmentCarrierInfo(c.ShipmentId, account.Code, account.Kind.ToString(), c.Service, c.Tracking, null, before?.BolNumber, c.Cost, c.Currency, c.ShipTo, c.Packages,
                c.Label is { Length: > 0 }, c.LabelReference, CarrierShipmentStatus.Labeled, "Label created", null, null, context.Actor, now);
            await uow.Store<ICarrierStore>().SaveLabelAsync(c.ShipmentId, c.Label, c.LabelContentType, ct);
            return (info, string.Create(CultureInfo.InvariantCulture, $"Label bought for shipment {c.ShipmentId}: {account.Kind} {c.Service}, tracking {c.Tracking}{(c.Cost is { } cost ? $", {cost:N2} {c.Currency}" : string.Empty)}."));
        }, ct);
    }

    public Task<CommandResult> RecordManualAsync(RecordManualCarrier c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ShipmentAsync(nameof(RecordManualCarrier), c.ShipmentId, c, context, Permissions.ShippingRelease, (uow, shipment, before, now) =>
        {
            if (before is { Status: CarrierShipmentStatus.Labeled })
                throw new CommandRejectedException("already_labeled", $"Shipment {c.ShipmentId} has a carrier label; void it before entering tracking by hand.");
            var carrier = BusinessCode.RequireName(c.Carrier, "Carrier");
            string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().ToUpperInvariant();
            var info = new ShipmentCarrierInfo(c.ShipmentId, null, carrier, before?.Service, Clean(c.Tracking), Clean(c.ProNumber), Clean(c.BolNumber) ?? before?.BolNumber
                ?? string.Create(CultureInfo.InvariantCulture, $"BOL-{c.ShipmentId}"), null, null, c.ShipTo ?? before?.ShipTo, c.Packages ?? before?.Packages ?? [], false, null,
                CarrierShipmentStatus.Manual, "Entered by hand", null, null, context.Actor, now);
            return Task.FromResult((info, $"Shipment {c.ShipmentId} goes by {carrier}{(info.Tracking is null ? string.Empty : $", tracking {info.Tracking}")}{(info.ProNumber is null ? string.Empty : $", PRO {info.ProNumber}")}."));
        }, ct);
    }

    public Task<CommandResult> RecordTrackingAsync(RecordCarrierTracking c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ShipmentAsync(nameof(RecordCarrierTracking), c.ShipmentId, c, context, Permissions.CarriersTrack, (uow, shipment, before, now) =>
        {
            if (before is null) throw new CommandRejectedException("no_carrier", $"Shipment {c.ShipmentId} has no carrier.");
            var summary = c.Summary.Length > 400 ? c.Summary[..400] : c.Summary;
            var info = before with { Status = c.Status, TrackingSummary = summary, LastTrackedAt = now, DeliveredAt = c.DeliveredAt ?? before.DeliveredAt, UpdatedAt = now };
            return Task.FromResult((info, $"Shipment {c.ShipmentId} ({before.Carrier} {before.Tracking}): {summary}."));
        }, ct, noChangeWhen: before => before is not null && before.Status == c.Status && before.TrackingSummary == c.Summary);
    }

    public Task<CommandResult> VoidLabelAsync(VoidCarrierLabel c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ShipmentAsync(nameof(VoidCarrierLabel), c.ShipmentId, c, context, Permissions.ShippingRelease, async (uow, shipment, before, now) =>
        {
            if (before is not { Status: CarrierShipmentStatus.Labeled }) throw new CommandRejectedException("no_label", $"Shipment {c.ShipmentId} has no unused label.");
            if (shipment.Status == ShipmentStatus.Shipped) throw new CommandRejectedException("already_shipped", $"Shipment {c.ShipmentId} has shipped; its label cannot be voided here.");
            await uow.Store<ICarrierStore>().SaveLabelAsync(c.ShipmentId, null, null, ct);
            return (before with { Status = CarrierShipmentStatus.Voided, HasLabel = false, TrackingSummary = "Label voided", UpdatedBy = context.Actor, UpdatedAt = now },
                $"Label {before.Tracking} for shipment {c.ShipmentId} voided. Void it with the carrier too if it was not voided there.");
        }, ct, reasonRequired: true);
    }

    private Task<CommandResult> ShipmentAsync<TCommand>(string type, long shipmentId, TCommand command, CommandContext context, string permission,
        Func<Forge.Kernel.Access.IKernelUnitOfWork, ShipmentInfo, ShipmentCarrierInfo?, DateTimeOffset, Task<(ShipmentCarrierInfo Info, string Message)>> change, CancellationToken ct,
        bool reasonRequired = false, Func<ShipmentCarrierInfo?, bool>? noChangeWhen = null)
    {
        var key = shipmentId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(InventoryService.SourceContext, type, permission, ResourceRef.Of(ShipmentEntity, key), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            await uow.RequireOnAsync(SiteFeatures.ShippingCarriers, ct);
            var shipment = (await uow.Store<IInventoryLookup>().ListShipmentsAsync(true, ct)).FirstOrDefault(s => s.ShipmentId == shipmentId)
                ?? throw new CommandRejectedException("shipment_not_found", $"Shipment {shipmentId} does not exist.");
            if (shipment.Status == ShipmentStatus.Cancelled) throw new CommandRejectedException("shipment_cancelled", $"Shipment {shipmentId} is cancelled.");
            var lookup = uow.Store<ICarrierLookup>();
            var before = await lookup.FindShipmentCarrierAsync(shipmentId, ct);
            if (noChangeWhen?.Invoke(before) == true) return CommandOutcome.NoChange(CommandResult.NoChange(key, "No change."));
            var (info, message) = await change(uow, shipment, before, now);
            await uow.Store<ICarrierStore>().UpsertShipmentCarrierAsync(info, ct);
            var events = new List<EventEnvelope>
            {
                pipeline.Event(new ShipmentCarrierChanged(shipmentId, info.Carrier, info.Tracking, info.Status.ToString(), message), context, InventoryService.SourceContext, ShipmentEntity, key),
            };
            if (info.Status == CarrierShipmentStatus.Exception && before?.Status != CarrierShipmentStatus.Exception)
                events.Add(pipeline.Event(new CarrierException(shipmentId, info.Carrier, info.Tracking ?? string.Empty, message), context, InventoryService.SourceContext, ShipmentEntity, key));
            if (before?.Status == CarrierShipmentStatus.Exception && info.Status != CarrierShipmentStatus.Exception)
                events.Add(pipeline.Event(new CarrierExceptionCleared(shipmentId, info.Status.ToString()), context, InventoryService.SourceContext, ShipmentEntity, key));
            return new CommandOutcome(CommandResult.Ok(key, message), events,
                new AuditChange("ShipmentCarrier", key, before is null ? null : CanonicalJson.Serialize(before), CanonicalJson.Serialize(info)));
        }, ct);
    }
}
