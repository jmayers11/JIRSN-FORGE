using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CommercialQualityTests(PostgresFixture postgres)
{
    private const string Api = "/api/quality/commercial";
    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    [Fact]
    public async Task Actual_shipment_complaint_retains_return_and_acceptance_history_and_approves_customer_response()
    {
        var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: b => b.UseSetting(QmsService.ConfigurationKey, "COMMERCIAL-TEST-ORG"));
        try
        {
            var (plant, shipment, inspection, release, _) = await ReturnAcceptanceTests.Shipped(app);
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["approver"]}/roles", new { role = Roles.CustomerCommunicationsApprover, reason = "Independently approve recorded customer-response proposals" });
            var stores = plant.As("stores"); var approver = plant.Approver;
            var rma = Id(await plant.Ok(stores, "/api/inventory/customer-returns", new
            {
                shipmentId = shipment, partNumber = "ACCEPT-PRODUCT", lotNumber = "OUTPUT-1000", quantity = 100,
                locationCode = "QUARANTINE", reason = "Record the actual partial customer return against its shipment"
            }));
            var traceResponse = await stores.GetAsync($"{Api}/shipments/{shipment}/evidence");
            using var traceJson = JsonDocument.Parse(await traceResponse.Content.ReadAsStringAsync());
            Assert.True(traceResponse.IsSuccessStatusCode, traceJson.RootElement.GetRawText());
            Assert.True(traceJson.RootElement.GetProperty("complete").GetBoolean());
            var traceReferences = traceJson.RootElement.GetProperty("relatedRecords").EnumerateArray()
                .Select(item => item.GetProperty("kind").GetString() + ":" + item.GetProperty("key").GetString()).ToArray();
            Assert.Contains(traceReferences, item => item.StartsWith("Order:", StringComparison.Ordinal));
            Assert.Contains(traceReferences, item => item.StartsWith("Lot:", StringComparison.Ordinal));
            Assert.Contains(traceReferences, item => item.StartsWith("InventoryTransaction:", StringComparison.Ordinal));
            Assert.Contains(traceReferences, item => item.StartsWith("AcceptanceInspection:", StringComparison.Ordinal));
            Assert.Contains(traceReferences, item => item == "CustomerReturn:" + rma.ToString(CultureInfo.InvariantCulture));

            var complaintCode = "CUSTOMER-RETURN-COMPLAINT";
            var evidence = new[]
            {
                new CommercialReference(CommercialReferenceKind.CustomerReturn, rma.ToString(CultureInfo.InvariantCulture)),
                new CommercialReference(CommercialReferenceKind.AcceptanceInspection, inspection.ToString(CultureInfo.InvariantCulture)),
                new CommercialReference(CommercialReferenceKind.ProductReleaseDecision, release.ToString(CultureInfo.InvariantCulture))
            };
            var complaintPath = Api + $"/records/Complaint/{complaintCode}/revisions/1";
            await plant.Ok(stores, Api + "/records", new
            {
                kind = "Complaint", code = complaintCode, facilityCode = "PLANT1",
                content = new { complaint = new ComplaintContent(shipment, null, "Customer reported a product concern after delivery.", DateTimeOffset.UtcNow,
                    "Recorded customer contact", "Actual shipment contact", "Assigned quality investigator", Array.Empty<CommercialReference>()) },
                reason = "Record the actual shipped-product complaint and its exact source links"
            });
            await plant.Ok(stores, complaintPath + "/submit", new { expectedVersion = 1, basis = "Actual complaint source links and investigation ownership recorded", reason = "Submit complaint for independent review" });
            await plant.Ok(approver, complaintPath + "/review", new { expectedVersion = 2, basis = "Review exact shipment, release and returned cohort evidence", reason = "Independently review complaint history" });
            await plant.Ok(approver, complaintPath + "/approve", new { expectedVersion = 3, basis = "Approve the attributed investigation scope", reason = "Approve complaint investigation" });
            await plant.Ok(approver, complaintPath + "/effective", new { expectedVersion = 4, basis = "Make the controlled complaint effective", reason = "Publish complaint investigation" });
            await plant.Ok(stores, complaintPath + "/observations", new
            {
                expectedVersion = 5, observation = "Investigation", observedAt = DateTimeOffset.UtcNow,
                basis = "Investigate the exact returned cohort without replacing the original release evidence", evidence,
                recordedExternalPerson = (string?)null, reason = "Record actual investigation source use"
            });
            await plant.Ok(stores, complaintPath + "/observations", new
            {
                expectedVersion = 6, observation = "ComplaintDisposition", observedAt = DateTimeOffset.UtcNow,
                basis = "The partial return remains a separate inspected cohort; no duplicate quantity is credited", evidence,
                recordedExternalPerson = (string?)null, reason = "Record the actual human complaint disposition"
            });
            await plant.Ok(approver, complaintPath + "/close", new { expectedVersion = 7, basis = "Independently close after recorded disposition and actual evidence", reason = "Close the reviewed complaint" });
            var complaint = await stores.GetFromJsonAsync<JsonElement>(complaintPath);
            Assert.Equal("Closed", complaint.GetProperty("state").GetString());
            var complaintFacts = complaint.GetProperty("facts").EnumerateArray().Select(item => item.GetProperty("reference").GetProperty("kind").GetString()).ToArray();
            Assert.Contains("Shipment", complaintFacts);
            var investigation = complaint.GetProperty("observations").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "Investigation");
            Assert.Contains(investigation.GetProperty("evidence").EnumerateArray(), item => item.GetProperty("kind").GetString() == "CustomerReturn");
            Assert.Contains(investigation.GetProperty("evidence").EnumerateArray(), item => item.GetProperty("kind").GetString() == "AcceptanceInspection");
            Assert.Contains(investigation.GetProperty("evidence").EnumerateArray(), item => item.GetProperty("kind").GetString() == "ProductReleaseDecision");
            Assert.Contains(complaint.GetProperty("observations").EnumerateArray(), item => item.GetProperty("kind").GetString() == "ComplaintDisposition");

            var communicationCode = "CUSTOMER-RESPONSE-PROPOSAL";
            var communicationPath = Api + $"/records/Communication/{communicationCode}/revisions/1";
            await plant.Ok(stores, Api + "/records", new
            {
                kind = "Communication", code = communicationCode, facilityCode = "PLANT1",
                content = new { communication = new CommunicationContent(CommunicationAudience.Customer,
                    new(CommercialReferenceKind.Shipment, shipment.ToString(CultureInfo.InvariantCulture)),
                    new(CommercialReferenceKind.CommercialRecord, "Complaint/" + complaintCode, Revision: "1"),
                    "Recorded customer contact", "Response proposal for the product complaint", "The organization will investigate the exact returned cohort.",
                    "Approved customer channel", [new(CommercialReferenceKind.Shipment, shipment.ToString(CultureInfo.InvariantCulture))]) },
                reason = "Prepare a response linked to the actual customer and closed complaint"
            });
            await plant.Ok(stores, communicationPath + "/submit", new { expectedVersion = 1, basis = "Submit the attributed response proposal for communication approval", reason = "Submit response proposal" });
            await plant.Ok(approver, communicationPath + "/review", new { expectedVersion = 2, basis = "Review exact customer and complaint linkage", reason = "Review customer response proposal" });
            await plant.Ok(approver, communicationPath + "/approve", new { expectedVersion = 3, basis = "Approve the recorded response content and channel", reason = "Approve customer response proposal" });
            await plant.Ok(approver, communicationPath + "/effective", new { expectedVersion = 4, basis = "Authorize the exact response proposal for human-controlled use", reason = "Approve the governed response" });
            var communication = await stores.GetFromJsonAsync<JsonElement>(communicationPath);
            Assert.Equal("Effective", communication.GetProperty("state").GetString());
            Assert.Empty(communication.GetProperty("observations").EnumerateArray());

            var feedbackCode = "CUSTOMER-FEEDBACK-RECORD";
            var feedbackPath = Api + $"/records/CustomerFeedback/{feedbackCode}/revisions/1";
            await plant.Ok(stores, Api + "/records", new
            {
                kind = "CustomerFeedback", code = feedbackCode, facilityCode = "PLANT1",
                content = new { customerFeedback = new CustomerFeedbackContent(
                    new(CommercialReferenceKind.Shipment, shipment.ToString(CultureInfo.InvariantCulture)), CustomerFeedbackKind.Survey,
                    DateTimeOffset.UtcNow, "Customer submitted scorecard", [new("DELIVERY", "Customer-provided delivery score", 4m, 1m, 5m, "score", null, "Score transcribed from the customer record")],
                    "Customer gave an actual delivery score; no overall satisfaction conclusion is inferred.", evidence) },
                reason = "Record customer-provided feedback against its actual shipment"
            });
            await plant.Ok(stores, feedbackPath + "/submit", new { expectedVersion = 1, basis = "Feedback value and source are explicit", reason = "Submit source-linked feedback" });
            await plant.Ok(approver, feedbackPath + "/review", new { expectedVersion = 2, basis = "Review the actual customer record and measure", reason = "Independently review customer feedback" });
            await plant.Ok(approver, feedbackPath + "/approve", new { expectedVersion = 3, basis = "Approve only the transcribed source measure", reason = "Approve customer feedback record" });
            await plant.Ok(approver, feedbackPath + "/effective", new { expectedVersion = 4, basis = "Make the source-linked feedback available as a governed input", reason = "Publish customer feedback input" });
            var feedback = await stores.GetFromJsonAsync<JsonElement>(feedbackPath);
            Assert.Equal("Effective", feedback.GetProperty("state").GetString());
            Assert.Equal(4, feedback.GetProperty("content").GetProperty("customerFeedback").GetProperty("measures")[0].GetProperty("value").GetDecimal());
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Complaint_corrective_action_link_cannot_close_until_the_actual_capa_is_independently_verified()
    {
        var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: b => b.UseSetting(QmsService.ConfigurationKey, "COMMERCIAL-TEST-ORG"));
        try
        {
            var (plant, shipment, _, _, _) = await ReturnAcceptanceTests.Shipped(app);
            var stores = plant.As("stores");
            const string code = "COMPLAINT-LINKED-CAPA";
            var path = Api + $"/records/Complaint/{code}/revisions/1";
            await plant.Ok(stores, Api + "/records", new
            {
                kind = "Complaint", code, facilityCode = "PLANT1",
                content = new { complaint = new ComplaintContent(shipment, null, "Actual post-delivery complaint with an assigned corrective action.", DateTimeOffset.UtcNow,
                    "Recorded customer contact", "Actual shipment contact", "Assigned quality investigator", Array.Empty<CommercialReference>()) },
                reason = "Record the actual shipment complaint before linking its corrective action"
            });
            await plant.Ok(stores, path + "/submit", new { expectedVersion = 1, basis = "The actual shipped-product complaint has an assigned investigation owner", reason = "Submit complaint for review" });
            await plant.Ok(plant.Approver, path + "/review", new { expectedVersion = 2, basis = "Review the exact shipment complaint and current source", reason = "Independently review complaint" });
            await plant.Ok(plant.Approver, path + "/approve", new { expectedVersion = 3, basis = "Approve the actual investigation scope", reason = "Approve complaint investigation" });
            await plant.Ok(plant.Approver, path + "/effective", new { expectedVersion = 4, basis = "Publish the controlled complaint case", reason = "Make complaint effective" });

            var opened = await plant.Ok(plant.Engineer, "/api/quality/capas", new
            {
                ncrId = (long?)null, title = "Actual complaint-linked corrective action", rootCause = "Actual human investigation cause",
                action = "Actual corrective action requiring independent verification", dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
                reason = "Open the actual corrective action for this complaint"
            });
            var capaId = long.Parse(opened.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
            var linkedEvidence = new[] { new CommercialReference(CommercialReferenceKind.Capa, capaId.ToString(CultureInfo.InvariantCulture)) };
            await plant.Ok(stores, path + "/observations", new
            {
                expectedVersion = 5, observation = "CorrectiveAction", observedAt = DateTimeOffset.UtcNow,
                basis = "The corrective-action record is the actual CAPA opened for this complaint", evidence = linkedEvidence,
                recordedExternalPerson = (string?)null, reason = "Link the complaint to its actual CAPA"
            });

            var close = new { expectedVersion = 6, basis = "Close only after the actual linked corrective action is independently verified", reason = "Close complaint after CAPA" };
            using (var openFailure = await plant.Try(plant.Approver, path + "/close", close))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, openFailure.StatusCode);
                using var problem = JsonDocument.Parse(await openFailure.Content.ReadAsStringAsync());
                Assert.Equal("complaint_capa_not_closed", problem.RootElement.GetProperty("code").GetString());
            }
            await plant.Ok(plant.Engineer, $"/api/quality/capas/{capaId}/implement", new { note = "Actual assigned corrective action was implemented", reason = "Record actual CAPA implementation" });
            using (var implementedFailure = await plant.Try(plant.Approver, path + "/close", close))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, implementedFailure.StatusCode);
                using var problem = JsonDocument.Parse(await implementedFailure.Content.ReadAsStringAsync());
                Assert.Equal("complaint_capa_not_closed", problem.RootElement.GetProperty("code").GetString());
            }
            await plant.Ok(plant.Approver, $"/api/quality/capas/{capaId}/verify", new { reason = "Independently verify the actual CAPA action" });
            await plant.Ok(plant.Approver, path + "/close", close);

            var complaint = await stores.GetFromJsonAsync<JsonElement>(path);
            Assert.Equal("Closed", complaint.GetProperty("state").GetString());
            var correctiveAction = complaint.GetProperty("observations").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "CorrectiveAction");
            Assert.Contains(correctiveAction.GetProperty("evidence").EnumerateArray(), item =>
                item.GetProperty("kind").GetString() == "Capa" && item.GetProperty("key").GetString() == capaId.ToString(CultureInfo.InvariantCulture));
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Actual_field_failure_post_delivery_case_retains_original_shipment_inspection_and_release_evidence()
    {
        var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: b => b.UseSetting(QmsService.ConfigurationKey, "COMMERCIAL-TEST-ORG"));
        try
        {
            var (plant, shipment, inspection, release, _) = await ReturnAcceptanceTests.Shipped(app);
            var stores = plant.As("stores");
            var evidence = new[]
            {
                new CommercialReference(CommercialReferenceKind.Shipment, shipment.ToString(CultureInfo.InvariantCulture)),
                new CommercialReference(CommercialReferenceKind.AcceptanceInspection, inspection.ToString(CultureInfo.InvariantCulture)),
                new CommercialReference(CommercialReferenceKind.ProductReleaseDecision, release.ToString(CultureInfo.InvariantCulture))
            };
            const string code = "ACTUAL-FIELD-FAILURE";
            var path = Api + $"/records/PostDelivery/{code}/revisions/1";
            await plant.Ok(stores, Api + "/records", new
            {
                kind = "PostDelivery", code, facilityCode = "PLANT1",
                content = new { postDelivery = new PostDeliveryContent(shipment, PostDeliveryKind.FieldFailure,
                    "Actual customer field report tied to the delivered lot; investigation preserves the original release.", evidence) },
                reason = "Record actual field-failure notice against the shipped product"
            });
            await plant.Ok(stores, path + "/submit", new { expectedVersion = 1, basis = "Actual shipment, inspection and release sources are recorded", reason = "Submit post-delivery case for review" });
            await plant.Ok(plant.Approver, path + "/review", new { expectedVersion = 2, basis = "Review the exact shipped item and historical acceptance evidence", reason = "Independently review field-failure case" });
            await plant.Ok(plant.Approver, path + "/approve", new { expectedVersion = 3, basis = "Approve the defined investigation scope", reason = "Approve post-delivery investigation" });
            await plant.Ok(plant.Approver, path + "/effective", new { expectedVersion = 4, basis = "Make the controlled investigation effective", reason = "Publish post-delivery investigation" });
            await plant.Ok(stores, path + "/observations", new { expectedVersion = 5, observation = "Investigation", observedAt = DateTimeOffset.UtcNow,
                basis = "Investigate the actual field report against the immutable original release and inspection", evidence, reason = "Record actual post-delivery evidence review" });
            await plant.Ok(stores, path + "/observations", new { expectedVersion = 6, observation = "ComplaintDisposition", observedAt = DateTimeOffset.UtcNow,
                basis = "The reported field condition is dispositioned against the inspected shipped cohort; this is not a new release", evidence, reason = "Record human post-delivery disposition" });
            await plant.Ok(plant.Approver, path + "/close", new { expectedVersion = 7, basis = "Independently close after evidence review and recorded disposition", reason = "Close reviewed post-delivery case" });

            var result = await stores.GetFromJsonAsync<JsonElement>(path);
            Assert.Equal("Closed", result.GetProperty("state").GetString());
            var facts = result.GetProperty("facts").EnumerateArray().Select(item => item.GetProperty("reference").GetProperty("kind").GetString()).ToArray();
            Assert.Contains("Shipment", facts);
            Assert.Contains("AcceptanceInspection", facts);
            Assert.Contains("ProductReleaseDecision", facts);
            var investigation = result.GetProperty("observations").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "Investigation");
            Assert.Contains(investigation.GetProperty("evidence").EnumerateArray(), item => item.GetProperty("kind").GetString() == "Shipment");
            Assert.Contains(investigation.GetProperty("evidence").EnumerateArray(), item => item.GetProperty("kind").GetString() == "AcceptanceInspection");
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Warranty_and_recall_reviews_retain_actual_shipment_sources_and_require_human_disposition()
    {
        var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: b => b.UseSetting(QmsService.ConfigurationKey, "COMMERCIAL-TEST-ORG"));
        try
        {
            var (plant, shipment, inspection, release, _) = await ReturnAcceptanceTests.Shipped(app);
            var stores = plant.As("stores");
            var evidence = new[]
            {
                new CommercialReference(CommercialReferenceKind.Shipment, shipment.ToString(CultureInfo.InvariantCulture)),
                new CommercialReference(CommercialReferenceKind.AcceptanceInspection, inspection.ToString(CultureInfo.InvariantCulture)),
                new CommercialReference(CommercialReferenceKind.ProductReleaseDecision, release.ToString(CultureInfo.InvariantCulture))
            };
            foreach (var (kind, code) in new[]
            {
                (PostDeliveryKind.Warranty, "ACTUAL-WARRANTY-REVIEW"),
                (PostDeliveryKind.RecallReview, "ACTUAL-RECALL-REVIEW")
            })
            {
                var path = Api + $"/records/PostDelivery/{code}/revisions/1";
                await plant.Ok(stores, Api + "/records", new
                {
                    kind = "PostDelivery", code, facilityCode = "PLANT1",
                    content = new { postDelivery = new PostDeliveryContent(shipment, kind,
                        "Human review of the actual shipped cohort; this record does not infer a defect or send a customer notice.", evidence) },
                    reason = "Record an actual source-linked post-delivery review"
                });
                await plant.Ok(stores, path + "/submit", new { expectedVersion = 1, basis = "Submit the actual shipped-source review", reason = "Submit warranty/recall review" });
                await plant.Ok(plant.Approver, path + "/review", new { expectedVersion = 2, basis = "Review exact shipment, release and inspection sources", reason = "Independently review post-delivery case" });
                await plant.Ok(plant.Approver, path + "/approve", new { expectedVersion = 3, basis = "Approve the human-defined review scope", reason = "Approve post-delivery review" });
                await plant.Ok(plant.Approver, path + "/effective", new { expectedVersion = 4, basis = "Publish the reviewed internal case", reason = "Make post-delivery review effective" });
                await plant.Ok(stores, path + "/observations", new { expectedVersion = 5, observation = "Investigation", observedAt = DateTimeOffset.UtcNow,
                    basis = "Review actual shipment, historical acceptance and release evidence without inferring affected product", evidence, reason = "Record source-linked investigation" });
                using (var premature = await plant.Try(plant.Approver, path + "/close", new { expectedVersion = 6, basis = "Attempt close before human disposition", reason = "Premature close attempt" }))
                {
                    Assert.Equal(HttpStatusCode.UnprocessableEntity, premature.StatusCode);
                    using var problem = JsonDocument.Parse(await premature.Content.ReadAsStringAsync());
                    Assert.Equal("closure_evidence_required", problem.RootElement.GetProperty("code").GetString());
                }
                await plant.Ok(stores, path + "/observations", new { expectedVersion = 6, observation = "ComplaintDisposition", observedAt = DateTimeOffset.UtcNow,
                    basis = "Quality records an explicit human disposition for only the reviewed shipment cohort", evidence, reason = "Record human post-delivery disposition" });
                await plant.Ok(plant.Approver, path + "/close", new { expectedVersion = 7, basis = "Independently close after recorded disposition and actual sources", reason = "Close reviewed warranty/recall case" });
                var record = await stores.GetFromJsonAsync<JsonElement>(path);
                Assert.Equal("Closed", record.GetProperty("state").GetString());
                Assert.Equal(kind.ToString(), record.GetProperty("content").GetProperty("postDelivery").GetProperty("kind").GetString());
                var linkedKinds = record.GetProperty("facts").EnumerateArray().Select(fact => fact.GetProperty("reference").GetProperty("kind").GetString()).ToArray();
                Assert.Contains("Shipment", linkedKinds);
                Assert.Contains("AcceptanceInspection", linkedKinds);
                Assert.Contains("ProductReleaseDecision", linkedKinds);
            }
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Actual_supplier_owned_tool_requires_recorded_owner_acceptance_before_return_and_preserves_measured_condition()
    {
        var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: b => b.UseSetting(QmsService.ConfigurationKey, "COMMERCIAL-TEST-ORG"));
        try
        {
            var (plant, _, _, _, _) = await ReturnAcceptanceTests.Shipped(app);
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["stores"]}/roles", new { role = Roles.Buyer, reason = "Read the actual supplier who retains title to this governed property" });
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["eng"]}/roles", new { role = Roles.Buyer, reason = "Record the actual supplier and tool used by the agreement" });
            var stores = plant.As("stores"); var code = "COMM-SUPPLIER-TOOL"; var supplier = "COMM-TOOL-OWNER";
            await plant.Ok(plant.Engineer, "/api/procurement/suppliers", new { code = supplier, name = "Actual supplier property owner", reason = "Record the actual supplier holding property title" });
            await plant.Ok(plant.Engineer, "/api/resources/tool-types", new { code = "COMM-FIXTURE", name = "Supplier-owned inspection fixture", kind = "Fixture", reason = "Record the actual tool type" });
            await plant.Ok(plant.Engineer, "/api/resources/tools", new { code, typeCode = "COMM-FIXTURE", description = "Actual supplier-owned fit-check fixture", serial = "COMM-OWNER-SERIAL", location = "TOOL-CRIB", eligibleMachines = Array.Empty<string>(), eligibleActivities = Array.Empty<string>(), reason = "Register the actual physical tool in the owner system" });

            var now = DateTimeOffset.UtcNow;
            var agreement = new PropertyAgreementContent(new(PropertyUseKind.Tool, code), PropertyTitleKind.SupplierConsigned,
                new(CommercialReferenceKind.Supplier, supplier), "Actual supplier agreement controls this specific reusable fixture.",
                now.AddDays(-1), now.AddDays(30), ["INSPECTION-FIT-CHECK"], ["SLIT-WC"], [], false, true,
                "Return this fixture to the supplier after the authorized fit check.",
                [new("STORE-TEMP", PreservationControlKind.Environment, "Keep the actual fixture within the stated environment range.", 10m, 30m, "C", [])],
                [new(CommercialReferenceKind.Supplier, supplier)]);
            const string recordCode = "SUPPLIER-TOOL-AGREEMENT";
            var path = Api + $"/records/PropertyAgreement/{recordCode}/revisions/1";
            await plant.Ok(stores, Api + "/records", new { kind = "PropertyAgreement", code = recordCode, facilityCode = "PLANT1", content = new CommercialContent(PropertyAgreement: agreement), reason = "Record the exact external-property agreement and preservation limit" });
            await plant.Ok(stores, path + "/submit", new { expectedVersion = 1, basis = "Actual supplier title, tool and preservation conditions are linked", reason = "Submit supplier property agreement" });
            await plant.Ok(plant.Approver, path + "/review", new { expectedVersion = 2, basis = "Independently review owner, scope, custody and preservation", reason = "Review supplier property agreement" });
            await plant.Ok(plant.Approver, path + "/approve", new { expectedVersion = 3, basis = "Approve the bounded fixture use and owner-return terms", reason = "Approve supplier property agreement" });
            await plant.Ok(plant.Approver, path + "/effective", new { expectedVersion = 4, basis = "Make the exact agreement effective", reason = "Publish supplier property agreement" });

            static CommercialReference ToolEvent(string tool, JsonElement[] events, string kind)
            {
                var actual = events.Last(item => item.GetProperty("kind").GetString() == kind);
                return new(CommercialReferenceKind.ToolEvent, tool + "/" + actual.GetProperty("eventId").GetInt64().ToString(CultureInfo.InvariantCulture));
            }
            async Task<CommercialReference> LatestEvent(string kind)
            {
                var events = await stores.GetFromJsonAsync<JsonElement[]>("/api/resources/tools/" + code + "/history") ?? [];
                return ToolEvent(code, events, kind);
            }
            async Task Observe(int expected, CommercialObservationKind kind, CommercialReference evidence, string basis,
                DateTimeOffset? observedAt = null, string? external = null, IReadOnlyList<PreservationReading>? preservation = null)
            {
                await plant.Ok(stores, path + "/observations", new { expectedVersion = expected, observation = kind.ToString(), observedAt = observedAt ?? DateTimeOffset.UtcNow,
                    basis, evidence = new[] { evidence }, recordedExternalPerson = external, preservation, reason = "Record the actual governed custody or preservation observation" });
            }

            await plant.Ok(stores, "/api/resources/tools/" + code + "/move", new { custody = "WithPerson", custodian = "Actual supplier representative", location = "CUSTOMER-DOCK", reason = "Record the fixture at its actual external owner" });
            await plant.Ok(stores, "/api/resources/tools/" + code + "/move", new { custody = "InHouse", custodian = (string?)null, location = "TOOL-CRIB", reason = "Record receipt of the actual identified fixture" });
            await Observe(5, CommercialObservationKind.PropertyReceipt, await LatestEvent("Moved"), "Actual fixture receipt is linked to the tool custody event.");
            var priorEvents = await stores.GetFromJsonAsync<JsonElement[]>("/api/resources/tools/" + code + "/history") ?? [];
            var unpreservedUse = await plant.Try(stores, "/api/resources/tools/" + code + "/uses", new { uses = 1, machineCode = "SLITTER-A", activityCode = "INSPECTION-FIT-CHECK", reference = "Actual authorized inspection fit check", reason = "Check preservation before use" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unpreservedUse.StatusCode);
            Assert.Contains("property_preservation_not_met", await unpreservedUse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(priorEvents.Length, (await stores.GetFromJsonAsync<JsonElement[]>("/api/resources/tools/" + code + "/history") ?? []).Length);
            await Observe(6, CommercialObservationKind.PreservationObservation, await LatestEvent("Moved"), "Actual environment condition was recorded before tool use.",
                preservation: [new("STORE-TEMP", 22m, null, "C", "Actual measured storage temperature from the controlled observation")]);
            await plant.Ok(stores, "/api/resources/tools/" + code + "/uses", new { uses = 1, machineCode = "SLITTER-A", activityCode = "INSPECTION-FIT-CHECK", reference = "Actual authorized inspection fit check", reason = "Record the exact physical tool use" });
            var useEvent = await LatestEvent("Used");
            await Observe(7, CommercialObservationKind.PropertyUse, useEvent, "Actual fixture use is linked to its immutable use event.");

            await plant.Ok(stores, "/api/resources/tools/" + code + "/move", new { custody = "WithPerson", custodian = "Actual supplier representative", location = "CUSTOMER-DOCK", reason = "Return the fixture to the actual supplier owner" });
            var returnHistory = await stores.GetFromJsonAsync<JsonElement[]>("/api/resources/tools/" + code + "/history") ?? [];
            var ownerTransfer = returnHistory.Last(item => item.GetProperty("kind").GetString() == "Moved");
            Assert.Equal("Actual supplier representative", ownerTransfer.GetProperty("custodian").GetString());
            var returnedEvent = ToolEvent(code, returnHistory, "Moved");
            var returnAttempt = await plant.Try(stores, path + "/observations", new { expectedVersion = 8, observation = "PropertyReturn", observedAt = DateTimeOffset.UtcNow,
                basis = "Return attempt before the recorded legal owner acceptance", evidence = new[] { returnedEvent }, recordedExternalPerson = (string?)null,
                reason = "Attempt property return without the required owner acceptance" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, returnAttempt.StatusCode);
            Assert.Contains("owner_return_acceptance_required", await returnAttempt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var held = await stores.GetFromJsonAsync<JsonElement>(path); Assert.Equal(8, held.GetProperty("version").GetInt32());

            await Observe(8, CommercialObservationKind.OwnerAcceptance, new(CommercialReferenceKind.Supplier, supplier),
                "Actual supplier representative accepted the specific property return.", DateTimeOffset.UtcNow.AddMilliseconds(-100), "Actual supplier representative");
            await Observe(9, CommercialObservationKind.PropertyReturn, returnedEvent, "Actual tool custody return follows recorded owner acceptance.");
            var final = await stores.GetFromJsonAsync<JsonElement>(path);
            Assert.Equal("Effective", final.GetProperty("state").GetString());
            Assert.Equal(10, final.GetProperty("version").GetInt32());
            Assert.Contains(final.GetProperty("observations").EnumerateArray(), item => item.GetProperty("kind").GetString() == "PreservationObservation");
            Assert.Contains(final.GetProperty("observations").EnumerateArray(), item => item.GetProperty("kind").GetString() == "OwnerAcceptance");
            Assert.Contains(final.GetProperty("observations").EnumerateArray(), item => item.GetProperty("kind").GetString() == "PropertyReturn");
        }
        finally { await app.DisposeAsync(); }
    }


    [Fact]
    public async Task Effective_provider_evaluation_gates_the_actual_supplier_and_part_scope_of_purchase_approval()
    {
        var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: b => b.UseSetting(QmsService.ConfigurationKey, "COMMERCIAL-TEST-ORG"));
        try
        {
            var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["eng"]}/roles", new { role = Roles.QualityEngineer, reason = "Actual commercial quality record author" });
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["eng"]}/roles", new { role = Roles.Buyer, reason = "Read actual supplier source and create its purchase order" });
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["approver"]}/roles", new { role = Roles.QualityManager, reason = "Independent qualification approval" });
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["approver"]}/roles", new { role = Roles.PurchasingManager, reason = "Independent purchasing approval" });

            var author = plant.Engineer;
            var approver = plant.Approver;
            await plant.Ok(author, "/api/procurement/suppliers", new { code = "COMM-SUP", name = "Commercial quality test supplier" });
            foreach (var part in new[] { "COMM-SVC-A", "COMM-SVC-B" })
                await plant.Ok(author, "/api/engineering/items", new { partNumber = part, description = "Governed external service", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 5, @class = "Service" });

            var now = DateTimeOffset.UtcNow;
            var qualification = new
            {
                providerEvaluation = new
                {
                    supplierCode = "COMM-SUP",
                    scope = new { partNumber = "COMM-SVC-A", partRevision = (string?)null, processCode = (string?)null, workCenterCode = (string?)null,
                        enforceAtPurchaseApproval = true, enforceAtExternalDispatch = false, purchasingRequirements = "Actual audited service scope COMM-SVC-A only" },
                    criteria = new[] { new { code = "QMS", description = "Current quality system evidence", kind = "Attribute", mandatory = true, weight = 1m,
                        measured = (decimal?)null, minimum = (decimal?)null, maximum = (decimal?)null, accepted = (bool?)true, evidenceBasis = "Actual current supplier evidence reviewed" } },
                    minimumWeightedPassFraction = 1m, validFrom = now.AddDays(-1), validTo = now.AddDays(30), reevaluateBy = now.AddDays(14),
                    evaluationBasis = "Human evaluation of the actual supplier and service scope",
                    evidence = new[] { new { kind = "Supplier", key = "COMM-SUP" } }
                }
            };
            await plant.Ok(author, Api + "/records", new { kind = "ProviderEvaluation", code = "COMM-SUP-QUAL", facilityCode = "PLANT1", content = qualification,
                reason = "Create exact actual supplier qualification" });
            var record = Api + "/records/ProviderEvaluation/COMM-SUP-QUAL/revisions/1";
            await plant.Ok(author, record + "/submit", new { expectedVersion = 1, basis = "Supplier evaluation is complete", reason = "Submit actual evidence" });
            await plant.Ok(approver, record + "/review", new { expectedVersion = 2, basis = "The exact supplier and service scope are reviewable", reason = "Independent review" });
            await plant.Ok(approver, record + "/approve", new { expectedVersion = 3, basis = "The mandatory criterion passes for the declared interval", reason = "Independent approval" });
            await plant.Ok(approver, record + "/effective", new { expectedVersion = 4, basis = "Publish the bounded scope for purchasing use", reason = "Effective source approval" });

            async Task<long> Purchase(string part)
            {
                var result = await plant.Ok(author, "/api/procurement/purchase-orders", new { supplier = "COMM-SUP",
                    lines = new[] { new { partNumber = part, quantity = 1m, promisedDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)) } }, reason = "Actual service requirement" });
                return Id(result);
            }

            var outsideScope = await Purchase("COMM-SVC-B");
            var denied = await plant.Try(approver, $"/api/procurement/purchase-orders/{outsideScope}/approve", new { reason = "Attempt a different service scope" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);
            Assert.Contains("provider_scope_not_approved", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            var inScope = await Purchase("COMM-SVC-A");
            await plant.Ok(approver, $"/api/procurement/purchase-orders/{inScope}/approve", new { reason = "Approve only the qualified actual service" });
            await using var connection = new NpgsqlConnection(app.ConnectionString);
            await connection.OpenAsync();
            await using var query = new NpgsqlCommand("SELECT payload::text FROM quality.commercial_use", connection);
            await using var rows = await query.ExecuteReaderAsync();
            var acceptedUses = new List<string>();
            while (await rows.ReadAsync()) acceptedUses.Add(rows.GetString(0));
            var acceptedUse = Assert.Single(acceptedUses);
            Assert.Contains("PurchaseApproval", acceptedUse, StringComparison.Ordinal);
            Assert.Contains(inScope.ToString(CultureInfo.InvariantCulture), acceptedUse, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Effective_customer_requirements_follow_the_actual_contract_and_part_into_order_release()
    {
        var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: b => b.UseSetting(QmsService.ConfigurationKey, "COMMERCIAL-TEST-ORG"));
        try
        {
            var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
            foreach (var role in new[] { Roles.QualityEngineer, Roles.ProgramManager })
                await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["eng"]}/roles", new { role, reason = "Author actual contract-scoped customer requirements" });
            foreach (var role in new[] { Roles.QualityManager, Roles.ProgramManager })
                await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["approver"]}/roles", new { role, reason = "Independently review actual customer requirements" });
            var program = Guid.Parse((await plant.Ok(plant.Engineer, "/api/programs/", new { code = "COMM-ORDER", name = "Commercial customer requirements example", customer = "Actual contract customer", reason = "Create actual customer program" })).GetProperty("entityId").GetString()!);
            foreach (var person in new[] { "eng", "approver", "planner" })
                await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[person]}/programs", new { programId = program, reason = "Explicit actual contract source membership" });
            var contract = Id(await plant.Ok(plant.Engineer, $"/api/programs/{program}/contracts", new { number = "COMM-CONTRACT", customer = "Actual contract customer", title = "Actual production contract",
                periodStart = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)), periodEnd = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(90)), ceiling = 100000m, reason = "Create actual customer contract" }));
            await plant.Ok(plant.Engineer, $"/api/programs/contracts/{contract}/clins", new { clin = "0001", description = "Actual ROLL-100 customer order", partNumber = "ROLL-100", quantity = 100m, unitPrice = 10m, reason = "Record actual contract part scope" });
            var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 1m, dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)), priority = 5, programId = program, reason = "Create actual customer order" }));
            await plant.Ok(plant.Engineer, $"/api/orders/{order}/contract-commitment", new { contractId = contract, clin = "0001", reason = "Commit actual order to contract" });

            async Task EffectiveRequirements(string code, string part)
            {
                var content = new
                {
                    customerRequirements = new
                    {
                        contractId = contract, deliveryId = (long?)null, partNumber = part, partRevision = "A",
                        requirements = new[] { new { code = "QUALITY-1", kind = "Quality", requirement = "Customer-defined recorded acceptance requirement", acceptanceBasis = "Actual contract quality basis", evidence = Array.Empty<object>() } },
                        applicability = Array.Empty<object>(), enforceAtOrderRelease = true, enforceAtShipment = false,
                        requireIndependentComplaintClosure = true, reviewBasis = "Actual customer and contract review",
                        requireCustomerConcessionForNonconformingRelease = false, customerSource = (object?)null
                    }
                };
                await plant.Ok(plant.Engineer, Api + "/records", new { kind = "CustomerRequirements", code, facilityCode = "PLANT1", content, reason = "Record actual customer requirements" });
                var path = Api + $"/records/CustomerRequirements/{code}/revisions/1";
                await plant.Ok(plant.Engineer, path + "/submit", new { expectedVersion = 1, basis = "Exact contract configuration assessed", reason = "Submit customer requirements" });
                await plant.Ok(plant.Approver, path + "/review", new { expectedVersion = 2, basis = "Actual contract and customer obligations reviewed", reason = "Independent requirements review" });
                await plant.Ok(plant.Approver, path + "/approve", new { expectedVersion = 3, basis = "Required customer determination is explicit", reason = "Approve controlled requirements" });
                await plant.Ok(plant.Approver, path + "/effective", new { expectedVersion = 4, basis = "Publish the exact contract and part scope", reason = "Make requirements effective" });
            }

            await EffectiveRequirements("COMM-WRONG-PART", "COIL-AL");
            var blocked = await plant.Try(planner, $"/api/orders/{order}/release", new { reason = "Attempt order release without matching customer scope" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
            Assert.Contains("customer_requirements_scope_unapproved", await blocked.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            await EffectiveRequirements("COMM-CORRECT-PART", "ROLL-100");
            await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Release against effective actual customer requirements" });
            await using var connection = new NpgsqlConnection(app.ConnectionString);
            await connection.OpenAsync();
            await using var query = new NpgsqlCommand("SELECT count(*) FROM quality.commercial_use WHERE payload->>'entityType'='ProductionOrder' AND payload->>'entityId'=$1", connection);
            query.Parameters.AddWithValue(order.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(1L, (long)(await query.ExecuteScalarAsync())!);
        }
        finally { await app.DisposeAsync(); }
    }
}
