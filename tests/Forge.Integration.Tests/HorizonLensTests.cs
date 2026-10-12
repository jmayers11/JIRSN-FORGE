using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Forge.Kernel.Security;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class HorizonLensTests(PostgresFixture postgres)
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now += interval;
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
    private static async Task<JsonElement> Get(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    [Fact]
    public async Task All_seven_lenses_share_archived_scope_counts_and_keep_old_projection_and_commitment_history()
    {
        var clock = new Clock(new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-8).AddHours(16), TimeSpan.Zero));
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("horizon-planner2", "Planner");
        await plant.Person("horizon-restricted", "Planner");
        await plant.Person("horizon-programs", "ProgramManager");
        await plant.Person("horizon-maintenance", "MaintenancePlanner");
        await plant.Person("horizon-maintenance2", "MaintenancePlanner");
        await plant.Person("horizon-auditor", "Auditor");
        var planner2 = plant.As("horizon-planner2");
        var programs = plant.As("horizon-programs");
        var restricted = plant.As("horizon-restricted");
        var programA = Guid.Parse((await plant.Ok(programs, "/api/programs/", new { code = "HORIZON-A", name = "Recorded Horizon program A" })).GetProperty("entityId").GetString()!);
        var programB = Guid.Parse((await plant.Ok(programs, "/api/programs/", new { code = "HORIZON-B", name = "Hidden Horizon program B" })).GetProperty("entityId").GetString()!);
        foreach (var subject in new[] { "planner", "horizon-planner2", "horizon-programs", "eng" })
        foreach (var program in new[] { programA, programB })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[subject]}/programs", new { programId = program, reason = "Set up the reference scope" });
        foreach (var subject in new[] { "horizon-restricted", "horizon-auditor" })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[subject]}/programs", new { programId = programA, reason = "Program A only" });
        await plant.Ok(plant.Admin, "/api/kernel/roles", new { role = "HorizonProgramReader", displayName = "Scoped archived program reader", permissions = new[] { Permissions.ScheduleRead, Permissions.WorkforceRead, Permissions.ProgramsRead }, reason = "Configure actual Horizon action authority" });
        foreach (var permission in new[] { Permissions.ScheduleRead, Permissions.WorkforceRead, Permissions.ProgramsRead })
            await plant.Ok(plant.Admin, "/api/kernel/roles/HorizonProgramReader/permission-scopes", new { permission, scopeType = "program", scopeId = programA.ToString("D"), reason = "Every archived lens is limited to program A" });
        await plant.Person("horizon-scoped", "HorizonProgramReader");
        foreach (var program in new[] { programA, programB })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["horizon-scoped"]}/programs", new { programId = program, reason = "Membership alone must not widen scoped read authority" });
        var scoped = plant.As("horizon-scoped");
        await plant.Ok(plant.Admin, "/api/kernel/roles", new { role = "HorizonWorkCenterReader", displayName = "Scoped archived work center reader", permissions = new[] { Permissions.ScheduleRead }, reason = "Configure work center Horizon authority" });
        await plant.Ok(plant.Admin, "/api/kernel/roles/HorizonWorkCenterReader/permission-scopes", new { permission = Permissions.ScheduleRead, scopeType = "work_center", scopeId = "SLIT-WC", reason = "Archived work center scope" });
        await plant.Person("horizon-work-center", "HorizonWorkCenterReader");
        foreach (var program in new[] { programA, programB })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["horizon-work-center"]}/programs", new { programId = program, reason = "Independent program membership for the work center reader" });
        await plant.Ok(plant.Admin, "/api/kernel/roles", new { role = "HorizonFacilityReader", displayName = "Scoped archived facility reader", permissions = new[] { Permissions.ScheduleRead }, reason = "Configure recorded facility Horizon authority" });
        await plant.Ok(plant.Admin, "/api/kernel/roles/HorizonFacilityReader/permission-scopes", new { permission = Permissions.ScheduleRead, scopeType = "facility", scopeId = "PLANT1", reason = "Read the site recorded when the schedule was proposed" });
        await plant.Person("horizon-facility", "HorizonFacilityReader");
        foreach (var program in new[] { programA, programB })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["horizon-facility"]}/programs", new { programId = program, reason = "Independent program membership for the facility reader" });
        await plant.Ok(plant.Admin, "/api/kernel/roles", new { role = "HorizonOtherFacilityReader", displayName = "Reader of a different recorded facility", permissions = new[] { Permissions.ScheduleRead }, reason = "Prove a facility scope cannot include another site's archived work" });
        await plant.Ok(plant.Admin, "/api/kernel/roles/HorizonOtherFacilityReader/permission-scopes", new { permission = Permissions.ScheduleRead, scopeType = "facility", scopeId = "PLANT2", reason = "Different facility boundary" });
        await plant.Person("horizon-other-facility", "HorizonOtherFacilityReader");
        foreach (var program in new[] { programA, programB })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["horizon-other-facility"]}/programs", new { programId = program, reason = "Program membership must not widen the facility grant" });
        var startDate = clock.GetUtcNow().AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endDate = clock.GetUtcNow().AddDays(60).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var contractA = Id(await plant.Ok(programs, $"/api/programs/{programA}/contracts", new { number = "HORIZON-C-A", customer = "Customer A", title = "Recorded commitment A", periodStart = startDate, periodEnd = endDate }));
        var contractB = Id(await plant.Ok(programs, $"/api/programs/{programB}/contracts", new { number = "HORIZON-C-B", customer = "Customer B", title = "Other program commitment", periodStart = startDate, periodEnd = endDate }));
        foreach (var contract in new[] { contractA, contractB })
            await plant.Ok(programs, $"/api/programs/contracts/{contract}/clins", new { clin = "0001", description = "Slit rolls", partNumber = "ROLL-100", quantity = 100m, unitPrice = 1m });
        await plant.Ok(programs, $"/api/programs/contracts/{contractA}/clins", new { clin = "0002", description = "Unrelated material", partNumber = "COIL-AL", quantity = 100m, unitPrice = 1m });
        await plant.Ok(plant.Engineer, "/api/resources/shifts", new { code = "HORIZON-ALL", siteCode = "PLANT1", name = "Reference full-day calendar", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        foreach (var workCenter in new[] { "SLIT-WC", "PAINT-WC", "ASSY-WC" })
            await plant.Ok(plant.Engineer, $"/api/resources/work-centers/{workCenter}/shifts", new { shiftCode = "HORIZON-ALL" });
        foreach (var subject in new[] { "sarah", "michael", "jennifer", "dana" })
            await plant.Ok(plant.Engineer, $"/api/workforce/workers/{plant.People[subject]}/shift", new { shiftCode = "HORIZON-ALL" });
        foreach (var machine in new[] { "SLITTER-A", "SLITTER-B" })
        {
            var outage = Id(await plant.Ok(plant.As("horizon-maintenance"), $"/api/maintenance/machines/{machine}/planned", new { startsAt = clock.GetUtcNow().AddMinutes(1), endsAt = clock.GetUtcNow().AddDays(3), description = "Recorded future machine service", reason = "Place the old projection inside the seven-day historical window" }));
            await plant.Ok(plant.As("horizon-maintenance2"), $"/api/maintenance/outages/{outage}/approve", new { reason = "Independently approve the actual capacity loss after reviewing the preview" });
        }
        var orders = new List<long>();
        foreach (var program in new Guid?[] { programA, programB, null })
        {
            var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = clock.GetUtcNow().AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), priority = 3, programId = program }));
            await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Release the source order" });
            orders.Add(order);
        }
        var committedOrder = orders[0];
        var wrongProgram = await plant.Try(planner, $"/api/orders/{committedOrder}/contract-commitment", new { contractId = contractB, clin = "0001", reason = "Cannot invent cross-program attribution" });
        Assert.Equal(HttpStatusCode.NotFound, wrongProgram.StatusCode);
        var wrongPart = await plant.Try(planner, $"/api/orders/{committedOrder}/contract-commitment", new { contractId = contractA, clin = "0002", reason = "The contract line must cover this part" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongPart.StatusCode);
        await plant.Ok(planner, $"/api/orders/{committedOrder}/contract-commitment", new { contractId = contractA, clin = "0001", reason = "Explicit production commitment to CLIN 0001" });
        var first = Id(await plant.Ok(planner, "/api/planning/schedules", new { horizonDays = 14, reason = "Original historical projection" }));
        await plant.Ok(planner2, $"/api/planning/schedules/{first}/approve", new { reason = "Approve the original plan" });
        clock.Advance(TimeSpan.FromDays(1));
        var past = clock.GetUtcNow();
        var source = await Get(planner, $"/api/horizon/lenses?at={Uri.EscapeDataString(past.ToString("O"))}&from={Uri.EscapeDataString(past.ToString("O"))}&to={Uri.EscapeDataString(past.AddDays(14).ToString("O"))}&orderNumber={committedOrder}");
        Assert.Equal(first, source.GetProperty("scheduleVersion").GetInt64());
        Assert.True(source.GetProperty("operations").EnumerateArray().Any(operation => operation.GetProperty("sequence").GetInt32() == 10), source.GetRawText());
        var firstOperation = source.GetProperty("operations").EnumerateArray().Single(operation => operation.GetProperty("sequence").GetInt32() == 10);
        Assert.True(firstOperation.GetProperty("scheduled").GetBoolean());
        var person = firstOperation.GetProperty("segments").EnumerateArray().First(segment => segment.GetProperty("end").GetDateTimeOffset() > past).GetProperty("operators")[0].GetGuid();
        var machineBefore = firstOperation.GetProperty("machineCode").GetString()!;
        var endBefore = firstOperation.GetProperty("end").GetDateTimeOffset();
        Assert.True(endBefore > past, "The approved machine outage must leave the old projection inside the historical window.");
        clock.Advance(TimeSpan.FromDays(7));
        var now = clock.GetUtcNow();
        foreach (var subject in new[] { "sarah", "michael", "jennifer", "dana" })
            await plant.Ok(plant.Engineer, $"/api/workforce/workers/{plant.People[subject]}/presence", new { kind = "SupervisorConfirmed" });
        await plant.Ok(plant.Engineer, "/api/holds", new { kind = "Production", targetType = "Machine", targetId = machineBefore, reason = "Replan around the current machine disruption" });
        var second = Id(await plant.Ok(planner, "/api/planning/schedules", new { horizonDays = 14, reason = "Changed current projection" }));
        await plant.Ok(planner2, $"/api/planning/schedules/{second}/approve", new { reason = "Approve the current replan" });

        string Query(string lens, DateTimeOffset at, DateTimeOffset from, DateTimeOffset to, string extra = "") =>
            $"/api/horizon/lenses?lens={lens}&at={Uri.EscapeDataString(at.ToString("O"))}&from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}{extra}";
        var commonScope = $"&programId={programA}&contractId={contractA}&workCenter=SLIT-WC&machine={machineBefore}&orderNumber={committedOrder}&personId={person}";
        string? expectedCounts = null;
        foreach (var lens in new[] { "Factory", "Program", "Contract", "WorkCenter", "Machine", "Order", "Workforce" })
        {
            var view = await Get(planner, Query(lens, past, past, now, commonScope));
            Assert.Equal(first, view.GetProperty("scheduleVersion").GetInt64());
            var operation = Assert.Single(view.GetProperty("operations").EnumerateArray());
            Assert.Equal((machineBefore, endBefore), (operation.GetProperty("machineCode").GetString(), operation.GetProperty("end").GetDateTimeOffset()));
            Assert.Equal((1, 1), (view.GetProperty("counts").GetProperty("orders").GetInt32(), view.GetProperty("counts").GetProperty("operations").GetInt32()));
            expectedCounts ??= view.GetProperty("counts").GetRawText();
            Assert.Equal(expectedCounts, view.GetProperty("counts").GetRawText());
            var scopedView = await Get(scoped, Query(lens, past, past, now, commonScope));
            Assert.Equal(expectedCounts, scopedView.GetProperty("counts").GetRawText());
            var commitment = Assert.Single(view.GetProperty("commitments").EnumerateArray());
            Assert.Equal((contractA, "0001"), (commitment.GetProperty("contractId").GetInt64(), commitment.GetProperty("clin").GetString()));
        }
        var current = await Get(planner, Query("Order", now, now, now.AddDays(14), $"&orderNumber={committedOrder}"));
        Assert.Equal(second, current.GetProperty("scheduleVersion").GetInt64());
        var currentOperation = current.GetProperty("operations").EnumerateArray().Single(operation => operation.GetProperty("sequence").GetInt32() == 10);
        Assert.NotEqual(machineBefore, currentOperation.GetProperty("machineCode").GetString());
        Assert.NotEqual(endBefore, currentOperation.GetProperty("end").GetDateTimeOffset());
        var contractOnly = await Get(planner, Query("Contract", now, now, now.AddDays(14)));
        Assert.Equal(1, contractOnly.GetProperty("counts").GetProperty("orders").GetInt32());
        Assert.Equal(4, contractOnly.GetProperty("excludedUnlinkedOperations").GetInt32());
        var restrictedView = await Get(restricted, Query("Factory", now, now, now.AddDays(14)));
        Assert.DoesNotContain(restrictedView.GetProperty("orders").EnumerateArray(), order => order.GetProperty("orderNumber").GetInt64() == orders[1]);
        Assert.DoesNotContain("HORIZON-B", restrictedView.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await restricted.GetAsync(Query("Contract", now, now, now.AddDays(14), $"&contractId={contractB}"))).StatusCode);
        var scopedFactory = await Get(scoped, Query("Factory", now, now, now.AddDays(14)));
        Assert.Equal(1, scopedFactory.GetProperty("counts").GetProperty("orders").GetInt32());
        Assert.DoesNotContain("HORIZON-B", scopedFactory.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(scopedFactory.GetProperty("orders").EnumerateArray(), order => order.GetProperty("orderNumber").GetInt64() != committedOrder);
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync(Query("Contract", now, now, now.AddDays(14), $"&contractId={contractB}"))).StatusCode);
        var workCenterView = await Get(plant.As("horizon-work-center"), Query("WorkCenter", past, past, now, "&workCenter=SLIT-WC"));
        Assert.True(workCenterView.GetProperty("counts").GetProperty("operations").GetInt32() > 0);
        Assert.All(workCenterView.GetProperty("operations").EnumerateArray(), operation => Assert.Equal("SLIT-WC", operation.GetProperty("workCenterCode").GetString()));
        var facilityView = await Get(plant.As("horizon-facility"), Query("Factory", past, past, now));
        Assert.Equal(workCenterView.GetProperty("counts").GetRawText(), facilityView.GetProperty("counts").GetRawText());
        Assert.All(facilityView.GetProperty("operations").EnumerateArray(), operation => Assert.Equal("PLANT1", operation.GetProperty("siteCode").GetString()));
        var otherFacilityView = await Get(plant.As("horizon-other-facility"), Query("Factory", past, past, now));
        Assert.Equal((0, 0), (otherFacilityView.GetProperty("counts").GetProperty("orders").GetInt32(), otherFacilityView.GetProperty("counts").GetProperty("operations").GetInt32()));
        Assert.Empty(otherFacilityView.GetProperty("operations").EnumerateArray());
        Assert.Empty(otherFacilityView.GetProperty("choices").GetProperty("orders").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.As("horizon-work-center").GetAsync(Query("Workforce", past, past, now, "&workCenter=SLIT-WC"))).StatusCode);

        clock.Advance(TimeSpan.FromMinutes(1));
        await plant.Ok(planner, $"/api/orders/{committedOrder}/contract-commitment", new { contractId = (long?)null, clin = (string?)null, reason = "Clear today's commitment without changing recorded history" });
        var oldContract = await Get(planner, Query("Contract", past, past, now, $"&contractId={contractA}"));
        Assert.Equal(1, oldContract.GetProperty("counts").GetProperty("orders").GetInt32());
        var cleared = await Get(planner, Query("Contract", clock.GetUtcNow(), now, now.AddDays(14), $"&contractId={contractA}"));
        Assert.Equal(0, cleared.GetProperty("counts").GetProperty("orders").GetInt32());
        var evidence = await Get(plant.As("horizon-auditor"), $"/api/kernel/audit?entityType=ProductionOrder&entityId={committedOrder}");
        Assert.Contains(evidence.EnumerateArray(), entry => entry.GetProperty("entry").GetProperty("action").GetString() == "AssignOrderCommitment"
            && entry.GetProperty("entry").GetProperty("reason").GetString() == "Explicit production commitment to CLIN 0001");
    }
}
