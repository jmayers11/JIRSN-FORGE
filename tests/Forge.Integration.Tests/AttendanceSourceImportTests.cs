using System.Globalization;
using System.Text.Json;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AttendanceSourceImportTests(PostgresFixture postgres)
{
    [Fact]
    public async Task External_arrival_import_is_pending_deduplicated_private_and_requires_independent_review()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        var hr = await plant.Person("hris-attendance-admin", "HumanResources");
        var hrClient = plant.As("hris-attendance-admin");
        var importer = await plant.Person("hris-attendance-importer", "AttendanceImporter");
        var worker = await plant.Person("hris-attendance-worker");
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{worker}", new { homeWorkCenterCode = "SLIT-WC" });
        await plant.Ok(plant.Engineer, "/api/resources/shifts", new
        {
            code = "ATTENDANCE-ALL", siteCode = "PLANT1", name = "Attendance import test", start = "00:00:00",
            durationMinutes = 480, days = new[] { 1, 2, 3, 4, 5, 6, 7 },
        });
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{worker}/shift", new { shiftCode = "ATTENDANCE-ALL" });

        var zone = TimeZoneInfo.FindSystemTimeZoneById(TestSite.TimeZone);
        var localStart = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date.AddDays(-1), DateTimeKind.Unspecified);
        var from = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, zone), TimeSpan.Zero);
        var through = from.AddDays(1);
        await plant.Ok(hrClient, "/api/workforce/attendance-policies", new
        {
            policyCode = "DEFAULT", revision = 1, effectiveFrom = from, effectiveThrough = (DateTimeOffset?)null,
            graceMinutes = 5, includeCallOutsInArrivalDenominator = false, reason = "Attendance import acceptance fixture",
        });
        await plant.Ok(hrClient, "/api/workforce/attendance-schedule/generate", new { from, through, reason = "Attendance import acceptance fixture" });
        var query = $"from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(through.ToString("O", CultureInfo.InvariantCulture))}";
        using var response = await hrClient.GetAsync($"/api/workforce/attendance-opportunities?{query}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        var opportunity = JsonDocument.Parse(body).RootElement.EnumerateArray().Single(item => item.GetProperty("personId").GetGuid() == worker);
        var opportunityId = opportunity.GetProperty("opportunityId").GetGuid();
        var scheduledStart = opportunity.GetProperty("scheduledStart").GetDateTimeOffset();
        var sourceRecordId = "HRIS-WW-ARRIVAL-001";
        var importPath = "/api/workforce/attendance-intelligence/imports";
        var import = new
        {
            opportunityId, kind = "Arrival", observedAt = scheduledStart.AddMinutes(2), source = "Hris", sourceRecordId,
            reason = "Import verified fictional attendance source fact",
        };
        var imported = await plant.Ok(plant.As("hris-attendance-importer"), importPath, import);
        var importedId = Guid.Parse(imported.GetProperty("entityId").GetString()!);

        using (var sameActorReview = await plant.As("hris-attendance-importer").CommandAsync(
            $"/api/workforce/attendance-intelligence/events/{importedId:D}/review",
            new { decision = "Verified", reasonCode = "IMPORT", reason = "Importer cannot review own event" }, "import-self-review"))
        {
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, sameActorReview.StatusCode);
        }

        using (var duplicate = await plant.As("hris-attendance-importer").CommandAsync(importPath, import with { reason = "Retry with another command key" }, "import-duplicate"))
        {
            Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
            Assert.Contains("attendance_source_record_duplicate", await duplicate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var queryEvents = $"from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(through.ToString("O", CultureInfo.InvariantCulture))}";
        using (var eventsResponse = await hrClient.GetAsync($"/api/workforce/attendance-intelligence/events?{queryEvents}"))
        {
            var eventsJson = await eventsResponse.Content.ReadAsStringAsync();
            Assert.True(eventsResponse.IsSuccessStatusCode, eventsJson);
            var events = JsonDocument.Parse(eventsJson).RootElement.EnumerateArray().Where(item => item.GetProperty("opportunityId").GetGuid() == opportunityId).ToArray();
            var pending = Assert.Single(events);
            Assert.Equal("PendingReview", pending.GetProperty("reviewState").GetString());
            Assert.Equal("Hris", pending.GetProperty("source").GetString());
            Assert.False(pending.TryGetProperty("sourceRecordId", out _));
            Assert.False(pending.TryGetProperty("reason", out _));
        }

        await plant.Ok(hrClient, $"/api/workforce/attendance-intelligence/events/{importedId:D}/review", new
        {
            decision = "Verified", reasonCode = "SOURCE_CHECKED", reason = "Independent review of the imported source event",
        });
        var summaryJson = JsonDocument.Parse(await hrClient.GetStringAsync($"/api/workforce/attendance-intelligence?{query}"));
        var summary = summaryJson.RootElement;
        Assert.Equal(1, summary.GetProperty("scheduledOpportunities").GetInt32());
        Assert.Equal(1, summary.GetProperty("eligibleArrivalOpportunities").GetInt32());
        Assert.Equal(1, summary.GetProperty("onTimeArrivals").GetInt32());
        Assert.Equal(100m, summary.GetProperty("onTimeArrivalPercent").GetDecimal());
    }
}
