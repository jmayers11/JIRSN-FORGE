using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Host.Security;
using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Resources.Contracts;

namespace Forge.Host.Shipping;

public sealed record CarrierQuote(IReadOnlyList<CarrierRate> Rates, IReadOnlyList<string> Problems);

/// <summary>
/// Calls carrier connections for shipments (owner decisions 2026-11-01): rates from every active account (or one), a label bought at
/// the rate a shipper chose, tracking. Credentials are decrypted only here, for the call.
/// </summary>
public sealed class CarrierGateway(IKernelUnitOfWorkFactory unitOfWork, IEnumerable<ICarrierConnector> connectors, ICarrierTransport transport, ISecretProtector secrets,
    CarrierService carriers, IForgeAuthorizer authorizer, TimeProvider time)
{
    private CarrierAccountContext Context(CarrierAccountInfo account, string? encrypted)
    {
        var credentials = encrypted is null ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(secrets.Unprotect(encrypted)) ?? [];
        return new CarrierAccountContext(account, credentials, transport, time);
    }

    private ICarrierConnector Connector(CarrierKind kind) =>
        connectors.FirstOrDefault(c => c.Kind == kind) ?? throw new CommandRejectedException("no_connector", $"There is no {kind} connection; enter its tracking by hand.");

    private static async Task<(ShipmentInfo Shipment, IReadOnlyList<CarrierAccountInfo> Accounts)> LoadAsync(IKernelUnitOfWork uow, long shipmentId, CancellationToken ct)
    {
        await uow.RequireOnAsync(SiteFeatures.ShippingCarriers, ct);
        var shipment = (await uow.Store<IInventoryLookup>().ListShipmentsAsync(true, ct)).FirstOrDefault(s => s.ShipmentId == shipmentId)
            ?? throw new CommandRejectedException("shipment_not_found", $"Shipment {shipmentId} does not exist.");
        return (shipment, await uow.Store<ICarrierLookup>().ListCarrierAccountsAsync(ct));
    }

    public async Task<CarrierQuote> RateAsync(long shipmentId, string? accountCode, CarrierAddress shipTo, IReadOnlyList<CarrierPackage> packages, CancellationToken ct)
    {
        Validate(shipTo, packages);
        var calls = new List<(CarrierAccountContext Context, ICarrierConnector Connector)>();
        await using (var uow = await unitOfWork.BeginSnapshotAsync(ct))
        {
            var (_, accounts) = await LoadAsync(uow, shipmentId, ct);
            foreach (var account in accounts.Where(a => a.Active && a.Kind != CarrierKind.Manual && (accountCode is null || a.Code == accountCode)))
                calls.Add((Context(account, await uow.Store<ICarrierLookup>().CarrierCredentialsAsync(account.Code, ct)), Connector(account.Kind)));
        }

        if (calls.Count == 0) throw new CommandRejectedException("no_carrier_accounts", "No carrier account is set up and active. Set one up, or enter tracking by hand.");
        var rates = new List<CarrierRate>();
        var problems = new List<string>();
        foreach (var (context, connector) in calls)
        {
            try
            {
                var request = new CarrierRequest(CarrierHttp.From(context), shipTo, packages, string.Create(CultureInfo.InvariantCulture, $"Shipment {shipmentId}"));
                rates.AddRange(await connector.RateAsync(context, request, ct));
            }
            catch (CommandRejectedException ex)
            {
                problems.Add($"{context.Account.Code}: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                problems.Add($"{context.Account.Code}: could not reach the carrier ({ex.Message})");
            }
        }

        return new CarrierQuote(rates.OrderBy(r => r.Amount).ToList(), problems);
    }

    public async Task<CommandResult> BuyLabelAsync(long shipmentId, CarrierRate rate, CarrierAddress shipTo, IReadOnlyList<CarrierPackage> packages, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rate);
        Validate(shipTo, packages);
        CarrierAccountContext account;
        await using (var uow = await unitOfWork.BeginSnapshotAsync(ct))
        {
            var (shipment, accounts) = await LoadAsync(uow, shipmentId, ct);
            if (shipment.Status is not (ShipmentStatus.Open or ShipmentStatus.Packed))
                throw new CommandRejectedException("not_shippable", $"Shipment {shipmentId} is {shipment.Status}; labels are bought before it ships.");
            if ((await uow.Store<ICarrierLookup>().FindShipmentCarrierAsync(shipmentId, ct)) is { Status: CarrierShipmentStatus.Labeled } existing)
                throw new CommandRejectedException("already_labeled", $"Shipment {shipmentId} already has a label ({existing.Tracking}). Void it first.");
            var info = accounts.FirstOrDefault(a => a.Code == rate.AccountCode && a.Active) ?? throw new CommandRejectedException("unknown_carrier_account", $"Carrier account {rate.AccountCode} is not active.");
            var decision = authorizer.Authorize(context.Principal, Permissions.ShippingRelease, ResourceRef.Of("Shipment", shipmentId.ToString(CultureInfo.InvariantCulture)));
            if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision);
            account = Context(info, await uow.Store<ICarrierLookup>().CarrierCredentialsAsync(info.Code, ct));
        }

        var request = new CarrierRequest(CarrierHttp.From(account), shipTo, packages, string.Create(CultureInfo.InvariantCulture, $"Shipment {shipmentId}"));
        CarrierLabel label;
        try
        {
            label = await Connector(account.Account.Kind).LabelAsync(account, request, rate, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new CommandRejectedException("carrier_unreachable", $"Could not reach {account.Account.Kind}: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(label.Tracking)) throw new CommandRejectedException("carrier_error", $"{account.Account.Kind} returned no tracking number.");
        return await carriers.RecordLabelAsync(new RecordCarrierLabel(shipmentId, account.Account.Code, rate.ServiceName, label.Tracking, label.Cost ?? rate.Amount, label.Currency ?? rate.Currency,
            shipTo, packages, label.Label, label.ContentType, label.LabelReference), context, ct);
    }

    /// <summary>Asks the carrier where a labelled shipment is and records any change. Returns null when there is nothing to track.</summary>
    public async Task<CommandResult?> TrackAsync(long shipmentId, CommandContext context, CancellationToken ct)
    {
        CarrierAccountContext account;
        ShipmentCarrierInfo carrier;
        await using (var uow = await unitOfWork.BeginSnapshotAsync(ct))
        {
            if (!await uow.IsOnAsync(SiteFeatures.ShippingCarriers, ct)) return null;
            var found = await uow.Store<ICarrierLookup>().FindShipmentCarrierAsync(shipmentId, ct);
            if (found is not { AccountCode: { } code, Tracking: { } } || found.Status is CarrierShipmentStatus.Delivered or CarrierShipmentStatus.Voided) return null;
            carrier = found;
            var info = (await uow.Store<ICarrierLookup>().ListCarrierAccountsAsync(ct)).FirstOrDefault(a => a.Code == code);
            if (info is null || info.Kind == CarrierKind.Manual) return null;
            account = Context(info, await uow.Store<ICarrierLookup>().CarrierCredentialsAsync(code, ct));
        }

        var tracking = await Connector(account.Account.Kind).TrackAsync(account, carrier.Tracking!, carrier.LabelReference, ct);
        return await carriers.RecordTrackingAsync(new RecordCarrierTracking(shipmentId, tracking.Status, tracking.Summary, tracking.DeliveredAt), context, ct);
    }

    private static void Validate(CarrierAddress shipTo, IReadOnlyList<CarrierPackage> packages)
    {
        if (shipTo is null || string.IsNullOrWhiteSpace(shipTo.Name) || string.IsNullOrWhiteSpace(shipTo.Street1) || string.IsNullOrWhiteSpace(shipTo.City)
            || string.IsNullOrWhiteSpace(shipTo.State) || string.IsNullOrWhiteSpace(shipTo.PostalCode) || string.IsNullOrWhiteSpace(shipTo.Country))
            throw new CommandRejectedException("address_required", "Give the ship-to name, street, city, state, postal code and country.");
        if (packages is not { Count: > 0 } || packages.Count > 50 || packages.Any(p => p.WeightLb is <= 0 or > 2000))
            throw new CommandRejectedException("packages_required", "Give each package's weight (above zero, in pounds).");
    }

    /// <summary>A straight bill of lading for freight, printable from the browser.</summary>
    public static string BillOfLading(ShipmentInfo shipment, ShipmentCarrierInfo? carrier, CarrierAddress? shipFrom, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(shipment);
        static string E(string? v) => WebUtility.HtmlEncode(v ?? string.Empty);
        static string Address(CarrierAddress? a, string fallback) => a is null ? E(fallback)
            : $"{E(a.Company ?? a.Name)}<br>{(a.Company is null ? string.Empty : E(a.Name) + "<br>")}{E(a.Street1)}{(a.Street2 is null ? string.Empty : "<br>" + E(a.Street2))}<br>{E(a.City)}, {E(a.State)} {E(a.PostalCode)} {E(a.Country)}{(a.Phone is null ? string.Empty : "<br>" + E(a.Phone))}";
        var bol = carrier?.BolNumber ?? string.Create(CultureInfo.InvariantCulture, $"BOL-{shipment.ShipmentId}");
        var html = new StringBuilder();
        html.Append("<!doctype html><meta charset=\"utf-8\"><title>Bill of lading ").Append(E(bol)).Append("</title>")
            .Append("<style>body{font-family:sans-serif;margin:24px}table{border-collapse:collapse;width:100%}td,th{border:1px solid #444;padding:6px;vertical-align:top;text-align:left}h1{font-size:20px}.sig{height:48px}</style>")
            .Append("<h1>Straight bill of lading — not negotiable</h1>")
            .Append(CultureInfo.InvariantCulture, $"<table><tr><th>BOL number</th><td>{E(bol)}</td><th>Date</th><td>{at:yyyy-MM-dd}</td></tr>")
            .Append(CultureInfo.InvariantCulture, $"<tr><th>Carrier</th><td>{E(carrier?.Carrier)}</td><th>PRO / tracking</th><td>{E(carrier?.ProNumber ?? carrier?.Tracking)}</td></tr>")
            .Append(CultureInfo.InvariantCulture, $"<tr><th>Shipper</th><td>{Address(shipFrom, string.Empty)}</td><th>Consignee</th><td>{Address(carrier?.ShipTo, shipment.ShipTo ?? shipment.Customer)}</td></tr>")
            .Append(CultureInfo.InvariantCulture, $"<tr><th>Shipment</th><td>{shipment.ShipmentId}{(shipment.Reference is null ? string.Empty : " — " + E(shipment.Reference))}</td><th>Freight terms</th><td>Prepaid</td></tr></table>")
            .Append("<h2>Items</h2><table><tr><th>Part</th><th>Lot</th><th>Quantity</th></tr>");
        foreach (var line in shipment.Lines)
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(line.PartNumber)}</td><td>{E(line.LotNumber)}</td><td>{line.Quantity}</td></tr>");
        html.Append("</table><h2>Handling units</h2><table><tr><th>#</th><th>Weight (lb)</th><th>Dimensions (in)</th></tr>");
        var n = 0;
        foreach (var p in carrier?.Packages ?? [])
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>{++n}</td><td>{p.WeightLb}</td><td>{(p.LengthIn is null ? string.Empty : $"{p.LengthIn} × {p.WidthIn} × {p.HeightIn}")}</td></tr>");
        html.Append(CultureInfo.InvariantCulture, $"<tr><th>Total</th><th>{(carrier?.Packages ?? []).Sum(p => p.WeightLb)}</th><td></td></tr></table>")
            .Append("<table style=\"margin-top:16px\"><tr><th>Shipper signature / date</th><th>Carrier signature / pickup date</th><th>Consignee signature / date</th></tr><tr><td class=\"sig\"></td><td class=\"sig\"></td><td class=\"sig\"></td></tr></table>");
        return html.ToString();
    }
}

/// <summary>Checks carrier tracking for labelled and moving shipments every two hours (owner decisions 2026-11-01).</summary>
public sealed class CarrierTrackingMonitor(IKernelUnitOfWorkFactory unitOfWork, CarrierGateway gateway, TimeProvider time, ILogger<CarrierTrackingMonitor> logger) : BackgroundService
{
    private static readonly ForgePrincipal SystemPrincipal = new(new PersonId(Guid.Empty), "Carrier tracking", true,
        new HashSet<string>(StringComparer.Ordinal) { Roles.CarrierTracking }, new HashSet<ProgramId>(), DataMarking.None);

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<long> due;
        await using (var uow = await unitOfWork.BeginSnapshotAsync(ct))
        {
            if (!await uow.IsOnAsync(SiteFeatures.ShippingCarriers, ct)) return 0;
            var cutoff = time.GetUtcNow().AddDays(-30);
            due = (await uow.Store<ICarrierLookup>().ListShipmentCarriersAsync(ct))
                .Where(c => c.AccountCode is not null && c.Tracking is not null && c.Status is CarrierShipmentStatus.Labeled or CarrierShipmentStatus.InTransit or CarrierShipmentStatus.Exception
                    && c.UpdatedAt > cutoff)
                .Select(c => c.ShipmentId).ToList();
        }

        var changed = 0;
        foreach (var shipmentId in due)
        {
            var context = new CommandContext(SystemPrincipal, ForgeIds.New(time), string.Create(CultureInfo.InvariantCulture, $"system:tracking:{shipmentId}:{ForgeIds.New(time):N}"),
                CommandSource.System, "Carrier tracking check", SystemActors.CarrierTracking);
            try
            {
                if (await gateway.TrackAsync(shipmentId, context, ct) is { Code: not "no_change" }) changed++;
            }
            catch (Exception ex) when (ex is CommandRejectedException or HttpRequestException)
            {
                logger.LogWarning("Tracking shipment {Shipment} failed: {Message}", shipmentId, ex.Message);
            }
        }

        return changed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(2), time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Carrier tracking failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
