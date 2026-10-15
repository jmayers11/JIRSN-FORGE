using System.Net;
using System.Text.Json;
using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Quality.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Forge.Integration.Tests.QmsPlanningScenario;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ManagementReviewTests(PostgresFixture postgres)
{
    private const string ReviewApi = "/api/quality/qms/review";
    [Fact]
    public async Task Review_lists_are_bounded_and_planning_evidence_retains_authorized_capture()
    {
        await using var f = await CreateAsync(postgres); await DocumentAsync(f, "REVIEW-LIST-SOURCE");
        await CreateProgramAsync(f, "REVIEW-LIST", "REVIEW-LIST-SOURCE"); var id = Guid.NewGuid(); await CreateRunAsync(f, id, "REVIEW-LIST");
        var listed = await ReadAsync(f.Author, ReviewApi + "/runs?take=10");
        Assert.Contains(listed.GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetGuid() == id);
        var bridge = f.App.Services.GetRequiredService<IQmsPlanningEvidenceReader>();
        var factory = f.App.Services.GetRequiredService<IKernelUnitOfWorkFactory>();
        await using (var uow = await factory.BeginSnapshotAsync(CancellationToken.None))
        {
            var principal = (await uow.People.LoadPrincipalAsync(new PersonId(f.Plant.People["eng"]), CancellationToken.None))!;
            var captured = await bridge.ResolveAsync(uow, principal, new("Quality", "QmsReviewRun", id.ToString("D")), false, CancellationToken.None);
            Assert.Contains(id.ToString("D"), captured.CapturedCanonicalJson, StringComparison.Ordinal);
        }

        Assert.Single((await ReadAsync(f.Author, ReviewApi + "/runs?take=1")).GetProperty("items").EnumerateArray());
        Assert.Empty((await ReadAsync(f.Author, ReviewApi + "/mappings/assessments?take=10")).GetProperty("items").EnumerateArray());
        Assert.Empty((await ReadAsync(f.Author, ReviewApi + "/editions/assessments?take=10")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Real_review_freezes_actual_values_and_cutoff_then_records_independent_decisions_and_owned_verified_actions()
    {
        await using var f = await CreateAsync(postgres); await DocumentAsync(f, "REVIEW-INPUT");
        await CreateProgramAsync(f, "REVIEW-PROGRAM", "REVIEW-INPUT");
        var id = Guid.NewGuid(); await CreateRunAsync(f, id, "REVIEW-PROGRAM");
        var freeze = new { expectedVersion = 1, reason = "Capture the actual recorded owner inputs" };
        await JsonAsync(await f.Author.CommandAsync($"{ReviewApi}/runs/{id}/freeze", freeze, "review-actual-freeze"));
        var frozen = await ReadAsync(f.Author, $"{ReviewApi}/runs/{id}");
        var manifest = frozen.GetProperty("manifest"); var manifestBytes = manifest.GetRawText();
        Assert.False(string.IsNullOrWhiteSpace(manifest.GetProperty("databaseSnapshotIdentity").GetString()));
        Assert.True(manifest.GetProperty("periodTo").GetDateTimeOffset() <= manifest.GetProperty("dataCutoff").GetDateTimeOffset());
        var source = manifest.GetProperty("inputs")[0].GetProperty("sources")[0];
        var captured = source.GetProperty("capturedCanonicalJson").GetString();
        Assert.Contains("REVIEW-INPUT", captured, StringComparison.Ordinal);
        await f.Plant.Ok(f.Author, "/api/engineering/documents/REVIEW-INPUT", new { title = "Later actual document header title", markings = "None", reason = "A later owner metadata change" });
        Assert.Equal(manifestBytes, (await ReadAsync(f.Reviewer, $"{ReviewApi}/runs/{id}")).GetProperty("manifest").GetRawText());
        var assessment = new QmsReviewInputAssessment("CONTEXT", QmsEvidenceAdequacy.Adequate,
            "The exact frozen original owner values were reviewed", "The current owner title has since changed",
            "Assign an evidence review action", []);
        var decision = new QmsManagementReviewDecision("FOLLOWUP", "Verify the original retained source",
            "The meeting assessed the actual captured input", "Record exact source review", "Assigned controller review time",
            f.Plant.People["eng"], f.Time.Now.AddDays(14), "The assigned owner records the exact source and an independent verifier checks it", []);
        await f.Plant.Ok(f.Author, $"{ReviewApi}/runs/{id}/meeting", new { expectedVersion = 2, meetingRecord = "Actual participants assessed the immutable input",
            participants = new[] { f.Plant.People["eng"], f.Plant.People["approver"] }, assessments = new[] { assessment }, decisions = new[] { decision },
            limitations = "This local review does not establish normative conformity", reason = "Attributed human review judgments" });
        await f.Plant.Ok(f.Author, $"{ReviewApi}/runs/{id}/submit", Decision(3));
        await RejectedAsync(await f.Plant.Try(f.Author, $"{ReviewApi}/runs/{id}/review", Decision(4)), "review_independent_assessment_required");
        await f.Plant.Ok(f.Reviewer, $"{ReviewApi}/runs/{id}/review", Decision(4));
        await f.Plant.Ok(f.Reviewer, $"{ReviewApi}/runs/{id}/approve", Decision(5));
        var actions = await ReadAsync(f.Author, $"{ReviewApi}/runs/{id}/actions"); var actionId = actions[0].GetProperty("id").GetGuid();
        Assert.Single(actions.EnumerateArray()); Assert.Equal(f.Plant.People["eng"], actions[0].GetProperty("ownerPersonId").GetGuid());
        var evidence = new[] { new QmsPlanningEvidenceReference("Engineering", "DocumentRevision", "REVIEW-INPUT", "A") };
        var complete = new { expectedVersion = 1, result = "The assigned owner reviewed the retained actual source", evidence,
            reason = "Attributed actual completion" };
        await RejectedAsync(await f.Plant.Try(f.Reviewer, $"{ReviewApi}/actions/{actionId}/complete", complete), "review_action_actual_owner");
        await JsonAsync(await f.Author.CommandAsync($"{ReviewApi}/actions/{actionId}/complete", complete, "review-action-original-complete"));
        await RejectedAsync(await f.Plant.Try(f.Author, $"{ReviewApi}/actions/{actionId}/verify", complete with { expectedVersion = 2 }), "review_independent_assessment_required");
        await f.Plant.Ok(f.Reviewer, $"{ReviewApi}/actions/{actionId}/verify", complete with { expectedVersion = 2, result = "Independent verification confirmed the exact source review" });
        await f.Plant.Ok(f.Reviewer, $"{ReviewApi}/runs/{id}/close", Decision(6));
        await f.Plant.Ok(f.Author, $"{ReviewApi}/actions/{actionId}/reopen", complete with { expectedVersion = 3, result = "New actual issue requires a new verification" });
        await RejectedAsync(await f.Author.CommandAsync($"{ReviewApi}/actions/{actionId}/complete", complete, "review-action-original-complete"), "review_action_generation_changed");
        Assert.Equal("Approved", (await ReadAsync(f.Reviewer, $"{ReviewApi}/runs/{id}")).GetProperty("state").GetString());
        Assert.Equal(manifestBytes, (await ReadAsync(f.Reviewer, $"{ReviewApi}/runs/{id}")).GetProperty("manifest").GetRawText());
        await using var connection = new NpgsqlConnection(f.App.ConnectionString); await connection.OpenAsync();
        await using var tamper = new NpgsqlCommand("UPDATE quality.review_input_manifest SET data=jsonb_set(data,'{fingerprint}','\"tampered\"'::jsonb) WHERE review_id=$1", connection);
        tamper.Parameters.AddWithValue(id);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        Assert.Equal(manifestBytes, (await ReadAsync(f.Reviewer, $"{ReviewApi}/runs/{id}")).GetProperty("manifest").GetRawText());
    }
    [Fact]
    public async Task Current_controlled_source_withdrawal_blocks_original_freeze_replay_and_preserves_the_recorded_manifest()
    {
        await using var f = await CreateAsync(postgres); await DocumentAsync(f, "REVIEW-WITHDRAWN");
        await CreateProgramAsync(f, "REVIEW-REPLAY", "REVIEW-WITHDRAWN"); var id = Guid.NewGuid(); await CreateRunAsync(f, id, "REVIEW-REPLAY");
        var freeze = new { expectedVersion = 1, reason = "Actual first capture" };
        await JsonAsync(await f.Author.CommandAsync($"{ReviewApi}/runs/{id}/freeze", freeze, "review-frozen-retry"));
        var before = await ReadAsync(f.Author, $"{ReviewApi}/runs/{id}");
        await f.Plant.Ok(f.Reviewer, "/api/engineering/documents/REVIEW-WITHDRAWN/revisions/A/obsolete", new { reason = "Actual source controller withdrawal" });
        await RejectedAsync(await f.Author.CommandAsync($"{ReviewApi}/runs/{id}/freeze", freeze, "review-frozen-retry"), "review_document_not_current");
        var after = await ReadAsync(f.Author, $"{ReviewApi}/runs/{id}");
        Assert.Equal(before.GetProperty("manifest").GetRawText(), after.GetProperty("manifest").GetRawText());
        Assert.Equal(before.GetProperty("version").GetInt32(), after.GetProperty("version").GetInt32());
        await using var connection = new NpgsqlConnection(f.App.ConnectionString); await connection.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM quality.review_input_manifest WHERE review_id=$1", connection);
        count.Parameters.AddWithValue(id); Assert.Equal(1L, await count.ExecuteScalarAsync());
    }
    private static Task<JsonElement> CreateRunAsync(Flow f, Guid id, string program) => f.Plant.Ok(f.Author, ReviewApi + "/runs", new
    {
        id, programCode = program, programRevision = 1, periodFrom = f.Time.Now.AddDays(-30), periodTo = f.Time.Now.AddDays(-1), reason = "Actual organization review for a closed period"
    });
    private static async Task CreateProgramAsync(Flow f, string code, string document)
    {
        var content = new QmsReviewDefinitionContent(Program: new("Actual organization management review", f.Plant.People["eng"],
            "On the organization's recorded review schedule", "Actual controlled-source context",
            [new("CONTEXT", "Actual context input", true, "Exact controlled source revision", "The actual owner values must be available and human assessed",
                [new("Engineering", "DocumentRevision", document, "A")], [])], "Actual owner and independent controller",
            "Independent authorized governance approver", "Independent review of the exact assigned evidence", "Every assigned action verified",
            [f.Plant.People["eng"], f.Plant.People["approver"]]), Documents: [new(document, "A")]);
        await f.Plant.Ok(f.Author, ReviewApi + "/definitions", new { code, kind = "ManagementReviewProgram", facilityCode = "PLANT1", markings = "None", content,
            reason = "The organization explicitly chose this actual program" });
        var path = $"{ReviewApi}/definitions/{code}/revisions/1";
        await f.Plant.Ok(f.Author, path + "/submit", Decision(1)); await f.Plant.Ok(f.Reviewer, path + "/review", Decision(2));
        await f.Plant.Ok(f.Reviewer, path + "/approve", Decision(3)); await f.Plant.Ok(f.Reviewer, path + "/effective", Decision(4));
    }
}
