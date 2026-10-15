using System.Net;
using System.Net.Http.Json;
using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Quality.Audit;
using Forge.Quality.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Forge.Integration.Tests.QmsPlanningScenario;
using static Forge.Integration.Tests.AuditCapaScenario;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AuditCapaGovernanceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Investigation_original_retry_is_exactly_once_changed_body_conflicts_and_same_version_writers_serialize()
    {
        await using var f=await SetupAsync(postgres,false);var id=await CapaAsync(f);var draft=new WriteCapaInvestigation(id,1,1,null,null,Investigation(true));var body=Body(draft);
        var first=await JsonAsync(await f.Author.CommandAsync(CapaApi+"/"+id+"/investigations",body,"investigation-once"));var retry=await JsonAsync(await f.Author.CommandAsync(CapaApi+"/"+id+"/investigations",body,"investigation-once"));Assert.Equal(first.GetProperty("entityId").GetString(),retry.GetProperty("entityId").GetString());Assert.Single((await ReadCapa(f,id)).GetProperty("investigations").EnumerateArray());
        await JsonAsync(await f.Author.CommandAsync(CapaApi+"/"+id+"/investigations",Body(draft with{Content=draft.Content with{ActualInvestigation="Different actual investigation"}}),"investigation-once"),HttpStatusCode.Conflict);
        var edit=new WriteCapaInvestigation(id,2,1,1,1,Investigation(true));var results=await Task.WhenAll(f.Plant.Try(f.Author,CapaApi+"/"+id+"/investigations/1/edit",Body(edit)),f.Plant.Try(f.Author,CapaApi+"/"+id+"/investigations/1/edit",Body(edit with{Content=edit.Content with{ActualInvestigation="Concurrent independent edit"}})));
        Assert.Single(results,x=>x.IsSuccessStatusCode);foreach(var response in results)if(response.IsSuccessStatusCode)response.Dispose();else await RejectedAsync(response,"stale_version");
        Assert.Equal(3,await CaseVersion(f,id));
    }
    [Fact]
    public async Task Lost_actual_engineering_source_permission_blocks_original_audit_approval_replay()
    {
        await using var f=await SetupAsync(postgres,false);await f.Plant.Person("audit-only-approver","AuditApprover");var narrow=f.Plant.As("audit-only-approver");
        await f.Plant.Ok(f.Author,AuditApi+"/programs",Body(new DefineAuditProgram("PERMISSION-AUDIT","PLANT1",null,DataMarking.None,Program(f))));var path=AuditApi+"/programs/PERMISSION-AUDIT/revisions/1";
        await f.Plant.Ok(f.Author,path+"/submit",Body(DecisionData(1)));await f.Plant.Ok(narrow,path+"/review",Body(DecisionData(2)));var body=Body(DecisionData(3));await JsonAsync(await narrow.CommandAsync(path+"/approve",body,"audit-source-approval"));
        await f.Plant.Ok(f.Plant.Admin,"/api/kernel/roles/AuditApprover/permissions/engineering.read/revoke",new{reason="Remove actual Engineering owner visibility from this optional configured role"});
        using var rejected=await narrow.CommandAsync(path+"/approve",body,"audit-source-approval");Assert.Equal(HttpStatusCode.Forbidden,rejected.StatusCode);
        var saved=await ReadAsync(f.Reviewer,AuditApi+"/programs/PERMISSION-AUDIT");Assert.Equal(4,saved.GetProperty("revisions")[0].GetProperty("version").GetInt32());
    }
    [Fact]
    public async Task Audit_origin_and_CAPA_follow_up_are_real_reciprocal_links_without_rewriting_original_evidence_or_reusing_reopened_closure()
    {
        await using var f=await SetupAsync(postgres);var run=await ScheduleAsync(f);await f.Plant.Ok(f.Observer,AuditApi+"/runs/"+run+"/start",Body(new DecideAudit(run,1,1,"Start","Actual competent auditor starts")));
        var result=await f.Plant.Ok(f.Observer,AuditApi+"/runs/"+run+"/findings",Body(new RecordAuditFinding(run,2,1,"MATERIAL-CONTROL",QmsAuditFindingKind.Nonconformity,"Actual recurring discrepancy","Actual independent observation",QmsAuditAdequacy.Partial,"Required scope partially supported by actual evidence","Recorded source limits",true,Evidence,[],[])));var finding=Guid.Parse(result.GetProperty("entityId").GetString()!);
        var created=await f.Plant.Ok(f.Author,CapaApi,Body(Capa(f) with{OriginFindingId=finding}));var id=long.Parse(created.GetProperty("entityId").GetString()!);await PlanAsync(f,id);var action=await VerifiedActionAsync(f,id);await f.Plant.Ok(f.Author,CapaApi+"/"+id+"/implemented",Body(new DecideGovernedCapa(id,await CaseVersion(f,id),1,"Implemented","Actual source-control implementation",null,Evidence)));var evaluation=await ApprovedEffectivenessAsync(f,id,action,QmsEffectivenessResult.Effective);
        await f.Plant.Ok(f.Reviewer,CapaApi+"/"+id+"/close",Body(new DecideGovernedCapa(id,await CaseVersion(f,id),1,"Close","Actual verified source-control effectiveness",evaluation,Evidence)));
        await f.Plant.Ok(f.Observer,AuditApi+"/findings/"+finding+"/record",Body(new FollowUpAuditFinding(finding,1,1,"Record","Actual CAPA implementation and effectiveness evidence linked",Evidence,[id],[])));
        await f.Plant.Ok(f.Reviewer,AuditApi+"/findings/"+finding+"/verify",Body(new FollowUpAuditFinding(finding,2,1,"Verify","Independent human verified actual closed CAPA and evidence",Evidence,[id],[])));
        var report=new WriteAuditReport(run,3,1,"Actual report","Selected source control","Actual scope limitations","Qualified human assessment of current verified follow-up",[finding],Evidence,[new("CHECK-MATERIAL","Actual required sources reviewed",QmsAuditAdequacy.Partial,"Human evaluates limited selected scope","Other cohort facts unavailable",Evidence)]);
        await f.Plant.Ok(f.Observer,AuditApi+"/runs/"+run+"/report",Body(report));await f.Plant.Ok(f.Observer,AuditApi+"/runs/"+run+"/submitreport",Body(new DecideAudit(run,4,1,"SubmitReport","Actual report complete")));await f.Plant.Ok(f.Reviewer,AuditApi+"/runs/"+run+"/approvereport",Body(new DecideAudit(run,5,1,"ApproveReport","Independent human approves actual report")));
        var current=await ReadCapa(f,id);var origin=current.GetProperty("capa").GetProperty("sources").EnumerateArray().Single(x=>x.GetProperty("ownerType").GetString()=="QmsAuditFindingOrigin");Assert.Contains("\"version\":1",origin.GetProperty("capturedCanonicalJson").GetString());Assert.Contains("Actual independent observation",origin.GetProperty("capturedCanonicalJson").GetString());Assert.DoesNotContain("FollowUpVerified",origin.GetProperty("capturedCanonicalJson").GetString());
        await f.Plant.Ok(f.Reviewer,CapaApi+"/"+id+"/reopen",Body(new DecideGovernedCapa(id,await CaseVersion(f,id),1,"Reopen","New actual recurring evidence invalidates earlier closure",evaluation,Evidence)));
        await RejectedAsync(await f.Plant.Try(f.Reviewer,AuditApi+"/runs/"+run+"/close",Body(new DecideAudit(run,6,1,"Close","Old CAPA closure cannot authorize current audit close"))),"planning_source_changed");
    }
    [Fact]
    public async Task Exact_competence_port_rejects_uncovered_criteria_and_retains_actual_authorized_owner_values()
    {
        await using var f=await SetupAsync(postgres);await using var u=await f.App.Services.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(CancellationToken.None);
        var p=await u.People.LoadPrincipalAsync(new PersonId(f.Plant.People["planning-observer"]),CancellationToken.None)??throw new InvalidOperationException();
        var service=f.App.Services.GetRequiredService<IAuditorCompetenceValidator>();var query=new QmsAuditorValidation(p.PersonId.Value,f.Time.Now,new("INTERNAL-AUDIT",1),null,"Actual conflict review",[],[new("AUDIT.BASIS","A")]);
        var actual=await service.ValidateAsync(u,p,query,false,CancellationToken.None);Assert.Equal("Selected source-control process and material discrepancy records",actual.ApprovedScope);Assert.True(actual.Competence.Qualifications.Single().QualifiedAsOf);
        Assert.All(actual.Sources,source=>{Assert.False(string.IsNullOrWhiteSpace(source.CapturedCanonicalJson));Assert.Equal(source.Fingerprint,CanonicalJson.Sha256Hex(source.CapturedCanonicalJson!));});
        var failure=await Assert.ThrowsAsync<CommandRejectedException>(()=>service.ValidateAsync(u,p,query with{RequiredCriteria=[new("UNAPPROVED-CRITERION",1)]},false,CancellationToken.None));Assert.Equal("audit_competence_scope",failure.Code);
        var wrongStandard=await Assert.ThrowsAsync<CommandRejectedException>(()=>service.ValidateAsync(u,p,query with{RequiredStandardReferences=[new("AUDIT.BASIS","B")]},false,CancellationToken.None));Assert.Equal("audit_competence_scope",wrongStandard.Code);
    }
    [Fact]
    public async Task Correction_and_containment_do_not_substitute_for_actual_corrective_action_and_effectiveness()
    {
        await using var f=await SetupAsync(postgres,false);var id=await CapaAsync(f);await PlanAsync(f,id);
        await VerifiedActionAsync(f,id,QmsCorrectiveActionKind.Correction);
        await RejectedAsync(await f.Plant.Try(f.Author,CapaApi+"/"+id+"/implemented",Body(new DecideGovernedCapa(id,await CaseVersion(f,id),1,"Implemented","Correction alone is insufficient",null,Evidence))),"capa_implementation");
        var action=await VerifiedActionAsync(f,id);await f.Plant.Ok(f.Author,CapaApi+"/"+id+"/implemented",Body(new DecideGovernedCapa(id,await CaseVersion(f,id),1,"Implemented","Actual corrective action independently verified",null,Evidence)));
        f.Time.Now=f.Time.Now.AddDays(2);
        var actions=(await ReadCapa(f,id)).GetProperty("actions").EnumerateArray().Select(x=>x.GetProperty("id").GetGuid()).ToArray();Assert.Equal(2,actions.Length);
        var assessment=new RecordCapaEffectiveness(id,await CaseVersion(f,id),1,f.Time.Now.AddDays(-1),f.Time.Now,"Actual method",Capa(f).EffectivenessCriteria,"Actual observed results",QmsEffectivenessResult.Effective,"Known scope limits","Actual recurrence review","Human review decision",Evidence,[action]);
        await RejectedAsync(await f.Plant.Try(f.Author,CapaApi+"/"+id+"/effectiveness",Body(assessment)),"capa_action_manifest");
        var created=await f.Plant.Ok(f.Author,CapaApi+"/"+id+"/effectiveness",Body(assessment with{VerifiedActionIds=actions}));var evaluation=Guid.Parse(created.GetProperty("entityId").GetString()!);
        await RejectedAsync(await f.Plant.Try(f.Reviewer,CapaApi+"/"+id+"/close",Body(new DecideGovernedCapa(id,await CaseVersion(f,id),1,"Close","Draft evidence is not closure",evaluation,Evidence))),"capa_effectiveness");
    }

    [Fact]
    public async Task Analyst_answers_open_audit_findings_and_overdue_actions_only_with_quality_access()
    {
        await using var f=await SetupAsync(postgres);
        var run=await ScheduleAsync(f);
        await f.Plant.Ok(f.Observer,AuditApi+"/runs/"+run+"/start",Body(new DecideAudit(run,1,1,"Start","Actual competent auditor starts")));
        var findingResult=await f.Plant.Ok(f.Observer,AuditApi+"/runs/"+run+"/findings",Body(new RecordAuditFinding(run,2,1,
            "MATERIAL-CONTROL",QmsAuditFindingKind.Nonconformity,"Actual open source mismatch","Actual independent observation",
            QmsAuditAdequacy.Partial,"Qualified auditor assessed the selected scope","Other lots were not sampled",true,Evidence,[],[])));
        var finding=Guid.Parse(findingResult.GetProperty("entityId").GetString()!);

        var capa=await CapaAsync(f);await PlanAsync(f,capa);
        var createdAction=await f.Plant.Ok(f.Author,CapaApi+"/"+capa+"/actions",Body(new CreateCapaAction(capa,
            await CaseVersion(f,capa),1,QmsCorrectiveActionKind.CorrectiveAction,"Review exact part and lot validation",f.Plant.People["eng"],
            f.Time.Now.AddHours(1),"Assigned Quality owner time","Record actual exact part and lot observations")));
        var action=Guid.Parse(createdAction.GetProperty("entityId").GetString()!);
        f.Time.Now=f.Time.Now.AddDays(2);

        var open=await JsonAsync(await f.Author.PostAsJsonAsync("/api/analyst/ask",new{question="Which audits have open findings?"}));
        Assert.Equal("OpenAuditFindings",open.GetProperty("interpretation").GetProperty("intent").GetString());
        Assert.Single(open.GetProperty("rows").EnumerateArray());
        Assert.Contains(open.GetProperty("facts").EnumerateArray(),x=>x.GetProperty("trace").GetString()=="qms-audit-finding:"+finding.ToString("D"));

        var overdue=await JsonAsync(await f.Author.PostAsJsonAsync("/api/analyst/ask",new{question="Which corrective actions are overdue?"}));
        Assert.Equal("OverdueCorrectiveActions",overdue.GetProperty("interpretation").GetProperty("intent").GetString());
        Assert.Single(overdue.GetProperty("rows").EnumerateArray());
        Assert.Contains(overdue.GetProperty("facts").EnumerateArray(),x=>x.GetProperty("trace").GetString()=="capa-action:"+action.ToString("D"));

        await f.Plant.Person("analyst-without-quality",Roles.MaintenanceTechnician);
        var denied=await JsonAsync(await f.Plant.As("analyst-without-quality").PostAsJsonAsync("/api/analyst/ask",new{question="Which corrective actions are overdue?"}));
        Assert.True(denied.GetProperty("notAuthorized").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null,denied.GetProperty("rows").ValueKind);
        Assert.Empty(denied.GetProperty("facts").EnumerateArray());
    }

    [Theory]
    [InlineData(QmsEffectivenessResult.Ineffective)]
    [InlineData(QmsEffectivenessResult.Inconclusive)]
    public async Task Unsuccessful_human_effectiveness_cannot_close_but_controlled_reopen_retains_original_ID_and_evidence(QmsEffectivenessResult result)
    {
        await using var f=await SetupAsync(postgres,false);var id=await CapaAsync(f);await PlanAsync(f,id);var action=await VerifiedActionAsync(f,id);
        await f.Plant.Ok(f.Author,CapaApi+"/"+id+"/implemented",Body(new DecideGovernedCapa(id,await CaseVersion(f,id),1,"Implemented","Actual implementation evidence",null,Evidence)));
        var evaluation=await ApprovedEffectivenessAsync(f,id,action,result);
        await RejectedAsync(await f.Plant.Try(f.Reviewer,CapaApi+"/"+id+"/close",Body(new DecideGovernedCapa(id,await CaseVersion(f,id),1,"Close","An unsuccessful result cannot close",evaluation,Evidence))),"capa_effectiveness");
        await f.Plant.Ok(f.Reviewer,CapaApi+"/"+id+"/reopen",Body(new DecideGovernedCapa(id,await CaseVersion(f,id),1,"Reopen","Human reviewed recurring or inconclusive evidence",evaluation,Evidence)));
        var current=await ReadCapa(f,id);Assert.Equal(id,current.GetProperty("capa").GetProperty("capaId").GetInt64());Assert.Equal(2,current.GetProperty("capa").GetProperty("generation").GetInt32());Assert.Equal("Unknown",current.GetProperty("capa").GetProperty("causeKnowledge").GetString());Assert.Empty(current.GetProperty("actions").EnumerateArray());
        await using var connection=new NpgsqlConnection(f.App.ConnectionString);await connection.OpenAsync();await using var cmd=new NpgsqlCommand("SELECT count(*) FROM quality.audit_version WHERE entity_type='Effectiveness' AND entity_id=$1",connection);cmd.Parameters.AddWithValue(evaluation.ToString());Assert.Equal(4L,(long)(await cmd.ExecuteScalarAsync())!);
        await RejectedAsync(await f.Plant.Try(f.Reviewer,CapaApi+"/effectiveness/"+evaluation+"/approve",Body(new DecideCapaEffectiveness(evaluation,3,1,"Approve","Old generation approval cannot reapply"))),"capa_generation");
    }
    [Fact]
    public async Task Effective_result_closes_once_but_action_reverification_invalidates_earlier_approved_manifest_before_replay()
    {
        await using var f=await SetupAsync(postgres,false);var id=await CapaAsync(f);await PlanAsync(f,id);var action=await VerifiedActionAsync(f,id);
        await f.Plant.Ok(f.Author,CapaApi+"/"+id+"/implemented",Body(new DecideGovernedCapa(id,await CaseVersion(f,id),1,"Implemented","Actual implementation evidence",null,Evidence)));var evaluation=await ApprovedEffectivenessAsync(f,id,action,QmsEffectivenessResult.Effective);
        var body=Body(new DecideCapaEffectiveness(evaluation,3,1,"Approve","Independent human effect decision"));
        await f.Plant.Ok(f.Author,CapaApi+"/actions/"+action+"/reopen",Body(new DecideCapaAction(action,3,1,"Reopen","Actual changed implementation evidence",Evidence)));
        await f.Plant.Ok(f.Author,CapaApi+"/actions/"+action+"/complete",Body(new DecideCapaAction(action,4,1,"Complete","Actual revised implementation observed",Evidence,2)));
        await f.Plant.Ok(f.Reviewer,CapaApi+"/actions/"+action+"/verify",Body(new DecideCapaAction(action,5,1,"Verify","Actual revised implementation independently verified",Evidence,2)));
        await RejectedAsync(await f.Plant.Try(f.Reviewer,CapaApi+"/"+id+"/close",Body(new DecideGovernedCapa(id,await CaseVersion(f,id),1,"Close","Old approved effect cannot cover changed action",evaluation,Evidence))),"capa_effectiveness");
        var current=await ReadCapa(f,id);Assert.Equal("Open",current.GetProperty("core").GetProperty("status").GetString());Assert.Equal("Implementing",current.GetProperty("capa").GetProperty("state").GetString());Assert.Equal(System.Text.Json.JsonValueKind.Null,current.GetProperty("capa").GetProperty("latestEffectivenessId").ValueKind);
    }
    [Fact]
    public async Task Current_independent_effectiveness_closes_existing_core_ID_and_immutable_versions_reject_physical_rewrite()
    {
        await using var f=await SetupAsync(postgres,false);var id=await CapaAsync(f);await PlanAsync(f,id);var action=await VerifiedActionAsync(f,id);
        await f.Plant.Ok(f.Author,CapaApi+"/"+id+"/implemented",Body(new DecideGovernedCapa(id,await CaseVersion(f,id),1,"Implemented","Actual implementation evidence",null,Evidence)));var evaluation=await ApprovedEffectivenessAsync(f,id,action,QmsEffectivenessResult.Effective);
        var close=Body(new DecideGovernedCapa(id,await CaseVersion(f,id),1,"Close","Actual independent effectiveness closure",evaluation,Evidence));
        await JsonAsync(await f.Reviewer.CommandAsync(CapaApi+"/"+id+"/close",close,"close-once"));await JsonAsync(await f.Reviewer.CommandAsync(CapaApi+"/"+id+"/close",close,"close-once"));
        var current=await ReadCapa(f,id);Assert.Equal("Closed",current.GetProperty("capa").GetProperty("state").GetString());Assert.Equal("Verified",current.GetProperty("core").GetProperty("status").GetString());Assert.NotEqual(current.GetProperty("core").GetProperty("implementedBy").GetString(),current.GetProperty("core").GetProperty("verifiedBy").GetString());
        await using var connection=new NpgsqlConnection(f.App.ConnectionString);await connection.OpenAsync();await using var cmd=new NpgsqlCommand("DELETE FROM quality.audit_version WHERE entity_type='Capa' AND entity_id=$1",connection);cmd.Parameters.AddWithValue(id.ToString());var failure=await Assert.ThrowsAsync<PostgresException>(()=>cmd.ExecuteNonQueryAsync());Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,failure.SqlState);
    }
    [Fact]
    public async Task Unknown_cause_is_real_null_in_original_core_and_cannot_be_approved_without_human_determination()
    {
        await using var f=await SetupAsync(postgres,false);var id=await CapaAsync(f);
        var core=(await ReadCapa(f,id)).GetProperty("core");Assert.Equal(System.Text.Json.JsonValueKind.Null,core.GetProperty("rootCause").ValueKind);Assert.Equal(System.Text.Json.JsonValueKind.Null,core.GetProperty("action").ValueKind);Assert.True(core.GetProperty("governed").GetBoolean());
        await f.Plant.Ok(f.Author,CapaApi+"/"+id+"/investigations",Body(new WriteCapaInvestigation(id,1,1,null,null,Investigation(false))));
        await f.Plant.Ok(f.Author,CapaApi+"/"+id+"/investigations/1/submit",Body(new DecideCapaInvestigation(id,1,1,1,"Submit","Unknown cause still needs independent evidence review")));
        await RejectedAsync(await f.Plant.Try(f.Author,CapaApi+"/"+id+"/investigations/1/review",Body(new DecideCapaInvestigation(id,1,1,2,"Review","Cannot independently review own content"))),"separation_of_duties");
        await f.Plant.Ok(f.Reviewer,CapaApi+"/"+id+"/investigations/1/review",Body(new DecideCapaInvestigation(id,1,1,2,"Review","Review identifies actual evidence gaps")));
        await RejectedAsync(await f.Plant.Try(f.Reviewer,CapaApi+"/"+id+"/investigations/1/approve",Body(new DecideCapaInvestigation(id,1,1,3,"Approve","Attempt cause conclusion without evidence"))),"capa_cause_unknown");
        Assert.Equal("Unknown",(await ReadCapa(f,id)).GetProperty("capa").GetProperty("causeKnowledge").GetString());
    }
    [Fact]
    public async Task Existing_verified_ID_is_preserved_and_legacy_original_key_replay_cannot_bypass_governed_reopening()
    {
        await using var f=await SetupAsync(postgres,false);
        var created=await f.Plant.Ok(f.Author,"/api/quality/capas",new{title="Historic actual CAPA",rootCause="Historic human cause",action="Historic actual action",dueDate=DateOnly.FromDateTime(f.Time.Now.UtcDateTime.AddDays(10))});var id=long.Parse(created.GetProperty("entityId").GetString()!);
        var body=new{note="Actual historic implementation"};await JsonAsync(await f.Author.CommandAsync("/api/quality/capas/"+id+"/implement",body,"historic-implementation"));
        await f.Plant.Ok(f.Reviewer,"/api/quality/capas/"+id+"/verify",new{reason="Independent historic verification"});
        Assert.Equal(id,await CapaAsync(f,id));var current=await ReadCapa(f,id);Assert.Equal("Verified",current.GetProperty("capa").GetProperty("legacySnapshot").GetProperty("status").GetString());Assert.Equal("Historic human cause",current.GetProperty("capa").GetProperty("legacySnapshot").GetProperty("rootCause").GetString());
        await RejectedAsync(await f.Author.CommandAsync("/api/quality/capas/"+id+"/implement",body,"historic-implementation"),"capa_governed");
        await RejectedAsync(await f.Plant.Try(f.Reviewer,"/api/quality/capas/"+id+"/verify",new{reason="A role cannot bypass governed generation"}),"capa_governed");
    }
    [Fact]
    public async Task Actual_process_withdrawal_blocks_original_case_create_retry_without_duplicate_core_or_event()
    {
        await using var f=await SetupAsync(postgres,false);var body=Body(Capa(f));await JsonAsync(await f.Author.CommandAsync(CapaApi,body,"capa-original"));
        await f.Plant.Ok(f.Reviewer,"/api/quality/qms/records/AUDIT-PROCESS/revisions/1/withdraw",Decision(5));
        await RejectedAsync(await f.Author.CommandAsync(CapaApi,body,"capa-original"),"capa_process_not_effective");
        await using var connection=new NpgsqlConnection(f.App.ConnectionString);await connection.OpenAsync();await using var cmd=new NpgsqlCommand("SELECT count(*) FROM quality.capa",connection);Assert.Equal(1L,(long)(await cmd.ExecuteScalarAsync())!);
    }
    [Fact]
    public async Task Process_owner_cannot_audit_owned_scope_and_suspended_actual_qualification_blocks_original_start_retry()
    {
        await using var f=await SetupAsync(postgres);var owned=f.Plant.People["eng"];var program=new QmsRevisionReference("INTERNAL-AUDIT",1);
        await RejectedAsync(await f.Plant.Try(f.Author,AuditApi+"/runs",Body(new ScheduleAudit(program,"Actual owned process",f.Time.Now,f.Time.Now.AddDays(1),[new(owned,"Attempt owned audit")],[new(owned,f.Time.Now,program,null,"Role assignment alone is not impartiality")]))),"audit_impartiality");
        var id=await ScheduleAsync(f);var start=Body(new DecideAudit(id,1,1,"Start","Actual assigned competent auditor begins work"));
        await JsonAsync(await f.Observer.CommandAsync(AuditApi+"/runs/"+id+"/start",start,"audit-original-start"));
        await f.Plant.Ok(f.Reviewer,"/api/competency/people/"+f.Plant.People["planning-observer"]+"/suspensions",new{code="AUDITOR",reason="Actual recorded competence suspension"});
        var failure=await f.Observer.CommandAsync(AuditApi+"/runs/"+id+"/start",start,"audit-original-start");var problem=await JsonAsync(failure,HttpStatusCode.UnprocessableEntity);Assert.Contains(problem.GetProperty("code").GetString(),new[]{"planning_source_changed","audit_not_competent"});
        var read=await ReadAsync(f.Reviewer,AuditApi+"/runs/"+id);Assert.Equal("Qualified",read.GetProperty("audit").GetProperty("competence")[0].GetProperty("qualifications")[0].GetProperty("status").GetString());
    }
    [Fact]
    public async Task Independently_approved_competence_must_match_exact_person_criteria_scope_and_validity()
    {
        await using var f=await SetupAsync(postgres,true,true);var person=f.Plant.People["planning-observer"];
        await RejectedAsync(await f.Plant.Try(f.Author,AuditApi+"/runs",Body(new ScheduleAudit(new("INTERNAL-AUDIT",1),"Actual selected scope",f.Time.Now,f.Time.Now.AddDays(1),[new(person,"Actual audit")],[new(person,f.Time.Now,new("INTERNAL-AUDIT",1),null,"Actual impartiality review")]))),"audit_not_competent");
        var content=new AssessAuditor(person,new("INTERNAL-AUDIT",1),QmsAuditCompetenceDecision.Competent,f.Time.Now.AddDays(-1),f.Time.Now.AddDays(30),"Demonstrated evidence assessment for actual material source-control","Actual observed evidence judgment","Limited to the approved source-control scope",Evidence);
        var created=await f.Plant.Ok(f.Author,AuditApi+"/competence",Body(content));var id=Guid.Parse(created.GetProperty("entityId").GetString()!);
        await RejectedAsync(await f.Plant.Try(f.Author,AuditApi+"/competence/"+id+"/approve",Body(new ApproveAuditorAssessment(id,1,"Self approval is prohibited"))),"separation_of_duties");
        await f.Plant.Ok(f.Reviewer,AuditApi+"/competence/"+id+"/approve",Body(new ApproveAuditorAssessment(id,1,"Independent actual competence assessment")));
        await ScheduleAsync(f,id);f.Time.Now=f.Time.Now.AddDays(31);
        var failed=await f.Plant.Try(f.Author,AuditApi+"/runs",Body(new ScheduleAudit(new("INTERNAL-AUDIT",1),"Expired assessment",f.Time.Now,f.Time.Now.AddDays(1),[new(person,"Actual audit")],[new(person,f.Time.Now,new("INTERNAL-AUDIT",1),id,"Recorded impartiality")])));await RejectedAsync(failed,"audit_not_competent");
    }
    [Fact]
    public async Task Finding_requires_human_adequacy_report_manifest_and_independent_approval_then_reopen_preserves_old_generation()
    {
        await using var f=await SetupAsync(postgres);var id=await ScheduleAsync(f);await f.Plant.Ok(f.Observer,AuditApi+"/runs/"+id+"/start",Body(new DecideAudit(id,1,1,"Start","Actual assigned auditor starts")));
        var finding=new RecordAuditFinding(id,2,1,"MATERIAL-CONTROL",QmsAuditFindingKind.Observation,"Actual document evidence observation","Actual independent source inspection",QmsAuditAdequacy.NotEvaluated,"A link alone is not evaluation","Selected evidence limits",false,Evidence,[],[]);
        await RejectedAsync(await f.Plant.Try(f.Observer,AuditApi+"/runs/"+id+"/findings",Body(finding)),"audit_adequacy");
        var created=await f.Plant.Ok(f.Observer,AuditApi+"/runs/"+id+"/findings",Body(finding with{Adequacy=QmsAuditAdequacy.Partial,AdequacyBasis="Qualified human finds evidence relevant but scope limited"}));var findingId=Guid.Parse(created.GetProperty("entityId").GetString()!);
        var report=new WriteAuditReport(id,3,1,"Actual report","Actual selected scope","Scope explicitly partial","Qualified human judgment remains partial",[],Evidence,
            [new("CHECK-MATERIAL","Actual selected evidence was independently examined",QmsAuditAdequacy.Partial,"Human concludes required scope is only partly supported","Other cohort evidence is unavailable",Evidence)]);
        await RejectedAsync(await f.Plant.Try(f.Observer,AuditApi+"/runs/"+id+"/report",Body(report)),"audit_report_manifest");
        await f.Plant.Ok(f.Observer,AuditApi+"/runs/"+id+"/report",Body(report with{FindingIds=[findingId]}));
        await f.Plant.Ok(f.Observer,AuditApi+"/runs/"+id+"/submitreport",Body(new DecideAudit(id,4,1,"SubmitReport","Actual report ready for review")));
        await RejectedAsync(await f.Plant.Try(f.Observer,AuditApi+"/runs/"+id+"/approvereport",Body(new DecideAudit(id,5,1,"ApproveReport","Self approval prohibited"))),"separation_of_duties");
        await f.Plant.Ok(f.Reviewer,AuditApi+"/runs/"+id+"/approvereport",Body(new DecideAudit(id,5,1,"ApproveReport","Actual independent partial evidence assessment")));
        await f.Plant.Ok(f.Reviewer,AuditApi+"/runs/"+id+"/close",Body(new DecideAudit(id,6,1,"Close","No required follow-up in this actual observation")));
        await f.Plant.Ok(f.Observer,AuditApi+"/runs/"+id+"/reopen",Body(new DecideAudit(id,7,1,"Reopen","New actual evidence warrants follow-up")));
        var read=await ReadAsync(f.Reviewer,AuditApi+"/runs/"+id);Assert.Equal(2,read.GetProperty("audit").GetProperty("generation").GetInt32());Assert.Single(read.GetProperty("findings").EnumerateArray());Assert.Equal(1,read.GetProperty("findings")[0].GetProperty("auditGeneration").GetInt32());Assert.Equal(System.Text.Json.JsonValueKind.Null,read.GetProperty("audit").GetProperty("report").ValueKind);
        await RejectedAsync(await f.Plant.Try(f.Reviewer,AuditApi+"/runs/"+id+"/close",Body(new DecideAudit(id,8,1,"Close","Old generation cannot close new audit"))),"capa_generation");
    }
}
