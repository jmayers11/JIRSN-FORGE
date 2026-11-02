using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Inventory.Contracts;
using Forge.Kernel.Commands;

namespace Forge.Host.Shipping;

/// <summary>What to quote or label: from the account's ship-from address to <see cref="ShipTo"/>.</summary>
public sealed record CarrierRequest(CarrierAddress ShipFrom, CarrierAddress ShipTo, IReadOnlyList<CarrierPackage> Packages, string Reference);

/// <param name="RateReference">What the connector needs to buy this exact rate (a ShipEngine rate id, an EasyPost shipment and rate id).</param>
public sealed record CarrierRate(string AccountCode, string Carrier, string ServiceCode, string ServiceName, decimal Amount, string Currency, int? TransitDays, string? RateReference);

public sealed record CarrierLabel(string Tracking, decimal? Cost, string? Currency, byte[]? Label, string? ContentType, string? LabelReference);

public sealed record CarrierTracking(CarrierShipmentStatus Status, string Summary, DateTimeOffset? DeliveredAt);

/// <summary>One carrier account, ready to call: its settings, decrypted credentials and the transport.</summary>
public sealed record CarrierAccountContext(CarrierAccountInfo Account, IReadOnlyDictionary<string, string> Credentials, ICarrierTransport Transport, TimeProvider Time)
{
    public string Setting(string key) => Account.Settings.TryGetValue(key, out var v) ? v : string.Empty;

    public string Credential(string key) =>
        Credentials.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new CommandRejectedException("credentials_missing", $"Carrier account {Account.Code} has no {key}.");

    public string BaseUrl(string production, string sandbox) => (Account.Settings.TryGetValue("baseUrl", out var url) ? url : Account.Sandbox ? sandbox : production).TrimEnd('/');
}

/// <summary>Sends carrier API requests. Tests replace it with a simulator; real accounts use the named HTTP client "forge-carriers".</summary>
public interface ICarrierTransport
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);
}

public sealed class HttpCarrierTransport(IHttpClientFactory clients) : ICarrierTransport
{
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => clients.CreateClient("forge-carriers").SendAsync(request, ct);
}

/// <summary>A carrier connection (owner decision 2026-11-01: UPS, FedEx, USPS, ShipEngine and EasyPost loaded and ready).</summary>
public interface ICarrierConnector
{
    CarrierKind Kind { get; }

    Task<IReadOnlyList<CarrierRate>> RateAsync(CarrierAccountContext a, CarrierRequest r, CancellationToken ct);

    Task<CarrierLabel> LabelAsync(CarrierAccountContext a, CarrierRequest r, CarrierRate rate, CancellationToken ct);

    Task<CarrierTracking> TrackAsync(CarrierAccountContext a, string tracking, string? labelReference, CancellationToken ct);
}

internal static class CarrierHttp
{
    public static async Task<JsonNode> SendJsonAsync(CarrierAccountContext account, HttpMethod method, string url, JsonNode? body, Action<HttpRequestMessage>? headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        headers?.Invoke(request);
        using var response = await account.Transport.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new CommandRejectedException("carrier_error", $"{account.Account.Kind} answered {(int)response.StatusCode}: {Trim(text)}");
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) ?? new JsonObject();
        }
        catch (JsonException)
        {
            throw new CommandRejectedException("carrier_error", $"{account.Account.Kind} answered something that is not JSON: {Trim(text)}");
        }
    }

    public static async Task<string> FormTokenAsync(CarrierAccountContext account, string url, IEnumerable<KeyValuePair<string, string>> form, string? basicUser, string? basicPassword, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        if (basicUser is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basicUser}:{basicPassword}")));
        using var response = await account.Transport.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new CommandRejectedException("carrier_auth", $"{account.Account.Kind} refused the credentials ({(int)response.StatusCode}): {Trim(text)}");
        return JsonNode.Parse(text)?["access_token"]?.GetValue<string>() ?? throw new CommandRejectedException("carrier_auth", $"{account.Account.Kind} returned no access token.");
    }

    public static string Trim(string text) => text.Length > 300 ? text[..300] + "…" : text;

    public static decimal Money(JsonNode? node) =>
        node is null ? 0 : node.GetValueKind() == JsonValueKind.Number ? node.GetValue<decimal>() : decimal.Parse(node.GetValue<string>(), NumberStyles.Number, CultureInfo.InvariantCulture);

    public static string S(JsonNode? node) => node is null ? string.Empty : node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();

    /// <summary>A node that is either one object or an array of them, as the first element.</summary>
    public static JsonNode? First(JsonNode? node) => node is JsonArray a ? (a.Count > 0 ? a[0] : null) : node;

    public static IEnumerable<JsonNode> Many(JsonNode? node) => node switch
    {
        JsonArray a => a.OfType<JsonNode>(),
        null => [],
        _ => [node],
    };

    public static string N(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static CarrierAddress From(CarrierAccountContext a) => new(
        Required(a, "fromName"), a.Setting("fromCompany") is { Length: > 0 } c ? c : null, Required(a, "fromStreet1"), a.Setting("fromStreet2") is { Length: > 0 } s ? s : null,
        Required(a, "fromCity"), Required(a, "fromState"), Required(a, "fromPostal"), a.Setting("fromCountry") is { Length: > 0 } country ? country : "US", a.Setting("fromPhone"));

    private static string Required(CarrierAccountContext a, string key) =>
        a.Setting(key) is { Length: > 0 } v ? v : throw new CommandRejectedException("ship_from_missing", $"Carrier account {a.Account.Code} has no {key} (its ship-from address).");

    public static CarrierShipmentStatus Status(string text)
    {
        var t = text.ToUpperInvariant();
        if (t.Contains("DELIVERED", StringComparison.Ordinal) && !t.Contains("NOT DELIVERED", StringComparison.Ordinal) && !t.Contains("UNDELIVER", StringComparison.Ordinal))
            return CarrierShipmentStatus.Delivered;
        if (t.Contains("EXCEPTION", StringComparison.Ordinal) || t.Contains("RETURN", StringComparison.Ordinal) || t.Contains("UNDELIVER", StringComparison.Ordinal)
            || t.Contains("FAILURE", StringComparison.Ordinal) || t is "EX" or "ERROR")
            return CarrierShipmentStatus.Exception;
        if (t.Length == 0 || t.Contains("LABEL", StringComparison.Ordinal) || t.Contains("PRE_TRANSIT", StringComparison.Ordinal) || t.Contains("PRE-SHIPMENT", StringComparison.Ordinal)
            || t is "UN" or "AC" or "UNKNOWN")
            return CarrierShipmentStatus.Labeled;
        return CarrierShipmentStatus.InTransit;
    }
}

