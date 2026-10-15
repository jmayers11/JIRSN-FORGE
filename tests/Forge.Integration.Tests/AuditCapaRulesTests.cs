using Forge.Kernel.Commands;
using Forge.Kernel.Serialization;
using Forge.Quality.Audit;
using Forge.Quality.Contracts;

namespace Forge.Integration.Tests;

public sealed class AuditCapaRulesTests
{
    private static readonly DateTimeOffset At=new(2026,10,14,15,0,0,TimeSpan.Zero);
    private static QmsAuditProgramContent Program()=>new("Explicit audit","Selected production process","Owner-approved schedule","On process change",Guid.NewGuid(),
        [new("PROCESS",1)],[new("C1","Controlled work","Read exact source and compare actual work",[],[new("INSTRUCTION","A")],["ProductionReport"],"Qualified human assessment of relevance, completeness and reliability")],
        [new("Q1","C1","Is actual instruction followed?","Actual required source work and competence")],
        new(true,["AUDITOR"],false,"Demonstrated audit evidence judgment","Annual organization assessment"),
        new(true,true,true,"Exclude process owners/evidence contributors"),"Independent verified findings","Approved report and verified follow-up",[],[]);
    [Fact] public void Program_rejects_implicit_competence_or_impartiality_decisions()
    {
        var p=Program();
        Assert.Equal("audit_competence",Assert.Throws<CommandRejectedException>(()=>AuditCapaRules.Program(p with{Competence=p.Competence with{RequireFormalQualifications=null}})).Code);
        Assert.Equal("audit_impartiality",Assert.Throws<CommandRejectedException>(()=>AuditCapaRules.Program(p with{Impartiality=p.Impartiality with{RequireIndependentReportApproval=null}})).Code);
    }
    [Fact] public void Role_or_checklist_without_actual_competence_proof_is_insufficient()
    {
        var p=Program();
        Assert.Equal("audit_competence",Assert.Throws<CommandRejectedException>(()=>AuditCapaRules.Program(p with{Competence=p.Competence with{RequireFormalQualifications=false,RequireIndependentCompetenceAssessment=false}})).Code);
        Assert.Equal("audit_criteria_source",Assert.Throws<CommandRejectedException>(()=>AuditCapaRules.Program(p with{Criteria=[p.Criteria[0] with{Documents=[]}]})).Code);
    }
    [Theory]
    [InlineData("Qualified",-1,1,true)]
    [InlineData("Qualified",1,10,false)]
    [InlineData("Qualified",-1,0,false)]
    [InlineData("Revoked",-1,1,false)]
    [InlineData("Suspended",-1,1,false)]
    public void Competence_requires_actual_certification_by_as_of_and_exclusive_expiry(string status,int from,int to,bool expected)=>
        Assert.Equal(expected,AuditCapaRules.QualifiedAsOf(status,At.AddDays(from),At.AddDays(to),At));
    [Fact] public void Missing_certification_timestamp_is_not_qualification()=>
        Assert.False(AuditCapaRules.QualifiedAsOf("Qualified",null,null,At));
    [Fact] public void Adequate_judgment_requires_actual_declared_kind_and_partial_gaps_are_not_hidden()
    {
        var criterion=Program().Criteria[0];
        Assert.Equal("audit_evidence_incomplete",Assert.Throws<CommandRejectedException>(()=>AuditCapaRules.AdequacyEvidence(criterion,QmsAuditAdequacy.Adequate,[])).Code);
        AuditCapaRules.AdequacyEvidence(criterion,QmsAuditAdequacy.Partial,[]);
        AuditCapaRules.AdequacyEvidence(criterion,QmsAuditAdequacy.Unavailable,[]);
    }
    [Fact] public void Finding_links_do_not_satisfy_human_adequacy_or_nonconformity_follow_up()
    {
        var p=Program();
        Assert.Equal("audit_adequacy",Assert.Throws<CommandRejectedException>(()=>AuditCapaRules.Finding(p,"C1",QmsAuditFindingKind.Nonconformity,QmsAuditAdequacy.NotEvaluated,"Actual defect","Recorded event","Link exists","Known limits",true)).Code);
        Assert.Equal("audit_follow_up",Assert.Throws<CommandRejectedException>(()=>AuditCapaRules.Finding(p,"C1",QmsAuditFindingKind.Nonconformity,QmsAuditAdequacy.Adequate,"Actual defect","Recorded event","Human assessment","Known limits",false)).Code);
    }
    [Fact] public void Investigation_unknown_cause_is_null_and_proposals_need_actual_evidence()
    {
        var c=new QmsCapaInvestigationContent("Actual investigation","Recorded method",[],null,"Cause not determined from evidence yet","Proposed investigation plan","Actual systemic scope","Human risk review","Exact verification criteria",[new("Engineering","DocumentRevision","EVIDENCE","A")]);
        AuditCapaRules.Investigation(c);
        Assert.Equal("capa_cause",Assert.Throws<CommandRejectedException>(()=>AuditCapaRules.Investigation(c with{HumanDeterminedCause=""})).Code);
        Assert.Equal("capa_evidence",Assert.Throws<CommandRejectedException>(()=>AuditCapaRules.Investigation(c with{Proposals=[new("Possible mix-up","Actual supported premise","Contrary evidence reviewed",[])]})).Code);
    }
    [Fact] public void Earlier_generation_and_reverified_same_action_cannot_reuse_old_effectiveness()
    {
        Assert.Equal("capa_generation",Assert.Throws<CommandRejectedException>(()=>AuditCapaRules.Generation(2,1)).Code);
        var action=new QmsCapaAction(Guid.NewGuid(),501,1,3,QmsCorrectiveActionKind.CorrectiveAction,QmsCorrectiveActionState.Verified,"Control same-lot part selection",Guid.NewGuid(),At,"Owner time","Exact matching verified","Observed correct implementation","person:author",At,"person:owner",At,"person:verifier",At,[],[]);
        var e=new QmsCapaEffectiveness(Guid.NewGuid(),501,1,4,QmsState.Approved,At.AddDays(-2),At,"Actual method","Actual criteria","Actual results",QmsEffectivenessResult.Effective,"Limits","Recurrence evidence","Human closure",[new(new("Quality","Capa","501"),"Quality","Capa","501",null,"fingerprint",At,At,null,null,null,[],[],false)],[action.Id],"person:author","person:reviewer","person:reviewer",[],[new(action.Id,action.Version,CanonicalJson.Fingerprint(action))]);
        AuditCapaRules.ActionManifest(e,[action]);
        Assert.Equal("capa_action_manifest",Assert.Throws<CommandRejectedException>(()=>AuditCapaRules.ActionManifest(e,[action with{Version=5}])).Code);
        Assert.Equal("capa_effectiveness",Assert.Throws<CommandRejectedException>(()=>AuditCapaRules.CloseEffectiveness(e with{Result=QmsEffectivenessResult.Inconclusive})).Code);
    }
}
