using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class OutsideProcessingGovernanceTests(PostgresFixture postgres)
{
    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<string> Upload(HttpClient client, string name, string content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content)) };
        request.Headers.Add("Idempotency-Key", "coating-" + name);
        request.Headers.Add("X-File-Name", name);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("entityId").GetString()!;
    }

    [Fact]
    public async Task Coating_roundtrip_preserves_sent_configuration_and_identity_and_inspects_certificate_and_discrepancy()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("coating-buyer", "Buyer", "MaterialHandler");
        await plant.Person("coating-manager", "PurchasingManager");
        await plant.Person("coating-inspector", "Inspector", "QualityEngineer");
        await plant.Person("coating-controller", "InventoryController");
        var stores = plant.As("stores");
        var inspector = plant.As("coating-inspector");
        var buyer = plant.As("coating-buyer");
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = "COAT-MAT", description = "Lot to be coated", unitOfMeasure = "KG", makeBuy = "Buy", leadTimeDays = 7, lotTracked = true });
        await plant.Ok(plant.Engineer, "/api/engineering/items/COAT-MAT/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, "/api/engineering/items/COAT-MAT/revisions/A/release", new { reason = "Approved coating substrate" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "COAT-MAT", quantity = 100m, locationCode = "STORES", lotNumber = "COAT-LOT", revision = "A", reason = "Identified substrate" });
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = "COAT-SERVICE", description = "External coating", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 7, @class = "Service" });
        await plant.Ok(buyer, "/api/procurement/suppliers", new { code = "COATER", name = "Coating partner" });
        var po = Id(await plant.Ok(buyer, "/api/procurement/purchase-orders", new { supplier = "COATER", lines = new[] { new { partNumber = "COAT-SERVICE", quantity = 5m, promisedDate = "2026-12-01" } }, reason = "Coating service authorization" }));
        await plant.Ok(plant.As("coating-manager"), $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Approved process procurement" });
        await plant.Ok(stores, "/api/inventory/locations", new { code = "COATER-EXT", siteCode = "PLANT1", name = "Coater dock", kind = "ExternalProcessor", custodianSupplier = "COATER" });

        var specificationHash = await Upload(plant.Engineer, "coating-spec.pdf", "Coating specification A: dry-film thickness 10 to 12 micrometres.");
        await plant.Ok(plant.Engineer, "/api/engineering/documents", new { number = "COAT-SPEC", title = "Customer coating specification", type = "Specification", authority = "External", externalAuthority = "Customer", externalNumber = "CS-17" });
        await plant.Ok(plant.Engineer, "/api/engineering/documents/COAT-SPEC/revisions", new { revision = "A", fileSha256 = specificationHash, fileName = "coating-spec.pdf" });
        await plant.Ok(plant.Approver, "/api/engineering/documents/COAT-SPEC/revisions/A/release", new { reason = "Record customer applicable specification" });
        var dispatch = new { partNumber = "COAT-MAT", quantity = 5m, fromLocation = "STORES", processorLocation = "COATER-EXT", lotNumber = "COAT-LOT", purchaseOrder = po, purchaseOrderLine = 1,
            materialRevision = "A", specificationDocument = "COAT-SPEC", specificationRevision = "A", expectedReturnDate = "2026-12-01", reason = "Send for identity-preserving coating" };

        var hold = (await plant.Ok(plant.Admin, "/api/holds", new { kind = "Quality", targetType = "Lot", partNumber = "COAT-MAT", targetId = "COAT-LOT", source = "Coating dispatch review", reason = "Verify substrate before leaving company custody" })).GetProperty("entityId").GetString();
        var held = await plant.Try(stores, "/api/inventory/outside-processing/dispatch", dispatch);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, held.StatusCode);
        Assert.Contains("hold", await held.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await plant.Ok(inspector, $"/api/holds/{hold}/release", new { reason = "Substrate review complete" });
        await plant.Ok(stores, "/api/inventory/outside-processing/dispatch", dispatch);
        var processKey = $"COAT-MAT:COAT-LOT:PO{po}-L1";
        var sent = (await stores.GetFromJsonAsync<JsonElement>("/api/inventory/outside-processing")).GetProperty("dispatches")[0];
        Assert.Equal(("Company", "Preserved", "A", "COAT-SPEC", "A", po), (sent.GetProperty("ownership").GetString(), sent.GetProperty("identityPolicy").GetString(), sent.GetProperty("materialRevision").GetString(),
            sent.GetProperty("specificationDocument").GetString(), sent.GetProperty("specificationRevision").GetString(), sent.GetProperty("technicalPackage").GetProperty("poNumber").GetInt64()));

        await plant.Ok(stores, "/api/inventory/outside-processing/return", new { partNumber = "COAT-MAT", quantity = 3m, processorLocation = "COATER-EXT", toLocation = "STORES", lotNumber = "COAT-LOT", purchaseOrder = po, purchaseOrderLine = 1, reason = "Partial coating return for inspection" });
        var returned = (await stores.GetFromJsonAsync<JsonElement>("/api/inventory/outside-processing")).GetProperty("returns")[0];
        var returnId = returned.GetProperty("transactionId").GetInt64();
        var bypass = await plant.Try(plant.As("coating-controller"), "/api/inventory/releases", new { partNumber = "COAT-MAT", quantity = 3m, locationCode = "STORES", lotNumber = "COAT-LOT", status = "Inspection", reason = "Try generic release without coating inspection" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bypass.StatusCode);
        Assert.Contains("outside_process_inspection_required", await bypass.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(inspector, "/api/quality/fai-determinations", new { partNumber = "COAT-MAT", revision = "A", supplierCode = "COATER", state = "NotRequired", reason = "Qualified recurring coating process" });
        var inspectPath = $"/api/inventory/receiving/{returnId}/inspections";
        var missingResults = await plant.Try(inspector, inspectPath, new { accepted = 2m, rejected = 1m, defect = "One kilogram under coated", reason = "Check required results" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingResults.StatusCode);
        Assert.Contains("process_inspection_results_required", await missingResults.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var inspection = new { accepted = 2m, rejected = 1m, defect = "One kilogram under coated", results = new[] { new { characteristic = "Coating thickness", requirement = "10–12 µm", measured = "11 µm accepted; 7 µm rejected", pass = false } }, reason = "Measured accepted and rejected quantities separately" };
        var missingCertificate = await plant.Try(inspector, inspectPath, inspection);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingCertificate.StatusCode);
        Assert.Contains("processor_certificate_required", await missingCertificate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var certificateHash = await Upload(buyer, "coating-coc.pdf", "Coater signed certificate: COAT-LOT, COAT-SPEC revision A, partial shipment three kilograms.");
        await plant.Ok(buyer, $"/api/engineering/attachments/OutsideProcess/{processKey}/file", new { fileSha256 = certificateHash, fileName = "coating-coc.pdf", type = "CertificateOfConformance" });
        await plant.Ok(inspector, inspectPath, inspection);

        var balances = await stores.GetFromJsonAsync<JsonElement[]>("/api/inventory/stock?part=COAT-MAT");
        Assert.Contains(balances ?? [], b => b.GetProperty("locationCode").GetString() == "COATER-EXT" && b.GetProperty("quantity").GetDecimal() == 2m);
        Assert.Contains(balances ?? [], b => b.GetProperty("locationCode").GetString() == "STORES" && b.GetProperty("status").GetString() == "Released" && b.GetProperty("quantity").GetDecimal() == 97m);
        Assert.Contains(balances ?? [], b => b.GetProperty("status").GetString() == "Hold" && b.GetProperty("quantity").GetDecimal() == 1m);
        Assert.DoesNotContain(balances ?? [], b => b.GetProperty("status").GetString() == "Inspection");
        Assert.All(balances ?? [], b => Assert.Equal("COAT-LOT", b.GetProperty("lotNumber").GetString()));
        var receiving = await stores.GetFromJsonAsync<JsonElement[]>("/api/inventory/receiving");
        var inspectedReturn = Assert.Single(receiving ?? [], r => r.GetProperty("receipt").GetProperty("transactionId").GetInt64() == returnId);
        Assert.Equal((2m, 1m, 0m), (inspectedReturn.GetProperty("accepted").GetDecimal(), inspectedReturn.GetProperty("rejected").GetDecimal(), inspectedReturn.GetProperty("remaining").GetDecimal()));
        var lineage = await stores.GetFromJsonAsync<JsonElement>("/api/inventory/lots/COAT-MAT/COAT-LOT/lineage");
        Assert.Empty(lineage.GetProperty("sources").EnumerateArray());
        Assert.Empty(lineage.GetProperty("results").EnumerateArray());
    }
}