/// <summary>UPS: OAuth client credentials, Rating (Shop), Shipping and Tracking APIs.</summary>
public sealed class UpsConnector : ICarrierConnector
{
    public CarrierKind Kind => CarrierKind.Ups;

    private static string Base(CarrierAccountContext a) => a.BaseUrl("https://onlinetools.ups.com", "https://wwwcie.ups.com");

    private static async Task<Action<HttpRequestMessage>> AuthAsync(CarrierAccountContext a, CancellationToken ct)
    {
        var token = await CarrierHttp.FormTokenAsync(a, Base(a) + "/security/v1/oauth/token", [new("grant_type", "client_credentials")], a.Credential("clientId"), a.Credential("clientSecret"), ct);
        return r =>
        {
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            r.Headers.Add("transId", Forge.Kernel.Identity.ForgeIds.New(a.Time).ToString("N"));
            r.Headers.Add("transactionSrc", "JISRN Forge");
        };
    }

    private static JsonObject Party(CarrierAddress x, string? number = null)
    {
        var o = new JsonObject
        {
            ["Name"] = x.Company ?? x.Name,
            ["AttentionName"] = x.Name,
            ["Address"] = new JsonObject
            {
                ["AddressLine"] = new JsonArray(new[] { x.Street1, x.Street2 }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => (JsonNode)s!).ToArray()),
                ["City"] = x.City, ["StateProvinceCode"] = x.State, ["PostalCode"] = x.PostalCode, ["CountryCode"] = x.Country,
            },
        };
        if (x.Phone is { Length: > 0 }) o["Phone"] = new JsonObject { ["Number"] = x.Phone };
        if (number is not null) o["ShipperNumber"] = number;
        return o;
    }

    private static JsonArray Packages(CarrierRequest r, string packagingKey) => new(r.Packages.Select(p =>
    {
        var o = new JsonObject
        {
            [packagingKey] = new JsonObject { ["Code"] = "02" },
            ["PackageWeight"] = new JsonObject { ["UnitOfMeasurement"] = new JsonObject { ["Code"] = "LBS" }, ["Weight"] = CarrierHttp.N(p.WeightLb) },
        };
        if (p is { LengthIn: { } l, WidthIn: { } w, HeightIn: { } h })
            o["Dimensions"] = new JsonObject { ["UnitOfMeasurement"] = new JsonObject { ["Code"] = "IN" }, ["Length"] = CarrierHttp.N(l), ["Width"] = CarrierHttp.N(w), ["Height"] = CarrierHttp.N(h) };
        return (JsonNode)o;
    }).ToArray());

    public async Task<IReadOnlyList<CarrierRate>> RateAsync(CarrierAccountContext a, CarrierRequest r, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["RateRequest"] = new JsonObject
            {
                ["Request"] = new JsonObject { ["RequestOption"] = "Shop" },
                ["Shipment"] = new JsonObject
                {
                    ["Shipper"] = Party(r.ShipFrom, a.Account.AccountNumber), ["ShipFrom"] = Party(r.ShipFrom), ["ShipTo"] = Party(r.ShipTo),
                    ["Package"] = Packages(r, "PackagingType"),
                },
            },
        };
        var reply = await CarrierHttp.SendJsonAsync(a, HttpMethod.Post, Base(a) + "/api/rating/v2403/Shop", body, await AuthAsync(a, ct), ct);
        return CarrierHttp.Many(reply["RateResponse"]?["RatedShipment"]).Select(s =>
        {
            var code = CarrierHttp.S(s["Service"]?["Code"]);
            var charges = s["NegotiatedRateCharges"]?["TotalCharge"] ?? s["TotalCharges"];
            int? days = int.TryParse(CarrierHttp.S(s["GuaranteedDelivery"]?["BusinessDaysInTransit"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : null;
            return new CarrierRate(a.Account.Code, "UPS", code, UpsServiceName(code), CarrierHttp.Money(charges?["MonetaryValue"]), CarrierHttp.S(charges?["CurrencyCode"]) is { Length: > 0 } c ? c : "USD", days, null);
        }).OrderBy(x => x.Amount).ToList();
    }

    public async Task<CarrierLabel> LabelAsync(CarrierAccountContext a, CarrierRequest r, CarrierRate rate, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["ShipmentRequest"] = new JsonObject
            {
                ["Request"] = new JsonObject { ["RequestOption"] = "nonvalidate" },
                ["Shipment"] = new JsonObject
                {
                    ["Description"] = r.Reference,
                    ["Shipper"] = Party(r.ShipFrom, a.Account.AccountNumber), ["ShipFrom"] = Party(r.ShipFrom), ["ShipTo"] = Party(r.ShipTo),
                    ["PaymentInformation"] = new JsonObject { ["ShipmentCharge"] = new JsonObject { ["Type"] = "01", ["BillShipper"] = new JsonObject { ["AccountNumber"] = a.Account.AccountNumber } } },
                    ["Service"] = new JsonObject { ["Code"] = rate.ServiceCode },
                    ["ReferenceNumber"] = new JsonObject { ["Value"] = r.Reference },
                    ["Package"] = Packages(r, "Packaging"),
                },
                ["LabelSpecification"] = new JsonObject { ["LabelImageFormat"] = new JsonObject { ["Code"] = "GIF" } },
            },
        };
        var reply = await CarrierHttp.SendJsonAsync(a, HttpMethod.Post, Base(a) + "/api/shipments/v2409/ship", body, await AuthAsync(a, ct), ct);
        var results = reply["ShipmentResponse"]?["ShipmentResults"];
        var package = CarrierHttp.First(results?["PackageResults"]);
        var tracking = CarrierHttp.S(package?["TrackingNumber"]) is { Length: > 0 } t ? t : CarrierHttp.S(results?["ShipmentIdentificationNumber"]);
        var image = CarrierHttp.S(package?["ShippingLabel"]?["GraphicImage"]);
        var charges = results?["NegotiatedRateCharges"]?["TotalCharge"] ?? results?["ShipmentCharges"]?["TotalCharges"];
        return new CarrierLabel(tracking, charges is null ? rate.Amount : CarrierHttp.Money(charges["MonetaryValue"]), CarrierHttp.S(charges?["CurrencyCode"]) is { Length: > 0 } c ? c : rate.Currency,
            image.Length > 0 ? Convert.FromBase64String(image) : null, image.Length > 0 ? "image/gif" : null, CarrierHttp.S(results?["ShipmentIdentificationNumber"]));
    }

    public async Task<CarrierTracking> TrackAsync(CarrierAccountContext a, string tracking, string? labelReference, CancellationToken ct)
    {
        var reply = await CarrierHttp.SendJsonAsync(a, HttpMethod.Get, Base(a) + "/api/track/v1/details/" + Uri.EscapeDataString(tracking), null, await AuthAsync(a, ct), ct);
        var package = CarrierHttp.First(CarrierHttp.First(reply["trackResponse"]?["shipment"])?["package"]);
        var status = package?["currentStatus"]?["description"] ?? CarrierHttp.First(package?["activity"])?["status"]?["description"];
        var text = CarrierHttp.S(status).Trim();
        var kind = CarrierHttp.Status(CarrierHttp.S(CarrierHttp.First(package?["activity"])?["status"]?["type"]) is "D" ? "DELIVERED" : text);
        return new CarrierTracking(kind, text.Length > 0 ? text : "No tracking yet", kind == CarrierShipmentStatus.Delivered ? a.Time.GetUtcNow() : null);
    }

    private static string UpsServiceName(string code) => code switch
    {
        "01" => "UPS Next Day Air", "02" => "UPS 2nd Day Air", "03" => "UPS Ground", "12" => "UPS 3 Day Select", "13" => "UPS Next Day Air Saver", "14" => "UPS Next Day Air Early",
        "59" => "UPS 2nd Day Air A.M.", "07" => "UPS Worldwide Express", "08" => "UPS Worldwide Expedited", "11" => "UPS Standard", "65" => "UPS Worldwide Saver",
        _ => "UPS service " + code,
    };
}

