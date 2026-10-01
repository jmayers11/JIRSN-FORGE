using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// The reference plant built through the real API: Slitting (two slitters, two-person rule),
/// Paint and Assembly, with Sarah, Michael, Jennifer and Dana on shift.
/// </summary>
internal static class SlittingPlant
{
    internal sealed class Plant(ForgeApp app)
    {
        private int key;

        public HttpClient Admin { get; } = app.As(ForgeApp.AdminSubject);

        public HttpClient Engineer { get; } = app.As("eng");

        public HttpClient Approver { get; } = app.As("approver");

        public HttpClient As(string subject) => app.As(subject);

        public Dictionary<string, Guid> People { get; } = [];

        public async Task<JsonElement> Ok(HttpClient client, string url, object body)
        {
            var response = await client.CommandAsync(url, body, "k" + Interlocked.Increment(ref key));
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
            return JsonDocument.Parse(text).RootElement;
        }

        public async Task<HttpResponseMessage> Try(HttpClient client, string url, object body) =>
            await client.CommandAsync(url, body, "k" + Interlocked.Increment(ref key));

        public async Task<Guid> Person(string subject, params string[] roles)
        {
            var created = await Ok(Admin, "/api/kernel/people", new { idpSubject = subject, displayName = char.ToUpperInvariant(subject[0]) + subject[1..], reason = "Plant setup" });
            var id = Guid.Parse(created.GetProperty("entityId").GetString()!);
            foreach (var role in roles)
            {
                await Ok(Admin, $"/api/kernel/people/{id}/roles", new { role, reason = "Plant setup" });
            }

            People[subject] = id;
            return id;
        }

        public async Task Worker(string subject, string homeWorkCenter, params string[] qualifications)
        {
            var id = await Person(subject);
            await Ok(Engineer, $"/api/workforce/workers/{id}", new { homeWorkCenterCode = homeWorkCenter });
            foreach (var q in qualifications)
            {
                await Ok(Engineer, $"/api/competency/people/{id}/training", new { code = q });
                await Ok(Approver, $"/api/competency/people/{id}/certifications", new { code = q, evidence = "Practical assessment on file", reason = "Assessment passed" });
            }

            await Ok(Engineer, $"/api/workforce/workers/{id}/presence", new { kind = "SupervisorConfirmed" });
        }

        public Task Assign(string subject, object assignment) => Ok(Engineer, $"/api/workforce/workers/{People[subject]}/assignment", assignment);
    }

