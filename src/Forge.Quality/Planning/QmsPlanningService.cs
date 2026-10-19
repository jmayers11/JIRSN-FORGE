using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Programs.Contracts;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Forge.Resources.Contracts;
using static Forge.Quality.Planning.QmsPlanningRules;

namespace Forge.Quality.Planning;

public sealed record CreatePlanningRecord(string Code,QmsPlanningKind Kind,string FacilityCode,Guid? ProgramId,DataMarking Markings,QmsPlanningContent Content);
public sealed record RevisePlanningRecord(string Code,int? Revision,int ExpectedVersion,QmsPlanningContent Content);
public sealed record TransitionPlanningRecord(string Code,int Revision,int ExpectedVersion,string Action,string Note);
public sealed record QmsPlanningChanged(string EntityType,string EntityId,int Version,string Action,string State) : IDomainEvent
{
    public static string EventType => nameof(QmsPlanningChanged);
    public static int SchemaVersion => 1;
}

public sealed class QmsPlanningService(CommandPipeline pipeline,IKernelUnitOfWorkFactory factory,IForgeAuthorizer authorizer,
    QmsOptions options,QmsService qms,IQmsPlanningEvidenceReader evidence,TimeProvider time)
{
    public object Status => new { configured=options.Configured,organizationCode=options.OrganizationCode,physicalDeletionEnabled=false };
    public async Task<QmsPlanningPage<QmsPlanningRecord>> ListAsync(ForgePrincipal principal,QmsPlanningKind? kind,int take,string? after,CancellationToken ct)
    {
        Configured(); Require(take is >0 and <=100,"planning_page","Choose a page size from 1 to 100.");
        await using var uow=await factory.BeginSnapshotAsync(ct);
        var result=new List<QmsPlanningRecord>(); string? cursor=after;
        // Continue across inaccessible rows without returning hidden IDs, counts or incomplete visible pages.
        while(true)
        {
            var page=await uow.Store<IQmsPlanningLookup>().ListAsync(kind,100,cursor,ct);
            foreach(var record in page.Items)
                if(await CanReadAsync(uow,principal,record,ct))
                {
                    if(result.Count==take) return new(result,result[^1].Code);
                    result.Add(record);
                }
            if(page.NextCursor is null) return new(result,null);
            cursor=page.NextCursor;
        }
    }
    public async Task<QmsMetricDashboardSnapshot> MetricsDashboardAsync(ForgePrincipal principal,DateTimeOffset from,
        DateTimeOffset to,int take,string? after,CancellationToken ct)
    {
        Configured(); Range(from,to); Require(take is >0 and <=100,"planning_page","Choose a page size from 1 to 100.");
        var capturedAt=time.GetUtcNow();
        await using var uow=await factory.BeginSnapshotAsync(ct);
        var records=new List<QmsPlanningRecord>(); string? cursor=after; string? nextCursor=null;
        while(true)
        {
            var page=await uow.Store<IQmsPlanningLookup>().ListAsync(QmsPlanningKind.MetricDefinition,100,cursor,ct);
            foreach(var record in page.Items)
            {
                if(!await CanReadAsync(uow,principal,record,ct)) continue;
                var effective=record.Revisions.Where(x=>x.State==QmsState.Effective)
                    .OrderByDescending(x=>x.Revision).FirstOrDefault();
                if(effective is null) continue;
                if(records.Count==take){nextCursor=records[^1].Code;break;}
                records.Add(record);
            }
            if(nextCursor is not null||page.NextCursor is null) break;
            cursor=page.NextCursor;
        }
        var objectives=await EffectiveMetricObjectivesAsync(uow,principal,capturedAt,ct);
        var items=new List<QmsMetricDashboardItem>(records.Count);
        foreach(var record in records)
        {
            var revision=record.Revisions.Where(x=>x.State==QmsState.Effective)
                .OrderByDescending(x=>x.Revision).First();
            var definition=revision.Content.MetricDefinition
                ??throw new CommandRejectedException("metric_definition","The effective metric definition is unavailable.");
            if(definition.SourceKind==QmsMetricSourceKind.AttributedHumanObservation)
            {
                items.Add(new(record.Code,revision.Revision,definition.Name,definition.SourceKind,definition.Unit,
                    definition.NumeratorDefinition,definition.DenominatorDefinition,definition.MeasurementMethod,
                    definition.CohortBasis,"AssessmentRequired",false,null,
                    DashboardObjectives(objectives,record.Code,revision.Revision,definition,from,to,null)));
                continue;
            }
            if(from<definition.PeriodFrom||to>definition.PeriodTo)
            {
                items.Add(new(record.Code,revision.Revision,definition.Name,definition.SourceKind,definition.Unit,
                    definition.NumeratorDefinition,definition.DenominatorDefinition,definition.MeasurementMethod,
                    definition.CohortBasis,"OutsideDefinitionPeriod",true,null,
                    DashboardObjectives(objectives,record.Code,revision.Revision,definition,from,to,null)));
                continue;
            }
            var measurement=await evidence.MeasureSnapshotAsync(uow,principal,definition,from,to,ct);
            items.Add(new(record.Code,revision.Revision,definition.Name,definition.SourceKind,definition.Unit,
                definition.NumeratorDefinition,definition.DenominatorDefinition,definition.MeasurementMethod,
                definition.CohortBasis,measurement.Availability.ToString(),true,measurement,
                DashboardObjectives(objectives,record.Code,revision.Revision,definition,from,to,measurement)));
        }
        return new(from,to,capturedAt,items,nextCursor,nextCursor is null,
            items.Where(x=>x.Calculated).All(x=>x.Status==QmsMetricAvailability.Complete.ToString()));
    }

    /// <summary>Computes only completed, complete-cohort objective periods from exact effective metric revisions.</summary>
    public async Task<IReadOnlyList<QmsMetricObjectiveAnalystResult>> ObjectivesBelowTargetAsync(
        ForgePrincipal principal,int take,CancellationToken ct)
    {
        Configured();Require(take is >0 and <=100,"planning_page","Choose a page size from 1 to 100.");
        var asOf=time.GetUtcNow();await using var uow=await factory.BeginSnapshotAsync(ct);
        var objectives=await EffectiveMetricObjectivesAsync(uow,principal,asOf,ct);
        var results=new List<QmsMetricObjectiveAnalystResult>();
        foreach(var item in objectives.OrderBy(x=>x.Code,StringComparer.Ordinal))
        {
            var objective=item.Content;
            if(asOf<objective.PeriodTo)continue;
            var metricRecord=await uow.Store<IQmsPlanningLookup>().FindAsync(objective.Metric.Code,ct);
            if(metricRecord is null||!await CanReadAsync(uow,principal,metricRecord,ct))continue;
            var metricRevision=metricRecord.Revisions.FirstOrDefault(x=>x.Revision==objective.Metric.Revision);
            if(metricRevision is null||metricRevision.State!=QmsState.Effective||metricRevision.Content.MetricDefinition is null)continue;
            var definition=metricRevision.Content.MetricDefinition;
            if(definition.SourceKind==QmsMetricSourceKind.AttributedHumanObservation
                ||objective.PeriodFrom<definition.PeriodFrom||objective.PeriodTo>definition.PeriodTo)continue;
            var measurement=await evidence.MeasureSnapshotAsync(uow,principal,definition,objective.PeriodFrom,objective.PeriodTo,ct);
            if(ObjectiveStatus(objective,definition,objective.PeriodFrom,objective.PeriodTo,measurement)!="BelowTarget"
                ||measurement.Actual is null)continue;
            var actual=measurement.Actual.Value;
            results.Add(new(item.Code,item.Revision,item.Title,objective.Metric.Code,objective.Metric.Revision,
                actual,definition.Unit,objective.Comparison,objective.Target,objective.UpperTarget,
                objective.Tolerance,objective.PeriodFrom,objective.PeriodTo,measurement));
            if(results.Count==take)return results;
        }
        return results;
    }

    private async Task<IReadOnlyList<(string Code,int Revision,string Title,QmsObjectiveContent Content)>> EffectiveMetricObjectivesAsync(
        IKernelUnitOfWork uow,ForgePrincipal principal,DateTimeOffset asOf,CancellationToken ct)
    {
        var result=new List<(string,int,string,QmsObjectiveContent)>();string? cursor=null;
        while(true)
        {
            var page=await uow.Store<IQmsPlanningLookup>().ListAsync(QmsPlanningKind.Objective,100,cursor,ct);
            foreach(var record in page.Items)
            {
                if(!await CanReadAsync(uow,principal,record,ct))continue;
                var revision=record.Revisions.Where(x=>x.State==QmsState.Effective&&x.EffectiveFrom<=asOf
                    &&(x.EffectiveTo is null||asOf<x.EffectiveTo)).OrderByDescending(x=>x.Revision).FirstOrDefault();
                if(revision?.Content.Objective is{} objective)result.Add((record.Code,revision.Revision,objective.Title,objective));
            }
            if(page.NextCursor is null)return result;cursor=page.NextCursor;
        }
    }

    private static IReadOnlyList<QmsMetricDashboardObjective> DashboardObjectives(
        IReadOnlyList<(string Code,int Revision,string Title,QmsObjectiveContent Content)> all,
        string metricCode,int metricRevision,QmsMetricDefinitionContent metric,DateTimeOffset from,DateTimeOffset to,
        QmsMetricMeasurement? measurement)
    {
        return all.Where(x=>x.Content.Metric.Code==metricCode&&x.Content.Metric.Revision==metricRevision)
            .Select(x=>new QmsMetricDashboardObjective(x.Code,x.Revision,x.Title,x.Content.Comparison,x.Content.Target,
                x.Content.UpperTarget,x.Content.Tolerance,x.Content.PeriodFrom,x.Content.PeriodTo,
                ObjectiveStatus(x.Content,metric,from,to,measurement)))
            .OrderBy(x=>x.Code,StringComparer.Ordinal).ToArray();
    }

    private static string ObjectiveStatus(QmsObjectiveContent objective,QmsMetricDefinitionContent metric,
        DateTimeOffset from,DateTimeOffset to,QmsMetricMeasurement? measurement)
    {
        if(metric.SourceKind==QmsMetricSourceKind.AttributedHumanObservation)return "AssessmentRequired";
        if(from<objective.PeriodFrom||to>objective.PeriodTo)return "OutsideObjectivePeriod";
        if(measurement?.Availability!=QmsMetricAvailability.Complete||measurement.Actual is null)return "NotEvaluated";
        var actual=measurement.Actual.Value;
        var met=objective.Comparison switch
        {
            QmsTargetComparison.AtLeast=>actual+objective.Tolerance>=objective.Target,
            QmsTargetComparison.AtMost=>actual-objective.Tolerance<=objective.Target,
            QmsTargetComparison.Equal=>Math.Abs(actual-objective.Target)<=objective.Tolerance,
            QmsTargetComparison.WithinRange=>actual>=objective.Target-objective.Tolerance
                &&actual<=objective.UpperTarget!.Value+objective.Tolerance,
            _=>false,
        };
        return met?"OnTarget":"BelowTarget";
    }
    public async Task<QmsPlanningRecord> ReadAsync(ForgePrincipal principal,string code,CancellationToken ct)
    {
        await using var uow=await factory.BeginSnapshotAsync(ct); var record=await RecordAsync(uow,code,ct);
        if(!await CanReadAsync(uow,principal,record,ct)) throw Unavailable(); return record;
    }
    public Task<CommandResult> CreateAsync(CreatePlanningRecord c,CommandContext context,CancellationToken ct)
    {
        var code=BusinessCode.Require(c.Code,"Planning record"); var facility=BusinessCode.Require(c.FacilityCode,"Facility");
        var resource=new ResourceRef("QmsPlanningRecord",code,Program(c.ProgramId),c.Markings,FacilityId:facility);
        return pipeline.ExecuteAsync(new("Quality",nameof(CreatePlanningRecord),Permissions.QmsManage,resource,true),c,context,
            async(uow,now)=>
            {
                await uow.Store<IQmsPlanningStore>().LockAsync(code,ct);
                Require(await uow.Store<IQmsPlanningLookup>().FindAsync(code,ct) is null,"planning_exists","This planning record already exists.");
                await HeaderAsync(uow,facility,c.ProgramId,c.Markings,ct);
                var sources=await SourcesAsync(uow,context.Principal,code,c.Kind,c.Content,false,now,ct);
                var revision=Draft(code,1,c.Content,sources,context,now);
                var record=new QmsPlanningRecord(code,c.Kind,options.OrganizationCode!,facility,c.ProgramId,c.Markings,context.Actor,now,[revision]);
                await uow.Store<IQmsPlanningStore>().InsertAsync(record,ct); await uow.Store<IQmsPlanningStore>().SaveRevisionAsync(revision,null,ct);
                return Outcome(record,"QmsPlanningRevision",code+"/1",1,"Create","Draft",null,revision,context,sources);
            },ct,async(uow,now)=>
            {
                Configured();
                await uow.Store<IQmsPlanningStore>().LockAsync(code,ct);
                if(await uow.Store<IQmsPlanningLookup>().FindAsync(code,ct) is{} existing) return await AuthorityAsync(uow,context.Principal,existing,ct);
                await HeaderAsync(uow,facility,c.ProgramId,c.Markings,ct);
                var sources=await SourcesAsync(uow,context.Principal,code,c.Kind,c.Content,false,now,ct);
                return new CommandAuthority(resource with {Markings=Marks(c.Markings,sources)},options.OrganizationCode+":"+code);
            });
    }
    public Task<CommandResult> ReviseAsync(RevisePlanningRecord c,CommandContext context,CancellationToken ct) =>
        ExecuteAsync(nameof(RevisePlanningRecord),c,c.Code,Permissions.QmsManage,context,async(uow,record,now)=>
        {
            var old=c.Revision is{} number ? Revision(record,number) : record.Revisions[^1]; Version(old.Version,c.ExpectedVersion);
            var edit=c.Revision is not null;
            Require(edit ? old.State==QmsState.Draft : !record.Revisions.Any(x=>x.State is QmsState.Draft or QmsState.Submitted or QmsState.Reviewed or QmsState.Approved),
                "planning_open_revision",edit?"Only draft content may be edited.":"Complete or withdraw the open proposal before a new revision.");
            var sources=await SourcesAsync(uow,context.Principal,record.Code,record.Kind,c.Content,false,now,ct);
            var next=edit ? old with {Version=old.Version+1,Content=c.Content,LastEditedBy=context.Actor,Sources=sources,
                RequiresLicensedVerification=sources.Any(x=>x.RequiresLicensedVerification)} : Draft(record.Code,old.Revision+1,c.Content,sources,context,now);
            if(edit) next=next with {History=Append(old.History,"Edit",next.Version,old.State.ToString(),next.State.ToString(),"Human content revision",context,now)};
            await uow.Store<IQmsPlanningStore>().SaveRevisionAsync(next,edit?old.Version:null,ct);
            return Outcome(record,"QmsPlanningRevision",record.Code+"/"+next.Revision,next.Version,edit?"Edit":"NewRevision",next.State.ToString(),edit?old:null,next,context,sources);
        },ct,async(uow,record,now)=> { await SourcesAsync(uow,context.Principal,record.Code,record.Kind,c.Content,false,now,ct); });
    public Task<CommandResult> TransitionAsync(TransitionPlanningRecord c,CommandContext context,CancellationToken ct) =>
        ExecuteAsync("Planning"+c.Action,c,c.Code,Permission(c.Action),context,async(uow,record,now)=>
        {
            var old=Revision(record,c.Revision); Version(old.Version,c.ExpectedVersion); Text(c.Note,"Human decision note");
            var state=Transition(old.State,c.Action);
            var sources=await SourcesAsync(uow,context.Principal,record.Code,record.Kind,old.Content,c.Action!="Withdraw",now,ct);
            if(c.Action is "Review" or "Approve") Independent(context.Actor,Authors(old),sources);
            var next=old with {Version=old.Version+1,State=state,Sources=sources,RequiresLicensedVerification=sources.Any(x=>x.RequiresLicensedVerification),
                ReviewedBy=c.Action=="Review"?context.Actor:old.ReviewedBy,ApprovedBy=c.Action=="Approve"?context.Actor:old.ApprovedBy,
                EffectiveFrom=c.Action=="Effective"?now:old.EffectiveFrom,EffectiveTo=c.Action=="Withdraw"&&old.EffectiveFrom is not null?now:old.EffectiveTo};
            var superseded=new List<QmsPlanningRevision>();
            if(c.Action=="Effective") foreach(var prior in record.Revisions.Where(x=>x.State==QmsState.Effective))
            {
                var closed=prior with {Version=prior.Version+1,State=QmsState.Superseded,EffectiveTo=now,
                    History=Append(prior.History,"Supersede",prior.Version+1,"Effective","Superseded","Superseded by exact revision "+next.Revision,context,now)};
                await uow.Store<IQmsPlanningStore>().SaveRevisionAsync(closed,prior.Version,ct); superseded.Add(closed);
                await uow.Outbox.EnqueueAsync(pipeline.Event(new QmsPlanningChanged("QmsPlanningRevision",record.Code+"/"+closed.Revision,closed.Version,"Supersede","Superseded"),context,"Quality","QmsPlanningRevision",record.Code+"/"+closed.Revision,Program(record.ProgramId),Marks(record.Markings,closed.Sources)),ct);
            }
            next=next with {History=Append(old.History,c.Action,next.Version,old.State.ToString(),state.ToString(),c.Note,context,now)};
            await uow.Store<IQmsPlanningStore>().SaveRevisionAsync(next,old.Version,ct);
            return Outcome(record,"QmsPlanningRevision",record.Code+"/"+next.Revision,next.Version,c.Action,state.ToString(),new{revision=old,priorIntervals=record.Revisions.Where(x=>x.State==QmsState.Effective)},new{revision=next,superseded},context,sources);
        },ct);

    internal Task<CommandResult> ExecuteAsync<T>(string command,T c,string code,string permission,CommandContext context,
        Func<IKernelUnitOfWork,QmsPlanningRecord,DateTimeOffset,Task<CommandOutcome>> body,CancellationToken ct,
        Func<IKernelUnitOfWork,QmsPlanningRecord,DateTimeOffset,Task>? beforeReplay=null) =>
        pipeline.ExecuteAsync(new("Quality",command,permission,ResourceRef.Of("QmsPlanningRecord",code),true),c,context,async(uow,now)=>
        {
            await uow.Store<IQmsPlanningStore>().LockAsync(BusinessCode.Require(code,"Planning record"),ct); var record=await RecordAsync(uow,code,ct);
            await AuthorityAsync(uow,context.Principal,record,ct,true); Permit(context.Principal,permission,Resource(record));
            return await body(uow,record,now);
        },ct,async(uow,now)=>
        {
            await uow.Store<IQmsPlanningStore>().LockAsync(BusinessCode.Require(code,"Planning record"),ct);
            var record=await RecordAsync(uow,code,ct); var authority=await AuthorityAsync(uow,context.Principal,record,ct,true);
            if(beforeReplay is not null) await beforeReplay(uow,record,now); return authority;
        });
    internal async Task<CommandAuthority> AuthorityAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsPlanningRecord record,CancellationToken ct,bool lockSources=false)
    {
        Require(record.OrganizationCode==options.OrganizationCode,"planning_not_found","The planning source is unavailable.");
        Permit(principal,Permissions.QmsRead,Resource(record)); var sources=record.Revisions.SelectMany(x=>x.Sources).ToArray();
        var currentSources=await CurrentEvidenceAsync(uow,principal,sources,false,ct,lockSources);
        return new(Resource(record) with {Markings=Marks(record.Markings,sources.Concat(currentSources))},record.OrganizationCode+":"+record.Code);
    }
    public async Task<bool> CanReadAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsPlanningRecord record,CancellationToken ct)
    {
        try { await AuthorityAsync(uow,principal,record,ct); return true; }
        catch(ForgeAuthorizationException) {return false;} catch(CommandRejectedException) {return false;}
    }
    internal async Task<IReadOnlyList<QmsPlanningSourceSnapshot>> CurrentEvidenceAsync(IKernelUnitOfWork uow,ForgePrincipal principal,IEnumerable<QmsPlanningSourceSnapshot> sources,bool requireSame,CancellationToken ct,bool lockSources=false)
    {
        var result=new List<QmsPlanningSourceSnapshot>();
        foreach(var saved in sources)
        {
            foreach(var source in saved.Sources) Permit(principal,source.Permission,source.Resource);
            var current=await evidence.ResolveAsync(uow,principal,saved.Reference,requireSame||lockSources,ct);
            foreach(var source in current.Sources) Permit(principal,source.Permission,source.Resource);
            if(requireSame) Require(current.Fingerprint==saved.Fingerprint,"planning_source_changed","A recorded owner source changed; create a new assessment or dry-run.");
            result.Add(current);
        }
        return result;
    }
    internal async Task<IReadOnlyList<QmsPlanningSourceSnapshot>> SourcesAsync(IKernelUnitOfWork uow,ForgePrincipal principal,string ownCode,QmsPlanningKind kind,QmsPlanningContent content,bool requireEffective,DateTimeOffset now,CancellationToken ct)
    {
        Validate(kind,content); var sources=new List<QmsPlanningSourceSnapshot>();
        foreach(var d in content.Documents??[])
        {
            var documentNumber=QmsContentRules.DocumentNumber(d.DocumentNumber);var revisionCode=QmsContentRules.DocumentRevision(d.Revision);
            var document=await uow.Store<IDocumentLookup>().FindDocumentAsync(documentNumber,ct)??throw Unavailable();
            var revision=document.Revisions.FirstOrDefault(x=>x.Revision==revisionCode)??throw Unavailable();
            if(requireEffective) Require(document.Current?.Revision==revisionCode && revision.Status is DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable
                && (revision.EffectiveFrom is null || revision.EffectiveFrom<=(await uow.SiteDateAsync(now, ct))) && (revision.EffectiveTo is null || (await uow.SiteDateAsync(now, ct))<=revision.EffectiveTo),
                "planning_document_not_current","Use an exact currently applicable controlled document revision.");
            sources.Add(await evidence.ResolveAsync(uow,principal,new("Engineering","DocumentRevision",documentNumber,revisionCode),true,ct));
        }
        foreach(var (reference,planningKind,qmsKind) in References(content).Distinct())
        {
            if(planningKind is{} expected)
            {
                Require(reference.Code!=ownCode,"planning_cycle","A planning record cannot depend on itself.");
                var linked=await RecordAsync(uow,reference.Code,ct); var revision=Revision(linked,reference.Revision);
                Require(linked.Kind==expected,"planning_source_kind","The linked planning definition has the wrong kind.");
                await AuthorityAsync(uow,principal,linked,ct);
                if(requireEffective) Require(InEffect(revision,now),"planning_source_not_effective","The exact planning source must currently be effective.");
                sources.Add(await evidence.ResolveAsync(uow,principal,new("Quality","QmsPlanningRevision",linked.Code,reference.Revision.ToString()),true,ct));
            }
            else
            {
                var linked=await uow.Store<IQmsLookup>().FindAsync(BusinessCode.Require(reference.Code,"QMS source"),ct)??throw Unavailable();
                var revision=linked.Revisions.FirstOrDefault(x=>x.Revision==reference.Revision)??throw Unavailable();
                Require(linked.Kind==qmsKind && await qms.CanReadAsync(uow,principal,linked,ct,revision.Revision),"planning_source_kind","The actual QMS source is unavailable or has the wrong kind.");
                if(requireEffective) Require(revision.State==QmsState.Effective && revision.EffectiveFrom<=now && (revision.EffectiveTo is null||now<revision.EffectiveTo),"planning_source_not_effective","The exact QMS source must currently be effective.");
                sources.Add(await evidence.ResolveAsync(uow,principal,new("Quality","QmsRevision",linked.Code,reference.Revision.ToString()),true,ct));
            }
        }
        foreach(var reference in Evidence(content).Distinct()) sources.Add(await evidence.ResolveAsync(uow,principal,reference,true,ct));
        if(content.MetricDefinition is{} metric) foreach(var order in metric.OrderNumbers)
            sources.Add(await evidence.ResolveAsync(uow,principal,new("Execution","Order",order.ToString(System.Globalization.CultureInfo.InvariantCulture)),true,ct));
        var owner=content.RiskOpportunity?.OwnerPersonId??content.Objective?.OwnerPersonId??content.Change?.OwnerPersonId??content.RetentionSchedule?.OwnerPersonId??content.Improvement?.OwnerPersonId;
        if(owner is{} id) Require(await uow.People.LoadPrincipalAsync(new PersonId(id),ct) is{IsActive:true},"planning_owner","The responsible person must be actively registered.");
        await CurrentEvidenceAsync(uow,principal,sources,false,ct);
        return sources.DistinctBy(x=>x.Reference).OrderBy(x=>x.OwnerContext).ThenBy(x=>x.OwnerType).ThenBy(x=>x.OwnerId).ThenBy(x=>x.OwnerRevision).ToArray();
    }
    internal async Task<QmsPlanningRecord> RecordAsync(IKernelUnitOfWork uow,string code,CancellationToken ct)
    {
        Configured(); var record=await uow.Store<IQmsPlanningLookup>().FindAsync(BusinessCode.Require(code,"Planning record"),ct);
        if(record is null||record.OrganizationCode!=options.OrganizationCode) throw Unavailable(); return record;
    }
    internal static QmsPlanningRevision Revision(QmsPlanningRecord record,int revision) => record.Revisions.FirstOrDefault(x=>x.Revision==revision)??throw Unavailable();
    internal async Task HeaderAsync(IKernelUnitOfWork uow,string facility,Guid? program,DataMarking markings,CancellationToken ct)
    {
        Configured(); Require((markings&~(DataMarking.ControlledUnclassified|DataMarking.ExportControlled|DataMarking.Proprietary))==0,"invalid_markings","Choose canonical markings.");
        Require((await uow.Store<IResourcesLookup>().ListSitesAsync(ct)).Any(x=>x.Code==facility),"planning_facility","Select an actual facility.");
        Require(program is null||await uow.Store<IProgramsLookup>().FindProgramAsync(program.Value,ct) is{Status:ProgramStatus.Active},"planning_program","Select an active owning program.");
    }
    internal void Permit(ForgePrincipal p,string permission,ResourceRef resource) {var decision=authorizer.Authorize(p,permission,resource);if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision);}
    private void Configured() => Require(options.Configured,"qms_not_configured","Configure the explicit organization before QMS governance.");
    internal CommandOutcome Outcome(QmsPlanningRecord record,string type,string id,int version,string action,string state,object? old,object next,CommandContext context,IEnumerable<QmsPlanningSourceSnapshot> sources) =>
        new(CommandResult.Ok(id,$"{type}: {state}."),[pipeline.Event(new QmsPlanningChanged(type,id,version,action,state),context,"Quality",type,id,Program(record.ProgramId),Marks(record.Markings,sources))],
            new(type,id,old is null?null:CanonicalJson.Serialize(old),CanonicalJson.Serialize(next),Program(record.ProgramId),Marks(record.Markings,sources)));
    public static ResourceRef Resource(QmsPlanningRecord r) => new("QmsPlanningRecord",r.Code,Program(r.ProgramId),r.Markings,FacilityId:r.FacilityCode);
    internal static DataMarking Marks(DataMarking start,IEnumerable<QmsPlanningSourceSnapshot> sources) => sources.SelectMany(x=>x.Sources).Aggregate(start,(m,s)=>m|s.Resource.Markings);
    public static ProgramId? Program(Guid? id)=>id is{} value?new ProgramId(value):null;
    internal static CommandRejectedException Unavailable()=>new("planning_not_found","The planning record or evidence is unavailable.");
    internal static string Permission(string action)=>action switch {"Submit"=>Permissions.QmsManage,"Review"=>Permissions.QmsReview,"Approve"=>Permissions.QmsApprove,"Effective" or "Withdraw"=>Permissions.QmsPublish,_=>throw new CommandRejectedException("planning_action","Unknown lifecycle action.")};
    internal static IReadOnlyList<QmsPlanningTransition> Append(IReadOnlyList<QmsPlanningTransition> history,string action,int version,string from,string to,string note,CommandContext c,DateTimeOffset now)=>
        history.Append(new QmsPlanningTransition(action,version,c.Actor,now,from,to,note,c.Reason!.Trim())).ToArray();
    public static IEnumerable<string> Authors(QmsPlanningRevision r)=>new[]{r.CreatedBy,r.LastEditedBy}.Concat(r.History.Where(x=>x.Action is "Create" or "Edit").Select(x=>x.Actor));
    private static QmsPlanningRevision Draft(string code,int revision,QmsPlanningContent content,IReadOnlyList<QmsPlanningSourceSnapshot> sources,CommandContext c,DateTimeOffset now)=>
        new(code,revision,1,QmsState.Draft,content,c.Actor,c.Actor,null,null,null,null,sources.Any(x=>x.RequiresLicensedVerification),sources,Append([],"Create",1,"","Draft","Human-defined draft",c,now));
}