/// <summary>FedEx: OAuth client credentials, Rate, Ship and Track APIs.</summary>
public sealed class FedExConnector : ICarrierConnector
{
    public CarrierKind Kind => CarrierKind.FedEx;

    private static string Base(CarrierAccountContext a) => a.BaseUrl("https://apis.fedex.com", "https://apis-sandbox.fedex.com");

    private static async Task<Action<HttpRequestMessage>> AuthAsync(CarrierAccountContext a, CancellationToken ct)
    {
        var token = await CarrierHttp.FormTokenAsync(a, Base(a) + "/oauth/token",
            [new("grant_type", "client_credentials"), new("client_id", a.Credential("clientId")), new("client_secret", a.Credential("clientSecret"))], null, null, ct);
        return r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static JsonObject Address(CarrierAddress x) => new()
    {
        ["streetLines"] = new JsonArray(new[] { x.Street1, x.Street2 }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => (JsonNode)s!).ToArray()),
        ["city"] = x.City, ["stateOrProvinceCode"] = x.State, ["postalCode"] = x.PostalCode, ["countryCode"] = x.Country,
    };

    private static JsonObject Party(CarrierAddress x) => new()
    {
        ["contact"] = new JsonObject { ["personName"] = x.Name, ["companyName"] = x.Company, ["phoneNumber"] = x.Phone ?? "0000000000" },
        ["address"] = Address(x),
    };