    public static async Task<Plant> BuildAsync(ForgeApp app)
    {
        var plant = new Plant(app);
        await plant.Person("eng", "ManufacturingEngineer", "TrainingCoordinator", "SafetyEngineer", "Supervisor");
        await plant.Person("approver", "SafetyApprover", "Certifier");
        var e = plant.Engineer;

        foreach (var (code, name, kind) in new[] { ("SLIT-II", "Slitting Level II", "Machine"), ("FIRST-AID", "First aid", "Safety"), ("PAINT-I", "Paint", "Process") })
        {
            await plant.Ok(e, "/api/competency/qualification-types", new { code, name, kind, validityDays = 365 });
        }

        await plant.Ok(e, "/api/resources/sites", new { code = "PLANT1", name = "Plant 1" });
        foreach (var (area, wc) in new[] { ("SLITTING", "SLIT-WC"), ("PAINT", "PAINT-WC"), ("ASSEMBLY", "ASSY-WC") })
        {
            await plant.Ok(e, "/api/resources/safety-areas", new { code = area, name = area, parent = "PLANT1" });
            await plant.Ok(e, "/api/resources/work-centers", new { code = wc, name = wc, parent = area });
        }

        foreach (var (code, name) in new[] { ("RUN_SLITTER", "Run slitter"), ("STAGE_ROLL", "Stage next roll"), ("KITTING", "Kitting"), ("PAINT", "Paint"), ("ASSEMBLE", "Assemble") })
        {
            await plant.Ok(e, "/api/resources/activity-types", new { code, name });
        }

        await plant.Ok(e, "/api/resources/machines", new { code = "SLITTER-A", name = "Slitter A", parent = "SLIT-WC" });
        await plant.Ok(e, "/api/resources/machines", new { code = "SLITTER-B", name = "Slitter B", parent = "SLIT-WC" });
        await plant.Ok(e, "/api/resources/machines", new { code = "PAINT-BOOTH", name = "Paint booth", parent = "PAINT-WC" });
        await plant.Ok(e, "/api/resources/machines/SLITTER-A/activities", new { activityCode = "RUN_SLITTER", operatorQualifications = new[] { "SLIT-II" } });
        await plant.Ok(e, "/api/resources/machines/SLITTER-B/activities", new { activityCode = "RUN_SLITTER", operatorQualifications = new[] { "SLIT-II" } });
        await plant.Ok(e, "/api/resources/machines/PAINT-BOOTH/activities", new { activityCode = "PAINT", operatorQualifications = new[] { "PAINT-I" } });
        foreach (var (wc, activity) in new[] { ("SLIT-WC", "STAGE_ROLL"), ("SLIT-WC", "KITTING"), ("ASSY-WC", "ASSEMBLE") })
        {
            await plant.Ok(e, "/api/resources/work-centers/" + wc + "/activities", new { activityCode = activity });
        }

        var draft = await plant.Ok(e, "/api/safety-rules/drafts", new
        {
            code = "SLIT-AREA",
            title = "Two-person Slitting coverage",
            scopeKind = "SafetyArea",
            scopeCode = "SLITTING",
            activityCodes = new[] { "RUN_SLITTER" },
            minimumPhysicalPeople = 2,
            prohibitLoneWork = true,
            slots = new object[]
            {
                new { name = "Operator", count = 1, satisfiedBy = new[] { "SLIT-II" }, traineeMayFill = false },
                new { name = "Safety responder", count = 1, satisfiedBy = new[] { "FIRST-AID", "SLIT-II" }, traineeMayFill = false },
            },
            reason = "EHS procedure SP-12",
        });
        Assert.Equal("SafetyRule:SLIT-AREA@v1", draft.GetProperty("entityId").GetString());

        // The author cannot approve their own rule; a different approver can.
        var selfApproval = await plant.Try(e, "/api/safety-rules/SLIT-AREA/versions/1/publish", new { reason = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, selfApproval.StatusCode); // engineer lacks approval authority entirely
        await plant.Ok(plant.Approver, "/api/safety-rules/SLIT-AREA/versions/1/publish", new { reason = "Reviewed against SP-12" });

        await plant.Worker("sarah", "SLIT-WC", "SLIT-II", "FIRST-AID");
        await plant.Worker("michael", "SLIT-WC", "SLIT-II");
        await plant.Worker("jennifer", "PAINT-WC", "PAINT-I", "FIRST-AID");
        await plant.Worker("dana", "ASSY-WC");

        await plant.Assign("sarah", new { workCenterCode = "SLIT-WC", activityCode = "STAGE_ROLL", reason = "Shift start" });
        await plant.Assign("michael", new { machineCode = "SLITTER-B", activityCode = "RUN_SLITTER", reason = "Shift start" });
        await plant.Assign("jennifer", new { machineCode = "PAINT-BOOTH", activityCode = "PAINT", reason = "Shift start" });
        await plant.Assign("dana", new { workCenterCode = "ASSY-WC", activityCode = "ASSEMBLE", reason = "Shift start" });
        return plant;
    }

    /// <summary>The plant plus released engineering: COIL-AL (bought) and ROLL-100 (slit on A or B, then kitted).</summary>
    public static async Task<(Plant Plant, HttpClient Planner)> BuildEngineeredAsync(ForgeApp app)
    {
        var plant = await BuildAsync(app);
        var e = plant.Engineer;
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["approver"]}/roles", new { role = "EngineeringApprover", reason = "Release authority" });
        await plant.Person("planner", "Planner");

        await plant.Ok(e, "/api/engineering/items", new { partNumber = "COIL-AL", description = "Aluminum coil", unitOfMeasure = "KG", makeBuy = "Buy", leadTimeDays = 14 });
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "ROLL-100", description = "Slit roll 100 mm", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 2 });
        await plant.Ok(e, "/api/engineering/items/COIL-AL/revisions", new { revision = "A" });
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions", new { revision = "A" });
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "COIL-AL", quantityPer = 2.5m, scrapPercent = 4m });
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions/A/routing", new
        {
            sequence = 10,
            description = "Slit",
            workCenterCode = "SLIT-WC",
            activityCode = "RUN_SLITTER",
            eligibleMachines = new[] { "SLITTER-A", "SLITTER-B" },
            setupMinutes = 30m,
            runMinutesPerUnit = 1.5m,
            crewSize = 1,
        });
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions/A/routing", new
        {
            sequence = 20,
            description = "Kit",
            workCenterCode = "SLIT-WC",
            activityCode = "KITTING",
            requiredQualifications = new[] { "FIRST-AID" },
            setupMinutes = 0m,
            runMinutesPerUnit = 2m,
            crewSize = 1,
        });

        // ROLL-100 cannot be released before its component has a released revision.
        var blocked = await plant.Try(plant.Approver, "/api/engineering/items/ROLL-100/revisions/A/release", new { reason = "ECN-1" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Contains("COIL-AL", await blocked.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await plant.Ok(plant.Approver, "/api/engineering/items/COIL-AL/revisions/A/release", new { reason = "ECN-1" });
        await plant.Ok(plant.Approver, "/api/engineering/items/ROLL-100/revisions/A/release", new { reason = "ECN-1" });
        return (plant, plant.As("planner"));
    }
}
