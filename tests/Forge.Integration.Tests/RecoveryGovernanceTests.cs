using System.Globalization;
using System.Net;
using System.Text.Json;
using Forge.Kernel.Events;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>Governance acceptance L uses source maintenance events and live projections, with a human recovery.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RecoveryGovernanceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Actual_setup_stoppage_keeps_recorded_execution_provenance_in_recovery_evidence()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var due = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var order = await ReleaseAsync(plant, planner, "ROLL-100", due);
        await plant.Ok(plant.Engineer, "/api/execution/reason-codes", new { code = "REC-SETUP", kind = "Downtime", category = "Setup", description = "Install the specified slitter knife fixture" });
        const string observed = "Actual fixture setup observed REC-PROVENANCE-01";
        var stoppage = Id(await plant.Ok(plant.Engineer, "/api/execution/stoppages", new
        {
            machineCode = "SLITTER-A", category = "Setup", reasonCode = "REC-SETUP",
            orderNumber = order, sequence = 10, note = observed
        }));
        var scenario = await ScenarioAsync(plant, planner, order, DateTimeOffset.UtcNow);
        var alternate = Option((await RunAsync(planner, scenario)).GetProperty("recoveryOptions"), "alternate-machine");
        var facts = alternate.GetProperty("evidence").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var actual = Assert.Single(facts, f => f.Contains(observed, StringComparison.Ordinal));
        Assert.StartsWith("Recorded downtime -", actual, StringComparison.Ordinal);
        Assert.Contains($"Execution/Stoppage/{stoppage}", actual, StringComparison.Ordinal);
        Assert.Contains("Setup; observed reason REC-SETUP", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("Scenario downtime", actual, StringComparison.Ordinal);
        var hypothetical = Assert.Single(facts, f => f.StartsWith("Scenario downtime", StringComparison.Ordinal));
        Assert.Contains($"Planning/Scenario/{scenario:D}", hypothetical, StringComparison.Ordinal);
        Assert.Contains("SLITTER-B", hypothetical, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Machine_failure_changes_capacity_goals_alerts_and_fact_evaluated_recovery_without_auto_commit()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("recovery-tech", "MaintenanceTechnician");
        await plant.Person("recovery-supervisor", "Supervisor");
        await plant.Person("recovery-approver", "Planner");
        var tech = plant.As("recovery-tech");
        var recipient = plant.As("recovery-supervisor");
        var e = plant.Engineer;
        await plant.Ok(e, "/api/resources/shifts", new { code = "RECOVERY", siteCode = "PLANT1", name = "Recovery all-day calendar", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "RECOVERY" });
        foreach (var person in new[] { "sarah", "michael" })
            await plant.Ok(e, $"/api/workforce/workers/{plant.People[person]}/shift", new { shiftCode = "RECOVERY" });

        // A second item has an actual B-only released routing. The flexible item's A/B eligibility
        // must never be used to claim a feasible alternate for this unrelated B-only operation.
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "RECOVERY-B", description = "Wide slit requiring B", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 2 });
        await plant.Ok(e, "/api/engineering/items/RECOVERY-B/revisions", new { revision = "A" });
        await plant.Ok(e, "/api/engineering/items/RECOVERY-B/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "COIL-AL", quantityPer = 2.5m, scrapPercent = 0m });
        await plant.Ok(e, "/api/engineering/items/RECOVERY-B/revisions/A/routing", new { sequence = 10, description = "Wide slit on B", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-B" }, setupMinutes = 30m, runMinutesPerUnit = 3m, crewSize = 1 });
        await plant.Ok(plant.Approver, "/api/engineering/items/RECOVERY-B/revisions/A/release", new { reason = "Released B-only recovery test routing" });

        var due = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var flexible = await ReleaseAsync(plant, planner, "ROLL-100", due);
        var bound = await ReleaseAsync(plant, planner, "RECOVERY-B", due);
        var goal = Id(await plant.Ok(planner, "/api/planning/goals", new { kind = "Production", title = "B-only committed production", orderNumber = bound, requiredBy = due }));
        var originalVersion = Id(await plant.Ok(planner, "/api/planning/schedules", new { horizonDays = 14 }));
        await plant.Ok(plant.As("recovery-approver"), $"/api/planning/schedules/{originalVersion}/approve", new { reason = "Review current executable plan" });
        await plant.Ok(planner, "/api/planning/goals/reforecast", new { });
        var beforeGoal = await GoalAsync(planner, goal);
        Assert.Equal("OnTrack", beforeGoal.GetProperty("goal").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.String, beforeGoal.GetProperty("goal").GetProperty("projected").ValueKind);
        // Live forecasts start at the current clock and can move by milliseconds between reads.
        // Immutability compares the committed history entry to that same entry after source changes.
        var beforeProjection = beforeGoal.GetProperty("history")[0].GetProperty("projected").GetDateTimeOffset();

        await plant.Ok(plant.Admin, "/api/notifications/rules", new
        {
            name = "Recovery machine failure", active = true, triggerEventType = "MachineDown", clearEventType = "MachineRestored",
            scopeType = "Entity", scopeId = "Machine/SLITTER-B", conditionField = (string?)null, conditionEquals = (string?)null,
            severity = "ActionRequired", confidence = "High", recipientRoles = new[] { "Supervisor" }, escalationRoles = new[] { "Supervisor" },
            channels = new[] { "InApp" }, template = "{eventType} on {entityId}", resolutionTemplate = "Cleared by {eventType} on {entityId}",
            deepLinkTemplate = "#/maintenance", delaySeconds = 0, reminderSeconds = (int?)null, escalationSeconds = (int?)null,
            quietStart = (TimeOnly?)null, quietEnd = (TimeOnly?)null, timeZone = "America/New_York", dedupSeconds = 3600,
            reason = "Notify the supervisor from real machine events"
        });
        var from = DateTimeOffset.UtcNow;
        var to = from.AddDays(2);
        var beforeCapacity = await CapacityAsync(planner, from, to, "SLITTER-B");
        Assert.True(beforeCapacity.GetProperty("executableHours").GetDecimal() > 0);
        var flexibleScenario = await ScenarioAsync(plant, planner, flexible, from);
        var boundScenario = await ScenarioAsync(plant, planner, bound, from);

        // This API command changes the real downtime source, capacity, current goals and outbox.
        const string fault = "B blade-drive failed; technician inspection REC-L-01";
        await plant.Ok(tech, "/api/maintenance/machines/SLITTER-B/down", new { description = fault, reason = "Observed machine failure" });
        var downCapacity = await CapacityAsync(planner, from, to, "SLITTER-B");
        Assert.Equal(0m, downCapacity.GetProperty("executableHours").GetDecimal());
        Assert.Contains(downCapacity.GetProperty("intervals").EnumerateArray(), i => i.GetProperty("limiting").GetString() == "Equipment" &&
            i.GetProperty("detail").GetString()!.Contains(fault, StringComparison.Ordinal));
        var downGoal = await GoalAsync(planner, goal);
        Assert.Equal("AtRisk", downGoal.GetProperty("goal").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, downGoal.GetProperty("goal").GetProperty("projected").ValueKind);
        Assert.Contains("Unschedulable", downGoal.GetProperty("goal").GetProperty("evidence").GetRawText(), StringComparison.Ordinal);
        Assert.Equal(beforeProjection, downGoal.GetProperty("history")[0].GetProperty("projected").GetDateTimeOffset());
        await plant.Ok(planner, "/api/planning/goals/reforecast", new { });
        var history = (await GoalAsync(planner, goal)).GetProperty("history").EnumerateArray().ToArray();
        Assert.Equal(2, history.Length);
        Assert.Contains(history, h => h.GetProperty("projected").ValueKind == JsonValueKind.Null && h.GetProperty("state").GetString() == "AtRisk");
        Assert.Contains(history, h => h.GetProperty("projected").ValueKind == JsonValueKind.String && h.GetProperty("projected").GetDateTimeOffset() == beforeProjection);

        // The rule consumes the maintenance command's committed event, never a fabricated envelope.
        await PumpAsync(app);
        var alert = Assert.Single((await GetAsync(recipient, "/api/notifications/inbox?openOnly=true")).EnumerateArray());
        Assert.Equal(("Maintenance", "Machine", "SLITTER-B", "ActionRequired", "High"),
            (alert.GetProperty("sourceContext").GetString(), alert.GetProperty("entityType").GetString(), alert.GetProperty("entityId").GetString(), alert.GetProperty("severity").GetString(), alert.GetProperty("confidence").GetString()));
        Assert.Contains("MachineDown", alert.GetProperty("message").GetString(), StringComparison.Ordinal);
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using (var sourceEvent = new NpgsqlCommand("SELECT envelope FROM kernel.event_log WHERE event_type='MachineDown' AND entity_id='SLITTER-B'", connection))
            Assert.Contains(fault, (string)(await sourceEvent.ExecuteScalarAsync())!, StringComparison.Ordinal);

        // B-only work is infeasible even though another visible order has multiple eligible machines.
        var sourceCounts = await SourceCountsAsync(connection);
        var boundOptions = (await RunAsync(planner, boundScenario)).GetProperty("recoveryOptions");
        var boundAlternate = Option(boundOptions, "alternate-machine");
        Assert.False(boundAlternate.GetProperty("feasible").GetBoolean());
        Assert.Equal("Infeasible", boundAlternate.GetProperty("status").GetString());
        Assert.Empty(boundAlternate.GetProperty("machineAlternatives").EnumerateArray());
        Assert.Equal(bound, Assert.Single(boundAlternate.GetProperty("affectedOrders").EnumerateArray()).GetInt64());
        Assert.Contains("no other released eligible machine", boundAlternate.GetProperty("constraints").GetRawText(), StringComparison.Ordinal);

        // A is a real feasible alternate for the flexible order, with a dated full-queue projection.
        var options = (await RunAsync(planner, flexibleScenario)).GetProperty("recoveryOptions");
        var alternate = Option(options, "alternate-machine");
        Assert.True(alternate.GetProperty("feasible").GetBoolean());
        Assert.Equal("Feasible", alternate.GetProperty("status").GetString());
        var candidate = Assert.Single(alternate.GetProperty("machineAlternatives").EnumerateArray());
        Assert.Equal("SLITTER-A", candidate.GetProperty("machineCode").GetString());
        Assert.Equal(JsonValueKind.String, candidate.GetProperty("projectedCompletion").ValueKind);
        Assert.Empty(candidate.GetProperty("constraints").EnumerateArray());
        Assert.Contains(fault, alternate.GetProperty("evidence").GetRawText(), StringComparison.Ordinal);
        Assert.All(options.EnumerateArray().Where(o => o.GetProperty("id").GetString() != "alternate-machine"), o =>
        {
            Assert.False(o.GetProperty("feasible").GetBoolean());
            Assert.Contains(o.GetProperty("status").GetString(), new[] { "RequiresInputs", "NotAuthorized" });
            Assert.NotEmpty(o.GetProperty("requiredInputs").EnumerateArray());
            Assert.NotEmpty(o.GetProperty("projections").EnumerateArray());
            Assert.NotEmpty(o.GetProperty("evidence").EnumerateArray());
        });
        Assert.Equal(sourceCounts, await SourceCountsAsync(connection));
        Assert.Equal(originalVersion, (await GetAsync(planner, "/api/planning/schedules/approved")).GetProperty("versionId").GetInt64());
        await AssertOpenAsync(plant, from, to, "SLITTER-B");

        // A newly observed failure invalidates the previously feasible candidate on the next run.
        await plant.Ok(tech, "/api/maintenance/machines/SLITTER-A/down", new { description = "A guard interlock fault REC-L-02", reason = "Observed second failure" });
        sourceCounts = await SourceCountsAsync(connection);
        alternate = Option((await RunAsync(planner, flexibleScenario)).GetProperty("recoveryOptions"), "alternate-machine");
        Assert.False(alternate.GetProperty("feasible").GetBoolean());
        Assert.Equal("Infeasible", alternate.GetProperty("status").GetString());
        candidate = Assert.Single(alternate.GetProperty("machineAlternatives").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, candidate.GetProperty("projectedCompletion").ValueKind);
        Assert.Contains("A guard interlock fault REC-L-02", candidate.GetProperty("constraints").GetRawText(), StringComparison.Ordinal);
        Assert.Equal(sourceCounts, await SourceCountsAsync(connection));
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(planner, "/api/maintenance/machines/SLITTER-A/restore", new { reason = "Planner cannot certify repair" })).StatusCode);
        await AssertOpenAsync(plant, from, to, "SLITTER-A");
        await plant.Ok(tech, "/api/maintenance/machines/SLITTER-A/restore", new { reason = "Technician verified guard interlock repair" });

        // Availability alone still cannot override a source hold on the eligible alternate.
        var hold = Id(await plant.Ok(e, "/api/holds", new { kind = "Production", targetType = "Machine", targetId = "SLITTER-A", reason = "Recovery guard verification pending REC-L-03" }));
        sourceCounts = await SourceCountsAsync(connection);
        alternate = Option((await RunAsync(planner, flexibleScenario)).GetProperty("recoveryOptions"), "alternate-machine");
        Assert.False(alternate.GetProperty("feasible").GetBoolean());
        Assert.Contains("REC-L-03", alternate.GetProperty("constraints").GetRawText(), StringComparison.Ordinal);
        Assert.Equal(sourceCounts, await SourceCountsAsync(connection));
        await plant.Ok(planner, $"/api/holds/{hold}/release", new { reason = "Independent guard verification accepted" });
        Assert.True(Option((await RunAsync(planner, flexibleScenario)).GetProperty("recoveryOptions"), "alternate-machine").GetProperty("feasible").GetBoolean());
        await AssertOpenAsync(plant, from, to, "SLITTER-B");

        // A human chooses repair at the owning maintenance command; the resulting event clears the
        // communication projection and restores the actual goal. Scenario runs never did this.
        await plant.Ok(tech, "/api/maintenance/machines/SLITTER-B/restore", new { reason = "Human chose repair; blade-drive verification REC-L-04" });
        await PumpAsync(app);
        var restoredGoal = (await GoalAsync(planner, goal)).GetProperty("goal");
        Assert.Equal("OnTrack", restoredGoal.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.String, restoredGoal.GetProperty("projected").ValueKind);
        Assert.True(restoredGoal.GetProperty("projected").GetDateTimeOffset() >= beforeProjection);
        Assert.True((await CapacityAsync(planner, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(2), "SLITTER-B")).GetProperty("executableHours").GetDecimal() > 0);
        var resolved = Assert.Single((await GetAsync(recipient, "/api/notifications/inbox")).EnumerateArray());
        Assert.Equal(alert.GetProperty("alertId").GetGuid(), resolved.GetProperty("alertId").GetGuid());
        Assert.Equal("Resolved", resolved.GetProperty("state").GetString());
        Assert.Contains("MachineRestored", resolved.GetProperty("resolutionMessage").GetString(), StringComparison.Ordinal);
        await using var audit = new NpgsqlCommand("SELECT actor,old_value,new_value,reason FROM kernel.audit_log WHERE action='RestoreMachine' AND entity_id='SLITTER-B' AND outcome='Succeeded'", connection);
        await using var reader = await audit.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal($"person:{plant.People["recovery-tech"]}", reader.GetString(0));
        Assert.Equal(("Down", "Up", "Human chose repair; blade-drive verification REC-L-04"), (reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        Assert.False(await reader.ReadAsync());
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<long> ReleaseAsync(SlittingPlant.Plant plant, HttpClient planner, string part, string due)
    {
        var number = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = part, quantity = 10m, dueDate = due, priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{number}/release", new { reason = "Recovery acceptance commitment" });
        return number;
    }

    private static async Task<Guid> ScenarioAsync(SlittingPlant.Plant plant, HttpClient planner, long number, DateTimeOffset startsAt)
    {
        var result = await plant.Ok(planner, "/api/planning/scenarios", new
        {
            name = $"Current B failure recovery for {number}",
            change = new { kind = "MachineUnavailable", machineCode = "SLITTER-B", orderNumber = number, startsAt, durationHours = 336 },
            reason = "Evaluate eligible alternatives against current source facts"
        });
        return Guid.Parse(result.GetProperty("entityId").GetString()!);
    }

    private static async Task<JsonElement> RunAsync(HttpClient client, Guid id)
    {
        var response = await client.PostAsync($"/api/planning/scenarios/{id:D}/run", content: null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static JsonElement Option(JsonElement options, string id) =>
        options.EnumerateArray().Single(o => o.GetProperty("id").GetString() == id);

    private static async Task<JsonElement> GoalAsync(HttpClient client, long number) =>
        (await GetAsync(client, "/api/planning/goals")).EnumerateArray().Single(g => g.GetProperty("goal").GetProperty("goalId").GetInt64() == number);

    private static async Task<JsonElement> CapacityAsync(HttpClient client, DateTimeOffset from, DateTimeOffset to, string machine) =>
        (await GetAsync(client, $"/api/planning/capacity?from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}&workCenter=SLIT-WC"))
        .GetProperty("output").GetProperty("machines").EnumerateArray().Single(m => m.GetProperty("machineCode").GetString() == machine);

    private static async Task AssertOpenAsync(SlittingPlant.Plant plant, DateTimeOffset from, DateTimeOffset to, string machine) =>
        Assert.Contains((await GetAsync(plant.Engineer, $"/api/maintenance/downtime?from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}")).EnumerateArray(),
            d => d.GetProperty("machineCode").GetString() == machine && d.GetProperty("endsAt").ValueKind == JsonValueKind.Null);

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task PumpAsync(ForgeApp app)
    {
        await app.Services.GetRequiredService<OutboxRelay>().RelayBatchAsync(1_000_000, CancellationToken.None);
        await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
    }

    private static async Task<long[]> SourceCountsAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM kernel.audit_log), (SELECT count(*) FROM kernel.outbox),
                   (SELECT count(*) FROM kernel.event_log), (SELECT count(*) FROM planning.schedule_version),
                   (SELECT count(*) FROM planning.goal_forecast), (SELECT count(*) FROM maintenance.downtime WHERE ends_at IS NULL)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return Enumerable.Range(0, reader.FieldCount).Select(reader.GetInt64).ToArray();
    }
}