    private static JsonArray Packages(CarrierRequest r) => new(r.Packages.Select(p =>
    {
        var o = new JsonObject { ["weight"] = new JsonObject { ["units"] = "LB", ["value"] = p.WeightLb } };
        if (p is { LengthIn: { } l, WidthIn: { } w, HeightIn: { } h })
            o["dimensions"] = new JsonObject { ["length"] = (int)Math.Ceiling(l), ["width"] = (int)Math.Ceiling(w), ["height"] = (int)Math.Ceiling(h), ["units"] = "IN" };
        return (JsonNode)o;
    }).ToArray());

    public async Task<IReadOnlyList<CarrierRate>> RateAsync(CarrierAccountContext a, CarrierRequest r, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["accountNumber"] = new JsonObject { ["value"] = a.Account.AccountNumber },
            ["requestedShipment"] = new JsonObject
            {
                ["shipper"] = new JsonObject { ["address"] = Address(r.ShipFrom) }, ["recipient"] = new JsonObject { ["address"] = Address(r.ShipTo) },
                ["pickupType"] = "DROPOFF_AT_FEDEX_LOCATION", ["rateRequestType"] = new JsonArray("ACCOUNT", "LIST"), ["requestedPackageLineItems"] = Packages(r),
            },
        };
        var reply = await CarrierHttp.SendJsonAsync(a, HttpMethod.Post, Base(a) + "/rate/v1/rates/quotes", body, await AuthAsync(a, ct), ct);
        return CarrierHttp.Many(reply["output"]?["rateReplyDetails"]).Select(d =>
        {
            var detail = CarrierHttp.First(d["ratedShipmentDetails"]);
            return new CarrierRate(a.Account.Code, "FedEx", CarrierHttp.S(d["serviceType"]), CarrierHttp.S(d["serviceName"]) is { Length: > 0 } n ? n : CarrierHttp.S(d["serviceType"]),
                CarrierHttp.Money(detail?["totalNetCharge"]), CarrierHttp.S(detail?["currency"]) is { Length: > 0 } c ? c : "USD", null, null);
        }).OrderBy(x => x.Amount).ToList();
    }

    public async Task<CarrierLabel> LabelAsync(CarrierAccountContext a, CarrierRequest r, CarrierRate rate, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["labelResponseOptions"] = "LABEL",
            ["accountNumber"] = new JsonObject { ["value"] = a.Account.AccountNumber },
            ["requestedShipment"] = new JsonObject
            {
                ["shipper"] = Party(r.ShipFrom), ["recipients"] = new JsonArray(Party(r.ShipTo)), ["serviceType"] = rate.ServiceCode, ["packagingType"] = "YOUR_PACKAGING",
                ["pickupType"] = "DROPOFF_AT_FEDEX_LOCATION", ["shippingChargesPayment"] = new JsonObject { ["paymentType"] = "SENDER" },
                ["labelSpecification"] = new JsonObject { ["imageType"] = "PDF", ["labelStockType"] = "PAPER_85X11_TOP_HALF_LABEL" },
                ["requestedPackageLineItems"] = Packages(r),
            },
        };
        var reply = await CarrierHttp.SendJsonAsync(a, HttpMethod.Post, Base(a) + "/ship/v1/shipments", body, await AuthAsync(a, ct), ct);
        var shipment = CarrierHttp.First(reply["output"]?["transactionShipments"]);
        var piece = CarrierHttp.First(shipment?["pieceResponses"]);
        var document = CarrierHttp.First(piece?["packageDocuments"]);
        var encoded = CarrierHttp.S(document?["encodedLabel"]);
        var tracking = CarrierHttp.S(shipment?["masterTrackingNumber"]) is { Length: > 0 } t ? t : CarrierHttp.S(piece?["trackingNumber"]);
        var charge = CarrierHttp.First(shipment?["completedShipmentDetail"]?["shipmentRating"]?["shipmentRateDetails"])?["totalNetCharge"];
        return new CarrierLabel(tracking, charge is null ? rate.Amount : CarrierHttp.Money(charge), rate.Currency, encoded.Length > 0 ? Convert.FromBase64String(encoded) : null,
            encoded.Length > 0 ? "application/pdf" : null, tracking);
    }

    public async Task<CarrierTracking> TrackAsync(CarrierAccountContext a, string tracking, string? labelReference, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["includeDetailedScans"] = false,
            ["trackingInfo"] = new JsonArray(new JsonObject { ["trackingNumberInfo"] = new JsonObject { ["trackingNumber"] = tracking } }),
        };
        var reply = await CarrierHttp.SendJsonAsync(a, HttpMethod.Post, Base(a) + "/track/v1/trackingnumbers", body, await AuthAsync(a, ct), ct);
        var result = CarrierHttp.First(CarrierHttp.First(reply["output"]?["completeTrackResults"])?["trackResults"]);
        var latest = result?["latestStatusDetail"];
        var code = CarrierHttp.S(latest?["code"]);
        var text = CarrierHttp.S(latest?["description"]);
        var kind = code switch { "DL" => CarrierShipmentStatus.Delivered, "DE" or "SE" or "RS" => CarrierShipmentStatus.Exception, "OC" or "" => CarrierShipmentStatus.Labeled, _ => CarrierShipmentStatus.InTransit };
        return new CarrierTracking(kind, text.Length > 0 ? text : "No tracking yet", kind == CarrierShipmentStatus.Delivered ? a.Time.GetUtcNow() : null);
    }
}

