using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>Governance G/K: complete pass-on facts preserve unresolved work and the material equation preserves an exact 20-unit exception.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ShiftGovernanceTests(PostgresFixture postgres)
{
    private sealed class ShiftClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private static DateTimeOffset ShiftFrom()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TestSite.TimeZone);
        var local = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date.AddDays(-1), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    private static long Id(JsonElement value) => long.Parse(value.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
    private static string DueDate => DateTime.UtcNow.AddDays(14).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<(SlittingPlant.Plant Plant, HttpClient Planner, long Order)> BuildMaterialShiftAsync(ForgeApp app)
    {
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Ok(plant.Engineer, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "All day", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(plant.Engineer, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(plant.As("stores"), "/api/inventory/locations", new { code = "SHIFT-STAGE", siteCode = "PLANT1", name = "Shift staging", kind = "Staging", workCenterCode = "SLIT-WC" });
        foreach (var (part, makeBuy) in new[] { ("SHIFT-FOIL", "Buy"), ("SHIFT-STRIP", "Make") })
        {
            await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "M", makeBuy, leadTimeDays = 1, lotTracked = true });
            await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
        }

        await plant.Ok(plant.Engineer, "/api/engineering/items/SHIFT-STRIP/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "SHIFT-FOIL", quantityPer = 1m, scrapPercent = 0m });
        await plant.Ok(plant.Engineer, "/api/engineering/items/SHIFT-STRIP/revisions/A/routing", new { sequence = 10, description = "Slit foil", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 0m, runMinutesPerUnit = 0.01m, crewSize = 1 });
        foreach (var part in new[] { "SHIFT-FOIL", "SHIFT-STRIP" })
            await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Shift governance acceptance baseline" });
        await plant.Ok(plant.As("stores"), "/api/inventory/receipts", new { partNumber = "SHIFT-FOIL", quantity = 1000m, locationCode = "STORES", lotNumber = "SHIFT-ROLL", revision = "A", identityKind = "Roll", reason = "Physical master roll received" });
        var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "SHIFT-STRIP", quantity = 1000m, dueDate = DueDate, priority = 5 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Shift governance acceptance order" });
        return (plant, planner, order);
    }

    [Fact]
    public async Task Handoff_derives_all_G_facts_and_incoming_acknowledgment_preserves_each_unresolved_source()
    {
        var from = ShiftFrom();
        var until = from.AddHours(24);
        var clock = new ShiftClock(from.AddHours(8));
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var (plant, planner, order) = await BuildMaterialShiftAsync(app);
        var stores = plant.As("stores");
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "SHIFT-FOIL", quantity = 500m, locationCode = "STORES", lotNumber = "SHIFT-ROLL" });
        // Unrecorded material on an active order is unknown accounting, not a proven discrepancy.
        var earlierHandoffId = Id(await plant.Ok(plant.Engineer, "/api/execution/shifts/handoffs", new
        {
            workCenterCode = "SLIT-WC", shiftCode = "ALL", shiftFrom = from.AddHours(-24), shiftUntil = from,
            context = "Current issued roll is awaiting its first consumption observation.", checklist = Array.Empty<object>(), reason = "Preserve incomplete WIP accounting without inventing an exception",
        }));
        var incomplete = (await Get(plant.Engineer, $"/api/execution/shifts/handoffs/{earlierHandoffId}")).GetProperty("handoff").GetProperty("facts");
        var unrecorded = Assert.Single(incomplete.EnumerateArray(), f => f.GetProperty("kind").GetString() == "MaterialReconciliation");
        Assert.Equal("NotRecorded", unrecorded.GetProperty("status").GetString());
        Assert.Equal(500m, unrecorded.GetProperty("materialBalance").GetProperty("issued").GetDecimal());
        foreach (var unknown in new[] { "consumed", "scrap", "loss", "remnant", "remaining", "unexplained" })
            Assert.Equal(JsonValueKind.Null, unrecorded.GetProperty("materialBalance").GetProperty(unknown).ValueKind);
        Assert.DoesNotContain(incomplete.EnumerateArray(), f => f.GetProperty("kind").GetString() == "Reconciliation");
        await plant.Ok(stores, "/api/inventory/transformations", new
        {
            orderNumber = order,
            entries = new[]
            {
                new { role = "Consumed", partNumber = "SHIFT-FOIL", lotNumber = "SHIFT-ROLL", quantity = 250m },
                new { role = "Remaining", partNumber = "SHIFT-FOIL", lotNumber = "SHIFT-ROLL", quantity = 250m },
            },
        });
        var request = Id(await plant.Ok(stores, "/api/inventory/tasks", new { kind = "Stage", orderNumber = order, partNumber = "SHIFT-FOIL", quantity = 100m, toLocation = "SHIFT-STAGE", lotNumber = "SHIFT-ROLL" }));
        var preview = await plant.Ok(planner, $"/api/orders/{order}/priority-preview", new { priority = 1 });
        await plant.Ok(planner, $"/api/orders/{order}/priority", new
        {
            priority = 1, expectedPriority = 5, previewAsOf = preview.GetProperty("asOf").GetDateTimeOffset(),
            previewFingerprint = preview.GetProperty("fingerprint").GetString(), reason = "Expedite reviewed by the outgoing shift",
        });
        await plant.Ok(plant.Engineer, "/api/maintenance/machines/SLITTER-A/down", new { description = "Observed spindle vibration", reason = "Machine stopped for investigation" });
        await plant.Person("shift-quality", "QualityEngineer");
        var hold = Id(await plant.Ok(plant.As("shift-quality"), "/api/holds", new
        {
            kind = "Quality", targetType = "Lot", targetId = "SHIFT-ROLL", targetPart = "SHIFT-FOIL", source = "Outgoing shift observation",
            evidence = "Coil surface requires inspection", affectedRecords = new[] { $"order:{order}", "lot:SHIFT-ROLL" },
            requiredResolution = "Quality inspects the remaining roll", reason = "Keep the remaining roll stopped pending inspection",
        }));
        await plant.Ok(plant.Engineer, "/api/execution/shifts/checklist", new { workCenterCode = "SLIT-WC", code = "ROLL", description = "Account for the partially consumed roll", required = true, sortOrder = 1, active = true, reason = "Shift material checklist" });
        clock.UtcNow = until.AddMinutes(5);
        var handoffId = Id(await plant.Ok(plant.Engineer, "/api/execution/shifts/handoffs", new
        {
            workCenterCode = "SLIT-WC", shiftCode = "ALL", shiftFrom = from, shiftUntil = until,
            context = "Spindle investigation and remaining master roll require follow-up.",
            checklist = new[] { new { code = "ROLL", state = "Open", note = "Remaining roll is under quality hold" } }, reason = "Outgoing operational pass-on",
        }));
        var handoff = (await Get(plant.Engineer, $"/api/execution/shifts/handoffs/{handoffId}")).GetProperty("handoff");
        var facts = handoff.GetProperty("facts");
        Assert.Contains(facts.EnumerateArray(), f => f.GetProperty("kind").GetString() == "Downtime" && f.GetProperty("status").GetString() == "Open" && f.GetProperty("summary").GetString()!.Contains("spindle vibration", StringComparison.Ordinal));
        Assert.Equal(1, facts.EnumerateArray().Single(f => f.GetProperty("kind").GetString() == "Priority").GetProperty("priority").GetInt32());
        Assert.Contains(facts.EnumerateArray(), f => f.GetProperty("kind").GetString() == "MaterialRequest" && f.GetProperty("reference").GetString() == $"material-task:{request}" && f.GetProperty("status").GetString() == "Requested");
        Assert.Contains(facts.EnumerateArray(), f => f.GetProperty("kind").GetString() == "QualityHold" && f.GetProperty("reference").GetString() == $"hold:{hold}" && f.GetProperty("status").GetString() == "Open");
        var material = facts.EnumerateArray().Single(f => f.GetProperty("kind").GetString() == "MaterialReconciliation").GetProperty("materialBalance");
        Assert.Equal(("SHIFT-ROLL", "Roll", 500m, 250m, 250m, 0m),
            (material.GetProperty("lotNumber").GetString(), material.GetProperty("identityKind").GetString(), material.GetProperty("issued").GetDecimal(),
                material.GetProperty("consumed").GetDecimal(), material.GetProperty("remaining").GetDecimal(), material.GetProperty("unexplained").GetDecimal()));

        await plant.Person("shift-review", "Supervisor");
        await plant.Person("shift-incoming", "ShopFloorTerminal");
        var reviewer = plant.As("shift-review");
        await plant.Ok(reviewer, $"/api/execution/shifts/handoffs/{handoffId}/review", new { reason = "Reviewed generated facts and outgoing context" });
        await plant.Ok(reviewer, $"/api/execution/shifts/handoffs/{handoffId}/actions", new { description = "Investigate the spindle and inspect the remaining roll", sourceKind = "Order", sourceReference = order.ToString(CultureInfo.InvariantCulture), assignee = plant.People["sarah"], reason = "Assign unresolved incoming work" });
        await plant.Ok(plant.As("shift-incoming"), $"/api/execution/shifts/handoffs/{handoffId}/acknowledge", new { note = "Received; issues remain unresolved", reason = "Incoming shift receives the pass-on" });
        var after = await Get(reviewer, $"/api/execution/shifts/handoffs/{handoffId}");
        Assert.Equal(facts.GetRawText(), after.GetProperty("handoff").GetProperty("facts").GetRawText());
        Assert.Equal("Open", after.GetProperty("handoff").GetProperty("checklist")[0].GetProperty("state").GetString());
        Assert.Single(after.GetProperty("acknowledgments").EnumerateArray());
        Assert.Equal("Assigned", Assert.Single((await Get(reviewer, $"/api/execution/shifts/handoffs/{handoffId}/actions")).EnumerateArray()).GetProperty("status").GetString());
        Assert.Contains((await Get(planner, "/api/holds")).EnumerateArray(), h => h.GetProperty("reference").GetString() == hold.ToString(CultureInfo.InvariantCulture) && h.GetProperty("active").GetBoolean());
        Assert.Equal("Requested", (await Get(stores, "/api/inventory/tasks?open=true")).EnumerateArray().Single(r => r.GetProperty("taskId").GetInt64() == request).GetProperty("status").GetString());
        Assert.Contains((await Get(reviewer, $"/api/maintenance/downtime?from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(until.ToString("O", CultureInfo.InvariantCulture))}")).EnumerateArray(), d => d.GetProperty("machineCode").GetString() == "SLITTER-A" && d.GetProperty("endsAt").ValueKind == JsonValueKind.Null);
        var reconciliation = Assert.Single((await Get(stores, $"/api/inventory/orders/{order}/reconciliation")).GetProperty("rows").EnumerateArray());
        Assert.Equal((250m, 250m), (reconciliation.GetProperty("consumed").GetDecimal(), reconciliation.GetProperty("remaining").GetDecimal()));

        var closeoutId = Id(await plant.Ok(reviewer, "/api/execution/shifts/closeouts", new { workCenterCode = "SLIT-WC", shiftCode = "ALL", shiftFrom = from, shiftUntil = until, note = "Preserve unresolved incoming work", reason = "Close the outgoing shift without resolving its issues" }));
        var closeoutFacts = (await Get(reviewer, "/api/execution/shifts/closeouts?workCenter=SLIT-WC")).EnumerateArray().Single(c => c.GetProperty("closeoutId").GetInt64() == closeoutId).GetProperty("facts").GetProperty("facts");
        Assert.Contains(closeoutFacts.EnumerateArray(), f => f.GetProperty("kind").GetString() == "QualityHold" && f.GetProperty("reference").GetString() == $"hold:{hold}");
        Assert.Contains(closeoutFacts.EnumerateArray(), f => f.GetProperty("kind").GetString() == "Checklist" && f.GetProperty("status").GetString() == "Open");
        Assert.Contains(closeoutFacts.EnumerateArray(), f => f.GetProperty("kind").GetString() == "ShiftAction" && f.GetProperty("status").GetString() == "Assigned");
    }

    [Fact]
    public async Task Handoff_includes_recorded_unavailability_for_workers_assigned_to_its_work_center_without_the_private_reason()
    {
        var from = ShiftFrom();
        var until = from.AddHours(24);
        var clock = new ShiftClock(until.AddMinutes(5));
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Ok(plant.Engineer, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "All day", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(plant.Engineer, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });

        var workerId = await plant.Person("shift-absence-worker");
        await plant.Ok(plant.Admin, $"/api/workforce/workers/{workerId}", new { homeWorkCenterCode = "SLIT-WC" });
        await plant.Person("shift-absence-supervisor", "Supervisor");
        await plant.Ok(plant.As("shift-absence-supervisor"), $"/api/workforce/workers/{workerId}/unavailability", new
        {
            kind = "CallOut", startsAt = from.AddHours(4), endsAt = from.AddHours(5), reason = "PRIVATE REASON MUST NOT ENTER HANDOFF",
        });

        var handoffId = Id(await plant.Ok(plant.Engineer, "/api/execution/shifts/handoffs", new
        {
            workCenterCode = "SLIT-WC", shiftCode = "ALL", shiftFrom = from, shiftUntil = until,
            context = "Review recorded worker availability", checklist = Array.Empty<object>(), reason = "Capture work-center continuity snapshot",
        }));
        var facts = (await Get(plant.Engineer, $"/api/execution/shifts/handoffs/{handoffId}"))
            .GetProperty("handoff").GetProperty("facts");
        var fact = Assert.Single(facts.EnumerateArray(), item => item.GetProperty("kind").GetString() == "WorkerAvailability");
        Assert.Equal("CallOut", fact.GetProperty("status").GetString());
        Assert.Contains(workerId.ToString("D"), fact.GetProperty("reference").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE REASON", fact.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE REASON", facts.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closeout_preserves_exact_1000_750_200_30_equation_and_20_unit_exception_without_inventing_scrap()
    {
        var from = ShiftFrom();
        var until = from.AddHours(24);
        var clock = new ShiftClock(from.AddHours(8));
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var (plant, _, order) = await BuildMaterialShiftAsync(app);
        var stores = plant.As("stores");
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "SHIFT-FOIL", quantity = 1000m, locationCode = "STORES", lotNumber = "SHIFT-ROLL" });
        await plant.Ok(stores, "/api/inventory/returns", new { orderNumber = order, partNumber = "SHIFT-FOIL", quantity = 200m, locationCode = "STORES", lotNumber = "SHIFT-ROLL", reason = "200 unused units returned to stores" });
        await plant.Ok(stores, "/api/inventory/transformations", new
        {
            orderNumber = order, reason = "30 units of observed edge scrap",
            entries = new[]
            {
                new { role = "Consumed", partNumber = "SHIFT-FOIL", lotNumber = "SHIFT-ROLL", quantity = 750m },
                new { role = "Scrap", partNumber = "SHIFT-FOIL", lotNumber = "SHIFT-ROLL", quantity = 30m },
            },
        });
        await plant.Ok(stores, "/api/inventory/yield-tolerance/SHIFT-FOIL", new { percent = 1m, reason = "One percent is the approved material tolerance" });
        clock.UtcNow = until.AddMinutes(5);
        var closeoutId = Id(await plant.Ok(plant.Engineer, "/api/execution/shifts/closeouts", new { workCenterCode = "SLIT-WC", shiftCode = "ALL", shiftFrom = from, shiftUntil = until, note = "20 units remain unexplained", reason = "Capture the unresolved material equation" }));
        var original = (await Get(plant.Engineer, "/api/execution/shifts/closeouts?workCenter=SLIT-WC")).EnumerateArray().Single(c => c.GetProperty("closeoutId").GetInt64() == closeoutId);
        var facts = original.GetProperty("facts").GetProperty("facts");
        var exception = Assert.Single(facts.EnumerateArray(), f => f.GetProperty("kind").GetString() == "Reconciliation");
        Assert.Equal("Unexplained", exception.GetProperty("status").GetString());
        var material = exception.GetProperty("materialBalance");
        Assert.Equal((1000m, 750m, 200m, 30m, 0m, 0m, 0m, 20m, 1m),
            (material.GetProperty("issued").GetDecimal(), material.GetProperty("consumed").GetDecimal(), material.GetProperty("returned").GetDecimal(),
                material.GetProperty("scrap").GetDecimal(), material.GetProperty("loss").GetDecimal(), material.GetProperty("remnant").GetDecimal(),
                material.GetProperty("remaining").GetDecimal(), material.GetProperty("unexplained").GetDecimal(), material.GetProperty("tolerancePercent").GetDecimal()));
        Assert.Contains("do not infer scrap", exception.GetProperty("summary").GetString(), StringComparison.Ordinal);
        var row = Assert.Single((await Get(stores, $"/api/inventory/orders/{order}/reconciliation")).GetProperty("rows").EnumerateArray());
        Assert.Equal(("Unexplained", 20m, 30m), (row.GetProperty("status").GetString(), row.GetProperty("unexplained").GetDecimal(), row.GetProperty("scrap").GetDecimal()));
        Assert.Equal(200m, (await Get(stores, "/api/inventory/stock?part=SHIFT-FOIL")).EnumerateArray().Sum(b => b.GetProperty("quantity").GetDecimal()));

        // A later tolerance policy can change today's classification without rewriting the captured equation.
        await plant.Ok(stores, "/api/inventory/yield-tolerance/SHIFT-FOIL", new { percent = 3m, reason = "Later tolerance approved separately" });
        var today = Assert.Single((await Get(stores, $"/api/inventory/orders/{order}/reconciliation")).GetProperty("rows").EnumerateArray());
        Assert.Equal(("WithinTolerance", 20m, 30m), (today.GetProperty("status").GetString(), today.GetProperty("unexplained").GetDecimal(), today.GetProperty("scrap").GetDecimal()));
        var preserved = (await Get(plant.Engineer, "/api/execution/shifts/closeouts?workCenter=SLIT-WC")).EnumerateArray().Single(c => c.GetProperty("closeoutId").GetInt64() == closeoutId);
        Assert.Equal(original.GetRawText(), preserved.GetRawText());
    }
}
