using System.Globalization;
using System.Text.Json;
using Forge.Host.History;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// Owner-approved V3.22 acceptance uses the recovered original BI example plus the real engineering
/// propagation scenario in V25Tests. This tests the available source examples; it does not reconstruct
/// or claim exact parity with the unavailable original numeric section 54.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V322AvailableExampleTests(PostgresFixture postgres)
{
    private static string Day(int days) => DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private sealed record ObservedEvent(Guid Id, string Type, string Context, string EntityType, string EntityId,
        DateTimeOffset At, Guid Correlation, Guid? Cause, string Reason);

    [Fact]
    public async Task BI_at_risk_order_has_independent_machine_and_point_of_use_constraints_with_qualified_crew_and_valid_tool()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var engineer = plant.Engineer;
        var stores = plant.As("stores");
        await plant.Person("bi-quality", "QualityEngineer");
        await plant.Person("bi-schedule-approver", "Planner");

        // Real M-04 work uses the plant's existing slitting qualification and two-person safety rule.
        await plant.Ok(engineer, "/api/resources/machines", new { code = "M-04", name = "BI observed slitter", parent = "SLIT-WC", reason = "BI source machine" });
        await plant.Ok(engineer, "/api/resources/machines/M-04/activities", new { activityCode = "RUN_SLITTER", operatorQualifications = new[] { "SLIT-II" }, reason = "BI actual qualification requirement" });
        await plant.Assign("sarah", new { machineCode = "M-04", activityCode = "RUN_SLITTER", reason = "Assign the independently certified operator to M-04" });
        await plant.Ok(engineer, "/api/resources/shifts", new { code = "BI-ALL", siteCode = "PLANT1", name = "BI all-day calendar", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 }, reason = "BI production calendar" });
        await plant.Ok(engineer, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "BI-ALL", reason = "BI cell calendar" });
        foreach (var subject in new[] { "sarah", "michael" })
            await plant.Ok(engineer, $"/api/workforce/workers/{plant.People[subject]}/shift", new { shiftCode = "BI-ALL", reason = "BI qualified crew calendar" });

        var quality = plant.As("bi-quality");
        await plant.Ok(quality, "/api/resources/tool-types", new { code = "BI-GAUGE", name = "BI required gauge", kind = "Gauge", calibrationDays = 180, reason = "BI calibration-controlled tool" });
        await plant.Ok(quality, "/api/resources/tools", new { code = "BI-CAL-1", typeCode = "BI-GAUGE", description = "BI calibrated gauge", serial = "BI-SERIAL-04", location = "M04-TOOL-CRIB", eligibleMachines = new[] { "M-04" }, eligibleActivities = new[] { "RUN_SLITTER" }, calibratedOn = Day(-1), certificate = "BI-CERT-04", reason = "BI actual valid tool registration" });
        await plant.Ok(engineer, "/api/resources/tool-requirements", new { machineCode = "M-04", activityCode = "RUN_SLITTER", toolTypeCode = "BI-GAUGE", quantity = 1, reason = "BI required operation tool" });

        await plant.Ok(engineer, "/api/engineering/items", new { partNumber = "BI-M04-ROLL", description = "BI M-04-only roll", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 2, reason = "BI controlled part" });
        await plant.Ok(engineer, "/api/engineering/items/BI-M04-ROLL/revisions", new { revision = "A", reason = "BI baseline revision" });
        await plant.Ok(engineer, "/api/engineering/items/BI-M04-ROLL/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "COIL-AL", quantityPer = 1m, scrapPercent = 0m, reason = "BI ten-unit material requirement" });
        await plant.Ok(engineer, "/api/engineering/items/BI-M04-ROLL/revisions/A/routing", new { sequence = 10, description = "BI slit on M-04", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "M-04" }, setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1, reason = "BI released M-04-only routing" });
        await plant.Ok(plant.Approver, "/api/engineering/items/BI-M04-ROLL/revisions/A/release", new { reason = "Independently release the BI routing" });
        var order = Id(await plant.Ok(engineer, "/api/orders/", new { partNumber = "BI-M04-ROLL", quantity = 10m, dueDate = Day(10), priority = 3, reason = "BI observed production order" }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Release the BI source order" });
        var goal = Id(await plant.Ok(planner, "/api/planning/goals", new { kind = "Production", title = "BI M-04 committed production", orderNumber = order, requiredBy = Day(10), reason = "BI production goal" }));
        var schedule = Id(await plant.Ok(planner, "/api/planning/schedules", new { horizonDays = 14, reason = "BI initial feasible plan" }));
        await plant.Ok(plant.As("bi-schedule-approver"), $"/api/planning/schedules/{schedule}/approve", new { reason = "Independently review the BI plan" });
        var beforeGoal = Assert.Single((await GetAsync(planner, "/api/planning/goals")).EnumerateArray(), record => record.GetProperty("goal").GetProperty("goalId").GetInt64() == goal);
        Assert.Equal("OnTrack", beforeGoal.GetProperty("goal").GetProperty("state").GetString());

        // Normal commands intentionally share a business correlation. Neither command supplies a
        // causation ID: coincidence within this order's incident must not manufacture a causal edge.
        await plant.Ok(stores, "/api/inventory/locations", new { code = "BI-STAGE", siteCode = "PLANT1", name = "BI M-04 point of use", kind = "Staging", workCenterCode = "SLIT-WC", reason = "BI explicit material point of use" });
        var correlation = Guid.NewGuid();
        engineer.DefaultRequestHeaders.Add("X-Correlation-Id", correlation.ToString("D"));
        stores.DefaultRequestHeaders.Add("X-Correlation-Id", correlation.ToString("D"));
        long request;
        const string fault = "M-04 drive failed; BI observed machine condition";
        try
        {
            request = Id(await plant.Ok(stores, "/api/inventory/tasks", new { kind = "Kit", kitCode = "MR-22", orderNumber = order, partNumber = "COIL-AL", quantity = 10m, toLocation = "BI-STAGE", reason = "BI MR-22 requested; material has not been picked or delivered" }));
            await plant.Ok(engineer, "/api/maintenance/machines/M-04/down", new { description = fault, reason = "BI independent machine condition" });
        }
        finally
        {
            engineer.DefaultRequestHeaders.Remove("X-Correlation-Id");
            stores.DefaultRequestHeaders.Remove("X-Correlation-Id");
        }

        var readiness = await GetAsync(planner, $"/api/orders/{order}/readiness");
        var operation = Assert.Single(readiness.GetProperty("output").GetProperty("operations").EnumerateArray());
        Assert.False(operation.GetProperty("executable").GetBoolean());
        Assert.Equal("M-04", operation.GetProperty("bestMachine").GetString());
        var dimensions = operation.GetProperty("dimensions").EnumerateArray().ToDictionary(dimension => dimension.GetProperty("name").GetString()!);
        Assert.Equal(("Fail", "Fail", "Pass", "Pass", "Pass", "Pass"),
            (dimensions["Equipment"].GetProperty("outcome").GetString(), dimensions["PointOfUse"].GetProperty("outcome").GetString(),
             dimensions["Material"].GetProperty("outcome").GetString(), dimensions["Workforce"].GetProperty("outcome").GetString(),
             dimensions["Tooling"].GetProperty("outcome").GetString(), dimensions["Safety"].GetProperty("outcome").GetString()));
        Assert.Contains(fault, dimensions["Equipment"].GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains("COIL-AL needs 10, 0 there", dimensions["PointOfUse"].GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains("Sarah", dimensions["Workforce"].GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains("BI-CAL-1", dimensions["Tooling"].GetProperty("detail").GetString(), StringComparison.Ordinal);
        var operationEvidence = Assert.Single(readiness.GetProperty("evidence").GetProperty("children").EnumerateArray());
        Assert.Equal("Operation", operationEvidence.GetProperty("check").GetString());
        foreach (var name in new[] { "Equipment", "PointOfUse", "Workforce", "Tooling" })
        {
            var evidence = Assert.Single(operationEvidence.GetProperty("children").EnumerateArray(), node => node.GetProperty("check").GetString() == name);
            Assert.Equal(dimensions[name].GetProperty("detail").GetString(), evidence.GetProperty("detail").GetString());
        }

        var flow = Assert.Single((await GetAsync(stores, $"/api/inventory/orders/{order}/material-flow")).GetProperty("lines").EnumerateArray());
        Assert.Equal(("InWarehouse", 10m, 10m, 0m, 0m, 0m), (flow.GetProperty("state").GetString(), flow.GetProperty("required").GetDecimal(), flow.GetProperty("requested").GetDecimal(), flow.GetProperty("picked").GetDecimal(), flow.GetProperty("delivered").GetDecimal(), flow.GetProperty("atPointOfUse").GetDecimal()));
        var task = Assert.Single((await GetAsync(stores, $"/api/inventory/tasks?order={order}")).EnumerateArray());
        Assert.Equal((request, "MR-22", "Requested", "BI-STAGE"), (task.GetProperty("taskId").GetInt64(), task.GetProperty("kitCode").GetString(), task.GetProperty("status").GetString(), task.GetProperty("toLocation").GetString()));
        var present = (await GetAsync(planner, "/api/readiness/presence?workCenter=SLIT-WC")).EnumerateArray().ToDictionary(person => person.GetProperty("personId").GetGuid());
        Assert.True(present[plant.People["sarah"]].GetProperty("countsAsPresent").GetBoolean());
        Assert.True(present[plant.People["michael"]].GetProperty("countsAsPresent").GetBoolean());
        Assert.Contains((await GetAsync(engineer, $"/api/competency/people/{plant.People["sarah"]}")).EnumerateArray(), qualification => qualification.GetProperty("code").GetString() == "SLIT-II" && qualification.GetProperty("status").GetString() == "Qualified");
        var tool = Assert.Single((await GetAsync(engineer, "/api/resources/tools?machine=M-04&activity=RUN_SLITTER")).EnumerateArray());
        Assert.Equal(("BI-CAL-1", "BI-SERIAL-04", "InHouse"), (tool.GetProperty("tool").GetProperty("code").GetString(), tool.GetProperty("tool").GetProperty("serial").GetString(), tool.GetProperty("tool").GetProperty("custody").GetString()));
        Assert.True(tool.GetProperty("validity").GetProperty("valid").GetBoolean());
        var atRisk = Assert.Single((await GetAsync(planner, "/api/planning/goals")).EnumerateArray(), record => record.GetProperty("goal").GetProperty("goalId").GetInt64() == goal).GetProperty("goal");
        Assert.Equal((order, "AtRisk", JsonValueKind.Null), (atRisk.GetProperty("orderNumber").GetInt64(), atRisk.GetProperty("state").GetString(), atRisk.GetProperty("projected").ValueKind));

        var relay = app.Services.GetRequiredService<OutboxRelay>();
        while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0) { }
        var sourceEvents = new List<ObservedEvent>();
        await using (var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("""
            SELECT event_id,event_type,envelope::jsonb->>'sourceContext',entity_type,entity_id,occurred_at,correlation_id,causation_id,envelope::jsonb->>'reason'
            FROM kernel.event_log
            WHERE correlation_id=$1
              AND ((event_type='MachineDown' AND entity_type='Machine' AND entity_id='M-04')
                OR (event_type='MaterialTaskChanged' AND entity_type='MaterialTask' AND entity_id=$2))
            """, connection))
        {
            command.Parameters.AddWithValue(correlation);
            command.Parameters.AddWithValue(request.ToString(CultureInfo.InvariantCulture));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                sourceEvents.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetGuid(6), reader.IsDBNull(7) ? null : reader.GetGuid(7), reader.GetString(8)));
        }
        Assert.Equal(2, sourceEvents.Count);
        var machineEvent = Assert.Single(sourceEvents, source => source.Type == "MachineDown");
        var materialEvent = Assert.Single(sourceEvents, source => source.Type == "MaterialTaskChanged");
        Assert.Equal(("Maintenance", "M-04", "BI independent machine condition"),
            (machineEvent.Context, machineEvent.EntityId, machineEvent.Reason));
        Assert.Equal(("Inventory", request.ToString(CultureInfo.InvariantCulture), "BI MR-22 requested; material has not been picked or delivered"),
            (materialEvent.Context, materialEvent.EntityId, materialEvent.Reason));
        Assert.All(sourceEvents, source => { Assert.Equal(correlation, source.Correlation); Assert.Null(source.Cause); });
        var principal = await app.Services.GetRequiredService<KernelReadStore>().ResolvePrincipalBySubjectAsync("planner", CancellationToken.None);
        Assert.NotNull(principal);
        var map = await app.Services.GetRequiredService<CauseMapReadStore>().LoadAsync(principal!, sourceEvents.Min(source => source.At), sourceEvents.Max(source => source.At), CancellationToken.None);
        foreach (var source in sourceEvents)
        {
            var step = Assert.Single(map.Steps, step => step.EventId == source.Id);
            Assert.Equal((source.Type, source.Context, source.EntityType, source.EntityId, source.At, source.Reason, "event:" + source.Id.ToString("D")),
                (step.EventType, step.SourceContext, step.EntityType, step.EntityId, step.At, step.Reason, step.Trace));
            Assert.Contains(source.Id, map.EventsWithoutVisibleCause);
            Assert.DoesNotContain(map.Links, link => link.EffectEventId == source.Id);
        }
        Assert.DoesNotContain(map.Links, link => sourceEvents.Any(source => source.Id == link.CauseEventId)
            && sourceEvents.Any(source => source.Id == link.EffectEventId));
        Assert.Empty((await GetAsync(planner, "/api/integrity/exceptions")).EnumerateArray());
    }
}
