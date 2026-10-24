using System.Globalization;
using System.Text.Json;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AttendanceSelfCorrectionTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Employee_can_request_only_own_attendance_correction_and_reviewer_keeps_independent_decision()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        var employeeId = await plant.Person("s10-self-correction-employee", "Employee");
        var otherEmployeeId = await plant.Person("s10-other-correction-employee", "Employee");
        var reviewerId = await plant.Person("s10-self-correction-reviewer", "HumanResources");
        var recorderId = await plant.Person("s10-self-correction-recorder", "HumanResources");
        var employee = plant.As("s10-self-correction-employee");
        var otherEmployee = plant.As("s10-other-correction-employee");
        var reviewer = plant.As("s10-self-correction-reviewer");
        var recorder = plant.As("s10-self-correction-recorder");

        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{employeeId:D}", new { homeWorkCenterCode = "SLIT-WC" });
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{otherEmployeeId:D}", new { homeWorkCenterCode = "SLIT-WC" });
        using (var workersResponse = await plant.Admin.GetAsync("/api/workforce/workers"))
        {
            var workersBody = await workersResponse.Content.ReadAsStringAsync();
            Assert.True(workersResponse.IsSuccessStatusCode, workersBody);
            foreach (var existingWorker in JsonDocument.Parse(workersBody).RootElement.EnumerateArray())
            {
                var personId = existingWorker.GetProperty("personId").GetGuid();
                if (personId == employeeId || personId == otherEmployeeId) continue;
                await plant.Ok(plant.Admin, $"/api/workforce/workers/{personId:D}/shift", new { shiftCode = (string?)null, reason = "Restrict isolated correction fixture to two employees" });
            }
        }
        await plant.Ok(plant.Engineer, "/api/resources/shifts", new
        {
            code = "S10-SELF-CORRECTION", siteCode = "PLANT1", name = "Self-correction acceptance fixture", start = "00:00:00",
            durationMinutes = 480, days = new[] { 1, 2, 3, 4, 5, 6, 7 }, reason = "Create isolated attendance correction fixture",
        });
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{employeeId:D}/shift", new { shiftCode = "S10-SELF-CORRECTION", reason = "Assign isolated correction fixture shift" });
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{otherEmployeeId:D}/shift", new { shiftCode = "S10-SELF-CORRECTION", reason = "Assign isolated correction fixture shift" });

        var zone = TimeZoneInfo.FindSystemTimeZoneById(TestSite.TimeZone);
        var localToday = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;
        var localFrom = DateTime.SpecifyKind(localToday.AddDays(-1), DateTimeKind.Unspecified);
        var localThrough = DateTime.SpecifyKind(localToday, DateTimeKind.Unspecified);
        var from = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localFrom, zone), TimeSpan.Zero);
        var through = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localThrough, zone), TimeSpan.Zero);
        await plant.Ok(reviewer, "/api/workforce/attendance-policies", new
        {
            policyCode = "S10-SELF-CORRECTION", revision = 1, effectiveFrom = from.AddDays(-1), effectiveThrough = (DateTimeOffset?)null,
            graceMinutes = 5, includeCallOutsInArrivalDenominator = false, reason = "Create isolated attendance correction policy",
        });
        await plant.Ok(reviewer, "/api/workforce/attendance-schedule/generate", new { from, through, reason = "Generate prior-day correction opportunities" });
        var range = $"from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(through.ToString("O", CultureInfo.InvariantCulture))}";
        using var opportunitiesResponse = await reviewer.GetAsync($"/api/workforce/attendance-opportunities?{range}");
        var opportunitiesBody = await opportunitiesResponse.Content.ReadAsStringAsync();
        Assert.True(opportunitiesResponse.IsSuccessStatusCode, opportunitiesBody);
        var opportunities = JsonDocument.Parse(opportunitiesBody).RootElement.EnumerateArray().ToArray();
        var employeeOpportunity = opportunities.Single(item => item.GetProperty("personId").GetGuid() == employeeId);
        var otherOpportunity = opportunities.Single(item => item.GetProperty("personId").GetGuid() == otherEmployeeId);
        var eventRange = $"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-2).ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture))}";
        var employeeObservedAt = employeeOpportunity.GetProperty("scheduledStart").GetDateTimeOffset().AddMinutes(12);
        var otherObservedAt = otherOpportunity.GetProperty("scheduledEnd").GetDateTimeOffset().AddMinutes(5);
        var original = await plant.Ok(reviewer, $"/api/workforce/attendance-opportunities/{employeeOpportunity.GetProperty("opportunityId").GetGuid():D}/classifications", new
        {
            kind = "Arrival", observedAt = employeeObservedAt, reasonCode = "SOURCE_TIME_ENTRY", reason = "Record acceptance fixture arrival",
        });
        var otherOriginal = await plant.Ok(recorder, $"/api/workforce/attendance-opportunities/{otherOpportunity.GetProperty("opportunityId").GetGuid():D}/classifications", new
        {
            kind = "NoShow", observedAt = otherObservedAt, reasonCode = "SOURCE_TIME_ENTRY", reason = "Record acceptance fixture no-show",
        });
        var originalId = Guid.Parse(original.GetProperty("entityId").GetString()!);
        var otherOriginalId = Guid.Parse(otherOriginal.GetProperty("entityId").GetString()!);
        var noShowReview = await plant.Ok(reviewer, $"/api/workforce/attendance-intelligence/events/{otherOriginalId:D}/review", new
        {
            decision = "Verified", reasonCode = "SOURCE_CHECKED", reason = "Independently verify the prior no-show classification",
        });
        var verifiedNoShowId = Guid.Parse(noShowReview.GetProperty("entityId").GetString()!);

        using (var mine = await employee.GetAsync($"/api/workforce/my-attendance-events?{eventRange}"))
        {
            var mineBody = await mine.Content.ReadAsStringAsync();
            Assert.True(mine.IsSuccessStatusCode, mineBody);
            var events = JsonDocument.Parse(mineBody).RootElement.EnumerateArray().ToArray();
            Assert.Contains(events, item => item.GetProperty("eventId").GetGuid() == originalId);
            Assert.DoesNotContain(events, item => item.GetProperty("eventId").GetGuid() == otherOriginalId);
            Assert.All(events, item => Assert.False(item.TryGetProperty("personId", out _)));
        }

        using (var allEvents = await employee.GetAsync($"/api/workforce/attendance-intelligence/events?{eventRange}"))
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, allEvents.StatusCode);

        using (var anotherPersonsRecord = await plant.Try(employee, $"/api/workforce/my-attendance-events/{verifiedNoShowId:D}/correction", new
        {
            kind = "Arrival", observedAt = otherObservedAt, reasonCode = "WRONG_RECORD", reason = "Attempt to change another employee's record",
        }))
        {
            Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, anotherPersonsRecord.StatusCode);
            Assert.Contains("attendance_event_not_owned", await anotherPersonsRecord.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var correctedAt = employeeOpportunity.GetProperty("scheduledStart").GetDateTimeOffset().AddMinutes(2);
        var correction = await plant.Ok(employee, $"/api/workforce/my-attendance-events/{originalId:D}/correction", new
        {
            kind = "Arrival", observedAt = correctedAt, reasonCode = "CLOCK_SOURCE_CORRECTION", reason = "The time clock source shows arrival two minutes after shift start",
        });
        var correctionId = Guid.Parse(correction.GetProperty("entityId").GetString()!);

        using (var duplicate = await plant.Try(employee, $"/api/workforce/my-attendance-events/{originalId:D}/correction", new
        {
            kind = "Arrival", observedAt = correctedAt, reasonCode = "DUPLICATE", reason = "A second correction request must not fork the record",
        }))
        {
            Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
            Assert.Contains("attendance_event_already_corrected", await duplicate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var selfReview = await plant.Try(employee, $"/api/workforce/attendance-intelligence/events/{correctionId:D}/review", new
        {
            decision = "Verified", reasonCode = "SELF_REVIEW", reason = "Employee cannot review their own correction",
        }))
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, selfReview.StatusCode);

        await plant.Ok(reviewer, $"/api/workforce/attendance-intelligence/events/{correctionId:D}/review", new
        {
            decision = "Verified", reasonCode = "SOURCE_CHECKED", reason = "Independently verify the employee's correction request",
        });
        using var reviewedEvents = await reviewer.GetAsync($"/api/workforce/attendance-intelligence/events?{eventRange}");
        var reviewedBody = await reviewedEvents.Content.ReadAsStringAsync();
        Assert.True(reviewedEvents.IsSuccessStatusCode, reviewedBody);
        var reviewedCorrection = JsonDocument.Parse(reviewedBody).RootElement.EnumerateArray()
            .Single(item => item.GetProperty("eventId").GetGuid() == correctionId);
        Assert.Equal("Verified", reviewedCorrection.GetProperty("reviewState").GetString());
        Assert.Equal(originalId, reviewedCorrection.GetProperty("supersedesEventId").GetGuid());

        var scheduleChange = await plant.Ok(otherEmployee, $"/api/workforce/my-attendance-events/{verifiedNoShowId:D}/correction", new
        {
            kind = "ScheduleChange", observedAt = (DateTimeOffset?)null, reasonCode = "APPROVED_SHIFT_CHANGE", reason = "The supervisor approved a shift schedule change before the absence",
        });
        var scheduleChangeId = Guid.Parse(scheduleChange.GetProperty("entityId").GetString()!);
        await plant.Ok(reviewer, $"/api/workforce/attendance-intelligence/events/{scheduleChangeId:D}/review", new
        {
            decision = "Verified", reasonCode = "SCHEDULE_CONFIRMED", reason = "Confirm the approved schedule change against the shift record",
        });
        using var summaryResponse = await reviewer.GetAsync($"/api/workforce/attendance-intelligence?{range}");
        var summaryBody = await summaryResponse.Content.ReadAsStringAsync();
        Assert.True(summaryResponse.IsSuccessStatusCode, summaryBody);
        var summary = JsonDocument.Parse(summaryBody).RootElement;
        Assert.Equal(2, summary.GetProperty("scheduledOpportunities").GetInt32());
        Assert.Equal(1, summary.GetProperty("eligibleArrivalOpportunities").GetInt32());
        Assert.Equal(1, summary.GetProperty("onTimeArrivals").GetInt32());
        Assert.Equal(0, summary.GetProperty("verifiedNoShows").GetInt32());
        Assert.Equal(1, summary.GetProperty("excludedOpportunities").GetInt32());
        Assert.NotEqual(employeeId, reviewerId);
        Assert.NotEqual(recorderId, reviewerId);
    }
}
