using System.Globalization;
using System.Text.Json;
using Forge.Kernel.Security;
using Forge.Quality.Audit;
using Forge.Quality.Contracts;
using static Forge.Integration.Tests.QmsPlanningScenario;

namespace Forge.Integration.Tests;

internal static class AuditCapaScenario
{
    public const string AuditApi="/api/quality/audits",CapaApi="/api/quality/capa-governance";
    public static object Body(object data)=>new{data,reason="Actual attributed organization decision based on controlled evidence"};
    public static object DecisionData(int expectedVersion,string action="",int generation=1)=>new{expectedVersion,generation,action,note="Actual independent human assessment and limits"};
    public static QmsPlanningEvidenceReference[] Evidence=>[new("Engineering","DocumentRevision","AUDIT.EVIDENCE","A")];
    public static async Task<Flow> SetupAsync(PostgresFixture postgres,bool program=true,bool independentCompetence=false)
    {
        var f=await CreateAsync(postgres);
        foreach(var subject in new[]{"eng","approver","planning-observer"})
            foreach(var role in new[]{"AuditPlanner","InternalAuditor","AuditApprover",Roles.TrainingCoordinator,Roles.Certifier,Roles.QualityEngineer,Roles.QualityManager})
                await f.Plant.Ok(f.Plant.Admin,"/api/kernel/people/"+f.Plant.People[subject]+"/roles",new{role,reason="Explicit audit/CAPA authority; this role assignment is not competence"});
        await DocumentAsync(f,"AUDIT.BASIS");await DocumentAsync(f,"AUDIT.EVIDENCE");
        var process=new QmsContent(Process:new("Actual source-control and evidence process",["Recorded material discrepancy"],["Controlled action evidence"],f.Plant.People["eng"],[],["Qualified audit time"],["Repeated source mismatch"],[new("Source mismatch monitoring","Review actual events","Owner records","On recurrence")]),Documents:[new("AUDIT.BASIS","A")]);
        await f.Plant.Ok(f.Author,"/api/quality/qms/records",new{code="AUDIT-PROCESS",kind="Process",facilityCode="PLANT1",markings="None",content=process,reason="Actual approved organization process"});
        var path="/api/quality/qms/records/AUDIT-PROCESS/revisions/1";await f.Plant.Ok(f.Author,path+"/submit",Decision(1));await f.Plant.Ok(f.Reviewer,path+"/review",Decision(2));await f.Plant.Ok(f.Reviewer,path+"/approve",Decision(3));await f.Plant.Ok(f.Reviewer,path+"/effective",Decision(4));
        await f.Plant.Ok(f.Author,"/api/competency/qualification-types",new{code="AUDITOR",name="Actual auditor evidence assessment qualification",kind="General",validityDays=30});
        foreach(var subject in new[]{"eng","planning-observer"})
        {
            var person=f.Plant.People[subject];await f.Plant.Ok(f.Author,"/api/competency/people/"+person+"/training",new{code="AUDITOR"});
            await f.Plant.Ok(f.Reviewer,"/api/competency/people/"+person+"/certifications",new{code="AUDITOR",evidence="Actual independently assessed evidence judgment",reason="Independent competence assessment completed"});
        }
        if(program)
        {
            var content=Program(f,independentCompetence);
            await f.Plant.Ok(f.Author,AuditApi+"/programs",Body(new DefineAuditProgram("INTERNAL-AUDIT","PLANT1",null,DataMarking.None,content)));
            var url=AuditApi+"/programs/INTERNAL-AUDIT/revisions/1";await f.Plant.Ok(f.Author,url+"/submit",Body(DecisionData(1)));await f.Plant.Ok(f.Reviewer,url+"/review",Body(DecisionData(2)));await f.Plant.Ok(f.Reviewer,url+"/approve",Body(DecisionData(3)));await f.Plant.Ok(f.Reviewer,url+"/effective",Body(DecisionData(4)));
        }
        return f;
    }
    public static QmsAuditProgramContent Program(Flow f,bool independentCompetence=false)=>new("Actual source-control audit","Selected source-control process and material discrepancy records","Owner chosen October audit","On recurring mismatch",f.Plant.People["eng"],
        [new("AUDIT-PROCESS",1)],[new("MATERIAL-CONTROL","Actual material source control","Compare actual material work with exact approved procedure",[],[new("AUDIT.BASIS","A")],["DocumentRevision"],"Qualified human evaluates relevance, reliability and completeness")],
        [new("CHECK-MATERIAL","MATERIAL-CONTROL","Was actual approved source-control followed?","Read actual controlled instruction and actual owner evidence")],
        new(true,["AUDITOR"],independentCompetence,"Demonstrated evidence assessment for actual material source-control","30-day organization assessment"),
        new(true,true,true,"Exclude actual process owners and evidence contributors"),"Actual independently verified corrective actions","Approved report plus independently verified required follow-up",[],[]);
    public static async Task<Guid> ScheduleAsync(Flow f,Guid? assessment=null,string? replayKey=null)
    {
        var person=f.Plant.People["planning-observer"];var program=new QmsRevisionReference("INTERNAL-AUDIT",1);
        var command=new ScheduleAudit(program,"Actual selected source-control audit",f.Time.Now,f.Time.Now.AddDays(1),[new(person,"Evaluate actual material source-control evidence")],[new(person,f.Time.Now,program,assessment,"No process ownership or evidence contribution")]);
        var result=replayKey is null?await f.Plant.Ok(f.Author,AuditApi+"/runs",Body(command)):await JsonAsync(await f.Author.CommandAsync(AuditApi+"/runs",Body(command),replayKey));return Guid.Parse(result.GetProperty("entityId").GetString()!);
    }
    public static CreateGovernedCapa Capa(Flow f,long? existing=null)=>new("Repeated material discrepancy","PLANT1",null,DataMarking.None,new("AUDIT-PROCESS",1),"Actual repeat part/lot source mismatch",f.Plant.People["eng"],DateOnly.FromDateTime(f.Time.Now.UtcDateTime.AddDays(14)),"Investigate actual matching controls","Evaluate actual observations and contrary evidence","No repeated same-lot cross-part selection in actual chosen review window","Review actual repeated discrepancy records",[],Evidence,existing);
    public static async Task<long> CapaAsync(Flow f,long? existing=null)
    {var result=await f.Plant.Ok(f.Author,CapaApi,Body(Capa(f,existing)));return long.Parse(result.GetProperty("entityId").GetString()!,CultureInfo.InvariantCulture);}
    public static QmsCapaInvestigationContent Investigation(bool determined)=>new("Actual review of selected material/source evidence","Recorded cause comparison",[],determined?"The organization determined an omitted exact-part validation from actual controlled evidence":null,
        determined?"Human selected supported cause after reviewing alternatives":"Cause remains unknown; evidence is incomplete","Require exact physical part and lot validation at selection","Review related material source-control path","Human evaluated repeated mismatch risk","Verify actual implementation and actual review window",Evidence);
    public static Task<JsonElement> ReadCapa(Flow f,long id)=>ReadAsync(f.Reviewer,CapaApi+"/"+id);
    public static async Task<int> CaseVersion(Flow f,long id)=>(await ReadCapa(f,id)).GetProperty("capa").GetProperty("version").GetInt32();
    public static async Task PlanAsync(Flow f,long id)
    {
        await f.Plant.Ok(f.Author,CapaApi+"/"+id+"/investigations",Body(new WriteCapaInvestigation(id,await CaseVersion(f,id),1,null,null,Investigation(true))));
        var path=CapaApi+"/"+id+"/investigations/1";
        await f.Plant.Ok(f.Author,path+"/submit",Body(new DecideCapaInvestigation(id,1,1,1,"Submit","Actual investigation ready for review")));
        await f.Plant.Ok(f.Reviewer,path+"/review",Body(new DecideCapaInvestigation(id,1,1,2,"Review","Independent human reviewed actual supporting and contrary evidence")));
        await f.Plant.Ok(f.Reviewer,path+"/approve",Body(new DecideCapaInvestigation(id,1,1,3,"Approve","Independent human confirmed cause and plan")));
    }
    public static async Task<Guid> VerifiedActionAsync(Flow f,long id,QmsCorrectiveActionKind kind=QmsCorrectiveActionKind.CorrectiveAction)
    {
        var created=await f.Plant.Ok(f.Author,CapaApi+"/"+id+"/actions",Body(new CreateCapaAction(id,await CaseVersion(f,id),1,kind,"Actual exact part/lot source validation",f.Plant.People["eng"],f.Time.Now.AddDays(2),"Actual assigned owner time","Observe actual exact part/lot matching")));
        var actionId=Guid.Parse(created.GetProperty("entityId").GetString()!);
        await f.Plant.Ok(f.Author,CapaApi+"/actions/"+actionId+"/complete",Body(new DecideCapaAction(actionId,1,1,"Complete","Actual owner implemented source-control validation",Evidence)));
        await f.Plant.Ok(f.Reviewer,CapaApi+"/actions/"+actionId+"/verify",Body(new DecideCapaAction(actionId,2,1,"Verify","Independent human verified actual implementation evidence",Evidence)));
        return actionId;
    }
    public static async Task<Guid> ApprovedEffectivenessAsync(Flow f,long id,Guid action,QmsEffectivenessResult result)
    {
        f.Time.Now=f.Time.Now.AddDays(2);
        var record=await ReadCapa(f,id);var c=record.GetProperty("capa");
        var assessment=new RecordCapaEffectiveness(id,c.GetProperty("version").GetInt32(),1,f.Time.Now.AddDays(-1),f.Time.Now,"Review actual selected source observations",c.GetProperty("effectivenessCriteria").GetString()!,"Actual recorded results for the human-approved window",result,"Limited to actual selected owner evidence","Human reviewed actual recurrence records","Human declared follow-up based on recorded result",Evidence,[action]);
        var created=await f.Plant.Ok(f.Author,CapaApi+"/"+id+"/effectiveness",Body(assessment));var evaluation=Guid.Parse(created.GetProperty("entityId").GetString()!);
        var path=CapaApi+"/effectiveness/"+evaluation;
        await f.Plant.Ok(f.Author,path+"/submit",Body(new DecideCapaEffectiveness(evaluation,1,1,"Submit","Actual observed result ready for review")));
        await f.Plant.Ok(f.Reviewer,path+"/review",Body(new DecideCapaEffectiveness(evaluation,2,1,"Review","Independent actual evidence review")));
        await f.Plant.Ok(f.Reviewer,path+"/approve",Body(new DecideCapaEffectiveness(evaluation,3,1,"Approve","Independent human effect decision")));
        return evaluation;
    }
}
