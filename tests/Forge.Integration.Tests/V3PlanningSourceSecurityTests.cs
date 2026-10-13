using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Serialization;
using Forge.Planning;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V3PlanningSourceSecurityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Private_supply_remains_real_while_current_and_saved_planning_views_hide_its_source_after_revocation()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("supply-programs", "ProgramManager");
        await plant.Person("supply-plan-approver", "Planner");
        var program = Guid.Parse((await plant.Ok(plant.As("supply-programs"), "/api/programs/",
            new { code = "PRIVATE-SUPPLY", name = "Private supplying work" })).GetProperty("entityId").GetString()!);
        foreach (var subject in new[] { "eng", "planner" })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[subject]}/programs",
                new { programId = program, reason = "Authorized source creation and planning review" });

        await plant.Ok(plant.Engineer, "/api/resources/shifts", new
        {
            code = "SUPPLY-ALL", siteCode = "PLANT1", name = "Source privacy regression calendar", start = "00:00:00",
            durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 },
        });
        await plant.Ok(plant.Engineer, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "SUPPLY-ALL" });
        foreach (var subject in new[] { "sarah", "michael" })
            await plant.Ok(plant.Engineer, $"/api/workforce/workers/{plant.People[subject]}/shift", new { shiftCode = "SUPPLY-ALL" });

        const string component = "PUBLIC-SUPPLY-COMP";
        const string parent = "PUBLIC-SUPPLY-PARENT";
        await MadePartAsync(plant, component, null);
        await MadePartAsync(plant, parent, component);
        var supplyDay = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var consumerDay = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var hidden = Id(await plant.Ok(plant.Engineer, "/api/orders/", new
        {
            partNumber = component, quantity = 6m, dueDate = supplyDay, priority = 3, programId = program,
        }));
        await plant.Ok(plant.As("stores"), "/api/inventory/receipts", new
        {
            partNumber = component, quantity = 4m, locationCode = "STORES", reason = "Limited available component stock",
        });
        async Task<long> Consumer(int priority)
        {
            var number = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = parent, quantity = 10m, dueDate = consumerDay, priority }));
            await plant.Ok(planner, $"/api/orders/{number}/release", new { reason = "Independent visible production release" });
            return number;
        }
        var first = await Consumer(2);
        var second = await Consumer(4);
        var authorized = await GetAsync(planner, "/api/planning/material");
        var authorizedRow = Material(authorized, first);
        Assert.Contains("Order " + hidden + " (Planned)", authorizedRow.GetProperty("summary").GetString()!, StringComparison.Ordinal);
        Assert.Contains(authorizedRow.GetProperty("lines")[0].GetProperty("allocations").EnumerateArray(), a => a.GetProperty("source").GetString() == "WO" + hidden);

        var beforeApproval = DateTimeOffset.UtcNow.AddSeconds(-1);
        var schedule = Id(await plant.Ok(planner, "/api/planning/schedules", new { horizonDays = 14, reason = "Canonical source plan while supplier work is visible" }));
        await plant.Ok(plant.As("supply-plan-approver"), $"/api/planning/schedules/{schedule}/approve", new { reason = "Independent source schedule review" });
        var afterApproval = DateTimeOffset.UtcNow.AddSeconds(1);
        var stored = await SourceStateAsync(app, schedule);
        Assert.Contains("Order " + hidden + " (Planned)", stored, StringComparison.Ordinal);
        var scenario = Guid.Parse((await plant.Ok(planner, "/api/planning/scenarios", new
        {
            name = "Expected supply recovery",
            change = new
            {
                kind = "MachineUnavailable", machineCode = "SLITTER-B", startsAt = DateTimeOffset.UtcNow, durationHours = 1m,
                recoverySelections = new[] { new { optionId = "alternate-machine", orderNumber = first, sequence = 10, machineCode = "SLITTER-A" } },
            },
            reason = "Independent private recovery against real supply",
        })).GetProperty("entityId").GetString()!);

        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["planner"]}/programs/{program}/revoke",
            new { reason = "Supplying work is now outside this viewer's scope" });
        var material = await GetAsync(planner, "/api/planning/material");
        NoSource(material, hidden);
        var expected = Material(material, first);
        Assert.Equal("Expected", expected.GetProperty("state").GetString());
        Assert.Equal(supplyDay, expected.GetProperty("readyOn").GetString());
        var allocation = Assert.Single(expected.GetProperty("lines")[0].GetProperty("allocations").EnumerateArray(), a => a.GetProperty("dueDate").ValueKind != JsonValueKind.Null);
        Assert.Equal((MaterialSourceVisibility.Restricted, 6m, supplyDay),
            (allocation.GetProperty("source").GetString(), allocation.GetProperty("quantity").GetDecimal(), allocation.GetProperty("dueDate").GetString()));
        Assert.Equal("Shortfall", Material(material, second).GetProperty("state").GetString());
        Assert.Equal(10m, Material(material, second).GetProperty("lines")[0].GetProperty("shortfall").GetDecimal());
        Assert.Contains(material.GetProperty("evidence").GetProperty("children").EnumerateArray(), n => n.GetProperty("detail").GetString()!.StartsWith("Order " + first + ":", StringComparison.Ordinal));

        foreach (var url in new[]
        {
            $"/api/orders/{first}/readiness", "/api/readiness/operations", $"/api/planning/schedules/{schedule}", "/api/planning/constraints",
            "/api/reports/executable-operations", "/api/reports/constraint-history",
            $"/api/planning/history/compare?at1={Uri.EscapeDataString(beforeApproval.ToString("O", CultureInfo.InvariantCulture))}&at2={Uri.EscapeDataString(afterApproval.ToString("O", CultureInfo.InvariantCulture))}&includeUnchanged=true",
        }) NoSource(await GetAsync(planner, url), hidden);
        var recoveryResponse = await planner.PostAsync(new Uri($"/api/planning/scenarios/{scenario}/run", UriKind.Relative), null);
        Assert.True(recoveryResponse.IsSuccessStatusCode, await recoveryResponse.Content.ReadAsStringAsync());
        var recovery = await recoveryResponse.Content.ReadFromJsonAsync<JsonElement>();
        NoSource(recovery, hidden);
        var candidate = Assert.Single(recovery.GetProperty("recoveryOptions").EnumerateArray(), o => o.GetProperty("id").GetString() == "alternate-machine");
        Assert.True(candidate.GetProperty("feasible").GetBoolean(), candidate.GetRawText());
        foreach (var question in new[] { $"What is the status of order {first}?", "What are my recovery options?" })
        {
            var response = await planner.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question, narrate = false });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            NoSource(await response.Content.ReadFromJsonAsync<JsonElement>(), hidden);
        }
        Assert.Equal(stored, await SourceStateAsync(app, schedule));

        await plant.Ok(plant.As("stores"), "/api/inventory/receipts", new
        {
            partNumber = component, quantity = 6m, locationCode = "STORES", reason = "Actual replenishment after the privacy projection",
        });
        material = await GetAsync(planner, "/api/planning/material");
        Assert.Equal("Ready", Material(material, first).GetProperty("state").GetString());
        NoSource(material, hidden);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["planner"]}/programs",
            new { programId = program, reason = "Restore authorized source review" });
        Assert.Contains("Order " + hidden + " (Planned)", (await GetAsync(planner, $"/api/planning/schedules/{schedule}")).GetRawText(), StringComparison.Ordinal);
    }

    private static async Task MadePartAsync(SlittingPlant.Plant plant, string part, string? component)
    {
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 1 });
        await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
        if (component is not null) await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions/A/bom", new { lineNumber = 1, componentPartNumber = component, quantityPer = 1m, scrapPercent = 0m });
        await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions/A/routing", new
        {
            sequence = 10, description = "Source privacy operation", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER",
            eligibleMachines = new[] { "SLITTER-A", "SLITTER-B" }, setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1,
        });
        await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Independent released configuration" });
    }

    private static long Id(JsonElement value) => long.Parse(value.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
    private static JsonElement Material(JsonElement run, long number) => Assert.Single(run.GetProperty("output").EnumerateArray(), o => o.GetProperty("orderNumber").GetInt64() == number);
    private static void NoSource(JsonElement value, long hidden)
    {
        var text = value.GetRawText();
        foreach (var token in new[] { "Order " + hidden, "order:" + hidden, "WO" + hidden }) Assert.DoesNotContain(token, text, StringComparison.Ordinal);
    }
    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }
    private static async Task<string> SourceStateAsync(ForgeApp app, long schedule)
    {
        await using var uow = await app.Services.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(CancellationToken.None);
        var inventory = uow.Store<IInventoryLookup>();
        return CanonicalJson.Serialize(new
        {
            Orders = await uow.Store<IExecutionLookup>().ListOrdersAsync(CancellationToken.None),
            Stock = await inventory.ListBalancesAsync(null, CancellationToken.None),
            Issues = await inventory.ListOrderIssuesAsync(CancellationToken.None),
            Receipts = await inventory.ListProductionReceiptsAsync(CancellationToken.None),
            Schedule = await uow.Store<IPlanningStore>().FindVersionAsync(schedule, true, CancellationToken.None),
            Constraints = await uow.Store<IPlanningStore>().ListConstraintsAsync(true, CancellationToken.None),
        });
    }
}