/// <summary>USPS: the USPS APIs (OAuth), Domestic Prices, Labels (with a payment authorization from the account's CRID and MID) and Tracking.</summary>
public sealed class UspsConnector : ICarrierConnector
{
    private static readonly string[] MailClasses = ["USPS_GROUND_ADVANTAGE", "PRIORITY_MAIL", "PRIORITY_MAIL_EXPRESS"];

    public CarrierKind Kind => CarrierKind.Usps;

    private static string Base(CarrierAccountContext a) => a.BaseUrl("https://apis.usps.com", "https://apis-tem.usps.com");

    private static async Task<string> TokenAsync(CarrierAccountContext a, CancellationToken ct)
    {
        var body = new JsonObject { ["grant_type"] = "client_credentials", ["client_id"] = a.Credential("clientId"), ["client_secret"] = a.Credential("clientSecret") };
        var reply = await CarrierHttp.SendJsonAsync(a, HttpMethod.Post, Base(a) + "/oauth2/v3/token", body, null, ct);
        return CarrierHttp.S(reply["access_token"]) is { Length: > 0 } t ? t : throw new CommandRejectedException("carrier_auth", "USPS returned no access token.");
    }

    private static string Zip(string postal) => postal.Length >= 5 ? postal[..5] : postal;

    public async Task<IReadOnlyList<CarrierRate>> RateAsync(CarrierAccountContext a, CarrierRequest r, CancellationToken ct)
    {
        var token = await TokenAsync(a, ct);
        var package = r.Packages[0];
        var rates = new List<CarrierRate>();
        foreach (var mailClass in MailClasses)
        {
            var body = new JsonObject
            {
                ["originZIPCode"] = Zip(r.ShipFrom.PostalCode), ["destinationZIPCode"] = Zip(r.ShipTo.PostalCode), ["weight"] = package.WeightLb,
                ["length"] = package.LengthIn ?? 6, ["width"] = package.WidthIn ?? 6, ["height"] = package.HeightIn ?? 6,
                ["mailClass"] = mailClass, ["processingCategory"] = "MACHINABLE", ["rateIndicator"] = "SP", ["destinationEntryFacilityType"] = "NONE", ["priceType"] = "COMMERCIAL",
            };
            JsonNode reply;
            try
            {
                reply = await CarrierHttp.SendJsonAsync(a, HttpMethod.Post, Base(a) + "/prices/v3/base-rates/search", body, req => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token), ct);
            }
            catch (CommandRejectedException) when (rates.Count > 0)
            {
                continue; // a mail class this package does not qualify for
            }

            var amount = reply["totalBasePrice"] is { } total ? CarrierHttp.Money(total) : CarrierHttp.Money(CarrierHttp.First(reply["rates"])?["price"]);
            var name = CarrierHttp.S(CarrierHttp.First(reply["rates"])?["description"]);
            rates.Add(new CarrierRate(a.Account.Code, "USPS", mailClass, name.Length > 0 ? name : mailClass.Replace('_', ' '), amount, "USD", null, null));
        }

