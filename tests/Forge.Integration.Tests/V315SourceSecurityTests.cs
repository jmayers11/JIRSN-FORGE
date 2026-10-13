using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Kernel.Security;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V315SourceSecurityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Ncr_linked_capa_keeps_actual_program_scope_for_reads_commands_replay_and_audit()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("capa-programs", "ProgramManager");
        await plant.Person("capa-author", "QualityEngineer");
        await plant.Person("capa-reviewer", "QualityManager");
        var programs = new List<Guid>();
        foreach (var suffix in new[] { "A", "B" })
            programs.Add(Guid.Parse((await plant.Ok(plant.As("capa-programs"), "/api/programs/", new { code = "CAPA-" + suffix, name = "CAPA source " + suffix })).GetProperty("entityId").GetString()!));
        const string scopedRole = "CapaProgramA";
        var permissions = new[] { Permissions.QualityRead, Permissions.QualityDisposition, Permissions.QualityApprove };
        await plant.Ok(plant.Admin, "/api/kernel/roles", new { role = scopedRole, displayName = "Corrective actions in program A", permissions, reason = "Configure source-scoped authority" });
        foreach (var permission in permissions)
            await plant.Ok(plant.Admin, $"/api/kernel/roles/{scopedRole}/permission-scopes", new { permission, scopeType = "program", scopeId = programs[0].ToString("D"), reason = "Actual program A only" });
        var scopedId = await plant.Person("capa-scoped", scopedRole);
        foreach (var subject in new[] { "eng", "planner", "capa-author", "capa-reviewer", "capa-scoped" })
        foreach (var program in programs)
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[subject]}/programs", new { programId = program, reason = "Membership stays distinct from action scope" });
        var ncrs = new List<long>();
        var capas = new List<long>();
        foreach (var program in programs)
        {
            var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3, programId = program }));
            await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Independently release actual CAPA operation source" });
            var ncr = Id(await plant.Ok(plant.As("capa-author"), "/api/quality/ncrs", new { partNumber = "ROLL-100", orderNumber = order, sequence = 10, quantity = 1m, defect = "Measured deviation", description = "Actual linked source", reason = "Observed production finding" }));
            ncrs.Add(ncr);
            capas.Add(Id(await plant.Ok(plant.As("capa-author"), "/api/quality/capas", Proposal(ncr, program == programs[1] ? "PRIVATE CAPA B" : "Permitted CAPA A"))));
        }
        var scoped = plant.As("capa-scoped");
        var visible = await Get(scoped, "/api/quality/capas");
        Assert.Equal(capas[0], Assert.Single(visible.EnumerateArray()).GetProperty("capaId").GetInt64());
        Assert.DoesNotContain("PRIVATE CAPA B", visible.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(scoped, "/api/quality/capas", Proposal(ncrs[1], "Not permitted"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(scoped, $"/api/quality/capas/{capas[1]}/implement", new { note = "Not permitted", reason = "Other source" })).StatusCode);
        var implementation = new { note = "Action implemented against actual NCR A", reason = "Recorded implementation" };
        var applied = await scoped.CommandAsync($"/api/quality/capas/{capas[0]}/implement", implementation, "capa-once");
        var appliedBody = await applied.Content.ReadAsStringAsync();
        Assert.True(applied.IsSuccessStatusCode, appliedBody);
        Assert.Equal(appliedBody, await (await scoped.CommandAsync($"/api/quality/capas/{capas[0]}/implement", implementation, "capa-once")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await plant.Try(scoped, $"/api/quality/capas/{capas[0]}/verify", new { reason = "Implementer cannot independently verify" })).StatusCode);
        await plant.Ok(plant.As("capa-reviewer"), $"/api/quality/capas/{capas[0]}/verify", new { reason = "Independent observed effectiveness" });
        await plant.Ok(plant.Admin, $"/api/kernel/people/{scopedId}/programs/{programs[0]}/revoke", new { reason = "Withdraw current source membership" });
        Assert.Empty((await Get(scoped, "/api/quality/capas?includeVerified=true")).EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.CommandAsync($"/api/quality/capas/{capas[0]}/implement", implementation, "capa-once")).StatusCode);
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var evidence = new NpgsqlCommand("SELECT count(*) FROM kernel.audit_log WHERE entity_type='Capa' AND entity_id=$1 AND outcome='Succeeded' AND program_id=$2", connection);
        evidence.Parameters.AddWithValue(capas[0].ToString(CultureInfo.InvariantCulture)); evidence.Parameters.AddWithValue(programs[0]);
        Assert.Equal(3L, (long)(await evidence.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Audit_reads_honor_recorded_and_current_document_classification_without_rewriting_legacy_hashes()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        var authorId = Guid.Parse((await plant.Ok(plant.Admin, "/api/kernel/people", new { idpSubject = "audit-cleared-author", displayName = "Cleared document author", markings = new[] { "ExportControlled" }, reason = "Approved clearance" })).GetProperty("entityId").GetString()!);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{authorId}/roles", new { role = "QualityEngineer", reason = "Document authority" });
        var readerId = Guid.Parse((await plant.Ok(plant.Admin, "/api/kernel/people", new { idpSubject = "audit-cleared-reader", displayName = "Cleared audit reader", markings = new[] { "ExportControlled" }, reason = "Approved audit clearance" })).GetProperty("entityId").GetString()!);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{readerId}/roles", new { role = "Auditor", reason = "Business audit read authority" });
        await plant.Person("audit-uncleared-reader", "Auditor");
        var author = plant.As("audit-cleared-author");
        await plant.Ok(author, "/api/engineering/documents", new { number = "AUDIT-CLASSIFIED", title = "PRIVATE CONTROLLED TITLE", type = "Procedure", authority = "Internal", markings = "ExportControlled", reason = "Controlled source evidence" });
        var allowed = await Get(plant.As("audit-cleared-reader"), "/api/kernel/audit?entityType=Document&entityId=AUDIT-CLASSIFIED");
        Assert.Single(allowed.EnumerateArray());
        Assert.Contains("PRIVATE CONTROLLED TITLE", allowed.GetRawText(), StringComparison.Ordinal);
        Assert.Empty((await Get(plant.As("audit-uncleared-reader"), "/api/kernel/audit?entityType=Document&entityId=AUDIT-CLASSIFIED")).EnumerateArray());
        Assert.Equal("ExportControlled", allowed[0].GetProperty("entry").GetProperty("markings").GetString());

        // An early-style immutable entry retained classification in its snapshot but omitted
        // the historical markings column. Seed it through the real hash writer.
        var uows = app.Services.GetRequiredService<Forge.Kernel.Access.IKernelUnitOfWorkFactory>();
        var nestedProgram = Guid.NewGuid();
        await using (var uow = await uows.BeginAsync(CancellationToken.None))
        {
            await uow.Audit.AppendAsync(new Forge.Kernel.Audit.AuditEntry
            {
                AuditId = Guid.NewGuid(), RecordedAt = DateTimeOffset.UtcNow, Actor = "person:" + authorId,
                Action = "HistoricalDocumentCreated", Outcome = "Succeeded", EntityType = "Document", EntityId = "AUDIT-CLASSIFIED",
                NewValue = Forge.Kernel.Serialization.CanonicalJson.Serialize(new { title = "LEGACY PRIVATE TITLE", markings = "ExportControlled" }),
                Source = "Api", CorrelationId = Guid.NewGuid(),
            }, CancellationToken.None);
            await uow.Audit.AppendAsync(new Forge.Kernel.Audit.AuditEntry
            {
                AuditId = Guid.NewGuid(), RecordedAt = DateTimeOffset.UtcNow, Actor = "person:" + authorId,
                Action = "HistoricalNestedSourceEvidence", Outcome = "Succeeded", EntityType = "Document", EntityId = "AUDIT-CLASSIFIED",
                NewValue = Forge.Kernel.Serialization.CanonicalJson.Serialize(new
                {
                    detail = "PRIVATE NESTED PROGRAM EVIDENCE",
                    requiredDocuments = new[] { new ResourceRef("Document", "NESTED-SOURCE", new Forge.Kernel.Identity.ProgramId(nestedProgram)) },
                }),
                Source = "Api", CorrelationId = Guid.NewGuid(),
            }, CancellationToken.None);
            await uow.CommitAsync(CancellationToken.None);
        }
        Assert.Equal(2, (await Get(plant.As("audit-cleared-reader"), "/api/kernel/audit?entityType=Document&entityId=AUDIT-CLASSIFIED")).GetArrayLength());
        Assert.Empty((await Get(plant.As("audit-uncleared-reader"), "/api/kernel/audit?entityType=Document&entityId=AUDIT-CLASSIFIED")).EnumerateArray());
        await plant.Ok(plant.Admin, $"/api/kernel/people/{readerId}/programs", new { programId = nestedProgram, reason = "Independent nested source program access" });
        Assert.Equal(3, (await Get(plant.As("audit-cleared-reader"), "/api/kernel/audit?entityType=Document&entityId=AUDIT-CLASSIFIED")).GetArrayLength());
        await plant.Ok(author, "/api/engineering/documents/AUDIT-CLASSIFIED", new { title = "Public current title", markings = "None", reason = "Authorized declassification preserves historical classification" });
        Assert.Empty((await Get(plant.As("audit-uncleared-reader"), "/api/kernel/audit?entityType=Document&entityId=AUDIT-CLASSIFIED")).EnumerateArray());
        Assert.True((await Get(plant.Admin, "/api/kernel/audit/verify")).GetProperty("intact").GetBoolean());
    }

    private static object Proposal(long ncr, string title) => new { ncrId = ncr, title, rootCause = "Verified setup cause", action = "Update controlled setup", dueDate = Day(10), reason = "Source corrective action" };
    private static string Day(int offset) => DateTime.UtcNow.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
    private static async Task<JsonElement> Get(HttpClient client, string url) => await client.GetFromJsonAsync<JsonElement>(url);
}
