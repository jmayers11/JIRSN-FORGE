using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Planning;

namespace Forge.Integration.Tests;

public sealed class QmsPlanningRulesTests
{
    private static readonly Guid Owner=Guid.Parse("374272b2-7693-4d2f-91cc-67c560528e6a");
    private static readonly DateTimeOffset From=DateTimeOffset.Parse("2026-10-01T00:00:00Z",System.Globalization.CultureInfo.InvariantCulture);
    private static QmsMetricDefinitionContent Metric()=>new("Recorded scrap fraction",QmsMetricSourceKind.RecordedProductionReports,QmsRecordedProductionMeasure.ScrapFraction,"fraction","Recorded scrap at operation20","Recorded good plus scrap at operation20","Sum actual report quantities over the exact cohort","Two explicitly selected orders",[10001,10002],[20],false,From,From.AddDays(1),1000,"All recorded rows in the closed period for every required order/operation","Weekly");
    private static QmsRetentionScheduleContent Schedule()=>new("Exact controlled document revisions","Owner-approved contract record requirement",QmsRetentionTrigger.SourceCreatedAt,30,false,Owner,"Explicit contract basis","Independent organization decision","Record actual observation with retained evidence",["Engineering/DocumentRevision"]);
    private static QmsPlanningSourceSnapshot Source(string hash="actual-file-hash")=>new(new("Engineering","DocumentRevision","DOC","A"),"Engineering","DocumentRevision","DOC","A","immutable-owner-content",From,From,null,null,hash,["person:author"],[],false);
    private static void Reject(string expected,Action action)=>Assert.Equal(expected,Assert.Throws<CommandRejectedException>(action).Code);