        return rates.OrderBy(x => x.Amount).ToList();
    }

    public async Task<CarrierLabel> LabelAsync(CarrierAccountContext a, CarrierRequest r, CarrierRate rate, CancellationToken ct)
    {
        var token = await TokenAsync(a, ct);
        void Bearer(HttpRequestMessage req) => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var crid = a.Setting("crid");
        var mid = a.Setting("mid");
        if (crid.Length == 0 || mid.Length == 0) throw new CommandRejectedException("usps_account", "USPS labels need the account's CRID and MID settings.");
        var payer = new JsonObject { ["CRID"] = crid, ["MID"] = mid, ["manifestMID"] = mid, ["accountType"] = "EPS", ["accountNumber"] = a.Account.AccountNumber };
        var authorization = await CarrierHttp.SendJsonAsync(a, HttpMethod.Post, Base(a) + "/payments/v3/payment-authorization",
            new JsonObject { ["roles"] = new JsonArray(payer.DeepClone().AsObject().Also("roleName", "PAYER"), payer.DeepClone().AsObject().Also("roleName", "LABEL_OWNER")) }, Bearer, ct);
        var paymentToken = CarrierHttp.S(authorization["paymentAuthorizationToken"]);
        var package = r.Packages[0];
        static JsonObject Address(CarrierAddress x) => new()
        {
            ["firstName"] = x.Name, ["firm"] = x.Company, ["streetAddress"] = x.Street1, ["secondaryAddress"] = x.Street2, ["city"] = x.City, ["state"] = x.State,
            ["ZIPCode"] = Zip(x.PostalCode),
        };
        var body = new JsonObject
        {
            ["imageInfo"] = new JsonObject { ["imageType"] = "PDF", ["labelType"] = "4X6LABEL", ["receiptOption"] = "NONE" },
            ["toAddress"] = Address(r.ShipTo), ["fromAddress"] = Address(r.ShipFrom),
            ["packageDescription"] = new JsonObject
            {
                ["mailClass"] = rate.ServiceCode, ["rateIndicator"] = "SP", ["weightUOM"] = "lb", ["weight"] = package.WeightLb, ["dimensionsUOM"] = "in",
                ["length"] = package.LengthIn ?? 6, ["width"] = package.WidthIn ?? 6, ["height"] = package.HeightIn ?? 6, ["processingCategory"] = "MACHINABLE",
                ["mailingDate"] = a.Time.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["destinationEntryFacilityType"] = "NONE",
                ["customerReference"] = new JsonArray(new JsonObject { ["referenceNumber"] = r.Reference }),
            },
        };
        var reply = await CarrierHttp.SendJsonAsync(a, HttpMethod.Post, Base(a) + "/labels/v3/label", body, req =>
        {
            Bearer(req);
            req.Headers.Add("X-Payment-Authorization-Token", paymentToken);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.usps.labels+json"));
        }, ct);
        var meta = reply["labelMetadata"] ?? reply;
        var image = CarrierHttp.S(reply["labelImage"]);
        var tracking = CarrierHttp.S(meta["trackingNumber"]);
        return new CarrierLabel(tracking, meta["postage"] is { } postage ? CarrierHttp.Money(postage) : rate.Amount, "USD", image.Length > 0 ? Convert.FromBase64String(image) : null,
            image.Length > 0 ? "application/pdf" : null, tracking);
    }

    public async Task<CarrierTracking> TrackAsync(CarrierAccountContext a, string tracking, string? labelReference, CancellationToken ct)
    {
        var token = await TokenAsync(a, ct);
        var reply = await CarrierHttp.SendJsonAsync(a, HttpMethod.Get, Base(a) + "/tracking/v3/tracking/" + Uri.EscapeDataString(tracking) + "?expand=SUMMARY", null,
            req => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token), ct);
        var category = CarrierHttp.S(reply["statusCategory"]);
        var text = CarrierHttp.S(reply["statusSummary"]) is { Length: > 0 } s ? s : CarrierHttp.S(reply["status"]);
        var kind = CarrierHttp.Status(category.Length > 0 ? category : text);
        return new CarrierTracking(kind, text.Length > 0 ? text : "No tracking yet", kind == CarrierShipmentStatus.Delivered ? a.Time.GetUtcNow() : null);
    }
}

/// <summary>ShipEngine: one API key for the carriers connected to the ShipEngine account (setting carrierIds).</summary>
public sealed class ShipEngineConnector : ICarrierConnector
{
    public CarrierKind Kind => CarrierKind.ShipEngine;

    private static string Base(CarrierAccountContext a) => a.BaseUrl("https://api.shipengine.com", "https://api.shipengine.com");

    private static Action<HttpRequestMessage> Key(CarrierAccountContext a) => r => r.Headers.Add("API-Key", a.Credential("apiKey"));

    private static JsonObject Address(CarrierAddress x) => new()
    {
        ["name"] = x.Name, ["company_name"] = x.Company, ["phone"] = x.Phone ?? "0000000000", ["address_line1"] = x.Street1, ["address_line2"] = x.Street2, ["city_locality"] = x.City,
        ["state_province"] = x.State, ["postal_code"] = x.PostalCode, ["country_code"] = x.Country,
    };

