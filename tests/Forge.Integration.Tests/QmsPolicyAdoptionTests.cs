using System.Net;
using System.Text.Json;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>Real approved policies/adoptions, no SQL-fabricated scope, approval, or source evidence.</summary>
[Collection(PostgresCollection.Name)]
public sealed class QmsPolicyAdoptionTests(PostgresFixture postgres)
{
    private static QmsLabelLifecycleRules Lifecycle()=>new(QmsActiveCopyMode.SingleActive,1,true,true,true,true,true,
        QmsLabelConfirmation.OperatorObservedPrintingAndAttachment,["material","order"],[new("MATERIAL",1)],[QmsLabelSymbology.Code128],true,true,true);
    private static QmsPolicyContent Labels(Guid owner)=>new(QmsPolicyKind.LabelCopy,"Each logical active copy is governed; physical duplicates are not silently detectable",owner,
        "Recorded supervisor briefing","On process change",true,new(LabelCopy:new(true,1,true,Lifecycle())));
    private static object Decision(int version)=>new{expectedVersion=version,note="Reviewed actual sources and explicit applicability",reason="Independent governance decision"};
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text=await response.Content.ReadAsStringAsync();Assert.True(response.IsSuccessStatusCode,$"{response.StatusCode}: {text}");
        using var document=JsonDocument.Parse(text);return document.RootElement.Clone();
    }
    private static async Task Rejected(SlittingPlant.Plant plant,HttpClient actor,string path,object body,string code,HttpStatusCode status=HttpStatusCode.UnprocessableEntity)
    {
        using var response=await plant.Try(actor,path,body);Assert.Equal(status,response.StatusCode);Assert.Contains(code,await response.Content.ReadAsStringAsync(),StringComparison.Ordinal);
    }
    private static async Task<string> Rows(ForgeApp app,string table)
    {
        // Fixed test-owned table names only. Normal commands create all governance/source facts.
        await using var connection=new NpgsqlConnection(app.ConnectionString);await connection.OpenAsync();
        await using var command=new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(qms_policy_snapshot_row) ORDER BY to_jsonb(qms_policy_snapshot_row)::text)::text,'[]') FROM {table} qms_policy_snapshot_row",connection);
        return(string)(await command.ExecuteScalarAsync())!;
    }
    private static async Task<QmsAdoptedPolicy?> Current(ForgeApp app,string subject,QmsPolicyPurpose purpose,QmsPolicyContext context,bool required=false)
    {
        var principal=await app.Services.GetRequiredService<KernelReadStore>().ResolvePrincipalBySubjectAsync(subject,CancellationToken.None);Assert.NotNull(principal);
        await using var uow=await app.Services.GetRequiredService<IKernelUnitOfWorkFactory>().BeginAsync(CancellationToken.None);
        var reader=app.Services.GetRequiredService<IQmsPolicyReader>();
        return required?await reader.RequireAsync(uow,principal,purpose,context,app.Services.GetRequiredService<TimeProvider>().GetUtcNow(),CancellationToken.None)
            :await reader.TryAsync(uow,principal,purpose,context,app.Services.GetRequiredService<TimeProvider>().GetUtcNow(),CancellationToken.None);
    }

    [Fact]
    public async Task Only_effective_adoption_establishes_controls_and_withdrawn_history_never_falls_back_to_unadopted()
    {
        await using var app=new ForgeApp(await postgres.CreateDatabaseAsync(),configureBuilder:b=>b.UseSetting(QmsService.ConfigurationKey,"EXAMPLE-ORG"));
        var(plant,_)=await SlittingPlant.BuildEngineeredAsync(app);
        await QmsPolicyScenario.AdoptAsync(plant,"COPY-POLICY","COPY-SLIT","COPY.PROCEDURE",Labels(plant.People["eng"]),QmsPolicyPurpose.LabelCopy,new(WorkCenterCode:"SLIT-WC"));
        var consumer=await plant.Person("policy-consumer","QmsPolicyConsumer");
        var slit=new QmsPolicyContext("PLANT1",WorkCenterCode:"SLIT-WC");
        var paint=new QmsPolicyContext("PLANT1",WorkCenterCode:"PAINT-WC");
        var current=await Current(app,"policy-consumer",QmsPolicyPurpose.LabelCopy,slit,true);Assert.NotNull(current);
        Assert.Equal(("COPY-SLIT",1,5,"COPY-POLICY",1,5), (current.AdoptionCode,current.AdoptionRevision,current.AdoptionVersion,current.PolicyCode,current.PolicyRevision,current.PolicyVersion));
        Assert.Equal(QmsActiveCopyMode.SingleActive,current.Rules.LabelCopy!.Lifecycle!.ActiveCopyMode);
        Assert.Equal(QmsLabelConfirmation.OperatorObservedPrintingAndAttachment,current.Rules.LabelCopy.Lifecycle.RequiredConfirmation);
        Assert.Contains(current.Sources,s=>s.Permission==Permissions.QmsRead&&s.Resource.Type=="QmsRevision");
        Assert.Contains(current.Sources,s=>s.Permission==Permissions.EngineeringRead&&s.Resource.Type=="DocumentRevision");
        var saved=await Rows(app,"quality.qms_policy_adoption_revision");
        var audit=await Rows(app,"kernel.audit_log");
        Assert.Equal(current.Fingerprint,(await Current(app,"policy-consumer",QmsPolicyPurpose.LabelCopy,slit,true))!.Fingerprint);
        Assert.Equal(saved,await Rows(app,"quality.qms_policy_adoption_revision"));Assert.Equal(audit,await Rows(app,"kernel.audit_log"));
        Assert.Null(await Current(app,"policy-consumer",QmsPolicyPurpose.LabelCopy,paint));
        var content=new QmsPolicyAdoptionContent(QmsPolicyPurpose.LabelCopy,"COPY-POLICY",1,new(WorkCenterCode:"PAINT-WC"),null,null,null,"Explicit scope proposed by the organization");
        await plant.Ok(plant.Engineer,"/api/quality/qms/adoptions",new{code="COPY-PAINT",facilityCode="PLANT1",markings="None",content,reason="Draft applicability proposal"});
        Assert.Null(await Current(app,"policy-consumer",QmsPolicyPurpose.LabelCopy,paint));
        Assert.Equal("governed_policy_not_adopted",(await Assert.ThrowsAsync<CommandRejectedException>(()=>Current(app,"policy-consumer",QmsPolicyPurpose.LabelCopy,paint,true))).Code);
        var path="/api/quality/qms/adoptions/COPY-PAINT/revisions/1";
        await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["eng"]}/roles",new{role=Roles.QmsReviewer,reason="An author still cannot review their own binding"});
        await plant.Ok(plant.Engineer,path+"/submit",Decision(1));
        await Rejected(plant,plant.Engineer,path+"/review",Decision(2),"separation_of_duties");
        await plant.Ok(plant.Approver,path+"/review",Decision(2));await plant.Ok(plant.Approver,path+"/approve",Decision(3));
        Assert.Null(await Current(app,"policy-consumer",QmsPolicyPurpose.LabelCopy,paint));
        await Rejected(plant,plant.Engineer,path+"/edit",new{expectedVersion=4,content,reason="Approved binding cannot change"},"immutable_revision");
        await plant.Ok(plant.Approver,path+"/effective",Decision(4));
        Assert.Equal("COPY-PAINT",(await Current(app,"policy-consumer",QmsPolicyPurpose.LabelCopy,paint,true))!.AdoptionCode);
        var withdrawn=await Json(await plant.Approver.CommandAsync(path+"/withdraw",Decision(5),"withdraw-adoption"));
        Assert.Equal("governed_policy_unavailable",(await Assert.ThrowsAsync<CommandRejectedException>(()=>Current(app,"policy-consumer",QmsPolicyPurpose.LabelCopy,paint))).Code);
        var historyBefore=await Rows(app,"quality.qms_policy_adoption_transition");var auditBefore=await Rows(app,"kernel.audit_log");
        Assert.Equal(withdrawn.GetRawText(),(await Json(await plant.Approver.CommandAsync(path+"/withdraw",Decision(5),"withdraw-adoption"))).GetRawText());
        Assert.Equal(historyBefore,await Rows(app,"quality.qms_policy_adoption_transition"));Assert.Equal(auditBefore,await Rows(app,"kernel.audit_log"));
        var history=await Json(await plant.As("policy-consumer").GetAsync(path));
        Assert.Equal(new[]{"Create","Submit","Review","Approve","Effective","Withdraw"},history.GetProperty("history").EnumerateArray().Select(h=>h.GetProperty("action").GetString()));
        Assert.Equal(consumer,(await Json(await plant.As("policy-consumer").GetAsync("/api/kernel/me"))).GetProperty("personId").GetGuid());
        var withdrawnHistory=history.GetRawText();
        await Rejected(plant,plant.Engineer,"/api/quality/qms/adoptions/COPY-PAINT/revisions",new{expectedVersion=5,content,reason="Stale adoption version"},"stale_version");
        await plant.Ok(plant.Engineer,"/api/quality/qms/adoptions/COPY-PAINT/revisions",new{expectedVersion=6,content,reason="New explicit governed successor"});
        Assert.Equal("governed_policy_unavailable",(await Assert.ThrowsAsync<CommandRejectedException>(()=>Current(app,"policy-consumer",QmsPolicyPurpose.LabelCopy,paint))).Code);
        await QmsPolicyScenario.EffectiveAsync(plant,"/api/quality/qms/adoptions/COPY-PAINT/revisions/2");
        var successor=await Current(app,"policy-consumer",QmsPolicyPurpose.LabelCopy,paint,true);Assert.NotNull(successor);
        Assert.Equal((2,5),(successor.AdoptionRevision,successor.AdoptionVersion));
        Assert.Equal(withdrawnHistory,(await Json(await plant.As("policy-consumer").GetAsync(path))).GetRawText());
        await plant.Ok(plant.Engineer,"/api/quality/qms/adoptions/COPY-PAINT/revisions",new{expectedVersion=5,content=content with{Basis="Independently reviewed revised applicability basis"},reason="Preserve historical effective applicability"});
        await QmsPolicyScenario.EffectiveAsync(plant,"/api/quality/qms/adoptions/COPY-PAINT/revisions/3");
        var superseded=await Json(await plant.As("policy-consumer").GetAsync("/api/quality/qms/adoptions/COPY-PAINT/revisions/2"));
        var effective=await Json(await plant.As("policy-consumer").GetAsync("/api/quality/qms/adoptions/COPY-PAINT/revisions/3"));
        Assert.Equal("Superseded",superseded.GetProperty("state").GetString());
        Assert.Equal(superseded.GetProperty("effectiveTo").GetString(),effective.GetProperty("effectiveFrom").GetString());
        Assert.Equal(content.Basis,superseded.GetProperty("content").GetProperty("basis").GetString());
        var newest=await Current(app,"policy-consumer",QmsPolicyPurpose.LabelCopy,paint,true);Assert.NotNull(newest);
        Assert.Equal((3,5),(newest.AdoptionRevision,newest.AdoptionVersion));
    }

    [Fact]
    public async Task Reader_rechecks_current_roles_programs_sources_and_ambiguity_in_the_consuming_transaction()
    {
        await using var app=new ForgeApp(await postgres.CreateDatabaseAsync(),configureBuilder:b=>b.UseSetting(QmsService.ConfigurationKey,"EXAMPLE-ORG"));
        var(plant,_)=await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("policy-programs",Roles.ProgramManager);
        var program=Guid.Parse((await plant.Ok(plant.As("policy-programs"),"/api/programs/",new{code="GOV-A",name="Actual policy source A"})).GetProperty("entityId").GetString()!);
        await QmsPolicyScenario.AdoptAsync(plant,"COPY-A","COPY-A-BINDING","COPY.A.PROCEDURE",Labels(plant.People["eng"]),QmsPolicyPurpose.LabelCopy,new(WorkCenterCode:"SLIT-WC"),program);
        var person=await plant.Person("governed-consumer","QmsPolicyConsumer");
        await plant.Ok(plant.Admin,$"/api/kernel/people/{person}/programs",new{programId=program,reason="Independent current source access"});
        var context=new QmsPolicyContext("PLANT1",program,WorkCenterCode:"SLIT-WC");
        Assert.Equal("governed_policy_scope_unknown",(await Assert.ThrowsAsync<CommandRejectedException>(()=>Current(app,"governed-consumer",QmsPolicyPurpose.LabelCopy,context with{ProgramId=null}))).Code);
        var cached=await app.Services.GetRequiredService<KernelReadStore>().ResolvePrincipalBySubjectAsync("governed-consumer",CancellationToken.None);Assert.NotNull(cached);
        var reader=app.Services.GetRequiredService<IQmsPolicyReader>();
        var accepted=await Current(app,"governed-consumer",QmsPolicyPurpose.LabelCopy,context,true);Assert.NotNull(accepted);
        var canonical=await Rows(app,"quality.qms_policy_adoption_revision");var evidence=await Rows(app,"quality.qms_policy_adoption_source");
        await plant.Ok(plant.Admin,$"/api/kernel/people/{person}/programs/{program}/revoke",new{reason="Withdraw source membership before another consume/retry"});
        await using(var uow=await app.Services.GetRequiredService<IKernelUnitOfWorkFactory>().BeginAsync(CancellationToken.None))
            await Assert.ThrowsAsync<ForgeAuthorizationException>(()=>reader.RequireAsync(uow,cached,QmsPolicyPurpose.LabelCopy,context,app.Services.GetRequiredService<TimeProvider>().GetUtcNow(),CancellationToken.None));
        Assert.Equal(canonical,await Rows(app,"quality.qms_policy_adoption_revision"));Assert.Equal(evidence,await Rows(app,"quality.qms_policy_adoption_source"));
        await plant.Ok(plant.Admin,$"/api/kernel/people/{person}/programs",new{programId=program,reason="Restore explicit current access"});
        Assert.Equal(accepted.Fingerprint,(await Current(app,"governed-consumer",QmsPolicyPurpose.LabelCopy,context,true))!.Fingerprint);
        await plant.Ok(plant.Admin,$"/api/kernel/people/{person}/roles/QmsPolicyConsumer/revoke",new{reason="Withdraw policy read authority"});
        await using(var uow=await app.Services.GetRequiredService<IKernelUnitOfWorkFactory>().BeginAsync(CancellationToken.None))
            await Assert.ThrowsAsync<ForgeAuthorizationException>(()=>reader.RequireAsync(uow,cached,QmsPolicyPurpose.LabelCopy,context,app.Services.GetRequiredService<TimeProvider>().GetUtcNow(),CancellationToken.None));
        await plant.Ok(plant.Admin,$"/api/kernel/people/{person}/roles",new{role="QmsPolicyConsumer",reason="Restore explicit read-only consumer role"});
        // A second normal approved binding cannot silently be preferred because it permits a looser scope.
        var content=new QmsPolicyAdoptionContent(QmsPolicyPurpose.LabelCopy,"COPY-A",1,new(),null,null,null,"Overlapping facility-wide binding deliberately exercised for fail-closed ambiguity");
        await plant.Ok(plant.Engineer,"/api/quality/qms/adoptions",new{code="OVERLAPPING",facilityCode="PLANT1",programId=program,markings="None",content,reason="Actual ambiguity acceptance"});
        await QmsPolicyScenario.EffectiveAsync(plant,"/api/quality/qms/adoptions/OVERLAPPING/revisions/1");
        Assert.Equal("governed_policy_ambiguous",(await Assert.ThrowsAsync<CommandRejectedException>(()=>Current(app,"governed-consumer",QmsPolicyPurpose.LabelCopy,context,true))).Code);
        await plant.Ok(plant.Approver,"/api/quality/qms/adoptions/OVERLAPPING/revisions/1/withdraw",Decision(5));
        Assert.Equal("governed_policy_unavailable",(await Assert.ThrowsAsync<CommandRejectedException>(()=>Current(app,"governed-consumer",QmsPolicyPurpose.LabelCopy,context,true))).Code);
    }

    [Fact]
    public async Task Execution_rule_uses_exact_effective_process_and_human_risk_basis_and_source_withdrawal_blocks_current_consumption()
    {
        await using var app=new ForgeApp(await postgres.CreateDatabaseAsync(),configureBuilder:b=>b.UseSetting(QmsService.ConfigurationKey,"EXAMPLE-ORG"));
        var(plant,_)=await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["eng"]}/roles",new{role=Roles.QmsAuthor,reason="Explicit process author"});
        foreach(var role in new[]{Roles.QmsReviewer,Roles.QmsApprover})
            await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["approver"]}/roles",new{role,reason="Independent process governance"});
        var process=new QmsRevisionReference("SLITTING-PROCESS",1);
        await plant.Ok(plant.Engineer,"/api/quality/qms/records",new{code=process.Code,kind="Process",facilityCode="PLANT1",markings="None",
            content=new QmsContent(Process:new("Actual slitting process",["Released routing","Actual roll"],["Cut roll","Recorded acceptance evidence"],plant.People["eng"],[],["SLIT-WC","Qualified present crew"],["Material mismatch assessment"],[new("Evidence completeness","Review actual recorded sources","Manufacturing operation evidence","On revision change")])),
            reason="Human process definition for this source context"});
        await QmsPolicyScenario.EffectiveAsync(plant,"/api/quality/qms/records/"+process.Code+"/revisions/1");
        var rule=new QmsOperationScanRule("EXACT-SLITTING",new(WorkCenterCode:"SLIT-WC",MachineCode:"SLITTER-A",OperationSequence:1),process,"MATERIAL-MISMATCH",
            [QmsRequiredScanKind.Operation,QmsRequiredScanKind.Material,QmsRequiredScanKind.ControlledInstruction],false,true,true);
        var presentation=new QmsExecutionPresentationRules(120,true,true,true,QmsProviderAssurance.StepUp,[rule]);
        var policy=new QmsPolicyContent(QmsPolicyKind.Execution,"Human-selected operation presentation controls",plant.People["eng"],"Recorded briefing","On process revision",true,
            new(Execution:new(true,true,true,true,presentation)));
        await QmsPolicyScenario.AdoptAsync(plant,"EXECUTION","EXECUTION-SLIT","SLIT.CONTROLS",policy,QmsPolicyPurpose.Execution,
            new(WorkCenterCode:"SLIT-WC",MachineCode:"SLITTER-A",OperationSequence:1),process:process,riskClassCode:"MATERIAL-MISMATCH",riskBasis:"Approved actual process assessment of wrong material use");
        await plant.Person("execution-consumer","QmsPolicyConsumer");
        var context=new QmsPolicyContext("PLANT1",WorkCenterCode:"SLIT-WC",MachineCode:"SLITTER-A",OperationSequence:1);
        var accepted=await Current(app,"execution-consumer",QmsPolicyPurpose.Execution,context,true);Assert.NotNull(accepted);
        Assert.Equal(process,accepted.Process);Assert.Equal("MATERIAL-MISMATCH",accepted.RiskClassCode);
        Assert.Equal("Approved actual process assessment of wrong material use",accepted.RiskBasis);
        Assert.NotNull(accepted.OperationRule);Assert.Equal(rule.Code,accepted.OperationRule.Code);
        Assert.Equal(rule.Selector,accepted.OperationRule.Selector);Assert.Equal(process,accepted.OperationRule.Process);
        Assert.Equal(rule.RequiredScans,accepted.OperationRule.RequiredScans);
        Assert.Equal((120,QmsProviderAssurance.StepUp,false),(accepted.Rules.Execution!.Presentation!.SessionExpirySeconds,accepted.Rules.Execution.Presentation.ProviderAssurance,accepted.OperationRule!.AllowManualSelection));
        Assert.Contains(accepted.Sources,s=>s.Permission==Permissions.QmsRead&&s.Resource.Id=="SLITTING-PROCESS/1");
        Assert.Null(await Current(app,"execution-consumer",QmsPolicyPurpose.Execution,context with{OperationSequence=2}));
        Assert.Equal("governed_policy_scope_unknown",(await Assert.ThrowsAsync<CommandRejectedException>(()=>Current(app,"execution-consumer",QmsPolicyPurpose.Execution,context with{OperationSequence=null}))).Code);
        Assert.Equal("governed_policy_scope_unknown",(await Assert.ThrowsAsync<CommandRejectedException>(()=>Current(app,"execution-consumer",QmsPolicyPurpose.Execution,context with{MachineCode=null}))).Code);
        Assert.Null(await Current(app,"execution-consumer",QmsPolicyPurpose.Execution,context with{MachineCode="SLITTER-B"}));
        await plant.Ok(plant.Admin,"/api/kernel/roles",new{role="OnlyGovernanceRead",displayName="QMS source read without Engineering source authority",permissions=new[]{Permissions.QmsRead},reason="Actual mixed-source access boundary"});
        await plant.Person("no-document-consumer","OnlyGovernanceRead");
        var documentDenial=await Assert.ThrowsAsync<ForgeAuthorizationException>(()=>Current(app,"no-document-consumer",QmsPolicyPurpose.Execution,context,true));
        Assert.NotNull(documentDenial.OwningResource);Assert.Equal("DocumentRevision",documentDenial.OwningResource.Type);
        Assert.Equal("SLIT.CONTROLS/A",documentDenial.OwningResource.Id);
        var stored=await Rows(app,"quality.qms_policy_adoption_revision");
        await plant.Ok(plant.Approver,"/api/quality/qms/records/SLITTING-PROCESS/revisions/1/withdraw",Decision(5));
        Assert.Equal("governed_policy_source_unavailable",(await Assert.ThrowsAsync<CommandRejectedException>(()=>Current(app,"execution-consumer",QmsPolicyPurpose.Execution,context))).Code);
        Assert.Equal(stored,await Rows(app,"quality.qms_policy_adoption_revision"));
        var historic=await Json(await plant.As("execution-consumer").GetAsync("/api/quality/qms/adoptions/EXECUTION-SLIT/revisions/1"));
        Assert.Equal("SLITTING-PROCESS",historic.GetProperty("content").GetProperty("process").GetProperty("code").GetString());
        Assert.Equal(1,historic.GetProperty("content").GetProperty("process").GetProperty("revision").GetInt32());
    }
}
