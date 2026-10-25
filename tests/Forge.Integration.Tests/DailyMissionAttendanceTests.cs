using System.Globalization;
using System.Text.Json;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DailyMissionAttendanceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Current_shift_mission_keeps_a_missing_clock_in_pending_and_aggregated()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var plant = await SlittingPlant.BuildAsync(app);
        var supervisorId = await plant.Person("daily-mission-attendance-supervisor", "Supervisor");
        var supervisor = plant.As("daily-mission-attendance-supervisor");
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{supervisorId:D}", new { homeWorkCenterCode = "SLIT-WC" });

        var now = DateTimeOffset.UtcNow;
        var shiftStart = now.AddHours(-1);
        await plant.Ok(plant.Engineer, "/api/resources/shifts", new
        {
            code = "MISSION-ATTENDANCE",
            siteCode = "PLANT1",
            name = "Daily Mission attendance test",
            start = shiftStart.ToString("HH:mm:00", CultureInfo.InvariantCulture), // whole minutes, as the shift form enters them
            durationMinutes = 480,
            days = new[] { 1, 2, 3, 4, 5, 6, 7 },
        });
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{supervisorId:D}/shift", new { shiftCode = "MISSION-ATTENDANCE" });

        await plant.Person("daily-mission-attendance-hr", "HumanResources");
        var hrClient = plant.As("daily-mission-attendance-hr");
        await plant.Ok(hrClient, "/api/workforce/attendance-policies", new
        {
            policyCode = "DEFAULT",
            revision = 1,
            effectiveFrom = now.AddYears(-1),
            effectiveThrough = (DateTimeOffset?)null,
            graceMinutes = 5,
            includeCallOutsInArrivalDenominator = false,
            reason = "Create the attendance policy for the Daily Mission integration test",
        });
        await plant.Ok(hrClient, "/api/workforce/attendance-schedule/generate", new
        {
            from = now.AddDays(-1),
            through = now.AddDays(1),
            reason = "Generate the current shift opportunity for the Daily Mission integration test",
        });

        using var response = await supervisor.GetAsync("/api/daily-mission");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var document = JsonDocument.Parse(body);
        var mission = document.RootElement;
        var attendanceItem = Assert.Single(mission.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("sourceType").GetString() == "AttendanceReliability");
        Assert.Equal("Watch/Upcoming", attendanceItem.GetProperty("group").GetString());
        Assert.Contains("pending review", attendanceItem.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing punches are not classified as absences", attendanceItem.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("explicitly verified no-show", attendanceItem.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Daily Mission attendance test", attendanceItem.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal("#/performance", attendanceItem.GetProperty("deepLink").GetString());
    }
}