    private static JsonArray Packages(CarrierRequest r) => new(r.Packages.Select(p =>
    {
        var o = new JsonObject { ["weight"] = new JsonObject { ["value"] = p.WeightLb, ["unit"] = "pound" } };
        if (p is { LengthIn: { } l, WidthIn: { } w, HeightIn: { } h })
            o["dimensions"] = new JsonObject { ["unit"] = "inch", ["length"] = l, ["width"] = w, ["height"] = h };
        return (JsonNode)o;
    }).ToArray());

    public async Task<IReadOnlyList<CarrierRate>> RateAsync(CarrierAccountContext a, CarrierRequest r, CancellationToken ct)
    {
        var carriers = a.Setting("carrierIds").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (carriers.Length == 0) throw new CommandRejectedException("shipengine_carriers", "Set the ShipEngine account's carrierIds (comma separated) to the carriers it should quote.");
        var body = new JsonObject
        {
            ["rate_options"] = new JsonObject { ["carrier_ids"] = new JsonArray(carriers.Select(c => (JsonNode)c).ToArray()) },
            ["shipment"] = new JsonObject { ["ship_from"] = Address(r.ShipFrom), ["ship_to"] = Address(r.ShipTo), ["packages"] = Packages(r) },
        };
        var reply = await CarrierHttp.SendJsonAsync(a, HttpMethod.Post, Base(a) + "/v1/rates", body, Key(a), ct);
        return CarrierHttp.Many(reply["rate_response"]?["rates"]).Select(x => new CarrierRate(a.Account.Code,
                CarrierHttp.S(x["carrier_friendly_name"]) is { Length: > 0 } n ? n : CarrierHttp.S(x["carrier_code"]), CarrierHttp.S(x["service_code"]), CarrierHttp.S(x["service_type"]),
                CarrierHttp.Money(x["shipping_amount"]?["amount"]) + CarrierHttp.Money(x["other_amount"]?["amount"]),
                CarrierHttp.S(x["shipping_amount"]?["currency"]).ToUpperInvariant() is { Length: > 0 } c ? c : "USD",
                x["delivery_days"] is { } d && d.GetValueKind() == JsonValueKind.Number ? d.GetValue<int>() : null, CarrierHttp.S(x["rate_id"])))
            .OrderBy(x => x.Amount).ToList();
    }