    [Fact] public void Typed_content_has_exact_kind_and_no_implicit_second_section()
    {
        var valid=new QmsPlanningContent(MetricDefinition:Metric());QmsPlanningRules.Validate(QmsPlanningKind.MetricDefinition,valid);
        Reject("planning_content",()=>QmsPlanningRules.Validate(QmsPlanningKind.Objective,valid));
        Reject("planning_content",()=>QmsPlanningRules.Validate(QmsPlanningKind.MetricDefinition,valid with{RetentionSchedule=Schedule()}));
    }
    [Fact] public void Metric_requires_explicit_full_order_operation_period_unit_and_method_definition()
    {
        QmsPlanningRules.Metric(Metric());
        Reject("metric_source",()=>QmsPlanningRules.Metric(Metric() with{IncludeAllOperations=null}));
        Reject("metric_cohort",()=>QmsPlanningRules.Metric(Metric() with{OrderNumbers=[]}));
        Reject("metric_cohort",()=>QmsPlanningRules.Metric(Metric() with{OrderNumbers=[10001,10001]}));
        Reject("metric_cohort",()=>QmsPlanningRules.Metric(Metric() with{OperationSequences=[]}));
        Reject("metric_cohort",()=>QmsPlanningRules.Metric(Metric() with{IncludeAllOperations=true}));
        Reject("metric_unit",()=>QmsPlanningRules.Metric(Metric() with{Unit="%"}));
        Reject("planning_period",()=>QmsPlanningRules.Metric(Metric() with{PeriodTo=From}));
        Reject("planning_field",()=>QmsPlanningRules.Metric(Metric() with{DenominatorDefinition=""}));
        QmsPlanningRules.Metric(Metric() with{IncludeAllOperations=true,OperationSequences=[]});
    }
    [Fact] public void Recorded_and_unknown_baselines_remain_distinct_and_evidence_backed()
    {
        var objective=new QmsObjectiveContent("Reduce recorded operation20 scrap",new("METRIC",1),Owner,QmsBaselineStatus.Unknown,null,"No verified baseline exists",[],QmsTargetComparison.AtMost,0.05m,null,0,From,From.AddMonths(1),"Weekly","Investigate and verify causes","Assigned time",From.AddDays(14));
        QmsPlanningRules.Validate(QmsPlanningKind.Objective,new(Objective:objective));
        Reject("planning_baseline",()=>QmsPlanningRules.Validate(QmsPlanningKind.Objective,new(Objective:objective with{Baseline=0})));
        Reject("planning_baseline",()=>QmsPlanningRules.Validate(QmsPlanningKind.Objective,new(Objective:objective with{BaselineStatus=QmsBaselineStatus.Recorded,Baseline=0.08m})));
        QmsPlanningRules.Validate(QmsPlanningKind.Objective,new(Objective:objective with{BaselineStatus=QmsBaselineStatus.Recorded,Baseline=0.08m,BaselineEvidence=[new("Execution","ProductionReport","1")]}));
    }
    [Fact] public void Retention_schedule_needs_explicit_duration_or_indefinite_choice_and_actual_category()
    {
        QmsPlanningRules.Validate(QmsPlanningKind.RetentionSchedule,new(RetentionSchedule:Schedule()));
        Reject("retention_trigger",()=>QmsPlanningRules.Validate(QmsPlanningKind.RetentionSchedule,new(RetentionSchedule:Schedule() with{Indefinite=null})));
        Reject("retention_duration",()=>QmsPlanningRules.Validate(QmsPlanningKind.RetentionSchedule,new(RetentionSchedule:Schedule() with{DurationDays=null})));
        Reject("retention_duration",()=>QmsPlanningRules.Validate(QmsPlanningKind.RetentionSchedule,new(RetentionSchedule:Schedule() with{Indefinite=true})));
        Reject("retention_category",()=>QmsPlanningRules.Validate(QmsPlanningKind.RetentionSchedule,new(RetentionSchedule:Schedule() with{AllowedOwnerTypes=[]})));
        QmsPlanningRules.Validate(QmsPlanningKind.RetentionSchedule,new(RetentionSchedule:Schedule() with{Indefinite=true,DurationDays=null}));
    }
    [Fact] public void Missing_owner_trigger_stays_unknown_and_client_cannot_override_it()
    {
        Assert.Null(QmsRetentionService.Trigger(Schedule() with{Trigger=QmsRetentionTrigger.SourceClosedAt},Source(),null,0));
        Reject("retention_trigger",()=>QmsRetentionService.Trigger(Schedule(),Source(),From.AddDays(-60),1));
        Reject("retention_trigger",()=>QmsRetentionService.Trigger(Schedule() with{Trigger=QmsRetentionTrigger.HumanVerifiedExternalOccurrence},Source(),From,0));
        Assert.Equal(From,QmsRetentionService.Trigger(Schedule() with{Trigger=QmsRetentionTrigger.HumanVerifiedExternalOccurrence},Source(),From,1));
    }
    [Fact] public void Lifecycle_disallows_direct_effective_and_closed_cases_without_effectiveness_path()
    {
        Reject("planning_state",()=>QmsPlanningRules.Transition(QmsState.Draft,"Effective"));
        Reject("planning_state",()=>QmsPlanningWorkService.CaseState(QmsPlanningCaseState.Implementing,"Close"));
        Reject("planning_state",()=>QmsPlanningWorkService.CaseState(QmsPlanningCaseState.VerificationPending,"Accept"));
        Assert.Equal(QmsPlanningCaseState.EffectivenessPending,QmsPlanningWorkService.CaseState(QmsPlanningCaseState.VerificationPending,"Verify"));
        Assert.Equal(QmsPlanningCaseState.Reopened,QmsPlanningWorkService.CaseState(QmsPlanningCaseState.Closed,"Reopen"));
    }
    [Fact] public void Every_author_and_actual_evidence_contributor_is_excluded_from_independent_decision()
    {
        Reject("separation_of_duties",()=>QmsPlanningRules.Independent("person:earlier-editor",["person:author","person:earlier-editor"],[]));
        Reject("separation_of_duties",()=>QmsPlanningRules.Independent("person:author",[],[Source()]));
        QmsPlanningRules.Independent("person:independent-reviewer",["person:author"],[Source()]);
    }
    [Fact] public void Effectiveness_and_verification_need_explicit_human_determinations()
    {
        var review=new QmsEffectivenessReview(From,From.AddDays(1),"Compare recurrence evidence","No verified recurrence","Actual measured result",null,"No limitations observed","Continue review");
        Reject("planning_effectiveness",()=>QmsPlanningWorkService.ValidateAssessment(QmsPlanningKind.Change,QmsAssessmentKind.EffectivenessReview,new(EffectivenessReview:review)));
        QmsPlanningWorkService.ValidateAssessment(QmsPlanningKind.Change,QmsAssessmentKind.EffectivenessReview,new(EffectivenessReview:review with{Effective=false}));
        var verification=new QmsImplementationVerification([Guid.NewGuid()],"Approved task completion criteria","Actual task result",null,"No limitations observed");
        Reject("planning_verification",()=>QmsPlanningWorkService.ValidateAssessment(QmsPlanningKind.Change,QmsAssessmentKind.ImplementationVerification,new(ImplementationVerification:verification)));
    }
    [Fact] public void Manifest_identity_excludes_read_clock_but_includes_actual_source_version_holds_and_shared_owner_set()
    {
        var candidate=new QmsDispositionCandidate(Guid.Parse("72588eb7-6803-4079-8b2c-68c7a09eb3a0"),2,new("SCHEDULE",1),5,Source(),From.AddDays(30),true,[],[],["Engineering/DocumentRevision/DOC/A"]);
        var hash=QmsRetentionService.ManifestFingerprint([candidate]);
        Assert.Equal(hash,QmsRetentionService.ManifestFingerprint([candidate with{Subject=candidate.Subject with{CapturedAt=From.AddYears(1)}}]));
        Assert.NotEqual(hash,QmsRetentionService.ManifestFingerprint([candidate with{ScheduleVersion=6}]));
        Assert.NotEqual(hash,QmsRetentionService.ManifestFingerprint([candidate with{ActiveHoldIds=[Guid.NewGuid()],Eligible=false}]));
        Assert.NotEqual(hash,QmsRetentionService.ManifestFingerprint([candidate with{SharedFileOwnerIds=["Engineering/DocumentRevision/DOC/A","Procurement/TechnicalPackage/55/1"]}]));
    }
    [Fact] public void Canonical_engineering_document_keys_and_revisions_are_preserved()
    {
        QmsPlanningRules.Validate(QmsPlanningKind.EvaluationMethod,new(EvaluationMethod:new("Method",QmsEvaluationMethodKind.Qualitative,"Actual human method","Explicit criteria",[],"On change"),Documents:[new("QUALITY.METHOD","B.2")]));
    }
}
