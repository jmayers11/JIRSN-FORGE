using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V314Tests(PostgresFixture postgres)
{
    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    [Fact]
    public async Task Outside_processing_retains_company_stock_identity_and_requires_inspection_on_return()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("buyer", "Buyer");
        await plant.Person("manager", "PurchasingManager");
        var buyer = plant.As("buyer");
        var manager = plant.As("manager");
        var stores = plant.As("stores");
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = "MAT-OP", description = "Outside process test material", unitOfMeasure = "KG", makeBuy = "Buy", leadTimeDays = 7, lotTracked = true, serialized = false });
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = "SVC-HT", description = "Outside heat treatment", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 7, lotTracked = false, serialized = false, @class = "Service" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "MAT-OP", quantity = 100m, locationCode = "STORES", lotNumber = "LOT-OP-1", reason = "Identified opening lot" });
        await plant.Ok(buyer, "/api/procurement/suppliers", new { code = "HEAT-TREAT", name = "Heat Treat Inc." });
        var po = Id(await plant.Ok(buyer, "/api/procurement/purchase-orders", new
        {
            supplier = "HEAT-TREAT",
            lines = new[] { new { partNumber = "SVC-HT", quantity = 10m, promisedDate = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } },
            reason = "Outside process service order",
        }));
        await plant.Ok(manager, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Reviewed processor order" });
        await plant.Ok(stores, "/api/inventory/locations", new { code = "HEAT-TREAT-EXT", siteCode = "PLANT1", name = "Heat Treat Inc.", kind = "ExternalProcessor", custodianSupplier = "HEAT-TREAT" });

        var dispatched = await plant.Ok(stores, "/api/inventory/outside-processing/dispatch", new
        {
            partNumber = "MAT-OP", quantity = 5m, fromLocation = "STORES", processorLocation = "HEAT-TREAT-EXT", lotNumber = "LOT-OP-1", purchaseOrder = po, purchaseOrderLine = 1, reason = "Send material for treatment",
        });
        Assert.Contains("ownership and lot LOT-OP-1 are retained", dispatched.GetProperty("message").GetString(), StringComparison.Ordinal);

        var returned = await plant.Ok(stores, "/api/inventory/outside-processing/return", new
        {
            partNumber = "MAT-OP", quantity = 3m, processorLocation = "HEAT-TREAT-EXT", toLocation = "STORES", lotNumber = "LOT-OP-1", purchaseOrder = po, purchaseOrderLine = 1, reason = "Receive treated material and certificate",
        });
        Assert.Contains("for inspection", returned.GetProperty("message").GetString(), StringComparison.Ordinal);
        await plant.Person("evidence", "Buyer", "MaterialHandler");
        var evidence = plant.As("evidence");
        using var certificate = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("Processor certificate for V3.14 test")) };
        certificate.Headers.Add("Idempotency-Key", "v314-processor-certificate");
        certificate.Headers.Add("X-File-Name", "processor-coc.pdf");
        var uploaded = await evidence.SendAsync(certificate);
        var uploadedText = await uploaded.Content.ReadAsStringAsync();
        Assert.True(uploaded.IsSuccessStatusCode, uploadedText);
        var certificateHash = JsonDocument.Parse(uploadedText).RootElement.GetProperty("entityId").GetString();
        await plant.Ok(evidence, $"/api/engineering/attachments/OutsideProcess/MAT-OP:LOT-OP-1:PO{po}-L1/file", new { fileSha256 = certificateHash, fileName = "processor-coc.pdf", type = "CertificateOfConformance", note = "Heat-treatment certificate" });
        var balances = await stores.GetFromJsonAsync<JsonElement>("/api/inventory/stock?part=MAT-OP");
        Assert.Contains(balances.EnumerateArray(), row => row.GetProperty("locationCode").GetString() == "STORES" && row.GetProperty("lotNumber").GetString() == "LOT-OP-1" && row.GetProperty("status").GetString() == "Inspection" && row.GetProperty("quantity").GetDecimal() == 3m);
        Assert.Contains(balances.EnumerateArray(), row => row.GetProperty("locationCode").GetString() == "HEAT-TREAT-EXT" && row.GetProperty("lotNumber").GetString() == "LOT-OP-1" && row.GetProperty("quantity").GetDecimal() == 2m);

        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = "PKG-BOX", description = "Shipping carton", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 7, lotTracked = false, serialized = false, @class = "Packaging" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "PKG-BOX", quantity = 10m, locationCode = "STORES", reason = "Opening packaging stock" });
        var withoutPackaging = Id(await plant.Ok(stores, "/api/inventory/shipments", new
        {
            customer = "Customer", requirePackaging = true, lines = new[] { new { partNumber = "MAT-OP", quantity = 1m, locationCode = "STORES", lotNumber = "LOT-OP-1" } }, reason = "Prepare regulated shipment",
        }));
        var blockedPack = await plant.Try(stores, $"/api/inventory/shipments/{withoutPackaging}/pack", new { reason = "Pack" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blockedPack.StatusCode);
        Assert.Contains("packaging_required", await blockedPack.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(stores, "/api/inventory/containers", new { code = "PALLET-OP-1", kind = "Pallet", reason = "Start customer pallet" });
        await plant.Ok(stores, "/api/inventory/containers", new { code = "PALLET-OP-2", kind = "Pallet", reason = "Second customer pallet" });
        await plant.Ok(stores, "/api/inventory/containers/PALLET-OP-1/contents", new { partNumber = "MAT-OP", quantity = 1m, locationCode = "STORES", lotNumber = "LOT-OP-1", reason = "Pack identified lot" });
        var duplicatePack = await plant.Try(stores, "/api/inventory/containers/PALLET-OP-2/contents", new { partNumber = "MAT-OP", quantity = 99m, locationCode = "STORES", lotNumber = "LOT-OP-1", reason = "Overpack" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicatePack.StatusCode);
        await plant.Ok(stores, "/api/inventory/containers/PALLET-OP-1/contents", new { partNumber = "PKG-BOX", quantity = 1m, locationCode = "STORES", reason = "Add package protection" });
        await plant.Ok(stores, "/api/inventory/containers/PALLET-OP-1/seal", new { reason = "Pallet ready" });
        var withPackaging = Id(await plant.Ok(stores, "/api/inventory/shipments", new
        {
            customer = "Customer", requirePackaging = true, containerCode = "PALLET-OP-1", lines = new object[]
            {
                new { partNumber = "MAT-OP", quantity = 1m, locationCode = "STORES", lotNumber = "LOT-OP-1" },
                new { partNumber = "PKG-BOX", quantity = 1m, locationCode = "STORES", lotNumber = (string?)null },
            }, reason = "Prepare packaged shipment",
        }));
        await plant.Ok(stores, $"/api/inventory/shipments/{withPackaging}/pack", new { reason = "Package contents checked" });
        await plant.Person("shipper", "Shipper");
        await plant.Ok(plant.As("shipper"), $"/api/inventory/shipments/{withPackaging}/ship", new { carrier = "Carrier", tracking = "TRACK-314" });
        var containers = await stores.GetFromJsonAsync<JsonElement>("/api/inventory/containers");
        var pallet = containers.EnumerateArray().Single(c => c.GetProperty("code").GetString() == "PALLET-OP-1");
        Assert.Equal("Shipped", pallet.GetProperty("status").GetString());
        Assert.Equal(withPackaging, pallet.GetProperty("shipmentId").GetInt64());
        Assert.Equal(2, pallet.GetProperty("contents").GetArrayLength());
        var immutable = await plant.Try(stores, "/api/inventory/containers/PALLET-OP-1/contents", new { partNumber = "MAT-OP", quantity = 1m, locationCode = "STORES", lotNumber = "LOT-OP-1", reason = "Try to modify sealed pallet" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, immutable.StatusCode);
        var excessiveReturn = await plant.Try(stores, "/api/inventory/customer-returns", new { shipmentId = withPackaging, partNumber = "MAT-OP", lotNumber = "LOT-OP-1", quantity = 2m, locationCode = "STORES", reason = "Correction exceeds shipment" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, excessiveReturn.StatusCode);
        Assert.Contains("over_return", await excessiveReturn.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(stores, "/api/inventory/customer-returns", new { shipmentId = withPackaging, partNumber = "MAT-OP", lotNumber = "LOT-OP-1", quantity = 1m, locationCode = "STORES", reason = "Customer returned one damaged unit" });

        var demand = Id(await plant.Ok(planner, "/api/planning/demand", new { partNumber = "MAT-OP", quantity = 4m, needDate = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), kind = "CustomerOrder", customer = "Partial-shipment customer", reason = "Stage delivery in two lots" }));
        foreach (var chunk in new[] { 2m, 2m })
        {
            var shipment = Id(await plant.Ok(stores, "/api/inventory/shipments", new
            {
                demandId = demand, customer = "Partial-shipment customer", lines = new[] { new { partNumber = "MAT-OP", quantity = chunk, locationCode = "STORES", lotNumber = "LOT-OP-1" } }, reason = "Ship available partial quantity",
            }));
            await plant.Ok(stores, $"/api/inventory/shipments/{shipment}/pack", new { reason = "Confirm partial shipment contents" });
            await plant.Ok(plant.As("shipper"), $"/api/inventory/shipments/{shipment}/ship", new { carrier = "Carrier" });
        }
        var demandView = await planner.GetFromJsonAsync<JsonElement>("/api/planning/demand");
        var delivery = demandView.EnumerateArray().Single(d => d.GetProperty("demandId").GetInt64() == demand);
        Assert.Equal(4m, delivery.GetProperty("shipped").GetDecimal());
        Assert.Equal(0m, delivery.GetProperty("remaining").GetDecimal());

        var mismatched = await plant.Try(stores, "/api/inventory/outside-processing/return", new
        {
            partNumber = "MAT-OP", quantity = 1m, processorLocation = "HEAT-TREAT-EXT", toLocation = "STORES", lotNumber = "LOT-OP-1", purchaseOrder = po, purchaseOrderLine = 99, reason = "Wrong PO line",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatched.StatusCode);
        Assert.Contains("po_line_not_open", await mismatched.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
