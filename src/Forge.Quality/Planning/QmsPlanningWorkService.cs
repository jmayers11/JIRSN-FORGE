using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using static Forge.Quality.Planning.QmsPlanningRules;
using static Forge.Quality.Planning.QmsPlanningService;

namespace Forge.Quality.Planning;

public sealed record CreatePlanningAssessment(string Code,int DefinitionRevision,QmsAssessmentKind Kind,QmsPlanningAssessmentContent Content,IReadOnlyList<QmsPlanningEvidenceReference> Evidence,Guid? CaseId=null,int? ExpectedCaseVersion=null);
public sealed record DecidePlanningAssessment(Guid Id,int ExpectedVersion,string Action,string Note);
public sealed record CreatePlanningTask(string Code,int DefinitionRevision,string Title,Guid OwnerPersonId,DateTimeOffset DueAt,string Resources,string CompletionCriteria);
public sealed record DecidePlanningTask(Guid Id,int ExpectedVersion,string Action,string Result,IReadOnlyList<QmsPlanningEvidenceReference> Evidence);
public sealed record CreatePlanningCase(string Code,int DefinitionRevision);
public sealed record DecidePlanningCase(Guid Id,int ExpectedVersion,string Action,Guid? AssessmentId,string Note);

/// <summary>Actual owned action and decision records. Definition approval alone never claims implementation or effectiveness.</summary>
public sealed class QmsPlanningWorkService(QmsPlanningService planning,IKernelUnitOfWorkFactory factory,
    IQmsPlanningEvidenceReader evidence,CommandPipeline pipeline)
{
    public async Task<object> ReadChildrenAsync(ForgePrincipal principal,string code,int revision,CancellationToken ct)
    {
        await using var uow=await factory.BeginSnapshotAsync(ct);var record=await planning.RecordAsync(uow,code,ct);
        await planning.AuthorityAsync(uow,principal,record,ct); Revision(record,revision); var store=uow.Store<IQmsPlanningLookup>();
        var assessments=await store.AssessmentsAsync(code,revision,ct); var tasks=await store.TasksAsync(code,revision,ct);
        foreach(var assessment in assessments) await planning.CurrentEvidenceAsync(uow,principal,assessment.Sources,false,ct);
        foreach(var task in tasks) await planning.CurrentEvidenceAsync(uow,principal,task.Evidence,false,ct);
        return new{assessments,tasks,cases=(await store.CasesAsync(code,ct)).Where(x=>x.DefinitionRevision==revision)};
    }
    public Task<CommandResult> CreateAssessmentAsync(CreatePlanningAssessment c,CommandContext context,CancellationToken ct)=>
        planning.ExecuteAsync(nameof(CreatePlanningAssessment),c,c.Code,Permissions.QmsManage,context,async(uow,record,now)=>
        {
            var definition=await ActiveAsync(uow,context.Principal,record,c.DefinitionRevision,now,ct); ValidateAssessment(record.Kind,c.Kind,c.Content);
            QmsPlanningCase? boundCase=null;
            if(c.CaseId is{} caseId)
            {
                boundCase=await uow.Store<IQmsPlanningLookup>().FindCaseAsync(caseId,ct)??throw Unavailable();
                Require(boundCase.Code==record.Code&&boundCase.DefinitionRevision==c.DefinitionRevision&&boundCase.State==AssessmentPhase(c.Kind)&&boundCase.Version==c.ExpectedCaseVersion,"planning_case_context","Create this assessment for the same actual case and captured version at its current decision phase.");
            }
            var sources=await CaptureAsync(uow,context.Principal,c.Evidence,ct); sources=definition.Sources.Concat(sources).DistinctBy(x=>x.Reference).ToArray();
            QmsMetricMeasurement? measurement=null;
            if(c.Content.ObjectiveEvaluation is{} evaluation)
            {
                var reference=definition.Content.Objective!.Metric;var metric=await planning.RecordAsync(uow,reference.Code,ct);
                var metricDefinition=await ActiveAsync(uow,context.Principal,metric,reference.Revision,now,ct);
                var d=metricDefinition.Content.MetricDefinition!;
                Require(evaluation.From>=d.PeriodFrom&&evaluation.To<=d.PeriodTo,"metric_period","Evaluate within the approved metric definition period.");
                Require(evaluation.From>=definition.Content.Objective.PeriodFrom&&evaluation.To<=definition.Content.Objective.PeriodTo,"metric_period","Evaluate within the approved objective period.");
                if(d.SourceKind==QmsMetricSourceKind.RecordedProductionReports)
                {
                    Require(evaluation.HumanActual is null,"metric_actual","Recorded measures come from actual owner reports, not a submitted scalar.");
                    measurement=await evidence.MeasureAsync(uow,context.Principal,d,evaluation.From,evaluation.To,ct);
                    sources=sources.Concat(measurement.Evidence.Select(x=>x.Source)).DistinctBy(x=>x.Reference).ToArray();
                }
                else {Require(evaluation.HumanActual is not null&&c.Evidence.Count>0,"metric_actual","An attributed human actual needs explicit value, method and evidence.");Text(evaluation.HumanMeasurementBasis,"Human measurement basis");}
            }
            if(c.Content.RiskEvaluation is{} risk)
            {
                var methodRef=definition.Content.RiskOpportunity!.EvaluationMethod;
                Require(risk.MethodCode==methodRef.Code&&risk.MethodRevision==methodRef.Revision,"planning_method","Use the exact approved evaluation method.");
                var method=Revision(await planning.RecordAsync(uow,methodRef.Code,ct),methodRef.Revision).Content.EvaluationMethod!;
                Require(method.Kind!=QmsEvaluationMethodKind.Ordinal||method.Scale.Any(x=>x.Code==risk.ScaleCode),"planning_scale","Use an explicit scale entry from the exact method.");
                Require(method.Kind!=QmsEvaluationMethodKind.Numeric||risk.NumericResult is not null,"planning_result","A numeric method requires its actual numeric evaluation.");
            }
            if(c.Content.ImplementationVerification is{} implementation)
            {
                await VerifiedTasksAsync(uow,record,c.DefinitionRevision,implementation.TaskIds,ct);
                foreach(var taskId in implementation.TaskIds) sources=sources.Append(await evidence.ResolveAsync(uow,context.Principal,new("Quality","QmsPlanningTask",taskId.ToString()),true,ct)).ToArray();
            }
            if(c.Content.EffectivenessReview is not null)
            {
                var tasks=await uow.Store<IQmsPlanningLookup>().TasksAsync(record.Code,c.DefinitionRevision,ct);
                Require(tasks.Count>0&&tasks.All(x=>x.State==QmsTaskState.Verified),"planning_tasks","Effectiveness evaluation follows actual independently verified implementation tasks.");
                foreach(var task in tasks)sources=sources.Append(await evidence.ResolveAsync(uow,context.Principal,new("Quality","QmsPlanningTask",task.Id.ToString()),true,ct)).ToArray();
            }
            Require(sources.Count>0||c.Kind==QmsAssessmentKind.ChangeImpact,"planning_evidence","Link actual assessment evidence.");
            var id=ForgeIds.New(pipeline.Time);
            var next=new QmsPlanningAssessment(id,record.Code,c.DefinitionRevision,1,c.Kind,QmsAssessmentState.Draft,c.Content,measurement,context.Actor,context.Actor,null,null,now,sources,
                Append([],"Create",1,"","Draft","Human assessment draft",context,now),boundCase?.Id,boundCase?.Generation,boundCase?.Version);
            await uow.Store<IQmsPlanningStore>().SaveAssessmentAsync(next,null,ct);
            return planning.Outcome(record,"QmsPlanningAssessment",id.ToString(),1,"Create","Draft",null,next,context,sources);
        },ct,async(uow,record,now)=>{await ActiveAsync(uow,context.Principal,record,c.DefinitionRevision,now,ct);if(c.CaseId is{} id){var actualCase=await uow.Store<IQmsPlanningLookup>().FindCaseAsync(id,ct)??throw Unavailable();Require(actualCase.Code==record.Code&&actualCase.DefinitionRevision==c.DefinitionRevision&&actualCase.Version==c.ExpectedCaseVersion&&actualCase.State==AssessmentPhase(c.Kind),"planning_case_context","This assessment create/retry is detached from the actual case decision phase.");}await CaptureAsync(uow,context.Principal,c.Evidence,ct);});

    public async Task<CommandResult> DecideAssessmentAsync(DecidePlanningAssessment c,CommandContext context,CancellationToken ct)
    {
        var saved=await FindAssessmentAsync(context.Principal,c.Id,ct);
        var permission=c.Action switch {"Submit"=>Permissions.QmsManage,"Review"=>Permissions.QmsReview,"Approve"=>Permissions.QmsApprove,"Withdraw"=>Permissions.QmsPublish,_=>throw new CommandRejectedException("planning_action","Unknown assessment action.")};
        return await planning.ExecuteAsync("Assessment"+c.Action,c,saved.Code,permission,context,async(uow,record,now)=>
        {
            var old=await uow.Store<IQmsPlanningLookup>().FindAssessmentAsync(c.Id,ct)??throw Unavailable();Version(old.Version,c.ExpectedVersion);Text(c.Note,"Decision note");
            await ActiveAsync(uow,context.Principal,record,old.DefinitionRevision,now,ct); await planning.CurrentEvidenceAsync(uow,context.Principal,old.Sources,true,ct);
            await AssessmentCaseAsync(uow,old,ct);
            var state=c.Action switch {"Submit" when old.State==QmsAssessmentState.Draft=>QmsAssessmentState.Submitted,"Review" when old.State==QmsAssessmentState.Submitted=>QmsAssessmentState.Reviewed,"Approve" when old.State==QmsAssessmentState.Reviewed=>QmsAssessmentState.Approved,"Withdraw" when old.State!=QmsAssessmentState.Withdrawn=>QmsAssessmentState.Withdrawn,_=>throw new CommandRejectedException("planning_state","This assessment action is unavailable.")};
            if(c.Action is "Review" or "Approve") Independent(context.Actor,new[]{old.CreatedBy,old.LastEditedBy}.Concat(old.History.Where(x=>x.Action is "Create" or "Edit").Select(x=>x.Actor)),old.Sources);
            if(old.Content.ImplementationVerification is{} implementation) await VerifiedTasksAsync(uow,record,old.DefinitionRevision,implementation.TaskIds,ct);
            var next=old with {Version=old.Version+1,State=state,ReviewedBy=c.Action=="Review"?context.Actor:old.ReviewedBy,ApprovedBy=c.Action=="Approve"?context.Actor:old.ApprovedBy,
                History=Append(old.History,c.Action,old.Version+1,old.State.ToString(),state.ToString(),c.Note,context,now)};
            await uow.Store<IQmsPlanningStore>().SaveAssessmentAsync(next,old.Version,ct);
            return planning.Outcome(record,"QmsPlanningAssessment",next.Id.ToString(),next.Version,c.Action,state.ToString(),old,next,context,next.Sources);
        },ct,async(uow,record,now)=>{var current=await uow.Store<IQmsPlanningLookup>().FindAssessmentAsync(c.Id,ct)??throw Unavailable();await ActiveAsync(uow,context.Principal,record,current.DefinitionRevision,now,ct);await AssessmentCaseAsync(uow,current,ct);await planning.CurrentEvidenceAsync(uow,context.Principal,current.Sources,true,ct);});
    }
    public Task<CommandResult> CreateTaskAsync(CreatePlanningTask c,CommandContext context,CancellationToken ct)=>
        planning.ExecuteAsync(nameof(CreatePlanningTask),c,c.Code,Permissions.QmsManage,context,async(uow,record,now)=>
        {
            var definition=await ActiveAsync(uow,context.Principal,record,c.DefinitionRevision,now,ct);Text(c.Title,"Task title");Text(c.Resources,"Task resources");Text(c.CompletionCriteria,"Task completion criteria");Instant(c.DueAt);
            var assignee=await uow.People.LoadPrincipalAsync(new PersonId(c.OwnerPersonId),ct);
            Require(assignee is{IsActive:true},"planning_owner","Select an active registered task owner.");await planning.AuthorityAsync(uow,assignee!,record,ct);
            var id=ForgeIds.New(pipeline.Time);var next=new QmsPlanningTask(id,record.Code,c.DefinitionRevision,1,QmsTaskState.Open,c.Title,c.OwnerPersonId,c.DueAt,c.Resources,c.CompletionCriteria,context.Actor,now,null,null,null,null,"",[],Append([],"Create",1,"","Open","Actual owned task",context,now));
            await uow.Store<IQmsPlanningStore>().SaveTaskAsync(next,null,ct);
            return planning.Outcome(record,"QmsPlanningTask",id.ToString(),1,"Create","Open",null,next,context,definition.Sources);
        },ct,async(uow,record,now)=>{await ActiveAsync(uow,context.Principal,record,c.DefinitionRevision,now,ct);});
    public async Task<CommandResult> DecideTaskAsync(DecidePlanningTask c,CommandContext context,CancellationToken ct)
    {
        await using var read=await factory.BeginSnapshotAsync(ct);var saved=await read.Store<IQmsPlanningLookup>().FindTaskAsync(c.Id,ct)??throw Unavailable();
        return await planning.ExecuteAsync("Task"+c.Action,c,saved.Code,c.Action=="Verify"?Permissions.QmsApprove:c.Action=="Withdraw"?Permissions.QmsPublish:Permissions.QmsManage,context,async(uow,record,now)=>
        {
            var old=await uow.Store<IQmsPlanningLookup>().FindTaskAsync(c.Id,ct)??throw Unavailable();Version(old.Version,c.ExpectedVersion);Text(c.Result,"Result and human basis");
            var definition=await ActiveAsync(uow,context.Principal,record,old.DefinitionRevision,now,ct); var sources=old.Evidence.Concat(await CaptureAsync(uow,context.Principal,c.Evidence,ct)).DistinctBy(x=>x.Reference).ToArray();
            sources=(await planning.CurrentEvidenceAsync(uow,context.Principal,sources,true,ct)).ToArray();
            var state=c.Action switch {"Complete" when old.State is QmsTaskState.Open or QmsTaskState.Reopened=>QmsTaskState.Completed,"Verify" when old.State==QmsTaskState.Completed=>QmsTaskState.Verified,"Reopen" when old.State is QmsTaskState.Completed or QmsTaskState.Verified=>QmsTaskState.Reopened,"Withdraw" when old.State!=QmsTaskState.Withdrawn=>QmsTaskState.Withdrawn,_=>throw new CommandRejectedException("planning_state","The task action is unavailable.")};
            if(c.Action=="Complete") {Require(context.Principal.PersonId.Value==old.OwnerPersonId,"planning_task_owner","The assigned owner records task completion.");Require(c.Evidence.Count>0,"planning_evidence","Task completion needs actual source evidence.");}
            if(c.Action=="Verify") Independent(context.Actor,new[]{old.CreatedBy,"person:"+old.OwnerPersonId,old.CompletedBy??""},sources);
            var next=old with {Version=old.Version+1,State=state,Result=c.Result,Evidence=sources,
                CompletedBy=c.Action=="Complete"?context.Actor:old.CompletedBy,CompletedAt=c.Action=="Complete"?now:old.CompletedAt,
                VerifiedBy=c.Action=="Verify"?context.Actor:c.Action=="Reopen"?null:old.VerifiedBy,VerifiedAt=c.Action=="Verify"?now:c.Action=="Reopen"?null:old.VerifiedAt,
                History=Append(old.History,c.Action,old.Version+1,old.State.ToString(),state.ToString(),c.Result,context,now)};
            await uow.Store<IQmsPlanningStore>().SaveTaskAsync(next,old.Version,ct);return planning.Outcome(record,"QmsPlanningTask",next.Id.ToString(),next.Version,c.Action,state.ToString(),old,next,context,sources.Concat(definition.Sources));
        },ct,async(uow,record,now)=>{var current=await uow.Store<IQmsPlanningLookup>().FindTaskAsync(c.Id,ct)??throw Unavailable();await ActiveAsync(uow,context.Principal,record,current.DefinitionRevision,now,ct);await planning.CurrentEvidenceAsync(uow,context.Principal,current.Evidence,false,ct);await CaptureAsync(uow,context.Principal,c.Evidence,ct);});
    }
    public Task<CommandResult> CreateCaseAsync(CreatePlanningCase c,CommandContext context,CancellationToken ct)=>
        planning.ExecuteAsync(nameof(CreatePlanningCase),c,c.Code,Permissions.QmsManage,context,async(uow,record,now)=>
        {
            await ActiveAsync(uow,context.Principal,record,c.DefinitionRevision,now,ct);
            Require(record.Kind is QmsPlanningKind.RiskOpportunity or QmsPlanningKind.Objective or QmsPlanningKind.Change or QmsPlanningKind.Improvement,"planning_case","Operational cases require a risk, objective, change or improvement definition.");
            var id=ForgeIds.New(pipeline.Time);var next=new QmsPlanningCase(id,record.Code,c.DefinitionRevision,1,QmsPlanningCaseState.Identified,context.Actor,now,null,Append([],"Create",1,"","Identified","Operational case identified",context,now));
            await uow.Store<IQmsPlanningStore>().SaveCaseAsync(next,null,ct);return planning.Outcome(record,"QmsPlanningCase",id.ToString(),1,"Create","Identified",null,next,context,[]);
        },ct,async(uow,record,now)=>{await ActiveAsync(uow,context.Principal,record,c.DefinitionRevision,now,ct);});
    public async Task<CommandResult> DecideCaseAsync(DecidePlanningCase c,CommandContext context,CancellationToken ct)
    {
        await using var read=await factory.BeginSnapshotAsync(ct);var saved=await read.Store<IQmsPlanningLookup>().FindCaseAsync(c.Id,ct)??throw Unavailable();
        return await planning.ExecuteAsync("Case"+c.Action,c,saved.Code,c.Action is "Accept" or "Close"?Permissions.QmsApprove:c.Action=="Withdraw"?Permissions.QmsPublish:Permissions.QmsManage,context,async(uow,record,now)=>
        {
            var old=await uow.Store<IQmsPlanningLookup>().FindCaseAsync(c.Id,ct)??throw Unavailable();Version(old.Version,c.ExpectedVersion);Text(c.Note,"Case decision");
            await ActiveAsync(uow,context.Principal,record,old.DefinitionRevision,now,ct);
            var state=CaseState(old.State,c.Action);QmsPlanningAssessment? assessment=null;
            if(c.AssessmentId is{} id)
            {
                assessment=await uow.Store<IQmsPlanningLookup>().FindAssessmentAsync(id,ct)??throw Unavailable();
                Require(assessment.Code==record.Code&&assessment.DefinitionRevision==old.DefinitionRevision&&assessment.State==QmsAssessmentState.Approved,"planning_assessment","Use an independently approved assessment for this exact definition.");
                Require(assessment.CaseId==old.Id&&assessment.CaseGeneration==old.Generation,"planning_case_context","Use an assessment bound to this actual case generation; reopened cases require a new current assessment.");
                Require(c.Action=="Close"?old.LastApprovedAssessmentId==assessment.Id:assessment.CaseVersion==old.Version,"planning_case_context","The assessment belongs to this case's exact current decision phase.");
                await planning.CurrentEvidenceAsync(uow,context.Principal,assessment.Sources,true,ct);
            }
            if(c.Action=="Plan") Require(assessment is not null && assessment.Kind==RequiredAssessment(record.Kind),"planning_assessment","Action planning requires the exact approved risk, objective, change-impact or improvement assessment.");
            if(c.Action=="Verify") Require(assessment?.Content.ImplementationVerification?.Satisfied==true,"planning_verification","Implementation verification requires approved satisfied criteria and actual verified tasks.");
            if(c.Action is "Accept" or "Close")
            {
                Require(assessment?.Content.EffectivenessReview?.Effective==true,"planning_effectiveness","Acceptance and closure require an independently approved effective result.");
                var tasks=await uow.Store<IQmsPlanningLookup>().TasksAsync(record.Code,old.DefinitionRevision,ct);Require(tasks.Count>0&&tasks.All(x=>x.State==QmsTaskState.Verified),"planning_tasks","Every required owned task must be independently verified before closure.");
                Independent(context.Actor,new[]{old.CreatedBy}.Concat(tasks.SelectMany(x=>new[]{x.CreatedBy,"person:"+x.OwnerPersonId,x.CompletedBy??""})),assessment!.Sources);
            }
            var next=old with {Version=old.Version+1,State=state,Generation=c.Action=="Reopen"?old.Generation+1:old.Generation,LastApprovedAssessmentId=c.Action=="Reopen"?null:assessment?.Id??old.LastApprovedAssessmentId,
                History=Append(old.History,c.Action,old.Version+1,old.State.ToString(),state.ToString(),c.Note,context,now)};
            await uow.Store<IQmsPlanningStore>().SaveCaseAsync(next,old.Version,ct);return planning.Outcome(record,"QmsPlanningCase",next.Id.ToString(),next.Version,c.Action,state.ToString(),old,next,context,assessment?.Sources??[]);
        },ct,async(uow,record,now)=>{await ActiveAsync(uow,context.Principal,record,saved.DefinitionRevision,now,ct);if(c.AssessmentId is{} id){var current=await uow.Store<IQmsPlanningLookup>().FindAssessmentAsync(id,ct)??throw Unavailable();var actualCase=await uow.Store<IQmsPlanningLookup>().FindCaseAsync(c.Id,ct)??throw Unavailable();Require(current.Code==record.Code&&current.DefinitionRevision==saved.DefinitionRevision&&current.State==QmsAssessmentState.Approved,"planning_assessment","The exact supporting assessment must remain independently approved.");Require(current.CaseId==actualCase.Id&&current.CaseGeneration==actualCase.Generation,"planning_case_context","The supporting assessment belongs to an earlier/different case generation.");await planning.CurrentEvidenceAsync(uow,context.Principal,current.Sources,true,ct);}});
    }
    internal async Task<QmsPlanningRevision> ActiveAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsPlanningRecord record,int revision,DateTimeOffset now,CancellationToken ct)
    {
        var exact=Revision(record,revision);Require(InEffect(exact,now),"planning_source_not_effective","The exact governing definition is no longer effective.");
        var currentSources=await planning.SourcesAsync(uow,principal,record.Code,record.Kind,exact.Content,true,now,ct);return exact with {Sources=currentSources};
    }
    private async Task<QmsPlanningAssessment> FindAssessmentAsync(ForgePrincipal principal,Guid id,CancellationToken ct)
    {
        await using var uow=await factory.BeginSnapshotAsync(ct);var assessment=await uow.Store<IQmsPlanningLookup>().FindAssessmentAsync(id,ct)??throw Unavailable();
        await planning.AuthorityAsync(uow,principal,await planning.RecordAsync(uow,assessment.Code,ct),ct);await planning.CurrentEvidenceAsync(uow,principal,assessment.Sources,false,ct);return assessment;
    }
    internal async Task<IReadOnlyList<QmsPlanningSourceSnapshot>> CaptureAsync(IKernelUnitOfWork uow,ForgePrincipal principal,IReadOnlyList<QmsPlanningEvidenceReference> references,CancellationToken ct)
    {
        List(references,"Actual evidence references",500);
        var result=new List<QmsPlanningSourceSnapshot>(); foreach(var r in references.Distinct()){EvidenceReference(r);result.Add(await evidence.ResolveAsync(uow,principal,r,true,ct));}
        await planning.CurrentEvidenceAsync(uow,principal,result,false,ct);return result;
    }
    private static async Task VerifiedTasksAsync(IKernelUnitOfWork uow,QmsPlanningRecord record,int revision,IReadOnlyList<Guid> ids,CancellationToken ct)
    {
        Require(ids.Count>0&&ids.Distinct().Count()==ids.Count,"planning_tasks","Identify the actual distinct implementation tasks.");
        var all=await uow.Store<IQmsPlanningLookup>().TasksAsync(record.Code,revision,ct);
        Require(all.Count>0&&all.All(x=>x.State==QmsTaskState.Verified)&&ids.Order().SequenceEqual(all.Select(x=>x.Id).Order()),"planning_tasks","The implementation assessment must cover every independently verified task of this exact definition.");
    }
    private static async Task AssessmentCaseAsync(IKernelUnitOfWork uow,QmsPlanningAssessment assessment,CancellationToken ct)
    {
        if(assessment.CaseId is not{} id)return;
        var current=await uow.Store<IQmsPlanningLookup>().FindCaseAsync(id,ct)??throw Unavailable();
        Require(current.Code==assessment.Code&&current.DefinitionRevision==assessment.DefinitionRevision&&current.Generation==assessment.CaseGeneration,"planning_case_context","This assessment belongs to an earlier or different actual case generation.");
    }
    public static QmsPlanningCaseState AssessmentPhase(QmsAssessmentKind kind)=>kind switch{QmsAssessmentKind.ImplementationVerification=>QmsPlanningCaseState.VerificationPending,QmsAssessmentKind.EffectivenessReview=>QmsPlanningCaseState.EffectivenessPending,_=>QmsPlanningCaseState.AssessmentPending};
    public static QmsAssessmentKind RequiredAssessment(QmsPlanningKind kind)=>kind switch{QmsPlanningKind.RiskOpportunity=>QmsAssessmentKind.RiskEvaluation,QmsPlanningKind.Objective=>QmsAssessmentKind.ObjectiveEvaluation,QmsPlanningKind.Change=>QmsAssessmentKind.ChangeImpact,QmsPlanningKind.Improvement=>QmsAssessmentKind.ImprovementEvaluation,_=>throw new CommandRejectedException("planning_assessment","This definition has no operational assessment.")};
    public static QmsPlanningCaseState CaseState(QmsPlanningCaseState state,string action)=>action switch
    {
        "Assess" when state is QmsPlanningCaseState.Identified or QmsPlanningCaseState.Reopened=>QmsPlanningCaseState.AssessmentPending,
        "Plan" when state==QmsPlanningCaseState.AssessmentPending=>QmsPlanningCaseState.ActionPlanned,
        "Implement" when state==QmsPlanningCaseState.ActionPlanned=>QmsPlanningCaseState.Implementing,
        "RequestVerification" when state==QmsPlanningCaseState.Implementing=>QmsPlanningCaseState.VerificationPending,
        "Verify" when state==QmsPlanningCaseState.VerificationPending=>QmsPlanningCaseState.EffectivenessPending,
        "Accept" when state==QmsPlanningCaseState.EffectivenessPending=>QmsPlanningCaseState.Accepted,
        "Close" when state==QmsPlanningCaseState.Accepted=>QmsPlanningCaseState.Closed,
        "Reopen" when state is QmsPlanningCaseState.Accepted or QmsPlanningCaseState.Closed=>QmsPlanningCaseState.Reopened,
        "Withdraw" when state!=QmsPlanningCaseState.Withdrawn=>QmsPlanningCaseState.Withdrawn,
        _=>throw new CommandRejectedException("planning_state","The case action is unavailable in its current governed state.")
    };
    public static void ValidateAssessment(QmsPlanningKind definition,QmsAssessmentKind kind,QmsPlanningAssessmentContent content)
    {
        Require(content is not null,"planning_assessment","Supply typed human assessment content.");
        var sections=new object?[]{content.RiskEvaluation,content.ObjectiveEvaluation,content.ChangeImpact,content.ImplementationVerification,content.EffectivenessReview,content.ImprovementEvaluation};
        Require(Enum.IsDefined(kind)&&sections.Count(x=>x is not null)==1&&sections[(int)kind] is not null,"planning_assessment","Supply exactly the selected typed assessment content.");
        Require(kind is QmsAssessmentKind.ImplementationVerification or QmsAssessmentKind.EffectivenessReview||kind==RequiredAssessment(definition),"planning_assessment","The assessment kind must match its actual definition.");
        if(content.RiskEvaluation is{} risk){foreach(var text in new[]{risk.Evaluation,risk.Decision,risk.Basis,risk.ActionPlan,risk.EffectivenessCriteria})Text(text,"Risk evaluation");Instant(risk.NextReviewAt);}
        if(content.ObjectiveEvaluation is{} objective){Range(objective.From,objective.To);Text(objective.CompletenessAssessment,"Completeness assessment");Text(objective.ActionDecision,"Action decision");}
        if(content.ChangeImpact is{} change)foreach(var text in new[]{change.ProcessImpact,change.RiskOpportunityImpact,change.ResourceImpact,change.ResponsibilityImpact,change.IntegrityAssessment,change.ApprovalBasis})Text(text,"Change impact");
        if(content.ImplementationVerification is{} implementation){Text(implementation.Criteria,"Verification criteria");Text(implementation.Result,"Verification result");Text(implementation.Limitations,"Limitations");Require(implementation.Satisfied is not null,"planning_verification","Record an explicit satisfied choice.");}
        if(content.EffectivenessReview is{} effectiveness){Range(effectiveness.From,effectiveness.To);foreach(var text in new[]{effectiveness.Method,effectiveness.Criteria,effectiveness.Results,effectiveness.Limitations,effectiveness.FollowUpDecision})Text(text,"Effectiveness review");Require(effectiveness.Effective is not null,"planning_effectiveness","Record an explicit effective choice.");}
        if(content.ImprovementEvaluation is{} improvement){foreach(var text in new[]{improvement.Decision,improvement.Basis,improvement.Unit,improvement.ResultInterpretation,improvement.Limitations})Text(text,"Improvement evaluation");Require((improvement.Before is null)==(improvement.After is null),"planning_measurement","Before and after values must be supplied together or explicitly remain unmeasured.");}
    }
}
