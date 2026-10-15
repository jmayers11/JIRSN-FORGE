using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Quality.Contracts;
using static Forge.Quality.Planning.QmsPlanningRules;

namespace Forge.Quality.Audit;

public static class AuditCapaRules
{
    public static void Program(QmsAuditProgramContent p)
    {
        Require(p is not null,"audit_content","Supply the organization's audit program.");
        foreach(var text in new[]{p.Title,p.Scope,p.ScheduleBasis,p.Frequency,p.FindingFollowUpCriteria,p.ClosureCriteria})Text(text,"Audit program decision");
        Person(p.OwnerPersonId);List(p.Processes,"Actual processes",200);Require(p.Processes.Count>0,"audit_process","Identify exact controlled processes.");foreach(var r in p.Processes)Reference(r);
        List(p.Criteria,"Criteria",200);Require(p.Criteria.Count>0&&p.Criteria.Select(x=>x.Code).Distinct(StringComparer.Ordinal).Count()==p.Criteria.Count,"audit_criteria","Declare distinct audit criteria.");
        foreach(var c in p.Criteria)
        {
            BusinessCode.Require(c.Code,"Criterion");Text(c.Name,"Criterion name");Text(c.EvaluationMethod,"Evaluation method");Text(c.EvidenceAdequacyCriteria,"Human evidence adequacy criteria");
            List(c.Requirements,"Actual requirements",200);List(c.Documents,"Controlled criteria documents",200);List(c.RequiredEvidenceKinds,"Evidence kinds",200);
            Require(c.Requirements.Count+c.Documents.Count>0,"audit_criteria_source","An audit criterion needs an actual requirement or exact controlled document revision.");
            foreach(var r in c.Requirements)Reference(r);foreach(var d in c.Documents){Text(d.DocumentNumber,"Document",40);Text(d.Revision,"Revision",40);}foreach(var kind in c.RequiredEvidenceKinds)Text(kind,"Evidence kind",80);
        }
        List(p.Checklist,"Checklist",500);Require(p.Checklist.Count>0&&p.Checklist.Select(x=>x.Code).Distinct().Count()==p.Checklist.Count,"audit_checklist","Declare a controlled checklist.");
        foreach(var c in p.Checklist){BusinessCode.Require(c.Code,"Checklist");Require(p.Criteria.Any(x=>x.Code==c.CriterionCode),"audit_checklist","A checklist entry must cite an actual declared criterion.");Text(c.Question,"Question");Text(c.RequiredEvidenceBasis,"Required evidence basis");}
        Require(p.Competence is not null&&p.Impartiality is not null,"audit_rules","Explicit competence and impartiality rules are required.");
        var competence=p.Competence;Require(competence.RequireFormalQualifications is not null&&competence.RequireIndependentCompetenceAssessment is not null,"audit_competence","Declare both competence choices.");
        Require(competence.RequireFormalQualifications==true||competence.RequireIndependentCompetenceAssessment==true,"audit_competence","At least one actual competence proof is required.");
        List(competence.QualificationCodes,"Qualification codes",100);Require(competence.RequireFormalQualifications!=true||competence.QualificationCodes.Count>0,"audit_competence","Identify actual formal qualifications.");
        Require(competence.QualificationCodes.Distinct().Count()==competence.QualificationCodes.Count,"audit_competence","Qualification codes must be distinct.");foreach(var code in competence.QualificationCodes)BusinessCode.Require(code,"Qualification");
        Text(competence.CompetenceCriteria,"Competence criteria");Text(competence.ReassessmentInterval,"Reassessment interval");
        var i=p.Impartiality;Require(i.ProhibitAuditingOwnedProcesses is not null&&i.ProhibitAuditingContributedEvidence is not null&&i.RequireIndependentReportApproval is not null,"audit_impartiality","Declare every impartiality choice.");Text(i.ConflictAssessmentCriteria,"Conflict criteria");
        List(p.Documents,"Documents",200);List(p.Evidence,"Evidence",500);
    }
    public static bool QualifiedAsOf(string status,DateTimeOffset? certified,DateTimeOffset? expires,DateTimeOffset asOf)=>
        status=="Qualified"&&certified is{} from&&from<=asOf&&(expires is null||asOf<expires);
    public static void Finding(QmsAuditProgramContent p,string criterion,QmsAuditFindingKind kind,QmsAuditAdequacy adequacy,string statement,string observation,string basis,string limits,bool? followUp)
    {
        Require(p.Criteria.Any(x=>x.Code==criterion),"audit_criterion","Use a criterion from this approved program.");
        Require(Enum.IsDefined(kind)&&Enum.IsDefined(adequacy)&&adequacy!=QmsAuditAdequacy.NotEvaluated,"audit_adequacy","A qualified human must explicitly evaluate evidence adequacy.");
        Text(statement,"Finding");Text(observation,"Actual observation");Text(basis,"Evidence adequacy basis");Text(limits,"Evidence limitations");
        Require(followUp is not null,"audit_follow_up","Declare whether follow-up is required.");
        Require(kind!=QmsAuditFindingKind.Nonconformity||followUp==true,"audit_follow_up","A nonconformity requires corrective follow-up.");
    }
    public static void Investigation(QmsCapaInvestigationContent c)
    {
        Require(c is not null,"capa_investigation","Supply the actual investigation.");
        foreach(var value in new[]{c.ActualInvestigation,c.Method,c.CauseDecisionBasis,c.ActionPlan,c.SystemicScope,c.RiskOpportunityImpact,c.VerificationCriteria})Text(value,"Investigation evidence and decision");
        List(c.Proposals,"Cause proposals",100);List(c.Evidence,"Investigation evidence",500);
        Require(c.Evidence.Count>0,"capa_evidence","Record actual investigation evidence.");
        Require(c.HumanDeterminedCause is null||!string.IsNullOrWhiteSpace(c.HumanDeterminedCause),"capa_cause","Unknown cause is null, never a placeholder.");
        if(c.HumanDeterminedCause is{} cause)Text(cause,"Human determined cause");
        foreach(var p in c.Proposals){Text(p.Description,"Proposed cause");Text(p.SupportingBasis,"Supporting basis");Text(p.ContraryEvidence,"Contrary evidence review");List(p.Evidence,"Proposal evidence",500);Require(p.Evidence.Count>0,"capa_evidence","A proposed cause needs evidence.");}
    }
    public static void AdequacyEvidence(QmsAuditCriterion criterion,QmsAuditAdequacy adequacy,IReadOnlyList<QmsPlanningSourceSnapshot> sources)
    {
        Require(Enum.IsDefined(adequacy)&&adequacy!=QmsAuditAdequacy.NotEvaluated,"audit_adequacy","A qualified human must actually assess adequacy.");
        if(adequacy==QmsAuditAdequacy.Adequate)Require(sources.Count>0&&criterion.RequiredEvidenceKinds.All(kind=>sources.Any(source=>source.OwnerType==kind)),
            "audit_evidence_incomplete","An adequate judgment needs actual evidence of each declared required kind. Record partial or unavailable evidence honestly.");
    }
    public static void CloseEffectiveness(QmsCapaEffectiveness e)=>Require(e.State==QmsState.Approved&&e.Result==QmsEffectivenessResult.Effective&&e.Evidence.Count>0,"capa_effectiveness","Only a current independently approved effective result can close the CAPA.");
    public static void ActionManifest(QmsCapaEffectiveness e,IReadOnlyList<QmsCapaAction> actions)=>
        Require(e.ActionManifest.Count==actions.Count&&e.ActionManifest.All(saved=>actions.Any(actual=>actual.Id==saved.Id&&actual.Version==saved.Version&&Forge.Kernel.Serialization.CanonicalJson.Fingerprint(actual)==saved.Fingerprint)),
            "capa_action_manifest","An implemented action changed after this effectiveness assessment; record a fresh actual assessment.");
    public static void Generation(int actual,int supplied)=>Require(actual==supplied,"capa_generation","This evidence belongs to an earlier investigation generation.");
}
