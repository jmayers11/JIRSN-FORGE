using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// Phase 2 exit gate through the real API and database: the Slitting example, spec §78 (absence)
/// and §79 (reallocation preview), plus the separation-of-duties rules around safety and certification.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class Phase2Tests(PostgresFixture postgres)
{
    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task Healthy_plant_everything_active_is_executable()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var plant = await SlittingPlant.BuildAsync(app);

        var active = await Get(plant.Engineer, "/api/readiness/active-work");

        Assert.Equal(4, active.GetArrayLength());
        foreach (var run in active.EnumerateArray())
        {
            Assert.True(run.GetProperty("output").GetProperty("executable").GetBoolean(), run.GetProperty("evidence").GetProperty("detail").GetString());
            Assert.Equal(64, run.GetProperty("inputFingerprint").GetString()!.Length);
        }
    }

    [Fact]
    public async Task Spec_78_absence_stops_the_slitter_with_evidence()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var plant = await SlittingPlant.BuildAsync(app);
        var now = DateTimeOffset.UtcNow;

        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{plant.People["sarah"]}/unavailability",
            new { kind = "CallOut", startsAt = now.AddMinutes(-1), endsAt = now.AddHours(8), reason = "Called out" });

        var run = await Get(plant.Engineer, "/api/readiness/machines/SLITTER-B?activity=RUN_SLITTER");
        var output = run.GetProperty("output");
        Assert.False(output.GetProperty("executable").GetBoolean());
        Assert.Equal("Safety", output.GetProperty("primaryConstraint").GetString());
        Assert.Contains("SafetyRule:SLIT-AREA@v1", run.GetRawText(), StringComparison.Ordinal);

        // The call-out reason is not stored anywhere, and readiness does not expose the kind of absence.
        Assert.DoesNotContain("CallOut", run.GetRawText(), StringComparison.Ordinal);
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var columns = new NpgsqlCommand("SELECT count(*) FROM information_schema.columns WHERE table_schema = 'workforce' AND column_name LIKE '%reason%'", connection);
        Assert.Equal(0L, (long)(await columns.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Spec_79_reallocation_preview_shows_impact_and_options_without_committing()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var plant = await SlittingPlant.BuildAsync(app);
        var sarah = plant.People["sarah"];

        var preview = await plant.Ok(plant.Engineer, $"/api/readiness/reallocation-preview/{sarah}", new { workCenterCode = "ASSY-WC", activityCode = "ASSEMBLE" });
        var output = preview.GetProperty("output");

        var stops = output.GetProperty("impacts").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == "WouldStop").ToList();
        var stop = Assert.Single(stops);
        Assert.Equal("SLITTER-B", stop.GetProperty("work").GetProperty("machineCode").GetString());

        var options = output.GetProperty("options").EnumerateArray().Select(o => (Kind: o.GetProperty("kind").GetString(), Text: o.GetProperty("description").GetString()!)).ToList();
        Assert.Contains(options, o => o.Kind == "ReplacePerson" && o.Text.StartsWith("Move Jennifer", StringComparison.Ordinal));
        Assert.Contains(options, o => o.Kind == "SwitchToOtherActivity" && o.Text.Contains("Michael to KITTING", StringComparison.Ordinal));
        Assert.Contains(options, o => o.Kind == "ReviewManually");

        // Nothing changed: Slitter B still runs and Sarah is still staging.
        var run = await Get(plant.Engineer, "/api/readiness/machines/SLITTER-B?activity=RUN_SLITTER");
        Assert.True(run.GetProperty("output").GetProperty("executable").GetBoolean());

        // The supervisor decides; committing the move is the ordinary assignment command.
        await plant.Assign("sarah", new { workCenterCode = "ASSY-WC", activityCode = "ASSEMBLE", reason = "Assembly behind plan" });
        run = await Get(plant.Engineer, "/api/readiness/machines/SLITTER-B?activity=RUN_SLITTER");
        Assert.False(run.GetProperty("output").GetProperty("executable").GetBoolean());
    }

    [Fact]
    public async Task Reallocation_plan_preview_rechecks_cascading_moves_without_changing_assignments()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var plant = await SlittingPlant.BuildAsync(app);
        var sarah = plant.People["sarah"];
        var michael = plant.People["michael"];

        var response = await plant.Engineer.PostAsJsonAsync("/api/readiness/reallocation-plan-preview", new
        {
            moves = new object[]
            {
                new { personId = sarah, workCenterCode = "ASSY-WC", activityCode = "ASSEMBLE", asTrainee = false },
                new { personId = michael, unassign = true, asTrainee = false },
            },
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"reallocation plan preview -> {(int)response.StatusCode}: {text}");
        using var document = JsonDocument.Parse(text);
        var output = document.RootElement.GetProperty("output");
        var steps = output.GetProperty("steps");
        Assert.Equal(2, steps.GetArrayLength());
        Assert.Contains(steps[0].GetProperty("preview").GetProperty("impacts").EnumerateArray(), impact =>
            impact.GetProperty("kind").GetString() == "WouldStop" && impact.GetProperty("work").GetProperty("machineCode").GetString() == "SLITTER-B");
        Assert.Contains(steps[1].GetProperty("preview").GetProperty("before").EnumerateArray(), work =>
            work.GetProperty("work").GetProperty("machineCode").GetString() == "SLITTER-B" && !work.GetProperty("executable").GetBoolean());
        Assert.DoesNotContain(output.GetProperty("finalActiveWork").EnumerateArray(), work =>
            work.GetProperty("work").GetProperty("machineCode").GetString() == "SLITTER-B");

        // The analysis used successive in-memory snapshots; it did not change the live assignments or readiness.
        var live = await Get(plant.Engineer, "/api/readiness/machines/SLITTER-B?activity=RUN_SLITTER");
        Assert.True(live.GetProperty("output").GetProperty("executable").GetBoolean());
    }

    [Fact]
    public async Task Certification_and_safety_rules_enforce_separation_of_duties()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var plant = await SlittingPlant.BuildAsync(app);

        // A certifier cannot certify themselves.
        var approverId = plant.People["approver"];
        await plant.Ok(plant.Engineer, $"/api/competency/people/{approverId}/training", new { code = "PAINT-I" });
        var self = await plant.Try(plant.Approver, $"/api/competency/people/{approverId}/certifications", new { code = "PAINT-I", evidence = "x", reason = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
        Assert.Contains("separation_of_duties", await self.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // An approver who also drafts a rule still cannot approve their own draft.
        await plant.Ok(plant.Admin, $"/api/kernel/people/{approverId}/roles", new { role = "SafetyEngineer", reason = "Covering EHS" });
        await plant.Ok(plant.Approver, "/api/safety-rules/drafts", new
        {
            code = "PAINT-SOLO",
            title = "Paint booth",
            scopeKind = "Machine",
            scopeCode = "PAINT-BOOTH",
            minimumPhysicalPeople = 1,
            prohibitLoneWork = false,
            reason = "New booth",
        });
        var ownApproval = await plant.Try(plant.Approver, "/api/safety-rules/PAINT-SOLO/versions/1/publish", new { reason = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ownApproval.StatusCode);

        // A new version replaces the old one only when approved; history is kept.
        await plant.Ok(plant.Engineer, "/api/safety-rules/drafts", new
        {
            code = "SLIT-AREA",
            title = "Two-person Slitting coverage (rev 2)",
            scopeKind = "SafetyArea",
            scopeCode = "SLITTING",
            activityCodes = new[] { "RUN_SLITTER" },
            minimumPhysicalPeople = 2,
            prohibitLoneWork = true,
            slots = new object[] { new { name = "Operator", count = 2, satisfiedBy = new[] { "SLIT-II" }, traineeMayFill = false } },
            reason = "SP-12 rev B",
        });
        await plant.Ok(plant.Approver, "/api/safety-rules/SLIT-AREA/versions/2/publish", new { reason = "Reviewed SP-12 rev B" });

        var rules = await Get(plant.Engineer, "/api/safety-rules/");
        var statuses = rules.EnumerateArray().Where(r => r.GetProperty("code").GetString() == "SLIT-AREA").Select(r => r.GetProperty("status").GetString()).ToList();
        Assert.Equal(["Retired", "Published"], statuses);

        // Under v2 Slitter B needs two SLIT-II operators; Sarah (staging) counts, so it still runs, citing v2.
        var run = await Get(plant.Engineer, "/api/readiness/machines/SLITTER-B?activity=RUN_SLITTER");
        Assert.True(run.GetProperty("output").GetProperty("executable").GetBoolean());
        Assert.Contains("SLIT-AREA@v2", run.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_assignments_and_unauthorized_readiness_are_rejected()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var plant = await SlittingPlant.BuildAsync(app);

        var wrongActivity = await plant.Try(plant.Engineer, $"/api/workforce/workers/{plant.People["dana"]}/assignment", new { machineCode = "SLITTER-A", activityCode = "PAINT" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongActivity.StatusCode);

        var notAllowedHere = await plant.Try(plant.Engineer, $"/api/workforce/workers/{plant.People["dana"]}/assignment", new { workCenterCode = "PAINT-WC", activityCode = "KITTING" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notAllowedHere.StatusCode);

        // A worker with no role cannot see readiness.
        var denied = await app.As("dana").GetAsync("/api/readiness/active-work");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Specialized_assignment_requires_a_current_qualification_or_present_trainer_before_commit()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var plant = await SlittingPlant.BuildAsync(app);
        await plant.Ok(plant.Engineer, "/api/competency/qualification-types", new
        {
            code = "SLIT-TRAINER", name = "Authorized slitting trainer", kind = "Trainer", validityDays = 365,
        });
        await plant.Ok(plant.Engineer, "/api/competency/qualification-types/SLIT-TRAINER/prerequisites", new
        {
            prerequisiteCode = "SLIT-II", reason = "Trainer must hold current slitting qualification",
        });

        var trainee = await plant.Person("slitting-trainee");
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{trainee}", new { homeWorkCenterCode = "SLIT-WC" });
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{trainee}/presence", new { kind = "SupervisorConfirmed" });
        await plant.Ok(plant.Engineer, $"/api/competency/people/{trainee}/training", new { code = "SLIT-II" });

        var independent = await plant.Try(plant.Engineer, $"/api/workforce/workers/{trainee}/assignment", new
        {
            machineCode = "SLITTER-A", activityCode = "RUN_SLITTER", asTrainee = false, reason = "Reject unauthorized independent assignment",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, independent.StatusCode);
        Assert.Contains("qualification_required", await independent.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var noTrainer = await plant.Try(plant.Engineer, $"/api/workforce/workers/{trainee}/assignment", new
        {
            machineCode = "SLITTER-A", activityCode = "RUN_SLITTER", asTrainee = true, reason = "Require trainer before supervised assignment",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noTrainer.StatusCode);
        Assert.Contains("qualified_trainer_required", await noTrainer.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await plant.Ok(plant.Engineer, $"/api/competency/people/{plant.People["sarah"]}/training", new { code = "SLIT-TRAINER" });
        await plant.Ok(plant.Approver, $"/api/competency/people/{plant.People["sarah"]}/certifications", new
        {
            code = "SLIT-TRAINER", evidence = "Fictional trainer qualification assessment", reason = "Trainer approval recorded",
        });
        await plant.Assign("sarah", new { machineCode = "SLITTER-A", activityCode = "RUN_SLITTER", reason = "Place the qualified trainer at the work area" });
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{trainee}/assignment", new
        {
            machineCode = "SLITTER-A", activityCode = "RUN_SLITTER", asTrainee = true, reason = "Assign under current on-site trainer coverage",
        });

        var workers = await plant.Engineer.GetFromJsonAsync<JsonElement[]>("/api/workforce/workers");
        Assert.Equal("SLITTER-A", workers!.Single(worker => worker.GetProperty("personId").GetGuid() == trainee)
            .GetProperty("assignment").GetProperty("machineCode").GetString());
    }

    [Fact]
    public async Task Cnc_setup_assignment_requires_toolpath_and_one_current_mill_or_lathe_authorization()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var plant = await SlittingPlant.BuildAsync(app);
        foreach (var (code, name) in new[] { ("CNC_MILL", "CNC mill"), ("CNC_LATHE", "CNC lathe"), ("CNC_SETUP", "CNC setup") })
            await plant.Ok(plant.Engineer, "/api/resources/activity-types", new { code, name });
        await plant.Ok(plant.Engineer, "/api/resources/safety-areas", new { code = "CNC", name = "CNC", parent = "PLANT1" });
        await plant.Ok(plant.Engineer, "/api/resources/work-centers", new { code = "CNC-WC", name = "CNC", parent = "CNC" });
        foreach (var machine in new[] { ("CNC-01", "CNC_MILL", "QUAL-CNC-MILL"), ("CNC-02", "CNC_SETUP", "QUAL-CNC-SETUP"), ("CNC-03", "CNC_LATHE", "QUAL-CNC-LATHE") })
        {
            await plant.Ok(plant.Engineer, "/api/resources/machines", new { code = machine.Item1, name = machine.Item1, parent = "CNC-WC" });
        }
        foreach (var (code, kind) in new[]
        {
            ("QUAL-SITE-SAFE", "Safety"), ("QUAL-CNC-MILL", "Machine"), ("QUAL-CNC-LATHE", "Machine"),
            ("QUAL-CNC-TOOLPATH", "SpecialProcess"), ("QUAL-CNC-SETUP", "SpecialProcess"),
        })
            await plant.Ok(plant.Engineer, "/api/competency/qualification-types", new { code, name = code, kind, validityDays = 365 });
        foreach (var machine in new[] { ("CNC-01", "CNC_MILL", "QUAL-CNC-MILL"), ("CNC-02", "CNC_SETUP", "QUAL-CNC-SETUP"), ("CNC-03", "CNC_LATHE", "QUAL-CNC-LATHE") })
            await plant.Ok(plant.Engineer, $"/api/resources/machines/{machine.Item1}/activities", new { activityCode = machine.Item2, operatorQualifications = new[] { machine.Item3 } });
        await plant.Ok(plant.Engineer, "/api/competency/qualification-types/QUAL-CNC-MILL/prerequisites", new { prerequisiteCode = "QUAL-SITE-SAFE" });
        await plant.Ok(plant.Engineer, "/api/competency/qualification-types/QUAL-CNC-LATHE/prerequisites", new { prerequisiteCode = "QUAL-SITE-SAFE" });
        await plant.Ok(plant.Engineer, "/api/competency/qualification-types/QUAL-CNC-TOOLPATH/prerequisites", new { prerequisiteCode = "QUAL-SITE-SAFE" });
        await plant.Ok(plant.Engineer, "/api/competency/qualification-types/QUAL-CNC-SETUP/prerequisites", new { prerequisiteCode = "QUAL-CNC-TOOLPATH" });
        await plant.Ok(plant.Engineer, "/api/competency/qualification-types/QUAL-CNC-SETUP/prerequisite-alternatives", new
        {
            groupCode = "MILL-OR-LATHE", name = "Mill or lathe authorization", prerequisiteCodes = new[] { "QUAL-CNC-MILL", "QUAL-CNC-LATHE" },
        });

        var operatorId = await plant.Person("cnc-mill-operator");
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{operatorId}", new { homeWorkCenterCode = "CNC-WC" });
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{operatorId}/presence", new { kind = "SupervisorConfirmed" });
        foreach (var code in new[] { "QUAL-SITE-SAFE", "QUAL-CNC-MILL" })
        {
            await plant.Ok(plant.Engineer, $"/api/competency/people/{operatorId}/training", new { code });
            await plant.Ok(plant.Approver, $"/api/competency/people/{operatorId}/certifications", new { code, evidence = "Fictional setup qualification assessment", reason = "Approval recorded" });
        }

        var wrongMachine = await plant.Try(plant.Engineer, $"/api/workforce/workers/{operatorId}/assignment", new
        {
            machineCode = "CNC-03", activityCode = "CNC_LATHE", reason = "Mill qualification does not authorize lathe operation",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongMachine.StatusCode);
        Assert.Contains("qualification_required", await wrongMachine.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var missingSetup = await plant.Try(plant.Engineer, $"/api/workforce/workers/{operatorId}/assignment", new
        {
            machineCode = "CNC-02", activityCode = "CNC_SETUP", reason = "Toolpath and setup authorization are not recorded",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingSetup.StatusCode);
        Assert.Contains("qualification_required", await missingSetup.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        foreach (var code in new[] { "QUAL-CNC-TOOLPATH", "QUAL-CNC-SETUP" })
        {
            await plant.Ok(plant.Engineer, $"/api/competency/people/{operatorId}/training", new { code });
            await plant.Ok(plant.Approver, $"/api/competency/people/{operatorId}/certifications", new { code, evidence = "Fictional setup qualification assessment", reason = "Approval recorded" });
        }
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{operatorId}/assignment", new
        {
            machineCode = "CNC-02", activityCode = "CNC_SETUP", reason = "Mill authorization plus toolpath verification qualifies this setup lead",
        });
    }
}
