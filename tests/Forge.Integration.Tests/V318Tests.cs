using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V318Tests(PostgresFixture postgres)
{
    private static TimeZoneInfo PlantZone => TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    // Yesterday at the plant, stepped back past a daylight-saving change so the shift days around it are exactly 24 hours.
    private static DateTime PlantDate
    {
        get
        {
            var day = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, PlantZone).Date.AddDays(-1);
            while (Hours(day.AddDays(-1)) != 24 || Hours(day) != 24 || Hours(day.AddDays(1)) != 24) day = day.AddDays(-1);
            return day;
        }
    }

    private static double Hours(DateTime day) =>
        (TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.AddDays(1), DateTimeKind.Unspecified), PlantZone)
         - TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day, DateTimeKind.Unspecified), PlantZone)).TotalHours;
    private static DateTimeOffset LocalShift(int daysBack)
    {
        var local = DateTime.SpecifyKind(PlantDate.AddDays(-daysBack), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, PlantZone), TimeSpan.Zero);
    }
    private static DateTimeOffset From => LocalShift(0); // midnight at PLANT1 in its configured time zone
    private static DateTimeOffset Until => LocalShift(-1);

    [Fact]
    public async Task Shift_handoff_uses_live_records_review_and_acknowledgment_are_distinct_and_closeout_is_idempotently_unique()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        // The spring-DST case needs a site that observes DST; test sites default to UTC.
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app, "America/New_York");
        await plant.Ok(plant.Engineer, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "All day", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(plant.Engineer, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(plant.Engineer, "/api/resources/work-centers/PAINT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(plant.Engineer, "/api/execution/shifts/checklist", new { workCenterCode = "SLIT-WC", code = "MACHINE", description = "Record machine state", required = true, sortOrder = 1, active = true, reason = "Configure shift checklist" });

        var springDst = await plant.Ok(plant.Engineer, "/api/execution/shifts/handoffs", new
        {
            workCenterCode = "PAINT-WC", shiftCode = "ALL", shiftFrom = "2026-03-08T05:00:00Z", shiftUntil = "2026-03-09T05:00:00Z",
            context = "DST boundary validation", checklist = Array.Empty<object>(), reason = "Verify the configured elapsed shift through spring DST",
        });
        Assert.NotNull(springDst.GetProperty("entityId").GetString());
        var invalidDst = await plant.Try(plant.Engineer, "/api/execution/shifts/handoffs", new
        {
            workCenterCode = "PAINT-WC", shiftCode = "ALL", shiftFrom = "2026-03-08T05:00:00Z", shiftUntil = "2026-03-09T04:00:00Z",
            context = "Wall-clock day is not the configured 24-hour shift", checklist = Array.Empty<object>(), reason = "Reject mismatched DST window",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidDst.StatusCode);
        Assert.Contains("invalid_shift_window", await invalidDst.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await plant.Person("shift-no-access");
        var unauthorized = await plant.Try(plant.As("shift-no-access"), "/api/execution/shifts/handoffs", new
        {
            workCenterCode = "SLIT-WC", shiftCode = "ALL", shiftFrom = LocalShift(1), shiftUntil = From,
            context = "Should not be captured", checklist = Array.Empty<object>(), reason = "Unauthorized test",
        });
        Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
        var missingRequired = await plant.Try(plant.Engineer, "/api/execution/shifts/handoffs", new
        {
            workCenterCode = "SLIT-WC", shiftCode = "ALL", shiftFrom = LocalShift(1), shiftUntil = From,
            context = "Missing required checklist", checklist = Array.Empty<object>(), reason = "Required checklist validation",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingRequired.StatusCode);
        var missingChecklistProblem = await missingRequired.Content.ReadAsStringAsync();
        Assert.True(missingChecklistProblem.Contains("required_checklist_missing", StringComparison.Ordinal), missingChecklistProblem);

        var order = JsonId(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 3m, dueDate = "2026-10-20", priority = 5 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Shift WIP fixture" });
        var captured = await plant.Ok(plant.Engineer, "/api/execution/shifts/handoffs", new
        {
            workCenterCode = "SLIT-WC", shiftCode = "ALL", shiftFrom = From, shiftUntil = Until,
            context = "Slitter A is ready; order is waiting for start.",
            checklist = new[] { new { code = "MACHINE", state = "Complete", note = (string?)null } },
            reason = "Outgoing shift pass-on",
        });
        var handoffId = JsonId(captured);
        var duplicateHandoff = await plant.Try(plant.Engineer, "/api/execution/shifts/handoffs", new
        {
            workCenterCode = "SLIT-WC", shiftCode = "ALL", shiftFrom = From, shiftUntil = Until,
            context = "Second capture should not replace the reviewed source snapshot.",
            checklist = new[] { new { code = "MACHINE", state = "Complete", note = (string?)null } },
            reason = "Check duplicate handoff protection",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicateHandoff.StatusCode);
        Assert.Contains("shift_handoff_exists", await duplicateHandoff.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var handoffResponse = await plant.Engineer.GetAsync($"/api/execution/shifts/handoffs/{handoffId}");
        Assert.True(handoffResponse.IsSuccessStatusCode, await handoffResponse.Content.ReadAsStringAsync());
        var handoff = await handoffResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(handoff.GetProperty("handoff").GetProperty("facts").EnumerateArray(), f => f.GetProperty("kind").GetString() == "WorkInProgress" && f.GetProperty("reference").GetString()!.Contains(order.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        Assert.Equal("Complete", handoff.GetProperty("handoff").GetProperty("checklist")[0].GetProperty("state").GetString());
        Assert.Null(handoff.GetProperty("handoff").GetProperty("reviewedAt").GetDateTimeOffsetOrNull());

        await plant.Person("shift-reviewer", "Supervisor");
        var reviewer = plant.As("shift-reviewer");
        await plant.Ok(reviewer, $"/api/execution/shifts/handoffs/{handoffId}/review", new { reason = "Supervisor reviewed current machine and order state" });
        await plant.Ok(reviewer, $"/api/execution/shifts/handoffs/{handoffId}/actions", new
        {
            description = "Inspect the slitter guard before the next run", sourceKind = "Machine", sourceReference = "SLITTER-A",
            assignee = plant.People["sarah"], dueAt = Until.AddHours(2), reason = "Assign the shift follow-up",
        });
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["sarah"]}/roles", new { role = "ShopFloorTerminal", reason = "Enable assigned shift action" });
        var action = (await reviewer.GetFromJsonAsync<JsonElement[]>($"/api/execution/shifts/handoffs/{handoffId}/actions"))!.Single();
        var actionId = action.GetProperty("actionId").GetInt64();
        Assert.Equal("Assigned", action.GetProperty("status").GetString());
        await plant.Person("shift-incoming", "ShopFloorTerminal");
        var incoming = plant.As("shift-incoming");
        await plant.Ok(incoming, $"/api/execution/shifts/handoffs/{handoffId}/acknowledge", new { note = "Received; checking the work center", reason = "Incoming shift acknowledgment" });
        var actionsBefore = await plant.Engineer.GetFromJsonAsync<JsonElement[]>($"/api/execution/shifts/handoffs/{handoffId}/actions");
        Assert.Equal("Assigned", Assert.Single(actionsBefore!).GetProperty("status").GetString()); // acknowledgment never completes assigned work
        var dependentResult = await plant.Ok(reviewer, $"/api/execution/shifts/handoffs/{handoffId}/actions", new
        {
            description = "Verify the guard repair before production resumes", sourceKind = "Machine", sourceReference = "SLITTER-A",
            assignee = plant.People["sarah"], blockedByActionIds = new[] { actionId }, reason = "Record the follow-up dependency",
        });
        var dependentId = long.Parse(dependentResult.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
        await plant.Ok(plant.As("sarah"), $"/api/execution/shifts/actions/{dependentId}/status",
            new { expected = "Assigned", status = "Acknowledged", reason = "Acknowledge dependent recovery action" });
        var blocked = await plant.Try(plant.As("sarah"), $"/api/execution/shifts/actions/{dependentId}/status",
            new { expected = "Acknowledged", status = "InProgress", reason = "Try starting before prerequisite" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Contains("action_dependencies_open", await blocked.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        foreach (var (before, after) in new[] { ("Assigned", "Acknowledged"), ("Acknowledged", "InProgress"), ("InProgress", "Completed") })
        {
            var response = await plant.As("sarah").CommandAsync($"/api/execution/shifts/actions/{actionId}/status",
                new { expected = before, status = after, evidence = after == "Completed" ? "Guard inspected and interlock response confirmed against the machine checklist." : null, reason = $"Action {after}" }, $"v318-action-{after}");
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }

        var actionAfter = (await reviewer.GetFromJsonAsync<JsonElement[]>($"/api/execution/shifts/handoffs/{handoffId}/actions"))!.Single(action => action.GetProperty("actionId").GetInt64() == actionId);
        Assert.Equal("Completed", actionAfter.GetProperty("status").GetString());
        Assert.True(actionAfter.GetProperty("completedAt").GetDateTimeOffset() >= actionAfter.GetProperty("acknowledgedAt").GetDateTimeOffset());
        Assert.Equal(Until.AddHours(2), actionAfter.GetProperty("dueAt").GetDateTimeOffset());
        Assert.Contains("interlock response confirmed", actionAfter.GetProperty("completionEvidence").GetString(), StringComparison.Ordinal);
        var dependentAction = (await reviewer.GetFromJsonAsync<JsonElement[]>($"/api/execution/shifts/handoffs/{handoffId}/actions"))!.Single(action => action.GetProperty("actionId").GetInt64() == dependentId);
        Assert.Equal(actionId, dependentAction.GetProperty("blockedByActionIds")[0].GetInt64());
        await plant.Ok(plant.As("sarah"), $"/api/execution/shifts/actions/{dependentId}/status",
            new { expected = "Acknowledged", status = "InProgress", reason = "Start after prerequisite completed" });
        await plant.Ok(plant.As("sarah"), $"/api/execution/shifts/actions/{dependentId}/status",
            new { expected = "InProgress", status = "Completed", evidence = "Prerequisite action #" + actionId + " is complete; verification passed.", reason = "Complete dependent action with evidence" });
        await plant.Ok(reviewer, $"/api/execution/shifts/handoffs/{handoffId}/actions", new
        {
            description = "Carry the open spindle inspection to the next shift", sourceKind = (string?)null, sourceReference = (string?)null,
            assignee = plant.People["michael"], reason = "Preserve unresolved follow-up for closeout",
        });

        var closeout = await plant.Ok(reviewer, "/api/execution/shifts/closeouts", new { workCenterCode = "SLIT-WC", shiftCode = "ALL", shiftFrom = From, shiftUntil = Until, note = "WIP carried forward", reason = "Shift closeout" });
        Assert.NotNull(closeout.GetProperty("entityId").GetString());
        var records = await reviewer.GetFromJsonAsync<JsonElement[]>("/api/execution/shifts/closeouts?workCenter=SLIT-WC");
        var facts = Assert.Single(records!).GetProperty("facts");
        Assert.Equal(2, facts.GetProperty("production").GetProperty("openOperations").GetInt32());
        Assert.Contains(facts.GetProperty("facts").EnumerateArray(), f => f.GetProperty("kind").GetString() == "WorkInProgress");
        Assert.Contains(facts.GetProperty("facts").EnumerateArray(), f => f.GetProperty("kind").GetString() == "ShiftAction" && f.GetProperty("status").GetString() == "Assigned");
        var duplicate = await plant.Try(reviewer, "/api/execution/shifts/closeouts", new { workCenterCode = "SLIT-WC", shiftCode = "ALL", shiftFrom = From, shiftUntil = Until, note = "duplicate", reason = "Check duplicate closeout" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        Assert.Contains("shift_already_closed", await duplicate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static long JsonId(JsonElement value) => long.Parse(value.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
}

