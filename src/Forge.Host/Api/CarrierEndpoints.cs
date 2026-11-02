using System.Globalization;
using System.Text.Json;
using Forge.Host.Security;
using Forge.Host.Shipping;
using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

/// <param name="Credentials">clientId and clientSecret (UPS, FedEx, USPS) or apiKey (ShipEngine, EasyPost); omitted keeps what is stored.</param>
public sealed record CarrierAccountRequest(string Code, CarrierKind Kind, string Name, string? AccountNumber, bool Sandbox, bool Active, Dictionary<string, string>? Settings,
    Dictionary<string, string>? Credentials, string? Reason);

public sealed record CarrierRateRequest(string? AccountCode, CarrierAddress ShipTo, CarrierPackage[] Packages);

public sealed record CarrierLabelRequest(CarrierRate Rate, CarrierAddress ShipTo, CarrierPackage[] Packages, string? Reason);

public sealed record ManualCarrierRequest(string Carrier, string? Tracking, string? ProNumber, string? BolNumber, CarrierAddress? ShipTo, CarrierPackage[]? Packages, string? Reason);

/// <summary>One shipment with its carrier side, for the Carriers page.</summary>
public sealed record CarrierShipmentView(ShipmentInfo Shipment, ShipmentCarrierInfo? Carrier);

/// <summary>Shipping carriers (owner decisions 2026-11-01): accounts, rates, labels, bills of lading and tracking.</summary>
internal static class CarrierEndpoints
{
    public static void MapCarrierEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/carriers").RequireAuthorization();
        api.MapGet("/accounts", (HttpContext http, CancellationToken _) =>
            ReadStore<ICarrierLookup>(http, Permissions.InventoryRead, async (l, ct) => await l.ListCarrierAccountsAsync(ct)));
        api.MapPost("/accounts", (HttpContext http, CarrierAccountRequest b, CarrierService s, ISecretProtector secrets, CancellationToken ct) =>
        {
            var credentials = b.Credentials?.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value.Trim(), StringComparer.Ordinal);
            var encrypted = credentials is { Count: > 0 } ? secrets.Protect(JsonSerializer.Serialize(credentials)) : null;
            return Command(http, b.Reason, c => s.SetAccountAsync(new SetCarrierAccount(b.Code, b.Kind, b.Name, b.AccountNumber, b.Sandbox, b.Active, b.Settings, encrypted), c, ct));
        });
        api.MapGet("/shipments", (HttpContext http, CancellationToken _) =>
            Read(http, Permissions.InventoryRead, async principal =>
            {
                var ct = http.RequestAborted;
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(ct);
                var carriers = (await uow.Store<ICarrierLookup>().ListShipmentCarriersAsync(ct)).ToDictionary(c => c.ShipmentId);
                return (await uow.Store<IInventoryLookup>().ListShipmentsAsync(true, ct)).Where(s => s.Status != ShipmentStatus.Cancelled).Take(300)
                    .Select(s => new CarrierShipmentView(s, carriers.GetValueOrDefault(s.ShipmentId))).ToList();
            }));
        api.MapPost("/shipments/{id:long}/rates", (HttpContext http, long id, CarrierRateRequest b, CarrierGateway gateway, CancellationToken _) =>
            Read(http, Permissions.ShippingRelease, async principal => await gateway.RateAsync(id, b.AccountCode, b.ShipTo, b.Packages ?? [], http.RequestAborted)));
        api.MapPost("/shipments/{id:long}/label", (HttpContext http, long id, CarrierLabelRequest b, CarrierGateway gateway, CancellationToken ct) =>
            Command(http, b.Reason, c => gateway.BuyLabelAsync(id, b.Rate, b.ShipTo, b.Packages ?? [], c, ct)));
        api.MapPost("/shipments/{id:long}/manual", (HttpContext http, long id, ManualCarrierRequest b, CarrierService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordManualAsync(new RecordManualCarrier(id, b.Carrier, b.Tracking, b.ProNumber, b.BolNumber, b.ShipTo, b.Packages), c, ct)));
        api.MapPost("/shipments/{id:long}/void", (HttpContext http, long id, ReasonRequest b, CarrierService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.VoidLabelAsync(new VoidCarrierLabel(id), c, ct)));
        api.MapPost("/shipments/{id:long}/track", (HttpContext http, long id, ReasonRequest b, CarrierGateway gateway, CancellationToken ct) =>
            Command(http, b.Reason ?? "Check tracking", async c => await gateway.TrackAsync(id, c, ct) ?? CommandResult.NoChange(id.ToString(CultureInfo.InvariantCulture), "Nothing to track.")));
        api.MapGet("/shipments/{id:long}/label", async (HttpContext http, long id, IKernelUnitOfWorkFactory unitOfWork, CancellationToken ct) =>
            await DownloadAsync(http, Permissions.InventoryRead, async () =>
            {
                await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
                var label = await uow.Store<ICarrierLookup>().ReadLabelAsync(id, ct) ?? throw new CommandRejectedException("no_label", $"Shipment {id} has no label.");
                var extension = label.ContentType switch { "application/pdf" => "pdf", "image/gif" => "gif", "image/png" => "png", _ => "bin" };
                return Results.File(label.Content, label.ContentType, string.Create(CultureInfo.InvariantCulture, $"label-{id}.{extension}"));
            }));
        api.MapGet("/shipments/{id:long}/bol", async (HttpContext http, long id, IKernelUnitOfWorkFactory unitOfWork, TimeProvider time, CancellationToken ct) =>
            await DownloadAsync(http, Permissions.InventoryRead, async () =>
            {
                await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
                var shipment = (await uow.Store<IInventoryLookup>().ListShipmentsAsync(true, ct)).FirstOrDefault(s => s.ShipmentId == id)
                    ?? throw new CommandRejectedException("shipment_not_found", $"Shipment {id} does not exist.");
                var lookup = uow.Store<ICarrierLookup>();
                var carrier = await lookup.FindShipmentCarrierAsync(id, ct);
                var account = (await lookup.ListCarrierAccountsAsync(ct)).FirstOrDefault(a => a.Code == carrier?.AccountCode)
                    ?? (await lookup.ListCarrierAccountsAsync(ct)).FirstOrDefault(a => a.Active && a.Settings.ContainsKey("fromStreet1"));
                CarrierAddress? from = account is null ? null : new CarrierAddress(Get(account, "fromName"), account.Settings.GetValueOrDefault("fromCompany"), Get(account, "fromStreet1"),
                    account.Settings.GetValueOrDefault("fromStreet2"), Get(account, "fromCity"), Get(account, "fromState"), Get(account, "fromPostal"),
                    account.Settings.GetValueOrDefault("fromCountry") ?? "US", account.Settings.GetValueOrDefault("fromPhone"));
                http.Response.Headers["Content-Security-Policy"] = "sandbox";
                return Results.Content(CarrierGateway.BillOfLading(shipment, carrier, from, time.GetUtcNow()), "text/html; charset=utf-8");
            }));
    }

    private static string Get(CarrierAccountInfo account, string key) => account.Settings.GetValueOrDefault(key) ?? string.Empty;

    private static async Task<IResult> DownloadAsync(HttpContext http, string permission, Func<Task<IResult>> produce)
    {
        var principal = await PrincipalAsync(http);
        if (principal is null) return NotRegistered();
        var decision = http.RequestServices.GetRequiredService<IForgeAuthorizer>().Authorize(principal, permission, ResourceRef.Of(permission));
        if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision);
        return await produce();
    }
}