    public async Task<CarrierLabel> LabelAsync(CarrierAccountContext a, CarrierRequest r, CarrierRate rate, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rate.RateReference)) throw new CommandRejectedException("rate_required", "Quote ShipEngine rates first and choose one.");
        var reply = await CarrierHttp.SendJsonAsync(a, HttpMethod.Post, Base(a) + "/v1/labels/rates/" + Uri.EscapeDataString(rate.RateReference),
            new JsonObject { ["label_format"] = "pdf", ["label_layout"] = "4x6" }, Key(a), ct);
        var href = CarrierHttp.S(reply["label_download"]?["pdf"]) is { Length: > 0 } pdf ? pdf : CarrierHttp.S(reply["label_download"]?["href"]);
        byte[]? label = null;
        if (href.Length > 0)
        {
            using var download = new HttpRequestMessage(HttpMethod.Get, href);
            Key(a)(download);
            using var response = await a.Transport.SendAsync(download, ct);
            if (response.IsSuccessStatusCode) label = await response.Content.ReadAsByteArrayAsync(ct);
        }

        return new CarrierLabel(CarrierHttp.S(reply["tracking_number"]), reply["shipment_cost"] is { } cost ? CarrierHttp.Money(cost["amount"]) : rate.Amount, rate.Currency, label,
            label is null ? null : "application/pdf", CarrierHttp.S(reply["label_id"]));
    }

    public async Task<CarrierTracking> TrackAsync(CarrierAccountContext a, string tracking, string? labelReference, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(labelReference)) return new CarrierTracking(CarrierShipmentStatus.Labeled, "No ShipEngine label to track", null);
        var reply = await CarrierHttp.SendJsonAsync(a, HttpMethod.Get, Base(a) + "/v1/labels/" + Uri.EscapeDataString(labelReference) + "/track", null, Key(a), ct);
        var code = CarrierHttp.S(reply["status_code"]);
        var text = CarrierHttp.S(reply["status_description"]);
        var kind = code switch { "DE" => CarrierShipmentStatus.Delivered, "EX" => CarrierShipmentStatus.Exception, "IT" or "AT" => CarrierShipmentStatus.InTransit, _ => CarrierShipmentStatus.Labeled };
        DateTimeOffset? delivered = DateTimeOffset.TryParse(CarrierHttp.S(reply["actual_delivery_date"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        return new CarrierTracking(kind, text.Length > 0 ? text : code, kind == CarrierShipmentStatus.Delivered ? delivered ?? a.Time.GetUtcNow() : null);
    }
}

/// <summary>EasyPost: one API key; a shipment returns its rates and is bought at one of them.</summary>
public sealed class EasyPostConnector : ICarrierConnector
{
    public CarrierKind Kind => CarrierKind.EasyPost;

    private static string Base(CarrierAccountContext a) => a.BaseUrl("https://api.easypost.com", "https://api.easypost.com");

    private static Action<HttpRequestMessage> Key(CarrierAccountContext a) => r =>
        r.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(a.Credential("apiKey") + ":")));

    private static JsonObject Address(CarrierAddress x) => new()
    {
        ["name"] = x.Name, ["company"] = x.Company, ["street1"] = x.Street1, ["street2"] = x.Street2, ["city"] = x.City, ["state"] = x.State, ["zip"] = x.PostalCode,
        ["country"] = x.Country, ["phone"] = x.Phone,
    };

    public async Task<IReadOnlyList<CarrierRate>> RateAsync(CarrierAccountContext a, CarrierRequest r, CancellationToken ct)
    {
        var p = r.Packages[0];
        var parcel = new JsonObject { ["weight"] = Math.Round(p.WeightLb * 16m, 1) };
        if (p is { LengthIn: { } l, WidthIn: { } w, HeightIn: { } h })
        {
            parcel["length"] = l;
            parcel["width"] = w;
            parcel["height"] = h;
        }

        var body = new JsonObject
        {
            ["shipment"] = new JsonObject { ["to_address"] = Address(r.ShipTo), ["from_address"] = Address(r.ShipFrom), ["parcel"] = parcel, ["reference"] = r.Reference },
        };
        var reply = await CarrierHttp.SendJsonAsync(a, HttpMethod.Post, Base(a) + "/v2/shipments", body, Key(a), ct);
        var shipmentId = CarrierHttp.S(reply["id"]);
        return CarrierHttp.Many(reply["rates"]).Select(x => new CarrierRate(a.Account.Code, CarrierHttp.S(x["carrier"]), CarrierHttp.S(x["service"]),
                $"{CarrierHttp.S(x["carrier"])} {CarrierHttp.S(x["service"])}", CarrierHttp.Money(x["rate"]), CarrierHttp.S(x["currency"]) is { Length: > 0 } c ? c : "USD",
                x["delivery_days"] is { } d && d.GetValueKind() == JsonValueKind.Number ? d.GetValue<int>() : null, $"{shipmentId}|{CarrierHttp.S(x["id"])}"))
            .OrderBy(x => x.Amount).ToList();
    }

    public async Task<CarrierLabel> LabelAsync(CarrierAccountContext a, CarrierRequest r, CarrierRate rate, CancellationToken ct)
    {
        var parts = (rate.RateReference ?? string.Empty).Split('|');
        if (parts.Length != 2) throw new CommandRejectedException("rate_required", "Quote EasyPost rates first and choose one.");
        var reply = await CarrierHttp.SendJsonAsync(a, HttpMethod.Post, Base(a) + "/v2/shipments/" + Uri.EscapeDataString(parts[0]) + "/buy",
            new JsonObject { ["rate"] = new JsonObject { ["id"] = parts[1] } }, Key(a), ct);
        var url = CarrierHttp.S(reply["postage_label"]?["label_url"]);
        byte[]? label = null;
        string? type = null;
        if (url.Length > 0)
        {
            using var download = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await a.Transport.SendAsync(download, ct);
            if (response.IsSuccessStatusCode)
            {
                label = await response.Content.ReadAsByteArrayAsync(ct);
                type = response.Content.Headers.ContentType?.MediaType ?? "image/png";
            }
        }

        return new CarrierLabel(CarrierHttp.S(reply["tracking_code"]), reply["selected_rate"]?["rate"] is { } paid ? CarrierHttp.Money(paid) : rate.Amount, rate.Currency, label, type, parts[0]);
    }

    public async Task<CarrierTracking> TrackAsync(CarrierAccountContext a, string tracking, string? labelReference, CancellationToken ct)
    {
        var reply = await CarrierHttp.SendJsonAsync(a, HttpMethod.Post, Base(a) + "/v2/trackers", new JsonObject { ["tracker"] = new JsonObject { ["tracking_code"] = tracking } }, Key(a), ct);
        var status = CarrierHttp.S(reply["status"]);
        var detail = CarrierHttp.S(reply["status_detail"]);
        var kind = status switch
        {
            "delivered" => CarrierShipmentStatus.Delivered,
            "failure" or "return_to_sender" or "error" or "cancelled" => CarrierShipmentStatus.Exception,
            "pre_transit" or "unknown" or "" => CarrierShipmentStatus.Labeled,
            _ => CarrierShipmentStatus.InTransit,
        };
        var text = string.Join(" — ", new[] { status.Replace('_', ' '), detail.Replace('_', ' ') }.Where(s => s.Length > 0));
        return new CarrierTracking(kind, text.Length > 0 ? text : "No tracking yet", kind == CarrierShipmentStatus.Delivered ? a.Time.GetUtcNow() : null);
    }
}

internal static class JsonObjectExtensions
{
    public static JsonObject Also(this JsonObject o, string key, string value)
    {
        o[key] = value;
        return o;
    }
}
