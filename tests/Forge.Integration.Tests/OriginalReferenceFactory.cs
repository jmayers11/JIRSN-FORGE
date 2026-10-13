using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// The recovered V3 brief BN names sixteen populations, each at hundreds of records. Every record here
/// is created by an ordinary authenticated command; SQL below only verifies the persisted populations.
/// Material requests are real staging tasks, BOM revisions contain released BOM lines, employees are
/// registered workers, transformations account for issued material, and shipments actually leave stock.
/// </summary>
internal sealed class OriginalReferenceFactory
{
    internal const int Population = 200;
    internal const string Prefix = "BN-";
    internal const string Activity = "BN-MAKE";
    // V327's factual shift fixture already defines the controlled full-day PLANT1 calendar.
    // Reuse it: production reports resolve their timestamps against current calendars, so a
    // duplicate covering window would make the retained last-shift source attribution ambiguous.
    internal const string Shift = "REF-ALL";
    internal const string FirstMachine = "BN-M-001";
    internal const string AlternateMachine = "BN-ZALT-001";
    internal const string FirstPart = "BN-MAKE-001";
    internal const string FirstRaw = "BN-RAW-001";
    internal const string FirstDocument = "BN-DOC-001";

    internal List<long> Orders { get; } = [];
    internal List<long> PurchaseOrders { get; } = [];
    internal List<long> Transformations { get; } = [];
    internal List<long> MaterialRequests { get; } = [];
    internal List<long> MaintenanceRecords { get; } = [];
    internal List<long> Shipments { get; } = [];
    internal List<Guid> Employees { get; } = [];
    internal long FirstOrder => Orders[0];
    internal long SiblingOrder { get; private set; }
    internal long Goal { get; private set; }
    internal long DeliveryGoal { get; private set; }
    internal string RequiredBy { get; private set; } = string.Empty;
    internal decimal FirstRequiredQuantity { get; private set; }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
    private static string Day(DateTimeOffset now, int days) => now.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static async Task<OriginalReferenceFactory> BuildAsync(SlittingPlant.Plant plant, HttpClient planner, string kind, DateTimeOffset now)
    {
        var factory = new OriginalReferenceFactory { RequiredBy = Day(now, 30) };
        var engineer = plant.Engineer;
        var stores = plant.As("stores");
        await plant.Person("bn-buyer", "Buyer");
        await plant.Person("bn-purchasing-approver", "PurchasingManager");
        await plant.Person("bn-shipper", "Shipper");
        await plant.Person("bn-maintenance", "MaintenancePlanner");
        await plant.Person("bn-schedule-approver", "Planner");
        var buyer = plant.As("bn-buyer");
        var manager = plant.As("bn-purchasing-approver");
        var shipper = plant.As("bn-shipper");
        var maintenance = plant.As("bn-maintenance");
        await plant.Ok(engineer, "/api/resources/activity-types", new { code = Activity, name = "Reference part manufacture", reason = "BN factory activity" });

        for (var index = 1; index <= Population; index++)
        {
            var suffix = index.ToString("000", CultureInfo.InvariantCulture);
            var raw = "BN-RAW-" + suffix;
            var made = "BN-MAKE-" + suffix;
            var wc = "BN-WC-" + suffix;
            var machine = "BN-M-" + suffix;
            var qualification = "BN-Q-" + suffix;
            var subject = "bn-worker-" + suffix;
            var document = "BN-DOC-" + suffix;
            var supplier = "BN-SUP-" + suffix;
            var staging = "BN-STAGE-" + suffix;
            // A different order's lot deliberately contains the first order number. Exact order scope
            // must not leak that unrelated reconciliation row through an ordinary substring search.
            var lot = index == 2 ? factory.FirstOrder.ToString(CultureInfo.InvariantCulture) : "BN-LOT-" + suffix;
            // Fractional mass and multi-skilled people are valid data, not integrity exceptions.
            var quantity = kind == "unusual-valid" && index % 10 == 0 ? 0.001m : 1m;
            var required = 100m * quantity;
            if (index == 1) factory.FirstRequiredQuantity = required;

            await plant.Ok(engineer, "/api/resources/work-centers", new { code = wc, name = "BN cell " + suffix, parent = "ASSEMBLY", reason = "BN factory work center" });
            await plant.Ok(engineer, $"/api/resources/work-centers/{wc}/shifts", new { shiftCode = Shift, reason = "BN cell calendar" });
            await plant.Ok(engineer, $"/api/resources/work-centers/{wc}/activities", new { activityCode = Activity, reason = "BN work center activity" });
            await plant.Ok(engineer, "/api/competency/qualification-types", new { code = qualification, name = "BN machine qualification " + suffix, kind = "Machine", validityDays = 365, reason = "BN competency definition" });
            await plant.Ok(engineer, "/api/resources/machines", new { code = machine, name = "BN machine " + suffix, parent = wc, reason = "BN factory machine" });
            await plant.Ok(engineer, $"/api/resources/machines/{machine}/activities", new { activityCode = Activity, operatorQualifications = new[] { qualification }, reason = "BN required machine qualification" });
            if (index == 1)
            {
                await plant.Ok(engineer, "/api/resources/machines", new { code = AlternateMachine, name = "BN approved alternate machine", parent = wc, reason = "BN recovery capacity" });
                await plant.Ok(engineer, $"/api/resources/machines/{AlternateMachine}/activities", new { activityCode = Activity, operatorQualifications = new[] { qualification }, reason = "Same qualification on the approved alternate" });
            }

            var employee = await plant.Person(subject);
            factory.Employees.Add(employee);
            await plant.Ok(engineer, $"/api/workforce/workers/{employee}", new { homeWorkCenterCode = wc, reason = "BN registered employee" });
            await plant.Ok(engineer, $"/api/competency/people/{employee}/training", new { code = qualification, reason = "BN training record" });
            await plant.Ok(plant.Approver, $"/api/competency/people/{employee}/certifications", new { code = qualification, evidence = "BN practical assessment " + suffix, reason = "Independent practical certification" });
            await plant.Ok(engineer, $"/api/workforce/workers/{employee}/shift", new { shiftCode = Shift, reason = "BN scheduled employee" });
            await plant.Ok(engineer, $"/api/workforce/workers/{employee}/presence", new { kind = "SupervisorConfirmed", validUntil = now.AddHours(16), reason = "BN confirmed presence window" });
            await plant.Assign(subject, new { workCenterCode = wc, activityCode = Activity, reason = "BN cell assignment" });
            if (kind == "unusual-valid" && index % 10 == 0)
            {
                await plant.Ok(engineer, $"/api/competency/people/{employee}/training", new { code = "FIRST-AID", reason = "Valid additional skill" });
                await plant.Ok(plant.Approver, $"/api/competency/people/{employee}/certifications", new { code = "FIRST-AID", evidence = "BN responder assessment " + suffix, reason = "Independently certified additional skill" });
            }

            foreach (var (part, makeBuy) in new[] { (raw, "Buy"), (made, "Make") })
            {
                await plant.Ok(engineer, "/api/engineering/items", new { partNumber = part, description = "BN reference " + part, unitOfMeasure = "KG", makeBuy, leadTimeDays = 2, lotTracked = true, reason = "BN engineering item" });
                await plant.Ok(engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A", reason = "BN baseline revision" });
            }
            await plant.Ok(plant.Approver, $"/api/engineering/items/{raw}/revisions/A/release", new { reason = "Independent BN raw material release" });

            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files")
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes($"{document} A: manufacture {made} from {raw}, qualified operator {qualification}; retain measured material evidence.")),
            })
            {
                request.Headers.Add("Idempotency-Key", "bn-document-" + suffix);
                request.Headers.Add("X-File-Name", document + ".txt");
                using var response = await engineer.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.IsSuccessStatusCode, body);
                using var uploaded = JsonDocument.Parse(body);
                var hash = uploaded.RootElement.GetProperty("entityId").GetString();
                await plant.Ok(engineer, "/api/engineering/documents", new { number = document, title = "BN manufacturing instruction " + suffix, type = "WorkInstruction", authority = "Internal", reason = "BN controlled document" });
                await plant.Ok(engineer, $"/api/engineering/documents/{document}/revisions", new { revision = "A", fileSha256 = hash, fileName = document + ".txt", reason = "BN immutable instruction file" });
            }
            await plant.Ok(engineer, $"/api/engineering/documents/{document}/revisions/A/submit", new { reason = "Submit BN instruction for independent review" });
            if (index == 1)
                Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(engineer, $"/api/engineering/documents/{document}/revisions/A/approve", new { reason = "An author without approval authority cannot approve" })).StatusCode);
            await plant.Ok(plant.Approver, $"/api/engineering/documents/{document}/revisions/A/approve", new { reason = "Independently review BN instruction" });
            await plant.Ok(plant.Approver, $"/api/engineering/documents/{document}/revisions/A/release", new { reason = "Independent BN instruction release" });
            await plant.Ok(engineer, $"/api/engineering/items/{made}/revisions/A/bom", new { lineNumber = 1, componentPartNumber = raw, quantityPer = 100m, scrapPercent = 0m, reason = "BN material requirement" });
            await plant.Ok(engineer, $"/api/engineering/items/{made}/revisions/A/routing", new { sequence = 10, description = "Manufacture BN part", workCenterCode = wc, activityCode = Activity, eligibleMachines = index == 1 ? new[] { machine, AlternateMachine } : new[] { machine }, setupMinutes = 0m, runMinutesPerUnit = 0.01m, crewSize = 1, reason = "BN controlled route" });
            await plant.Ok(engineer, $"/api/engineering/items/{made}/revisions/A/documents", new { documentNumber = document, documentRevision = "A", operationSequence = 10, reason = "BN pinned work instruction" });
            await plant.Ok(plant.Approver, $"/api/engineering/items/{made}/revisions/A/release", new { reason = "Independent BN BOM and route release" });
            await plant.Ok(engineer, "/api/engineering/document-training/", new { personId = employee, documentNumber = document, revision = "A", reason = "BN instruction training" });

            await plant.Ok(buyer, "/api/procurement/suppliers", new { code = supplier, name = "BN supplier " + suffix, reason = "BN supplier registration" });
            var po = Id(await plant.Ok(buyer, "/api/procurement/purchase-orders", new { supplier, lines = new[] { new { partNumber = raw, quantity = 500m, promisedDate = Day(now, 7) } }, reason = "BN replenishment purchase" }));
            factory.PurchaseOrders.Add(po);
            if (index == 1)
                Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(buyer, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "A buyer without approval authority cannot approve" })).StatusCode);
            await plant.Ok(manager, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Independent BN purchasing approval" });
            await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = raw, quantity = 300m, locationCode = "STORES", lotNumber = lot, supplierLot = "BN-SUPLOT-" + suffix, poNumber = po, poLine = 1, revision = "A", reason = "BN traceable receipt against approved purchasing" });
            await plant.Ok(stores, "/api/inventory/locations", new { code = staging, siteCode = "PLANT1", name = "BN staging " + suffix, kind = "Staging", workCenterCode = wc, reason = "BN material point of use" });
            var order = Id(await plant.Ok(engineer, "/api/orders/", new { partNumber = made, quantity, dueDate = factory.RequiredBy, priority = 1 + index % 5, reason = "BN production order" }));
            factory.Orders.Add(order);
            await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "BN release against approved definition" });
            var task = Id(await plant.Ok(stores, "/api/inventory/tasks", new { kind = "Stage", orderNumber = order, partNumber = raw, quantity = required, toLocation = staging, lotNumber = lot, reason = "BN requested material" }));
            factory.MaterialRequests.Add(task);
            await plant.Ok(stores, $"/api/inventory/tasks/{task}/allocate", new { fromLocation = "STORES", lotNumber = lot, reason = "BN allocate identified material" });
            await plant.Ok(stores, $"/api/inventory/tasks/{task}/pick", new { scannedLocation = "STORES", scannedLot = lot, quantity = required, reason = "BN scanned pick" });
            await plant.Ok(stores, $"/api/inventory/tasks/{task}/deliver", new { scannedLocation = staging, reason = "BN scanned delivery" });
            await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = raw, quantity = required, locationCode = staging, lotNumber = lot, reason = "BN issue from point of use" });
            factory.Transformations.Add(Id(await plant.Ok(stores, "/api/inventory/transformations", new { orderNumber = order, operationSequence = 10, entries = new[] { new { role = "Consumed", partNumber = raw, lotNumber = lot, quantity = required } }, reason = "BN measured material consumption" })));
            factory.MaintenanceRecords.Add(Id(await plant.Ok(maintenance, "/api/maintenance/work-orders", new { machineCode = machine, title = "BN planned condition check " + suffix, dueDate = Day(now, 60), kind = "Inspection", reason = "BN future maintenance record" })));

            var shipment = Id(await plant.Ok(stores, "/api/inventory/shipments", new { customer = "BN stock customer " + suffix, reference = "BN-SHIP-" + suffix, lines = new[] { new { partNumber = raw, quantity = 1m, locationCode = "STORES", lotNumber = lot } }, reason = "BN customer stock shipment" }));
            factory.Shipments.Add(shipment);
            await plant.Ok(stores, $"/api/inventory/shipments/{shipment}/pack", new { reason = "BN identified contents checked" });
            if (index == 1)
                Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(stores, $"/api/inventory/shipments/{shipment}/ship", new { carrier = "BN carrier", reason = "The material handler cannot release their own packing" })).StatusCode);
            await plant.Ok(shipper, $"/api/inventory/shipments/{shipment}/ship", new { carrier = "BN carrier", tracking = "BN-TRACK-" + suffix, reason = "Independent BN shipping release" });
        }

        // A second order of the same controlled part makes the downstream affected scope explicit.
        factory.SiblingOrder = Id(await plant.Ok(engineer, "/api/orders/", new { partNumber = FirstPart, quantity = 1m, dueDate = factory.RequiredBy, priority = 3, reason = "BN sibling order sharing the first cell" }));
        await plant.Ok(planner, $"/api/orders/{factory.SiblingOrder}/release", new { reason = "Release the explicitly related order" });
        var siblingTask = Id(await plant.Ok(stores, "/api/inventory/tasks", new { kind = "Stage", orderNumber = factory.SiblingOrder, partNumber = FirstRaw, quantity = 100m, toLocation = "BN-STAGE-001", fromLocation = "STORES", lotNumber = "BN-LOT-001", reason = "BN sibling material request" }));
        await plant.Ok(stores, $"/api/inventory/tasks/{siblingTask}/pick", new { scannedLocation = "STORES", scannedLot = "BN-LOT-001", quantity = 100m, reason = "BN sibling scanned pick" });
        await plant.Ok(stores, $"/api/inventory/tasks/{siblingTask}/deliver", new { scannedLocation = "BN-STAGE-001", reason = "BN sibling scanned delivery" });
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = factory.SiblingOrder, partNumber = FirstRaw, quantity = 100m, locationCode = "BN-STAGE-001", lotNumber = "BN-LOT-001", reason = "BN sibling issued material" });
        await plant.Ok(stores, "/api/inventory/transformations", new { orderNumber = factory.SiblingOrder, operationSequence = 10, entries = new[] { new { role = "Consumed", partNumber = FirstRaw, lotNumber = "BN-LOT-001", quantity = 100m } }, reason = "BN sibling measured consumption" });
        var deliveryDemand = Id(await plant.Ok(planner, "/api/planning/demand", new { partNumber = FirstPart, quantity = 2m, needDate = Day(now, 32), kind = "CustomerOrder", customer = "BN goal customer", reason = "BN linked delivery commitment" }));
        factory.DeliveryGoal = Id(await plant.Ok(planner, "/api/planning/goals", new { kind = "Delivery", title = "BN deliver first cell parts", demandId = deliveryDemand, requiredBy = Day(now, 32), reason = "BN downstream delivery goal" }));
        factory.Goal = Id(await plant.Ok(planner, "/api/planning/goals", new { kind = "Production", title = "BN make first cell part", parentGoalId = factory.DeliveryGoal, orderNumber = factory.FirstOrder, leadDays = 2, reason = "BN production goal before delivery" }));
        return factory;
    }

    internal async Task VerifyPopulationsAsync(ForgeApp app)
    {
        // Relay ordinary command events; no business state or fabricated evidence is inserted here.
        var relay = app.Services.GetRequiredService<OutboxRelay>();
        while (await relay.RelayBatchAsync(1000, CancellationToken.None) > 0) { }
        await using var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        var populations = new (string Category, string Query, string? Parameter, object? Value, int Expected)[]
        {
            ("parts", "SELECT count(*) FROM engineering.item WHERE part_number LIKE 'BN-%'", null, null, Population * 2),
            ("inventory instances", "SELECT count(*) FROM inventory.lot WHERE part_number LIKE 'BN-RAW-%'", null, null, Population),
            ("employees", "SELECT count(*) FROM workforce.worker WHERE person_id = ANY(@ids)", "ids", Employees.ToArray(), Population),
            ("qualifications", "SELECT count(*) FROM competency.person_qualification WHERE code LIKE 'BN-Q-%' AND status = 'Qualified' AND person_id = ANY(@ids)", "ids", Employees.ToArray(), Population),
            ("work centers", "SELECT count(*) FROM resources.work_center WHERE code LIKE 'BN-WC-%'", null, null, Population),
            ("machines", "SELECT count(*) FROM resources.machine WHERE code LIKE 'BN-M-%'", null, null, Population),
            ("BOM revisions", "SELECT count(*) FROM engineering.revision r WHERE r.part_number LIKE 'BN-MAKE-%' AND r.status = 'Released' AND EXISTS (SELECT 1 FROM engineering.bom_line b WHERE b.part_number = r.part_number AND b.revision = r.revision)", null, null, Population),
            ("documents", "SELECT count(*) FROM engineering.document_revision WHERE document_number LIKE 'BN-DOC-%' AND status = 'Released' AND file_sha256 IS NOT NULL", null, null, Population),
            ("suppliers", "SELECT count(*) FROM procurement.supplier WHERE code LIKE 'BN-SUP-%'", null, null, Population),
            ("purchase orders", "SELECT count(*) FROM procurement.purchase_order WHERE po_number = ANY(@ids) AND status = 'Approved' AND approved_by <> created_by", "ids", PurchaseOrders.ToArray(), Population),
            ("production orders", "SELECT count(*) FROM execution.production_order WHERE order_number = ANY(@ids) AND status = 'Released'", "ids", Orders.ToArray(), Population),
            ("transformations", "SELECT count(*) FROM inventory.transformation WHERE transformation_id = ANY(@ids)", "ids", Transformations.ToArray(), Population),
            ("material requests", "SELECT count(*) FROM inventory.material_task WHERE task_id = ANY(@ids) AND kind = 'Stage' AND status = 'Delivered'", "ids", MaterialRequests.ToArray(), Population),
            ("maintenance records", "SELECT count(*) FROM maintenance.work_order WHERE work_order_id = ANY(@ids)", "ids", MaintenanceRecords.ToArray(), Population),
            ("shipments", "SELECT count(*) FROM inventory.shipment WHERE shipment_id = ANY(@ids) AND status = 'Shipped' AND shipped_by <> packed_by", "ids", Shipments.ToArray(), Population),
            // Registration, shift, recorded presence and assignment each emit one actual worker event.
            ("events", "SELECT count(*) FROM kernel.event_log WHERE entity_type = 'Worker' AND entity_id = ANY(@ids)", "ids", Employees.Select(id => id.ToString("D")).ToArray(), Population * 4),
        };
        Assert.Equal(16, populations.Length);
        foreach (var population in populations)
        {
            await using var command = new NpgsqlCommand(population.Query, connection);
            if (population.Parameter is { } name) command.Parameters.AddWithValue(name, population.Value!);
            var actual = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            Assert.True(actual == population.Expected, $"BN {population.Category}: expected {population.Expected} actual persisted records, found {actual}.");
        }
    }

    internal async Task VerifyUniversalQuestionsAsync(SlittingPlant.Plant plant, HttpClient planner, string kind, DateTimeOffset now, Action<TimeSpan> advance)
    {
        // Presence must be recorded in the current shift; advancing a test clock never manufactures it.
        foreach (var employee in Employees)
            await plant.Ok(plant.Engineer, $"/api/workforce/workers/{employee}/presence", new { kind = "SupervisorConfirmed", reason = "Confirm BN current-shift presence" });

        var baseline = Id(await plant.Ok(planner, "/api/planning/schedules", new { horizonDays = 14, reason = "BN approved goal baseline at full factory scale" }));
        await plant.Ok(plant.As("bn-schedule-approver"), $"/api/planning/schedules/{baseline}/approve", new { reason = "Independent BN baseline approval" });
        var schedule = await GetAsync(planner, $"/api/planning/schedules/{baseline}");
        var scheduledOrder = schedule.GetProperty("result").GetProperty("orders").EnumerateArray().Single(order => order.GetProperty("orderNumber").GetInt64() == FirstOrder);
        Assert.Equal("OnTime", scheduledOrder.GetProperty("status").GetString());
        var scheduledFinish = scheduledOrder.GetProperty("projectedCompletion").GetDateTimeOffset();
        var categories = new HashSet<string>(StringComparer.Ordinal);

        async Task<JsonElement> Budget(string category, Func<Task<JsonElement>> read)
        {
            Assert.True(categories.Add(category), $"BN category {category} must have its own acceptance answer.");
            var stopwatch = Stopwatch.StartNew();
            var answer = await read();
            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"{kind}: BN {category} exceeded 10 seconds with at least 200 actual records in each of all sixteen populations ({stopwatch.Elapsed}).");
            return answer;
        }

        async Task<JsonElement> Ask(HttpClient client, string question, string intent)
        {
            using var response = await client.PostAsJsonAsync("/api/analyst/ask", new { question });
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            using var document = JsonDocument.Parse(body);
            var answer = document.RootElement.Clone();
            Assert.False(answer.GetProperty("notAuthorized").GetBoolean(), body);
            Assert.Equal(intent, answer.GetProperty("interpretation").GetProperty("intent").GetString());
            return answer;
        }

        // CAN THE GOAL BE MET? The specified production goal is independently reforecast against its date.
        var attainment = await Budget("CAN THE GOAL BE MET?", () => Ask(planner, "Show goal attainment", "Report"));
        Assert.Equal("goal-attainment", attainment.GetProperty("interpretation").GetProperty("report").GetProperty("reportId").GetString());
        var goalRow = attainment.GetProperty("rows").EnumerateArray().Single(row => Cell(attainment, row, "goal").GetString() == Goal.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(("Production", "OnTrack", 0m, 1m, DeliveryGoal.ToString(CultureInfo.InvariantCulture)),
            (Cell(attainment, goalRow, "kind").GetString(), Cell(attainment, goalRow, "status").GetString(), Cell(attainment, goalRow, "done").GetDecimal(), Cell(attainment, goalRow, "target").GetDecimal(), Cell(attainment, goalRow, "parent").GetString()));
        Assert.Equal(RequiredBy, Cell(attainment, goalRow, "requiredBy").GetString());
        Assert.True(Cell(attainment, goalRow, "projected").GetDateTimeOffset() < DateTimeOffset.Parse(RequiredBy, CultureInfo.InvariantCulture).AddDays(1));
        Assert.Contains("order:" + FirstOrder, Traces(goalRow));
        Assert.Contains("goal:" + Goal, Traces(goalRow));

        // WHY? Preserve the authoritative scheduler explanation and its actual approved version.
        var why = await Budget("WHY?", () => Ask(planner, $"Why is order {FirstOrder} on schedule?", "OrderStatus"));
        Assert.Contains(why.GetProperty("facts").EnumerateArray(), fact => fact.GetProperty("text").GetString() == scheduledOrder.GetProperty("explanation").GetString()
            && fact.GetProperty("trace").GetString() == "schedule:" + baseline);
        Assert.Contains("Approved schedule " + baseline, why.GetProperty("sources").EnumerateArray().Select(source => source.GetString()));

        // WHAT IS REQUIRED? The pinned instruction plus the frozen BOM, qualification and staged issue facts.
        var required = await Budget("WHAT IS REQUIRED?", () => Ask(planner, $"What documentation applies to order {FirstOrder}?", "OrderDocumentation"));
        var instruction = Assert.Single(required.GetProperty("rows").EnumerateArray());
        Assert.Equal((FirstDocument, "A", "A"), (Cell(required, instruction, "document").GetString(), Cell(required, instruction, "atRelease").GetString(), Cell(required, instruction, "applies").GetString()));
        Assert.Contains("document:" + FirstDocument, Traces(instruction));
        var orderDefinition = await GetAsync(planner, $"/api/orders/{FirstOrder}");
        var material = Assert.Single(orderDefinition.GetProperty("materials").EnumerateArray());
        Assert.Equal((FirstRaw, FirstRequiredQuantity), (material.GetProperty("componentPartNumber").GetString(), material.GetProperty("requiredQuantity").GetDecimal()));
        var resources = await GetAsync(planner, "/api/resources/");
        var activity = resources.GetProperty("machineActivities").EnumerateArray().Single(record => record.GetProperty("machineCode").GetString() == FirstMachine);
        Assert.Equal("BN-Q-001", Assert.Single(activity.GetProperty("operatorQualifications").EnumerateArray()).GetString());
        var flow = await GetAsync(planner, $"/api/inventory/orders/{FirstOrder}/material-flow");
        var flowRow = Assert.Single(flow.GetProperty("lines").EnumerateArray());
        Assert.Equal((FirstRequiredQuantity, FirstRequiredQuantity, FirstRequiredQuantity, FirstRequiredQuantity, FirstRequiredQuantity, FirstRequiredQuantity),
            (flowRow.GetProperty("required").GetDecimal(), flowRow.GetProperty("requested").GetDecimal(), flowRow.GetProperty("picked").GetDecimal(), flowRow.GetProperty("delivered").GetDecimal(), flowRow.GetProperty("issued").GetDecimal(), flowRow.GetProperty("consumed").GetDecimal()));

        // BY WHEN? Compare the actual approved completion with the goal's cascaded required-by date.
        var when = await Budget("BY WHEN?", () => Ask(planner, $"When will order {FirstOrder} finish?", "OrderStatus"));
        Assert.Contains(when.GetProperty("facts").EnumerateArray(), fact => fact.GetProperty("trace").GetString() == "order:" + FirstOrder && fact.GetProperty("text").GetString()!.Contains(RequiredBy, StringComparison.Ordinal));
        Assert.Contains(when.GetProperty("facts").EnumerateArray(), fact => fact.GetProperty("trace").GetString() == "schedule:" + baseline);
        Assert.True(scheduledFinish < DateTimeOffset.Parse(RequiredBy, CultureInfo.InvariantCulture).AddDays(1));

        // Inject a factual disruption in every variant. Healthy/unusual factories restore it; the
        // constrained factory keeps the unavailable machine and uses the approved alternate.
        var disruption = "BN injected " + kind + " machine interruption";
        var downCommand = new { description = disruption, reason = "Exercise BN recorded disruption" };
        using (var first = await plant.Engineer.CommandAsync($"/api/maintenance/machines/{FirstMachine}/down", downCommand, "bn-down-retry"))
        using (var retry = await plant.Engineer.CommandAsync($"/api/maintenance/machines/{FirstMachine}/down", downCommand, "bn-down-retry"))
        {
            var firstBody = await first.Content.ReadAsStringAsync();
            var retryBody = await retry.Content.ReadAsStringAsync();
            Assert.True(first.IsSuccessStatusCode, firstBody);
            Assert.True(retry.IsSuccessStatusCode, retryBody);
            using var firstResult = JsonDocument.Parse(firstBody);
            using var retryResult = JsonDocument.Parse(retryBody);
            Assert.Equal(firstResult.RootElement.GetProperty("entityId").GetString(), retryResult.RootElement.GetProperty("entityId").GetString());
            Assert.Equal(firstResult.RootElement.GetProperty("message").GetString(), retryResult.RootElement.GetProperty("message").GetString());
        }
        var blockers = await Budget("WHAT IS BLOCKING IT?", () => Ask(planner, $"What is preventing {FirstMachine} from running?", "MachineBlockers"));
        Assert.Equal(FirstMachine + " cannot run now.", blockers.GetProperty("headline").GetString());
        Assert.Contains(blockers.GetProperty("facts").EnumerateArray(), fact => fact.GetProperty("trace").GetString() == "machine:" + FirstMachine && fact.GetProperty("text").GetString()!.Contains(disruption, StringComparison.Ordinal));
        var readiness = await GetAsync(planner, $"/api/orders/{FirstOrder}/readiness");
        var operation = Assert.Single(readiness.GetProperty("output").GetProperty("operations").EnumerateArray());
        Assert.True(operation.GetProperty("executable").GetBoolean(), readiness.GetRawText());
        Assert.Equal(AlternateMachine, operation.GetProperty("bestMachine").GetString());
        Assert.Contains(operation.GetProperty("candidates").EnumerateArray(), candidate => candidate.GetProperty("machineCode").GetString() == FirstMachine && !candidate.GetProperty("executable").GetBoolean());
        // A restored downtime window must have an actual later end than its recorded start.
        advance(TimeSpan.FromMinutes(1));
        now = now.AddMinutes(1);
        if (kind != "constrained")
            await plant.Ok(plant.Engineer, $"/api/maintenance/machines/{FirstMachine}/restore", new { reason = "BN recovery restored inspected machine" });

        // WHAT CHANGED? Match a precise recorded machine event and actor, rather than a coincident claim.
        var changed = await Budget("WHAT CHANGED?", () => Ask(planner, "What changed for machine " + FirstMachine + "?", "WhatChanged"));
        var history = await GetAsync(planner, "/api/history/" + FirstMachine + "?kind=machine");
        var sourceEvent = Assert.Single(history.GetProperty("events").EnumerateArray(), record => record.GetProperty("kind").GetString() == "Downtime (Unplanned)" && record.GetProperty("title").GetString()!.Contains(disruption, StringComparison.Ordinal));
        // The history DTO omits downtime actors; the immutable command audit records the actual actor.
        var audit = await GetAsync(plant.Admin, "/api/kernel/audit?entityType=Machine&entityId=" + FirstMachine);
        var downAudit = Assert.Single(audit.EnumerateArray().Select(record => record.GetProperty("entry")), entry => entry.GetProperty("action").GetString() == "ReportMachineDown" && entry.GetProperty("outcome").GetString() == "Succeeded");
        Assert.Equal("person:" + plant.People["eng"].ToString("D"), downAudit.GetProperty("actor").GetString());
        Assert.Equal("Exercise BN recorded disruption", downAudit.GetProperty("reason").GetString());
        Assert.Contains(changed.GetProperty("facts").EnumerateArray(), fact => fact.GetProperty("trace").GetString() == sourceEvent.GetProperty("link").GetString()
            && fact.GetProperty("text").GetString()!.Contains(sourceEvent.GetProperty("title").GetString()!, StringComparison.Ordinal));

        // WHAT ELSE IS AFFECTED? A proposed controlled revision exposes both real related orders.
        await plant.Ok(plant.Engineer, $"/api/engineering/items/{FirstPart}/revisions", new { revision = "B", reason = "BN proposed revision for impact review" });
        var change = await plant.Ok(plant.Engineer, "/api/engineering/changes", new { title = "BN first-cell process change", @class = "Minor", type = "Process", changeReason = "BN disruption follow-up", reason = "Review actual affected scope before any release" });
        var number = change.GetProperty("entityId").GetString();
        await plant.Ok(plant.Engineer, $"/api/engineering/changes/{number}/lines", new { kind = "ItemRevision", subject = FirstPart, toRevision = "B", reason = "BN change line names the actual controlled part" });
        var affected = await Budget("WHAT ELSE IS AFFECTED?", () => GetAsync(plant.Engineer, $"/api/engineering/changes/{number}/impact"));
        Assert.Equal(number, affected.GetProperty("changeNumber").GetString());
        var affectedOrders = affected.GetProperty("records").EnumerateArray().Where(record => record.GetProperty("category").GetString() == "ReleasedNotStarted").ToArray();
        Assert.Equal(new[] { "order:" + FirstOrder, "order:" + SiblingOrder }.Order(StringComparer.Ordinal), affectedOrders.Select(record => record.GetProperty("trace").GetString()).Order(StringComparer.Ordinal));
        Assert.All(affectedOrders, record => Assert.Equal(FirstPart, record.GetProperty("partNumber").GetString()));
        Assert.Contains(affected.GetProperty("records").EnumerateArray(), record => record.GetProperty("category").GetString() == "Document" && record.GetProperty("trace").GetString() == "document:" + FirstDocument);

        // WHAT OPTIONS EXIST? Compare every returned option with this caller's private source scenario.
        var scenario = Guid.Parse((await plant.Ok(planner, "/api/planning/scenarios", new
        {
            name = "BN first-cell recovery",
            change = new
            {
                kind = "MachineUnavailable", machineCode = FirstMachine, startsAt = now, durationHours = 4m,
                recoverySelections = new[] { new { optionId = "alternate-machine", orderNumber = FirstOrder, sequence = 10, machineCode = AlternateMachine } },
            },
            reason = "BN evaluate approved alternate capacity",
        })).GetProperty("entityId").GetString()!);
        using (var hidden = await plant.As("bn-schedule-approver").GetAsync($"/api/planning/scenarios/{scenario:D}"))
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var projectionResponse = await planner.PostAsync($"/api/planning/scenarios/{scenario:D}/run", null);
        var projectionBody = await projectionResponse.Content.ReadAsStringAsync();
        Assert.True(projectionResponse.IsSuccessStatusCode, projectionBody);
        using var projection = JsonDocument.Parse(projectionBody);
        var sourceOptions = projection.RootElement.GetProperty("recoveryOptions").EnumerateArray().ToArray();
        var options = await Budget("WHAT OPTIONS EXIST?", () => Ask(planner, "What are my recovery options?", "RecoveryOptions"));
        var optionRows = options.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(15, sourceOptions.Length);
        Assert.Equal(sourceOptions.Length, optionRows.Length);
        var alternate = Assert.Single(sourceOptions, option => option.GetProperty("id").GetString() == "alternate-machine");
        Assert.True(alternate.GetProperty("feasible").GetBoolean(), alternate.GetRawText());
        Assert.Contains(alternate.GetProperty("affectedOrders").EnumerateArray(), order => order.GetInt64() == FirstOrder);
        Assert.Contains(alternate.GetProperty("projections").EnumerateArray(), projected => projected.GetProperty("orderNumber").GetInt64() == FirstOrder && projected.GetProperty("proposedCompletion").ValueKind == JsonValueKind.String);
        foreach (var sourceOption in sourceOptions)
        {
            var row = Assert.Single(optionRows, candidate => Cell(options, candidate, "option").GetString() == sourceOption.GetProperty("title").GetString());
            Assert.Equal(sourceOption.GetProperty("feasible").GetBoolean() ? "Yes" : "No", Cell(options, row, "feasible").GetString());
            Assert.Equal(sourceOption.GetProperty("feasibility").GetString(), Cell(options, row, "why").GetString());
            Assert.Equal(string.Join(" ", sourceOption.GetProperty("consequences").EnumerateArray().Select(consequence => consequence.GetString())), Cell(options, row, "consequences").GetString());
            Assert.Equal(sourceOption.GetProperty("requiredPermission").GetString(), Cell(options, row, "permission").GetString());
            Assert.Contains("scenario:" + scenario.ToString("D"), Traces(row));
        }

        // SHOW THE EVIDENCE. Reconcile the exact issued lot against its immutable transformation record.
        var evidence = await Budget("SHOW THE EVIDENCE.", () => Ask(planner, $"What became of the material issued to order {FirstOrder}?", "Report"));
        var evidenceRow = Assert.Single(evidence.GetProperty("rows").EnumerateArray());
        Assert.Equal((FirstRaw, "BN-LOT-001", FirstRequiredQuantity, FirstRequiredQuantity, 0m, "Balanced"),
            (Cell(evidence, evidenceRow, "part").GetString(), Cell(evidence, evidenceRow, "lot").GetString(), Cell(evidence, evidenceRow, "issued").GetDecimal(), Cell(evidence, evidenceRow, "consumed").GetDecimal(), Cell(evidence, evidenceRow, "unexplained").GetDecimal(), Cell(evidence, evidenceRow, "status").GetString()));
        Assert.Contains("order:" + FirstOrder, Traces(evidenceRow));
        var reconciliation = await GetAsync(plant.As("stores"), $"/api/inventory/orders/{FirstOrder}/reconciliation");
        var transformation = Assert.Single(reconciliation.GetProperty("transformations").EnumerateArray());
        Assert.Equal(Transformations[0], transformation.GetProperty("transformationId").GetInt64());
        Assert.Equal("person:" + plant.People["stores"].ToString("D"), transformation.GetProperty("actor").GetString());
        Assert.Equal(FirstRequiredQuantity, Assert.Single(transformation.GetProperty("lines").EnumerateArray()).GetProperty("quantity").GetDecimal());
        Assert.Equal(9, categories.Count);
    }

    private static JsonElement Cell(JsonElement answer, JsonElement row, string key)
    {
        var keys = answer.GetProperty("columns").EnumerateArray().Select(column => column.GetProperty("key").GetString()).ToList();
        var index = keys.IndexOf(key);
        Assert.True(index >= 0, "Expected source column " + key);
        return row.GetProperty("cells")[index];
    }

    private static IEnumerable<string?> Traces(JsonElement row) => row.GetProperty("trace").EnumerateArray().Select(reference => reference.GetString());

    internal static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
