using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>V4.3 foundation acceptance through ordinary commands. No fabricated QMS/document/audit source rows.</summary>
[Collection(PostgresCollection.Name)]
public sealed class QmsGovernanceTests(PostgresFixture postgres)
{
    private const string Api = "/api/quality/qms";
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance() => now += TimeSpan.FromMinutes(1);
    }

    private static async Task<SlittingPlant.Plant> SetupAsync(ForgeApp app)
    {
        var plant = new SlittingPlant.Plant(app);
        async Task Person(string subject, params string[] roles)
        {
            var id = Guid.Parse((await plant.Ok(plant.Admin, "/api/kernel/people", new { idpSubject = subject, displayName = subject,
                markings = new[] { "Proprietary" }, reason = "Explicit example QMS source handling authority" })).GetProperty("entityId").GetString()!);
            plant.People[subject] = id;
            foreach (var role in roles) await plant.Ok(plant.Admin, $"/api/kernel/people/{id}/roles", new { role, reason = "Independent example QMS role assignment" });
        }
        await Person("qms-author", Roles.QmsAuthor, Roles.QmsReviewer, Roles.QmsApprover, Roles.ManufacturingEngineer, Roles.ProgramManager);
        await Person("qms-reviewer", Roles.QmsReviewer, Roles.QmsApprover, Roles.DocumentController);
        await plant.Ok(plant.As("qms-author"), "/api/resources/sites", new { code = "PLANT1", name = "Governed facility", timeZone = TestSite.TimeZone });
        return plant;
    }
    private static QmsContent Process(Guid owner, string name = "Production", params QmsDocumentReference[] documents) => new(Process:
        new(name, ["Released product requirements"], ["Inspected product and recorded evidence"], owner, [],
            ["Qualified people and maintained equipment"], ["Supply interruption reviewed by the process owner"],
            [new("Acceptance", "Compare actual recorded inspection results", "Quality inspection records", "Monthly and on change")]), Documents: documents);
    private static object Create(string code, string kind, QmsContent content, Guid? program = null, string facility = "PLANT1") =>
        new { code, kind, facilityCode = facility, programId = program, markings = "None", content, reason = "Explicit human organization definition" };
    private static object Decision(int version, string note = "Reviewed the actual scope, sources and human applicability basis") =>
        new { expectedVersion = version, note, reason = "Recorded independent governance decision" };
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
    private static Task<JsonElement> RevisionAsync(HttpClient reader, string code, int revision) =>
        GetAsync(reader, $"{Api}/records/{code}/revisions/{revision}");
    private static async Task<JsonElement> GetAsync(HttpClient client, string url) => await Json(await client.GetAsync(url));
    private static async Task RejectedAsync(SlittingPlant.Plant plant, HttpClient actor, string path, object body, string code, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        using var response = await plant.Try(actor, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        Assert.Contains(code, text, StringComparison.Ordinal);
    }
    private static async Task EffectiveAsync(SlittingPlant.Plant plant, string code, int revision = 1)
    {
        var path = $"{Api}/records/{code}/revisions/{revision}";
        await plant.Ok(plant.As("qms-author"), path + "/submit", Decision(1));
        await plant.Ok(plant.As("qms-reviewer"), path + "/review", Decision(2));
        // The same independent reviewer may approve: no invented universal three-person rule.
        await plant.Ok(plant.As("qms-reviewer"), path + "/approve", Decision(3));
        await plant.Ok(plant.As("qms-reviewer"), path + "/effective", Decision(4));
    }
    private static async Task DocumentAsync(SlittingPlant.Plant plant, string number, Guid? program = null, bool release = true)
    {
        var author = plant.As("qms-author");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files")
        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("Human organization context and applicability assessment; no licensed normative text or conformity claim.")) };
        upload.Headers.Add("Idempotency-Key", "qms-file-" + number);
        upload.Headers.Add("X-File-Name", "QMS-assessment.txt");
        var sha = (await Json(await author.SendAsync(upload))).GetProperty("entityId").GetString();
        await plant.Ok(author, "/api/engineering/documents", new { number, title = "Organization context assessment", type = "Procedure", authority = "Internal", programId = program });
        await plant.Ok(author, $"/api/engineering/documents/{number}/revisions", new { revision = "A", fileSha256 = sha, fileName = "QMS-assessment.txt", changeNote = "Recorded local applicability basis" });
        if (!release) return;
        await plant.Ok(author, $"/api/engineering/documents/{number}/revisions/A/submit", new { });
        await plant.Ok(plant.As("qms-reviewer"), $"/api/engineering/documents/{number}/revisions/A/approve", new { });
        await plant.Ok(plant.As("qms-reviewer"), $"/api/engineering/documents/{number}/revisions/A/release", new { reason = "Independent document release" });
    }

    private static async Task<string> SnapshotAsync(ForgeApp app, string table, string where = "true")
    {
        // Names/where expressions below are fixed test-owned constants; this only reads actual canonical stored rows.
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(qms_snapshot_row) ORDER BY to_jsonb(qms_snapshot_row)::text)::text,'[]') FROM {table} qms_snapshot_row WHERE {where}", connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }
    private static async Task<string[]> StateAsync(ForgeApp app)
    {
        string[] tables = ["quality.qms_record", "quality.qms_revision", "quality.qms_revision_source", "quality.qms_transition", "kernel.audit_log", "kernel.outbox", "kernel.event_log", "kernel.idempotency"];
        var state = new List<string>();
        foreach (var table in tables) state.Add(await SnapshotAsync(app, table));
        return state.ToArray();
    }

    [Fact]
    public async Task Real_profile_process_requirement_lifecycle_preserves_historical_versions_and_pending_licensed_verification()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock),
            builder => builder.UseSetting(QmsService.ConfigurationKey, "EXAMPLE-ORG"));
        var plant = await SetupAsync(app);
        var author = plant.As("qms-author");
        var reviewer = plant.As("qms-reviewer");
        var owner = plant.People["qms-author"];
        var processContent = Process(owner);
        var created = await plant.Ok(author, Api + "/records", Create("PRODUCTION", "Process", processContent));
        Assert.Equal("PRODUCTION/1", created.GetProperty("entityId").GetString());
        var processPath = Api + "/records/PRODUCTION/revisions/1";
        await RejectedAsync(plant, author, processPath + "/edit", new { expectedVersion = 0, content = processContent, reason = "Stale view" }, "stale_version");
        await RejectedAsync(plant, reviewer, processPath + "/review", Decision(1), "invalid_status");
        await plant.Ok(author, processPath + "/submit", Decision(1));
        await RejectedAsync(plant, author, processPath + "/review", Decision(2), "separation_of_duties");
        await plant.Ok(reviewer, processPath + "/review", Decision(2));
        await RejectedAsync(plant, author, processPath + "/approve", Decision(3), "separation_of_duties");
        await plant.Ok(reviewer, processPath + "/approve", Decision(3));
        var frozenContent = (await RevisionAsync(reviewer, "PRODUCTION", 1)).GetProperty("content").GetRawText();
        await RejectedAsync(plant, author, processPath + "/edit", new { expectedVersion = 4, content = Process(owner, "Unauthorized replacement"), reason = "Try changing approved history" }, "immutable_revision");
        await RejectedAsync(plant, author, Api + "/records/PRODUCTION/revisions", new { expectedVersion = 4, content = processContent, reason = "Approved is still open" }, "open_revision");
        await plant.Ok(reviewer, processPath + "/effective", Decision(4));
        var firstEffective = clock.GetUtcNow();
        var process1 = await RevisionAsync(reviewer, "PRODUCTION", 1);
        Assert.Equal(("Effective", 5, "person:" + owner, "person:" + plant.People["qms-reviewer"]),
            (process1.GetProperty("state").GetString(), process1.GetProperty("version").GetInt32(), process1.GetProperty("createdBy").GetString(), process1.GetProperty("approvedBy").GetString()));
        Assert.Equal(process1.GetProperty("reviewedBy").GetString(), process1.GetProperty("approvedBy").GetString());

        // This real controlled file is a local assessment. It is expressly not fabricated licensed ISO text.
        await DocumentAsync(plant, "QMS.BASIS");
        var requirement = new QmsContent(Requirement: new("Organization context", QmsRequirementClass.ISO,
            "Maintain the local context determination; verify the licensed normative requirement before a conformity claim.",
            QmsApplicability.Applicable, "Leadership defined local applicability from the actual organization scope", owner,
            [new("ISO 9001", "2015", "4.1", QmsStandardVerification.PendingAuthorizedVerification, "Authorized licensed text is not available for normative verification", null)]),
            Documents: [new("QMS.BASIS", "A")]);
        await plant.Ok(author, Api + "/records", Create("CONTEXT-REQ", "Requirement", requirement));
        await EffectiveAsync(plant, "CONTEXT-REQ");
        var req1 = await RevisionAsync(reviewer, "CONTEXT-REQ", 1);
        Assert.True(req1.GetProperty("requiresLicensedVerification").GetBoolean());
        Assert.Equal("PendingAuthorizedVerification", Assert.Single(req1.GetProperty("content").GetProperty("requirement").GetProperty("standardReferences").EnumerateArray()).GetProperty("verification").GetString());
        var actualSource = Assert.Single(req1.GetProperty("sources").EnumerateArray());
        Assert.Equal(("DocumentRevision", "QMS.BASIS", "A"), (actualSource.GetProperty("kind").GetString(), actualSource.GetProperty("key").GetString(), actualSource.GetProperty("revision").GetString()));
        var suppliedDesign = new QmsContent(Requirement: new("Product design responsibility", QmsRequirementClass.Organization,
            "Determine whether the local organization supplies product design services", QmsApplicability.NotApplicable,
            "Leadership defined this example facility's scope as manufacture to customer drawings with no product design service", owner, []),
            Documents: [new("QMS.BASIS", "A")]);
        await plant.Ok(author, Api + "/records", Create("SUPPLIED-DESIGN", "Requirement", suppliedDesign));
        await EffectiveAsync(plant, "SUPPLIED-DESIGN");
        var profile = new QmsContent(Profile: new("Example organization", "Production of accepted assemblies at PLANT1",
            ["Assemblies"], [], [new("External", "Supplier continuity", "Reviewed actual supplier performance")],
            [new("Customer", "Accepted products and delivery", "Actual contract obligations")], ["Actual delivery and acceptance requirements"],
            ["Leadership's recorded applicable safety obligations"], false, "Leadership assessed the facility context and recorded its basis",
            [new("SUPPLIED-DESIGN", 1, "Human scope decision cites the exact independently approved responsibility determination")],
            [new("PRODUCTION", 1)], [new("CONTEXT-REQ", 1), new("SUPPLIED-DESIGN", 1)]));
        await RejectedAsync(plant, author, Api + "/records", Create("CONFLICTING-SCOPE", "Profile", profile with { Profile = profile.Profile! with {
            NonApplicability = [new("CONTEXT-REQ", 1, "Conflicting attempted exclusion")] } }), "non_applicability_conflict");
        await plant.Ok(author, Api + "/records", Create("ORG-SCOPE", "Profile", profile));
        await EffectiveAsync(plant, "ORG-SCOPE");
        var scope = await RevisionAsync(reviewer, "ORG-SCOPE", 1);
        Assert.Equal("EXAMPLE-ORG", (await GetAsync(reviewer, Api + "/records/ORG-SCOPE")).GetProperty("record").GetProperty("organizationCode").GetString());
        Assert.Equal(new[] { "CONTEXT-REQ", "PRODUCTION", "SUPPLIED-DESIGN" }, scope.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("key").GetString()));
        Assert.Equal(("SUPPLIED-DESIGN", 1), (Assert.Single(scope.GetProperty("content").GetProperty("profile").GetProperty("nonApplicability").EnumerateArray()).GetProperty("requirementCode").GetString(),
            Assert.Single(scope.GetProperty("content").GetProperty("profile").GetProperty("nonApplicability").EnumerateArray()).GetProperty("revision").GetInt32()));

        clock.Advance();
        await plant.Ok(author, Api + "/records/PRODUCTION/revisions", new { expectedVersion = 5, content = Process(owner, "Production with revised monitoring responsibility"), reason = "Planned organization change with resource review" });
        await EffectiveAsync(plant, "PRODUCTION", 2);
        var process2 = await RevisionAsync(reviewer, "PRODUCTION", 2);
        process1 = await RevisionAsync(reviewer, "PRODUCTION", 1);
        Assert.Equal("Superseded", process1.GetProperty("state").GetString());
        Assert.Equal(frozenContent, process1.GetProperty("content").GetRawText());
        Assert.Equal(process1.GetProperty("effectiveTo").GetDateTimeOffset(), process2.GetProperty("effectiveFrom").GetDateTimeOffset());
        var historic = await GetAsync(reviewer, Api + "/records/PRODUCTION/effective?at=" + Uri.EscapeDataString(firstEffective.ToString("O", CultureInfo.InvariantCulture)));
        Assert.Equal(1, historic.GetProperty("revision").GetInt32());
        Assert.Equal(2, (await GetAsync(reviewer, Api + "/records/PRODUCTION/effective")).GetProperty("revision").GetInt32());
        Assert.Equal(new[] { "Create", "Submit", "Review", "Approve", "Effective", "Supersede" }, process1.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("action").GetString()));

        // Later obsolescence does not rewrite an effective historical requirement or hide its exact document version.
        var originalRequirementBytes = await SnapshotAsync(app, "quality.qms_revision", "code='CONTEXT-REQ' AND revision=1");
        var originalSourceBytes = await SnapshotAsync(app, "quality.qms_revision_source", "code='CONTEXT-REQ' AND revision=1");
        await plant.Ok(reviewer, "/api/engineering/documents/QMS.BASIS/revisions/A/obsolete", new { reason = "Assessment document superseded by a future controlled change" });
        Assert.Equal(originalRequirementBytes, await SnapshotAsync(app, "quality.qms_revision", "code='CONTEXT-REQ' AND revision=1"));
        Assert.Equal(originalSourceBytes, await SnapshotAsync(app, "quality.qms_revision_source", "code='CONTEXT-REQ' AND revision=1"));
        Assert.True((await RevisionAsync(reviewer, "CONTEXT-REQ", 1)).GetProperty("requiresLicensedVerification").GetBoolean());
        await plant.Ok(author, Api + "/records/CONTEXT-REQ/revisions", new { expectedVersion = 5, content = requirement, reason = "Prepare new review while retaining history" });
        await RejectedAsync(plant, author, Api + "/records/CONTEXT-REQ/revisions/2/submit", Decision(1), "document_not_effective");
        await plant.Ok(reviewer, Api + "/records/CONTEXT-REQ/revisions/2/withdraw", Decision(1, "Withdraw draft until its controlled source is current"));

        clock.Advance();
        var beforeWithdrawal = clock.GetUtcNow().AddSeconds(-1);
        var withdrawPath = Api + "/records/PRODUCTION/revisions/2/withdraw";
        var withdrawBody = Decision(5, "End current applicability while retaining the historical production definition");
        var withdrawn = await Json(await reviewer.CommandAsync(withdrawPath, withdrawBody, "qms-withdraw-exact"));
        var replayBefore = await StateAsync(app);
        Assert.Equal(withdrawn.GetRawText(), (await Json(await reviewer.CommandAsync(withdrawPath, withdrawBody, "qms-withdraw-exact"))).GetRawText());
        Assert.Equal(replayBefore, await StateAsync(app));
        Assert.Equal(HttpStatusCode.NotFound, (await reviewer.GetAsync(Api + "/records/PRODUCTION/effective")).StatusCode);
        Assert.Equal(2, (await GetAsync(reviewer, Api + "/records/PRODUCTION/effective?at=" + Uri.EscapeDataString(beforeWithdrawal.ToString("O", CultureInfo.InvariantCulture)))).GetProperty("revision").GetInt32());
        var history = (await RevisionAsync(reviewer, "PRODUCTION", 2)).GetProperty("history").EnumerateArray().ToArray();
        Assert.Equal("Withdraw", history[^1].GetProperty("action").GetString());
        Assert.Equal("person:" + plant.People["qms-reviewer"], history[^1].GetProperty("actor").GetString());
        Assert.Equal("Recorded independent governance decision", history[^1].GetProperty("reason").GetString());
        var audit = await SnapshotAsync(app, "kernel.audit_log", "entity_type IN ('QmsRecord','QmsRevision')");
        Assert.Contains("separation_of_duties", audit, StringComparison.Ordinal);
        Assert.Contains("Try changing approved history", audit, StringComparison.Ordinal);
        Assert.Contains("Withdraw", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Actual_program_facility_and_linked_source_access_is_rechecked_before_replay_without_rewriting_canonical_rows()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: builder => builder.UseSetting(QmsService.ConfigurationKey, "EXAMPLE-ORG"));
        var plant = await SetupAsync(app);
        var author = plant.As("qms-author");
        var reviewer = plant.As("qms-reviewer");
        var programs = new List<Guid>();
        foreach (var suffix in new[] { "A", "B" }) programs.Add(Guid.Parse((await plant.Ok(author, "/api/programs/", new { code = "QMS-" + suffix, name = "Actual QMS program " + suffix })).GetProperty("entityId").GetString()!));
        await plant.Ok(author, "/api/resources/sites", new { code = "PLANT2", name = "Another governed facility", timeZone = TestSite.TimeZone });
        foreach (var person in new[] { plant.People["qms-author"], plant.People["qms-reviewer"] })
        foreach (var program in programs) await plant.Ok(plant.Admin, $"/api/kernel/people/{person}/programs", new { programId = program, reason = "Independent source membership" });
        const string role = "ScopedQmsGovernance";
        await plant.Ok(plant.Admin, "/api/kernel/roles", new { role, displayName = "Governance limited to PLANT1", permissions = new[] { Permissions.QmsRead, Permissions.QmsReview, Permissions.QmsApprove, Permissions.QmsPublish, Permissions.EngineeringRead }, reason = "Configure actual governed authority" });
        foreach (var permission in new[] { Permissions.QmsRead, Permissions.QmsReview, Permissions.QmsApprove, Permissions.QmsPublish })
            await plant.Ok(plant.Admin, $"/api/kernel/roles/{role}/permission-scopes", new { permission, scopeType = "facility", scopeId = "PLANT1", reason = "Actual facility scope" });
        var scopedId = await plant.Person("qms-scoped", role);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{scopedId}/programs", new { programId = programs[0], reason = "Only actual program A" });
        var scoped = plant.As("qms-scoped");
        Assert.True((await GetAsync(scoped, Api + "/status")).GetProperty("configured").GetBoolean());
        await DocumentAsync(plant, "QMS.A", programs[0]);
        await DocumentAsync(plant, "QMS.B", programs[1]);
        var sourceContent = Process(plant.People["qms-author"], "Program A production", new QmsDocumentReference("QMS.A", "A"));
        await plant.Ok(author, Api + "/records", Create("SCOPED-A", "Process", sourceContent, programs[0]));
        await plant.Ok(author, Api + "/records", Create("SCOPED-B", "Process", Process(plant.People["qms-author"]), programs[1]));
        await plant.Ok(author, Api + "/records", Create("OTHER-FACILITY", "Process", Process(plant.People["qms-author"]), programs[0], "PLANT2"));
        await plant.Ok(author, Api + "/records", Create("HIDDEN-SOURCE", "Process", Process(plant.People["qms-author"], "Copied B source", new QmsDocumentReference("QMS.B", "A")), programs[0]));
        Assert.Equal(new[] { "SCOPED-A" }, (await GetAsync(scoped, Api + "/records")).EnumerateArray().Select(r => r.GetProperty("code").GetString()));
        foreach (var hidden in new[] { "SCOPED-B", "OTHER-FACILITY", "HIDDEN-SOURCE", "UNKNOWN" })
        {
            using var missing = await scoped.GetAsync(Api + "/records/" + hidden + "/revisions/1");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var missingBody = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
            Assert.Equal("qms_not_found", missingBody.RootElement.GetProperty("code").GetString());
        }
        await plant.Ok(author, Api + "/records/SCOPED-A/revisions/1/submit", Decision(1));
        var reviewPath = Api + "/records/SCOPED-A/revisions/1/review";
        var reviewBody = Decision(2);
        var result = await Json(await scoped.CommandAsync(reviewPath, reviewBody, "qms-source-retry"));
        var noWriteBefore = await StateAsync(app);
        await Json(await scoped.CommandAsync(reviewPath, reviewBody, "qms-source-retry"));
        Assert.Equal(noWriteBefore, await StateAsync(app));
        var qmsBytes = await SnapshotAsync(app, "quality.qms_revision");
        var sourcesBytes = await SnapshotAsync(app, "quality.qms_revision_source");
        var originalSuccessAudit = await SnapshotAsync(app, "kernel.audit_log", "entity_type='QmsRevision' AND outcome='Succeeded'");
        await plant.Ok(plant.Admin, $"/api/kernel/people/{scopedId}/programs/{programs[0]}/revoke", new { reason = "Withdraw current source membership" });
        using (var deniedReplay = await scoped.CommandAsync(reviewPath, reviewBody, "qms-source-retry")) Assert.Equal(HttpStatusCode.Forbidden, deniedReplay.StatusCode);
        Assert.Equal(qmsBytes, await SnapshotAsync(app, "quality.qms_revision"));
        Assert.Equal(sourcesBytes, await SnapshotAsync(app, "quality.qms_revision_source"));
        Assert.Equal(originalSuccessAudit, await SnapshotAsync(app, "kernel.audit_log", "entity_type='QmsRevision' AND outcome='Succeeded'"));
        using var scopeDenials = JsonDocument.Parse(await SnapshotAsync(app, "kernel.audit_log", "entity_type='QmsRecord' AND entity_id='SCOPED-A' AND outcome='Denied'"));
        var denial = Assert.Single(scopeDenials.RootElement.EnumerateArray());
        Assert.Equal((programs[0], 0, "person:" + scopedId, "QmsReview", "Recorded independent governance decision"),
            (denial.GetProperty("program_id").GetGuid(), denial.GetProperty("markings").GetInt32(), denial.GetProperty("actor").GetString(), denial.GetProperty("action").GetString(), denial.GetProperty("reason").GetString()));
        Assert.Contains("No access to the program", denial.GetProperty("new_value").GetString()!, StringComparison.Ordinal);
        Assert.NotNull(denial.GetProperty("hash").GetString());
        Assert.NotNull(denial.GetProperty("previous_hash").GetString());
        await plant.Ok(plant.Admin, $"/api/kernel/people/{scopedId}/programs", new { programId = programs[0], reason = "Restore independently approved current access" });
        Assert.Equal(result.GetRawText(), (await Json(await scoped.CommandAsync(reviewPath, reviewBody, "qms-source-retry"))).GetRawText());
        // The recorded source remains revision A with its original markings; current source classification is rechecked separately.
        await plant.Ok(author, "/api/engineering/documents/QMS.A", new { title = "Organization context assessment", markings = "Proprietary", reason = "Current source handling becomes protected" });
        using (var deniedCurrentSource = await scoped.CommandAsync(reviewPath, reviewBody, "qms-source-retry"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedCurrentSource.StatusCode);
            Assert.Contains("Missing data markings", await deniedCurrentSource.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync(Api + "/records/SCOPED-A/revisions/1")).StatusCode);
        Assert.Equal(qmsBytes, await SnapshotAsync(app, "quality.qms_revision"));
        Assert.Equal(sourcesBytes, await SnapshotAsync(app, "quality.qms_revision_source"));
        Assert.Equal(originalSuccessAudit, await SnapshotAsync(app, "kernel.audit_log", "entity_type='QmsRevision' AND outcome='Succeeded'"));
        await plant.Ok(author, "/api/engineering/documents/QMS.A", new { title = "Organization context assessment", markings = "None", reason = "Current source handling returns to its authorized scope" });
        Assert.Equal(result.GetRawText(), (await Json(await scoped.CommandAsync(reviewPath, reviewBody, "qms-source-retry"))).GetRawText());
        await plant.Ok(plant.Admin, $"/api/kernel/people/{scopedId}/roles/{role}/revoke", new { reason = "Withdraw role authority after previous success" });
        using (var deniedRoleReplay = await scoped.CommandAsync(reviewPath, reviewBody, "qms-source-retry")) Assert.Equal(HttpStatusCode.Forbidden, deniedRoleReplay.StatusCode);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{scopedId}/roles", new { role, reason = "Restore governed role" });
        await plant.Ok(scoped, Api + "/records/SCOPED-A/revisions/1/approve", Decision(3));
        await plant.Ok(scoped, Api + "/records/SCOPED-A/revisions/1/effective", Decision(4));

        // Later protected source revisions cannot taint the earlier exact revision's original scopes or stored bytes.
        await plant.Ok(author, Api + "/records/SCOPED-A/revisions", new { expectedVersion = 5, content = Process(plant.People["qms-author"], "New private source context", new QmsDocumentReference("QMS.B", "A")), reason = "Prepare a later protected revision" });
        Assert.Equal(1, (await RevisionAsync(scoped, "SCOPED-A", 1)).GetProperty("revision").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync(Api + "/records/SCOPED-A/revisions/2")).StatusCode);
        var profileContent = new QmsContent(Profile: new("Example organization", "Program A production scope", ["Accepted products"], [], [], [], [], [], false,
            "Leadership assessed the actual site context", [], [new("SCOPED-A", 1)], []));
        // A separate normal author with only A membership adopts exact revision 1, not later protected revision 2.
        var adopterId = await plant.Person("qms-adopter", Roles.QmsAuthor);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{adopterId}/programs", new { programId = programs[0], reason = "Adopt only authorized program A sources" });
        var adopter = plant.As("qms-adopter");
        await plant.Ok(adopter, Api + "/records", Create("EXACT-A-PROFILE", "Profile", profileContent, programs[0]));
        await plant.Ok(adopter, Api + "/records/EXACT-A-PROFILE/revisions/1/submit", Decision(1));
        var exact = await RevisionAsync(scoped, "EXACT-A-PROFILE", 1);
        Assert.Equal(("SCOPED-A", "1", programs[0]), (Assert.Single(exact.GetProperty("sources").EnumerateArray()).GetProperty("key").GetString(),
            Assert.Single(exact.GetProperty("sources").EnumerateArray()).GetProperty("revision").GetString(), Assert.Single(exact.GetProperty("sources").EnumerateArray()).GetProperty("programId").GetGuid()));
        var denials = await SnapshotAsync(app, "kernel.audit_log", "entity_type='QmsRecord' AND outcome='Denied'");
        Assert.Contains("Recorded independent governance decision", denials, StringComparison.Ordinal);
        Assert.Contains("No access to the program", denials, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid organization")]
    public async Task Organization_configuration_is_explicit_and_has_no_fabricated_default(string configuration)
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: builder => builder.UseSetting(QmsService.ConfigurationKey, configuration));
        var plant = await SetupAsync(app);
        var author = plant.As("qms-author");
        var status = await GetAsync(author, Api + "/status");
        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("organizationCode").ValueKind);
        await RejectedAsync(plant, author, Api + "/records", Create("NO-ORG", "Process", Process(plant.People["qms-author"])), "qms_not_configured", HttpStatusCode.ServiceUnavailable);
        Assert.Equal("[]", await SnapshotAsync(app, "quality.qms_record"));
        Assert.Equal("[]", await SnapshotAsync(app, "quality.qms_revision"));
    }

    [Fact]
    public async Task Every_recorded_content_contributor_is_excluded_from_own_review_and_approval_after_later_edits()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: builder => builder.UseSetting(QmsService.ConfigurationKey, "EXAMPLE-ORG"));
        var plant = await SetupAsync(app);
        await plant.Person("qms-earlier-author", Roles.QmsAuthor, Roles.QmsReviewer, Roles.QmsApprover);
        await plant.Person("qms-latest-author", Roles.QmsAuthor);
        var owner = plant.People["qms-author"];
        await plant.Ok(plant.As("qms-author"), Api + "/records", Create("CONTRIBUTORS", "Process", Process(owner)));
        var path = Api + "/records/CONTRIBUTORS/revisions/1";
        await plant.Ok(plant.As("qms-earlier-author"), path + "/edit", new { expectedVersion = 1, content = Process(owner, "Earlier author's process definition"), reason = "Record actual authored contribution" });
        await plant.Ok(plant.As("qms-latest-author"), path + "/edit", new { expectedVersion = 2, content = Process(owner, "Later authored monitoring change"), reason = "A later editor does not erase earlier authorship" });
        await plant.Ok(plant.As("qms-author"), path + "/submit", Decision(3));
        var revision = await RevisionAsync(plant.As("qms-reviewer"), "CONTRIBUTORS", 1);
        Assert.NotEqual("person:" + plant.People["qms-earlier-author"], revision.GetProperty("createdBy").GetString());
        Assert.NotEqual("person:" + plant.People["qms-earlier-author"], revision.GetProperty("lastEditedBy").GetString());
        await RejectedAsync(plant, plant.As("qms-earlier-author"), path + "/review", Decision(4), "separation_of_duties");
        await plant.Ok(plant.As("qms-reviewer"), path + "/review", Decision(4));
        await RejectedAsync(plant, plant.As("qms-earlier-author"), path + "/approve", Decision(5), "separation_of_duties");
        await plant.Ok(plant.As("qms-reviewer"), path + "/approve", Decision(5));
        var approved = await RevisionAsync(plant.As("qms-reviewer"), "CONTRIBUTORS", 1);
        Assert.Equal("Approved", approved.GetProperty("state").GetString());
        Assert.Equal(6, approved.GetProperty("version").GetInt32());
        Assert.Equal(new[] { "person:" + owner, "person:" + plant.People["qms-earlier-author"], "person:" + plant.People["qms-latest-author"] },
            approved.GetProperty("history").EnumerateArray().Where(h => h.GetProperty("action").GetString() is "Create" or "Edit").Select(h => h.GetProperty("actor").GetString()));
    }

    [Fact]
    public async Task Operational_policy_versions_are_real_governed_definitions_with_typed_rules_and_controlled_sources()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: builder => builder.UseSetting(QmsService.ConfigurationKey, "EXAMPLE-ORG"));
        var plant = await SetupAsync(app);
        var author = plant.As("qms-author");
        var reviewer = plant.As("qms-reviewer");
        var owner = plant.People["qms-author"];
        await DocumentAsync(plant, "QMS.POLICY");
        QmsContent Content(QmsPolicyKind kind, QmsPolicyRules? rules) => new(Policy: new(kind,
            "Explicit local policy approved for the example organization; future consumers must preserve canonical enforced guards.", owner,
            "Named supervisor briefing and acknowledgment records", "Annual and on change", true, rules), Documents: [new("QMS.POLICY", "A")]);
        var policies = new[]
        {
            ("EXECUTION-POLICY", QmsPolicyKind.Execution, new QmsPolicyRules(Execution: new(true, true, true, true))),
            ("RELEASE-POLICY", QmsPolicyKind.ProductRelease, new QmsPolicyRules(ProductRelease: new(true, true, true, Permissions.QualityProductRelease))),
            ("LABEL-COPY-POLICY", QmsPolicyKind.LabelCopy, new QmsPolicyRules(LabelCopy: new(true, 3, true))),
            ("ATTEMPT-POLICY", QmsPolicyKind.AttemptRetention, new QmsPolicyRules(AttemptRetention: new(30, 365, 10000, true, true, true)))
        };
        await RejectedAsync(plant, author, Api + "/records", Create("MISSING-RULES", "Policy", Content(QmsPolicyKind.Execution, null)), "policy_rules");
        await RejectedAsync(plant, author, Api + "/records", Create("WRONG-RELEASE", "Policy", Content(QmsPolicyKind.ProductRelease,
            new(ProductRelease: new(true, true, true, Permissions.ShippingRelease)))), "release_permission");
        await RejectedAsync(plant, author, Api + "/records", Create("WEAK-RETENTION", "Policy", Content(QmsPolicyKind.AttemptRetention,
            new(AttemptRetention: new(30, 365, 10000, false, true, true)))), "audit_invariant");
        foreach (var (code, kind, rules) in policies)
        {
            var content = Content(kind, rules);
            await plant.Ok(author, Api + "/records", Create(code, "Policy", content));
            await EffectiveAsync(plant, code);
            var effective = await GetAsync(reviewer, $"{Api}/records/{code}/effective");
            Assert.Equal(("Effective", 1, 5, kind.ToString()), (effective.GetProperty("state").GetString(), effective.GetProperty("revision").GetInt32(),
                effective.GetProperty("version").GetInt32(), effective.GetProperty("content").GetProperty("policy").GetProperty("policyKind").GetString()));
            Assert.Equal(JsonSerializer.SerializeToElement(rules, Forge.Kernel.Serialization.CanonicalJson.Options).GetRawText(), effective.GetProperty("content").GetProperty("policy").GetProperty("rules").GetRawText());
            Assert.Equal(("QMS.POLICY", "A"), (Assert.Single(effective.GetProperty("sources").EnumerateArray()).GetProperty("key").GetString(),
                Assert.Single(effective.GetProperty("sources").EnumerateArray()).GetProperty("revision").GetString()));
            await RejectedAsync(plant, author, $"{Api}/records/{code}/revisions/1/edit", new { expectedVersion = 5, content, reason = "An effective definition is immutable" }, "immutable_revision");
        }
        var release = await GetAsync(reviewer, Api + "/records/RELEASE-POLICY/effective");
        Assert.Equal(Permissions.QualityProductRelease, release.GetProperty("content").GetProperty("policy").GetProperty("rules").GetProperty("productRelease").GetProperty("releasePermission").GetString());
        var retention = await GetAsync(reviewer, Api + "/records/ATTEMPT-POLICY/effective");
        var actualRules = retention.GetProperty("content").GetProperty("policy").GetProperty("rules").GetProperty("attemptRetention");
        Assert.Equal((30, 365, 10000), (actualRules.GetProperty("minimumRetentionDays").GetInt32(), actualRules.GetProperty("maximumRawAttemptAgeDays").GetInt32(), actualRules.GetProperty("maximumStoredRawAttempts").GetInt32()));
        // Adoption does not assign the future execution authority or conflate Quality product release with physical shipment.
        var permissions = (await GetAsync(reviewer, "/api/kernel/me")).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.DoesNotContain(Permissions.QualityProductRelease, permissions);
        Assert.DoesNotContain(Permissions.ShippingRelease, permissions);
    }
}
