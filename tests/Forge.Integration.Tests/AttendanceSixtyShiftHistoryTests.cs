using System.Globalization;
using System.Text.Json;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AttendanceSixtyShiftHistoryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Sixty_reviewed_shift_history_preserves_denominator_and_protected_exclusions()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        var policyAdmin = plant.As("s10-attendance-policy-admin");
        var reviewer = plant.As("s10-independent-attendance-reviewer");
        var importer = plant.As("s10-attendance-importer");
        var policyAdminId = await plant.Person("s10-attendance-policy-admin", "HumanResources");
        var reviewerId = await plant.Person("s10-independent-attendance-reviewer", "HumanResources");
        var importerId = await plant.Person("s10-attendance-importer", "AttendanceImporter");
        Assert.NotEqual(policyAdminId, reviewerId);
        Assert.NotEqual(importerId, reviewerId);
        var worker = await plant.Person("s10-attendance-history-worker");
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{worker}", new { homeWorkCenterCode = "SLIT-WC" });
        using (var workersResponse = await plant.Admin.GetAsync("/api/workforce/workers"))
        {
            var workersBody = await workersResponse.Content.ReadAsStringAsync();
            Assert.True(workersResponse.IsSuccessStatusCode, workersBody);
            foreach (var existingWorker in JsonDocument.Parse(workersBody).RootElement.EnumerateArray())
            {
                var personId = existingWorker.GetProperty("personId").GetGuid();
                if (personId == worker) continue;
                await plant.Ok(plant.Admin, $"/api/workforce/workers/{personId:D}/shift", new { shiftCode = (string?)null, reason = "Restrict isolated 60-shift fixture to one employee" });
            }
        }
        await plant.Ok(plant.Engineer, "/api/resources/shifts", new
        {
            code = "S10-ALL-DAY", siteCode = "PLANT1", name = "Sprint 10 history fixture", start = "00:00:00",
            durationMinutes = 480, days = new[] { 1, 2, 3, 4, 5, 6, 7 }, reason = "Create isolated attendance acceptance fixture",
        });
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{worker}/shift", new { shiftCode = "S10-ALL-DAY", reason = "Assign isolated attendance acceptance fixture" });

        var zone = TimeZoneInfo.FindSystemTimeZoneById(TestSite.TimeZone);
        var localToday = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;
        var localFrom = DateTime.SpecifyKind(localToday.AddDays(-60), DateTimeKind.Unspecified);
        var localThrough = DateTime.SpecifyKind(localToday, DateTimeKind.Unspecified);
        var from = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localFrom, zone), TimeSpan.Zero);
        var through = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localThrough, zone), TimeSpan.Zero);
        await plant.Ok(policyAdmin, "/api/workforce/attendance-policies", new
        {
            policyCode = "S10-SIXTY-SHIFT", revision = 1, effectiveFrom = from.AddDays(-1), effectiveThrough = (DateTimeOffset?)null,
            graceMinutes = 5, includeCallOutsInArrivalDenominator = false, reason = "Create isolated attendance acceptance policy",
        });
        await plant.Ok(policyAdmin, "/api/workforce/attendance-schedule/generate", new { from, through, reason = "Generate 60 historical daily shift opportunities" });

        var range = $"from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(through.ToString("O", CultureInfo.InvariantCulture))}";
        using var opportunitiesResponse = await policyAdmin.GetAsync($"/api/workforce/attendance-opportunities?{range}");
        var opportunitiesBody = await opportunitiesResponse.Content.ReadAsStringAsync();
        Assert.True(opportunitiesResponse.IsSuccessStatusCode, opportunitiesBody);
        var opportunities = JsonDocument.Parse(opportunitiesBody).RootElement.EnumerateArray()
            .Where(item => item.GetProperty("personId").GetGuid() == worker).ToArray();
        Assert.Equal(60, opportunities.Length);

        for (var index = 0; index < opportunities.Length; index++)
        {
            var opportunity = opportunities[index];
            var opportunityId = opportunity.GetProperty("opportunityId").GetGuid();
            var start = opportunity.GetProperty("scheduledStart").GetDateTimeOffset();
            var kind = index < 4 ? "ApprovedPto" : index == 4 ? "ProtectedLeave" : "Arrival";
            JsonElement created;
            if (index < 5)
            {
                created = await plant.Ok(policyAdmin, $"/api/workforce/attendance-opportunities/{opportunityId:D}/classifications", new
                {
                    kind, observedAt = (DateTimeOffset?)null, reasonCode = "S10_DEMO_EXCLUSION", reason = "Fictional verified attendance fixture",
                });
            }
            else
            {
                var delay = index >= 58 ? 12 : 2;
                created = await plant.Ok(importer, "/api/workforce/attendance-intelligence/imports", new
                {
                    opportunityId, kind, observedAt = start.AddMinutes(delay), source = "Hris",
                    sourceRecordId = $"S10-SIXTY:{index:D2}", reason = "Import fictional historical arrival fact",
                });
            }

            Assert.True(created.TryGetProperty("entityId", out var eventIdJson), created.GetRawText());
            var eventId = Guid.Parse(eventIdJson.GetString()!);
            await plant.Ok(reviewer, $"/api/workforce/attendance-intelligence/events/{eventId:D}/review", new
            {
                decision = "Verified", reasonCode = "S10_INDEPENDENT_REVIEW", reason = "Independently verify fictional source evidence",
            });
        }

        using var summaryResponse = await reviewer.GetAsync($"/api/workforce/attendance-intelligence?{range}");
        var summaryBody = await summaryResponse.Content.ReadAsStringAsync();
        Assert.True(summaryResponse.IsSuccessStatusCode, summaryBody);
        var summary = JsonDocument.Parse(summaryBody).RootElement;
        Assert.Equal(60, summary.GetProperty("scheduledOpportunities").GetInt32());
        Assert.Equal(55, summary.GetProperty("eligibleArrivalOpportunities").GetInt32());
        Assert.Equal(53, summary.GetProperty("onTimeArrivals").GetInt32());
        Assert.Equal(2, summary.GetProperty("verifiedLateArrivals").GetInt32());
        Assert.Equal(5, summary.GetProperty("excludedOpportunities").GetInt32());
        Assert.Equal(0, summary.GetProperty("pendingReviewOpportunities").GetInt32());
        Assert.Equal(96.36m, summary.GetProperty("onTimeArrivalPercent").GetDecimal());
    }
}
