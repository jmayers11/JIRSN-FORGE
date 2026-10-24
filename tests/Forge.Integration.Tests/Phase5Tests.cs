using System.Net;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// Phase 5 exit gate through the real API: badges, attendance kept apart from direct labor,
/// operation start/report/complete, spec §80 supervised training with badge+PIN signoff,
/// schedule attainment, and badge secrecy.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class Phase5Tests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-0001";
    private const string Michael = "B-MICHAEL-0002";
    private const string Alex = "B-ALEX-0003";

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<(int Status, string Body)> Try(SlittingPlant.Plant plant, HttpClient client, string url, object body)
    {
        var response = await plant.Try(client, url, body);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private sealed record Floor(SlittingPlant.Plant Plant, HttpClient Planner, HttpClient Kiosk, Guid AlexId);

    /// <summary>The engineered Slitting plant on a 24/7 shift, a trainer qualification, Alex in training, badges and a kiosk.</summary>
    private static async Task<Floor> FloorAsync(ForgeApp app)
    {
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        foreach (var wc in new[] { "SLIT-WC", "PAINT-WC", "ASSY-WC" })
        {
            await plant.Ok(e, $"/api/resources/work-centers/{wc}/shifts", new { shiftCode = "ALL" });
        }

        // Trainer qualification for SLIT-II: a different thing from being a qualified operator.
        await plant.Ok(e, "/api/competency/qualification-types", new { code = "TRAIN-SLIT", name = "Slitting trainer", kind = "Trainer", validityDays = 365 });
        await plant.Ok(e, "/api/competency/qualification-types/TRAIN-SLIT/prerequisites", new { prerequisiteCode = "SLIT-II" });
        await plant.Ok(e, $"/api/competency/people/{plant.People["michael"]}/training", new { code = "TRAIN-SLIT" });
        await plant.Ok(plant.Approver, $"/api/competency/people/{plant.People["michael"]}/certifications", new { code = "TRAIN-SLIT", evidence = "Train-the-trainer course", reason = "Trainer certified" });

        // Alex is a worker in training for SLIT-II, not qualified.
        var alex = await plant.Person("alex");
        await plant.Ok(e, $"/api/workforce/workers/{alex}", new { homeWorkCenterCode = "SLIT-WC" });
        await plant.Ok(e, $"/api/competency/people/{alex}/training", new { code = "SLIT-II" });
        foreach (var person in new[] { "sarah", "michael", "jennifer", "dana" })
        {
            await plant.Ok(e, $"/api/workforce/workers/{plant.People[person]}/shift", new { shiftCode = "ALL" });
        }

        foreach (var (person, badge) in new[] { ("sarah", Sarah), ("michael", Michael), ("alex", Alex) })
        {
            await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People[person]}/badge", new { badge });
        }

        await plant.Person("kiosk", "ShopFloorTerminal");
        return new Floor(plant, planner, plant.As("kiosk"), alex);
    }

    private static async Task<long> ReleasedOrderAsync(Floor f, decimal quantity)
    {
        var due = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var created = await f.Plant.Ok(f.Plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity, dueDate = due, priority = 3 });
        var number = long.Parse(created.GetProperty("entityId").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        await f.Plant.Ok(f.Planner, $"/api/orders/{number}/release", new { reason = "Customer order" });
        return number;
    }

    [Fact]
    public async Task Spec_80_trainee_starts_only_under_a_present_authorized_trainer_and_is_never_auto_qualified()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var f = await FloorAsync(app);
        var order = await ReleasedOrderAsync(f, 10m);
        var k = f.Kiosk;

        // A schedule says nothing about presence: Alex must clock in.
        var notIn = await Try(f.Plant, k, "/api/shopfloor/start", new { badge = Alex, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        Assert.Equal(422, notIn.Status);
        Assert.Contains("clock_in_required", notIn.Body, StringComparison.Ordinal);
        await f.Plant.Ok(k, "/api/shopfloor/clock-in", new { badge = Alex });

        // Forge detects the missing qualification and names the trainers who are here.
        var needTrainer = await Try(f.Plant, k, "/api/shopfloor/start", new { badge = Alex, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        Assert.Equal(422, needTrainer.Status);
        Assert.Contains("trainer_required", needTrainer.Body, StringComparison.Ordinal);
        Assert.Contains("Michael", needTrainer.Body, StringComparison.Ordinal);

        // A qualified operator is not a trainer.
        var notTrainer = await Try(f.Plant, k, "/api/shopfloor/start", new { badge = Alex, orderNumber = order, sequence = 10, machineCode = "SLITTER-A", trainerBadge = Sarah });
        Assert.Equal(422, notTrainer.Status);
        Assert.Contains("not an authorized trainer", notTrainer.Body, StringComparison.Ordinal);

        // The trainer badges: trainer authorization, certification, eligibility, safety and machine all check out.
        var started = await f.Plant.Ok(k, "/api/shopfloor/start", new { badge = Alex, orderNumber = order, sequence = 10, machineCode = "SLITTER-A", trainerBadge = Michael });
        Assert.Contains("Supervised training started", started.GetProperty("message").GetString(), StringComparison.Ordinal);

        var floor = await Get(k, $"/api/orders/{order}/shopfloor");
        var session = floor.GetProperty("training").EnumerateArray().Single();
        Assert.Equal(f.AlexId, session.GetProperty("traineeId").GetGuid());
        Assert.Equal(f.Plant.People["michael"], session.GetProperty("trainerId").GetGuid());
        var roles = floor.GetProperty("labor").EnumerateArray().Select(l => l.GetProperty("role").GetString()).Order().ToList();
        Assert.Equal(new[] { "Trainee", "Trainer" }, roles);
        Assert.All(floor.GetProperty("labor").EnumerateArray(), labor => Assert.Equal("Training", labor.GetProperty("phase").GetString()));

        // The trainee reports; the trainer cannot report for themselves.
        await f.Plant.Ok(k, "/api/shopfloor/report", new { badge = Alex, orderNumber = order, sequence = 10, good = 4m, scrap = 0m, complete = false });
        var trainerReport = await Try(f.Plant, k, "/api/shopfloor/report", new { badge = Michael, orderNumber = order, sequence = 10, good = 1m, scrap = 0m, complete = false });
        Assert.Equal(422, trainerReport.Status);

        // Evaluation is a consequential signoff: badge plus PIN.
        var sessionId = session.GetProperty("sessionId").GetInt64();
        var noPin = await Try(f.Plant, k, $"/api/shopfloor/training/{sessionId}/evaluate", new { trainerBadge = Michael, pin = "1234", evaluation = "ReadyForAssessment" });
        Assert.Contains("signoff_failed", noPin.Body, StringComparison.Ordinal);
        await f.Plant.Ok(k, "/api/shopfloor/pin", new { badge = Michael, newPin = "4321" });
        var wrongPin = await Try(f.Plant, k, $"/api/shopfloor/training/{sessionId}/evaluate", new { trainerBadge = Michael, pin = "9999", evaluation = "ReadyForAssessment" });
        Assert.Contains("signoff_failed", wrongPin.Body, StringComparison.Ordinal);
        var notTheTrainer = await Try(f.Plant, k, $"/api/shopfloor/training/{sessionId}/evaluate", new { trainerBadge = Sarah, pin = "4321", evaluation = "ReadyForAssessment" });
        Assert.Contains("not_the_trainer", notTheTrainer.Body, StringComparison.Ordinal);
        var evaluated = await f.Plant.Ok(k, $"/api/shopfloor/training/{sessionId}/evaluate", new { trainerBadge = Michael, pin = "4321", evaluation = "ReadyForAssessment", notes = "Threading and splice correct" });
        Assert.Contains("not certification", evaluated.GetProperty("message").GetString(), StringComparison.Ordinal);

        // No qualification is automatically granted.
        var quals = await Get(f.Plant.Engineer, $"/api/competency/people/{f.AlexId}");
        Assert.Equal("InTraining", quals.EnumerateArray().Single(q => q.GetProperty("code").GetString() == "SLIT-II").GetProperty("status").GetString());

        // The session and both labor entries are closed; the signoff is on record.
        floor = await Get(k, $"/api/orders/{order}/shopfloor");
        Assert.All(floor.GetProperty("labor").EnumerateArray(), l => Assert.NotEqual(JsonValueKind.Null, l.GetProperty("endedAt").ValueKind));
        Assert.Equal(1, await Scalar(app, "SELECT count(*) FROM execution.electronic_signoff WHERE method = 'BadgeAndPin'"));
        Assert.Equal(1, await Scalar(app, "SELECT count(*) FROM execution.electronic_signoff WHERE signer_role = 'Trainer' AND assurance = 'Badge+PIN' AND record_version = 2"));
    }

    [Fact]
    public async Task Attendance_and_direct_labor_stay_separate_and_reporting_completes_the_order()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var f = await FloorAsync(app);
        var order = await ReleasedOrderAsync(f, 10m);
        var k = f.Kiosk;

        await f.Plant.Ok(k, "/api/shopfloor/clock-in", new { badge = Sarah });
        var again = await f.Plant.Ok(k, "/api/shopfloor/clock-in", new { badge = Sarah });
        Assert.Equal("no_change", again.GetProperty("code").GetString()); // a second scan is not a second shift

        // A machine that is down cannot be started.
        await f.Plant.Person("tech", "MaintenanceTechnician");
        await f.Plant.Ok(f.Plant.As("tech"), "/api/maintenance/machines/SLITTER-B/down", new { description = "Blade drive fault" });
        var down = await Try(f.Plant, k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-B" });
        Assert.Equal(422, down.Status);
        Assert.Contains("SLITTER-B is down", down.Body, StringComparison.Ordinal); // refused by the machine check

        await f.Plant.Ok(k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A", phase = "Setup" });
        var startedFloor = await Get(k, $"/api/orders/{order}/shopfloor");
        Assert.Equal("Setup", Assert.Single(startedFloor.GetProperty("labor").EnumerateArray()).GetProperty("phase").GetString());
        var twice = await f.Plant.Ok(k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        Assert.Equal("no_change", twice.GetProperty("code").GetString());

        var noReason = await Try(f.Plant, k, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 0m, scrap = 1m, complete = false });
        Assert.Contains("scrap_reason_required", noReason.Body, StringComparison.Ordinal);
        await f.Plant.Ok(k, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 6m, scrap = 1m, scrapReason = "Edge wave", complete = false });
        var over = await Try(f.Plant, k, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 5m, scrap = 0m, complete = false });
        Assert.Contains("over_reported", over.Body, StringComparison.Ordinal);
        var done = await f.Plant.Ok(k, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 4m, scrap = 0m, complete = true });
        Assert.Contains("complete", done.GetProperty("message").GetString(), StringComparison.Ordinal);

        // Completing the operation ended Sarah's labor; she is still clocked in.
        var me = await f.Plant.Ok(k, "/api/shopfloor/identify", new { badge = Sarah });
        Assert.Equal(JsonValueKind.Null, me.GetProperty("labor").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, me.GetProperty("clockedInSince").ValueKind);

        await f.Plant.Ok(k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 20 });
        var finished = await f.Plant.Ok(k, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 20, good = 10m, scrap = 0m, complete = true });
        Assert.Contains("completed", finished.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("Completed", (await Get(f.Planner, $"/api/orders/{order}")).GetProperty("status").GetString());

        // Clock out: attendance closes; labor and attendance are separate records.
        var outResult = await f.Plant.Ok(k, "/api/shopfloor/clock-out", new { badge = Sarah });
        Assert.Equal("ok", outResult.GetProperty("clockedOut").GetProperty("code").GetString());
        var activity = await Get(k, "/api/shopfloor/activity");
        var sarahId = f.Plant.People["sarah"];
        Assert.Single(activity.GetProperty("attendance").EnumerateArray(), a => a.GetProperty("personId").GetGuid() == sarahId && a.GetProperty("clockOutAt").ValueKind != JsonValueKind.Null);
        Assert.Equal(2, activity.GetProperty("labor").EnumerateArray().Count(l => l.GetProperty("personId").GetGuid() == sarahId));
        var reports = activity.GetProperty("reports").EnumerateArray().ToList();
        Assert.Equal(10m, reports.Where(r => r.GetProperty("sequence").GetInt32() == 10).Sum(r => r.GetProperty("goodQuantity").GetDecimal()));
        Assert.Equal(10m, reports.Where(r => r.GetProperty("finalOperation").GetBoolean()).Sum(r => r.GetProperty("goodQuantity").GetDecimal()));
        Assert.Equal(1m, reports.Sum(r => r.GetProperty("scrapQuantity").GetDecimal()));
    }

    [Fact]
    public async Task Order_labor_detail_shows_linked_stoppage_reason_as_context_not_direct_labor()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var f = await FloorAsync(app);
        var order = await ReleasedOrderAsync(f, 10m);
        await f.Plant.Ok(f.Plant.Engineer, "/api/execution/reason-codes", new
        {
            code = "LABOR-STARVED", kind = "Downtime", category = "Starved", description = "Upstream material or work not available",
        });
        await f.Plant.Ok(f.Plant.Engineer, "/api/execution/stoppages", new
        {
            machineCode = "SLITTER-A", category = "Starved", reasonCode = "LABOR-STARVED", orderNumber = order, sequence = 10,
            startedAt = DateTimeOffset.UtcNow.AddMinutes(-12), note = "Fictional scenario note", reason = "Record a linked operation stoppage",
        });

        var detail = await Get(f.Plant.Engineer, $"/api/orders/{order}/shopfloor");
        var metric = detail.GetProperty("laborMetrics").EnumerateArray().Single(item => item.GetProperty("sequence").GetInt32() == 10);

        Assert.Equal(0m, metric.GetProperty("recordedOperatorHours").GetDecimal());
        Assert.True(metric.GetProperty("recordedStoppageMinutes").GetDecimal() >= 12m);
        Assert.Contains("Starved · LABOR-STARVED", metric.GetProperty("stoppageContext").GetString(), StringComparison.Ordinal);
        Assert.Contains("not confirmed root causes", metric.GetProperty("stoppageContext").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Fictional scenario note", metric.GetProperty("stoppageContext").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attainment_measures_completed_work_against_the_schedule_in_force()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var f = await FloorAsync(app);
        var order = await ReleasedOrderAsync(f, 10m);
        await f.Plant.Person("planner2", "Planner");
        var none = await Get(f.Planner, "/api/planning/attainment");
        Assert.Equal(JsonValueKind.Null, none.GetProperty("output").GetProperty("percent").ValueKind);

        var proposed = await f.Plant.Ok(f.Planner, "/api/planning/schedules", new { horizonDays = 14 });
        await f.Plant.Ok(f.Plant.As("planner2"), $"/api/planning/schedules/{proposed.GetProperty("entityId").GetString()}/approve", new { reason = "OK" });
        var from = DateTimeOffset.UtcNow;

        await f.Plant.Ok(f.Kiosk, "/api/shopfloor/clock-in", new { badge = Sarah });
        await f.Plant.Ok(f.Kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        await f.Plant.Ok(f.Kiosk, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 10m, scrap = 0m, complete = true });

        var q = $"from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(from.AddDays(3).ToString("o"))}";
        var result = (await Get(f.Planner, "/api/planning/attainment?" + q)).GetProperty("output");
        Assert.Equal(2, result.GetProperty("plannedOperations").GetInt32());
        Assert.Equal(1, result.GetProperty("completedOperations").GetInt32());
        Assert.True(result.GetProperty("percent").GetDecimal() > 0);
    }

    [Fact]
    public async Task Badge_numbers_and_pins_never_reach_audit_events_or_idempotency_records()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var f = await FloorAsync(app);
        await f.Plant.Ok(f.Kiosk, "/api/shopfloor/clock-in", new { badge = Sarah });
        await f.Plant.Ok(f.Kiosk, "/api/shopfloor/pin", new { badge = Sarah, newPin = "86420931" });
        await Try(f.Plant, f.Kiosk, "/api/shopfloor/clock-in", new { badge = "B-NOBODY-9999" });

        foreach (var table in new[] { "kernel.audit_log", "kernel.outbox", "kernel.event_log", "kernel.idempotency", "workforce.badge" })
        {
            var text = await Text(app, $"SELECT coalesce(string_agg(t::text, ' '), '') FROM {table} t");
            Assert.DoesNotContain(Sarah, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("86420931", text, StringComparison.Ordinal); // 8 digits: a timestamp or id can hold 5 by chance
            Assert.DoesNotContain("B-NOBODY", text, StringComparison.OrdinalIgnoreCase);
        }

        // Unknown badges are refused and the refusal is audited without the number.
        Assert.True(await Scalar(app, "SELECT count(*) FROM kernel.audit_log WHERE outcome <> 'Succeeded'") > 0);
    }

    private static async Task<long> Scalar(ForgeApp app, string sql)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> Text(ForgeApp app, string sql)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
