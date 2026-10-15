using Forge.Quality.Qms;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using static Forge.Quality.Planning.QmsPlanningRules;
using static Forge.Quality.Planning.QmsPlanningService;

namespace Forge.Quality.Planning;

public sealed record ProposeRetentionBinding(QmsRevisionReference Schedule,QmsPlanningEvidenceReference Subject,DateTimeOffset? ExternalTriggerAt,string TriggerBasis,IReadOnlyList<QmsPlanningEvidenceReference> Evidence,Guid? PreviousBindingId=null);
public sealed record DecideRetentionBinding(Guid Id,int ExpectedVersion,string Action,string Note);
public sealed record PlaceRetentionHold(string FacilityCode,Guid? ProgramId,DataMarking Markings,QmsRetentionHoldKind Kind,QmsPlanningEvidenceReference Subject,string Basis,string AuthorityBasis,IReadOnlyList<QmsPlanningEvidenceReference> Evidence);
public sealed record DecideRetentionHold(Guid Id,int ExpectedVersion,string Action,string Note,IReadOnlyList<QmsPlanningEvidenceReference> Evidence);
public sealed record ProposeDisposition(string FacilityCode,Guid? ProgramId,DataMarking Markings,IReadOnlyList<Guid> BindingIds,string Purpose);
public sealed record DecideDisposition(Guid Id,int ExpectedVersion,string Action,string Note,QmsDispositionObservation? ObservationKind=null,IReadOnlyList<QmsPlanningEvidenceReference>? Evidence=null);

/// <summary>Administrative retention/disposition governance. This service has no physical-delete port or SQL.</summary>
public sealed class QmsRetentionService(QmsPlanningService planning,QmsPlanningWorkService work,IKernelUnitOfWorkFactory factory,
    IQmsPlanningEvidenceReader evidence,CommandPipeline pipeline,QmsOptions options)
{
    public async Task<object> ReadAsync(ForgePrincipal principal,string type,Guid id,CancellationToken ct)
    {
        await using var uow=await factory.BeginSnapshotAsync(ct);
        return await LoadedAsync(uow,principal,type,id,ct);
    }
    public async Task<QmsPlanningPage<object>> ListAsync(ForgePrincipal principal,string type,int take,string? after,CancellationToken ct)
    {
        Require(take is >0 and <=100,"planning_page","Choose a page size from 1 to 100.");await using var uow=await factory.BeginSnapshotAsync(ct);
        var items=new List<object>();string? cursor=after;string? lastVisible=null;
        while(true)
        {
            IReadOnlyList<Guid> ids;string? next;
            if(type=="Binding"){var page=await uow.Store<IQmsPlanningLookup>().BindingsAsync(100,cursor,ct);ids=page.Items.Select(x=>x.Id).ToArray();next=page.NextCursor;}
            else {Require(type=="Disposition","retention_type","Select bindings or dispositions.");var page=await uow.Store<IQmsPlanningLookup>().DispositionsAsync(100,cursor,ct);ids=page.Items.Select(x=>x.Id).ToArray();next=page.NextCursor;}
            foreach(var id in ids)
            {
                object? visible=null;try{visible=await LoadedAsync(uow,principal,type,id,ct);}catch(CommandRejectedException){}catch(ForgeAuthorizationException){}
                if(visible is null)continue;if(items.Count==take)return new(items,lastVisible);items.Add(visible);lastVisible=id.ToString();
            }
            if(next is null)return new(items,null);cursor=next;
        }
    }
    public Task<CommandResult> ProposeBindingAsync(ProposeRetentionBinding c,CommandContext context,CancellationToken ct)=>
        planning.ExecuteAsync(nameof(ProposeRetentionBinding),c,c.Schedule.Code,Permissions.QmsManage,context,async(uow,record,now)=>
        {
            await uow.Store<IQmsPlanningStore>().LockEvidenceAsync("retention-governance",ct);
            var revision=await ScheduleAsync(uow,context.Principal,record,c.Schedule.Revision,now,ct);var schedule=revision.Content.RetentionSchedule!;
            var source=await evidence.ResolveAsync(uow,context.Principal,c.Subject,true,ct);Consequential(context.Principal,Permissions.QmsManage,[source]);var captured=await work.CaptureAsync(uow,context.Principal,c.Evidence,ct);
            Require(schedule.AllowedOwnerTypes.Contains(source.OwnerContext+"/"+source.OwnerType,StringComparer.Ordinal),"retention_category","The actual owner type is outside this schedule's explicit category.");
            var existing=await uow.Store<IQmsPlanningLookup>().BindingsForSourceAsync(c.Subject,ct);
            QmsRetentionBinding? prior=null;
            if(c.PreviousBindingId is{} previous)
            {
                prior=(QmsRetentionBinding)await LoadedAsync(uow,context.Principal,"Binding",previous,ct);
                Require(prior.State==QmsRetentionBindingState.Active&&prior.Subject.OwnerContext==source.OwnerContext&&prior.Subject.OwnerType==source.OwnerType&&prior.Subject.OwnerId==source.OwnerId&&prior.Subject.OwnerRevision==source.OwnerRevision,
                    "retention_previous_binding","A revised classification cites the current active binding for this same actual owner.");
                SameScope(prior,record.FacilityCode,record.ProgramId);
            }
            Require(!existing.Any(x=>x.State==QmsRetentionBindingState.Proposed||x.State==QmsRetentionBindingState.Active&&x.Id!=prior?.Id),"retention_binding_exists","The exact source already has another proposed or active binding.");
            Text(c.TriggerBasis,"Trigger basis");var trigger=Trigger(schedule,source,c.ExternalTriggerAt,c.Evidence.Count);
            var id=ForgeIds.New(pipeline.Time);var next=new QmsRetentionBinding(id,1,QmsRetentionBindingState.Proposed,record.OrganizationCode,record.FacilityCode,record.ProgramId,Marks(record.Markings,[source]),c.Schedule,source,schedule.Trigger,trigger,c.TriggerBasis,captured,context.Actor,now,null,null,Append([],"Create",1,"","Proposed","Explicit exact-source retention proposal",context,now),prior?.Id,prior?.Version);
            await uow.Store<IQmsPlanningStore>().SaveBindingAsync(next,null,ct);return planning.Outcome(record,"QmsRetentionBinding",id.ToString(),1,"Create","Proposed",null,next,context,new[]{source}.Concat(captured));
        },ct,async(uow,record,now)=>{await uow.Store<IQmsPlanningStore>().LockEvidenceAsync("retention-governance",ct);await ScheduleAsync(uow,context.Principal,record,c.Schedule.Revision,now,ct);Consequential(context.Principal,Permissions.QmsManage,[await evidence.ResolveAsync(uow,context.Principal,c.Subject,true,ct)]);await work.CaptureAsync(uow,context.Principal,c.Evidence,ct);});
    public async Task<CommandResult> DecideBindingAsync(DecideRetentionBinding c,CommandContext context,CancellationToken ct)
    {
        var loaded=(QmsRetentionBinding)await ReadAsync(context.Principal,"Binding",c.Id,ct);
        return await planning.ExecuteAsync("RetentionBinding"+c.Action,c,loaded.Schedule.Code,c.Action=="Approve"?Permissions.QmsApprove:Permissions.QmsPublish,context,async(uow,record,now)=>
        {
            await uow.Store<IQmsPlanningStore>().LockEvidenceAsync("retention-governance",ct);var old=(QmsRetentionBinding)await LoadedAsync(uow,context.Principal,"Binding",c.Id,ct);
            Version(old.Version,c.ExpectedVersion);Text(c.Note,"Binding decision");await ScheduleAsync(uow,context.Principal,record,old.Schedule.Revision,now,ct);
            var refreshedSources=await planning.CurrentEvidenceAsync(uow,context.Principal,new[]{old.Subject}.Concat(old.TriggerEvidence),true,ct);
            Consequential(context.Principal,c.Action=="Approve"?Permissions.QmsApprove:Permissions.QmsPublish,[old.Subject]);
            var state=c.Action switch{"Approve" when old.State==QmsRetentionBindingState.Proposed=>QmsRetentionBindingState.Active,"Withdraw" when old.State==QmsRetentionBindingState.Proposed=>QmsRetentionBindingState.Withdrawn,_=>throw new CommandRejectedException("retention_state","Active retention protections cannot be withdrawn to bypass their schedule.")};
            Independent(context.Actor,new[]{old.CreatedBy},new[]{old.Subject}.Concat(old.TriggerEvidence));
            QmsRetentionBinding? superseded=null;
            if(c.Action=="Approve"&&old.PreviousBindingId is{} previous)
            {
                var prior=(QmsRetentionBinding)await LoadedAsync(uow,context.Principal,"Binding",previous,ct);
                Require(prior.State==QmsRetentionBindingState.Active&&prior.Version==old.PreviousBindingVersion,"stale_version","The previous active classification changed; revise the binding proposal.");
                Independent(context.Actor,new[]{prior.CreatedBy},prior.TriggerEvidence);
                superseded=prior with{Version=prior.Version+1,State=QmsRetentionBindingState.Superseded,History=Append(prior.History,"Supersede",prior.Version+1,"Active","Superseded","Superseded by exact independently approved binding "+old.Id+"; prior retention protections remain",context,now)};
                await uow.Store<IQmsPlanningStore>().SaveBindingAsync(superseded,prior.Version,ct);
                await uow.Outbox.EnqueueAsync(pipeline.Event(new QmsPlanningChanged("QmsRetentionBinding",prior.Id.ToString(),superseded.Version,"Supersede","Superseded"),context,"Quality","QmsRetentionBinding",prior.Id.ToString(),Program(prior.ProgramId),prior.Markings),ct);
            }
            var next=old with{Version=old.Version+1,State=state,Subject=refreshedSources[0],Markings=Marks(old.Markings,refreshedSources),ApprovedBy=c.Action=="Approve"?context.Actor:old.ApprovedBy,ApprovedAt=c.Action=="Approve"?now:old.ApprovedAt,
                History=Append(old.History,c.Action,old.Version+1,old.State.ToString(),state.ToString(),c.Note,context,now)};
            await uow.Store<IQmsPlanningStore>().SaveBindingAsync(next,old.Version,ct);return planning.Outcome(record,"QmsRetentionBinding",next.Id.ToString(),next.Version,c.Action,state.ToString(),old,new{binding=next,superseded},context,[next.Subject]);
        },ct,async(uow,record,now)=>{await uow.Store<IQmsPlanningStore>().LockEvidenceAsync("retention-governance",ct);var current=(QmsRetentionBinding)await LoadedAsync(uow,context.Principal,"Binding",c.Id,ct);await ScheduleAsync(uow,context.Principal,record,current.Schedule.Revision,now,ct);var sources=await planning.CurrentEvidenceAsync(uow,context.Principal,new[]{current.Subject}.Concat(current.TriggerEvidence),true,ct);Consequential(context.Principal,c.Action=="Approve"?Permissions.QmsApprove:Permissions.QmsPublish,sources.Take(1));});
    }
    public Task<CommandResult> PlaceHoldAsync(PlaceRetentionHold c,CommandContext context,CancellationToken ct)=>
        ExecuteScopeAsync(nameof(PlaceRetentionHold),c,c.FacilityCode,c.ProgramId,c.Markings,Permissions.QmsPublish,context,async(uow,now)=>
        {
            await uow.Store<IQmsPlanningStore>().LockEvidenceAsync("retention-governance",ct);Require(Enum.IsDefined(c.Kind),"retention_hold","Declare legal or quality hold.");Text(c.Basis,"Hold basis");Text(c.AuthorityBasis,"Hold authority");
            var subject=await evidence.ResolveAsync(uow,context.Principal,c.Subject,true,ct);Consequential(context.Principal,Permissions.QmsPublish,[subject]);var sources=await work.CaptureAsync(uow,context.Principal,c.Evidence,ct);var id=ForgeIds.New(pipeline.Time);
            var next=new QmsRetentionHold(id,1,c.Kind,QmsRetentionHoldState.Active,options.OrganizationCode!,c.FacilityCode,c.ProgramId,Marks(c.Markings,new[]{subject}.Concat(sources)),subject,c.Basis,c.AuthorityBasis,context.Actor,now,null,null,null,sources,Append([],"Place",1,"","Active","Actual legal/quality hold",context,now));
            await uow.Store<IQmsPlanningStore>().SaveHoldAsync(next,null,ct);return ScopeOutcome("QmsRetentionHold",id,1,"Place","Active",null,next,next.FacilityCode,next.ProgramId,next.Markings,context);
        },async(uow,now)=>{Consequential(context.Principal,Permissions.QmsPublish,[await evidence.ResolveAsync(uow,context.Principal,c.Subject,true,ct)]);await work.CaptureAsync(uow,context.Principal,c.Evidence,ct);},ct);
    public async Task<CommandResult> DecideHoldAsync(DecideRetentionHold c,CommandContext context,CancellationToken ct)
    {
        var saved=(QmsRetentionHold)await ReadAsync(context.Principal,"Hold",c.Id,ct);
        return await ExecuteScopeAsync("Hold"+c.Action,c,saved.FacilityCode,saved.ProgramId,saved.Markings,c.Action=="Release"?Permissions.QmsApprove:Permissions.QmsPublish,context,async(uow,now)=>
        {
            await uow.Store<IQmsPlanningStore>().LockEvidenceAsync("retention-governance",ct);var old=(QmsRetentionHold)await LoadedAsync(uow,context.Principal,"Hold",c.Id,ct);Version(old.Version,c.ExpectedVersion);Text(c.Note,"Hold-release basis");
            var sources=old.Evidence.Concat(await work.CaptureAsync(uow,context.Principal,c.Evidence,ct)).DistinctBy(x=>x.Reference).ToArray();
            var state=c.Action switch{"ProposeRelease" when old.State==QmsRetentionHoldState.Active=>QmsRetentionHoldState.ReleaseProposed,"Release" when old.State==QmsRetentionHoldState.ReleaseProposed=>QmsRetentionHoldState.Released,_=>throw new CommandRejectedException("retention_state","A hold release requires a proposal followed by independent approval.")};
            Require(sources.Length>0,"retention_evidence","Hold release requires actual authority evidence.");
            if(c.Action=="Release")Independent(context.Actor,new[]{old.PlacedBy,old.ReleaseProposedBy??""},sources);
            var currentSubject=await evidence.ResolveAsync(uow,context.Principal,old.Subject.Reference,true,ct);
            var next=old with{Version=old.Version+1,State=state,Subject=currentSubject,Markings=Marks(old.Markings,new[]{currentSubject}.Concat(sources)),Evidence=sources,ReleaseProposedBy=c.Action=="ProposeRelease"?context.Actor:old.ReleaseProposedBy,ReleasedBy=c.Action=="Release"?context.Actor:null,ReleasedAt=c.Action=="Release"?now:null,
                History=Append(old.History,c.Action,old.Version+1,old.State.ToString(),state.ToString(),c.Note,context,now)};
            await uow.Store<IQmsPlanningStore>().SaveHoldAsync(next,old.Version,ct);return ScopeOutcome("QmsRetentionHold",next.Id,next.Version,c.Action,state.ToString(),old,next,next.FacilityCode,next.ProgramId,next.Markings,context);
        },async(uow,now)=>{var old=(QmsRetentionHold)await LoadedAsync(uow,context.Principal,"Hold",c.Id,ct);var source=await evidence.ResolveAsync(uow,context.Principal,old.Subject.Reference,true,ct);Consequential(context.Principal,c.Action=="Release"?Permissions.QmsApprove:Permissions.QmsPublish,[source]);await planning.CurrentEvidenceAsync(uow,context.Principal,old.Evidence,true,ct);await work.CaptureAsync(uow,context.Principal,c.Evidence,ct);},ct);
    }
    public Task<CommandResult> ProposeDispositionAsync(ProposeDisposition c,CommandContext context,CancellationToken ct)=>
        ExecuteScopeAsync(nameof(ProposeDisposition),c,c.FacilityCode,c.ProgramId,c.Markings,Permissions.QmsManage,context,async(uow,now)=>
        {
            await uow.Store<IQmsPlanningStore>().LockEvidenceAsync("retention-governance",ct);Text(c.Purpose,"Disposition purpose");Require(c.BindingIds.Count>0&&c.BindingIds.Count<=1000&&c.BindingIds.Distinct().Count()==c.BindingIds.Count,"retention_candidates","Identify distinct exact bindings; no whole-catalog inference.");
            foreach(var id in c.BindingIds){var binding=(QmsRetentionBinding)await LoadedAsync(uow,context.Principal,"Binding",id,ct);SameScope(binding,c.FacilityCode,c.ProgramId);Consequential(context.Principal,Permissions.QmsManage,[binding.Subject]);}
            var newId=ForgeIds.New(pipeline.Time);var next=new QmsDispositionRecord(newId,1,QmsDispositionState.Proposed,options.OrganizationCode!,c.FacilityCode,c.ProgramId,c.Markings,c.BindingIds.Order().ToArray(),c.Purpose,context.Actor,now,null,null,null,null,"",null,null,[],Append([],"Create",1,"","Proposed","Exact binding proposal; bytes preserved",context,now));
            await uow.Store<IQmsPlanningStore>().SaveDispositionAsync(next,null,ct);return ScopeOutcome("QmsDisposition",newId,1,"Create","Proposed",null,next,next.FacilityCode,next.ProgramId,next.Markings,context);
        },async(uow,now)=>{foreach(var id in c.BindingIds){var binding=(QmsRetentionBinding)await LoadedAsync(uow,context.Principal,"Binding",id,ct);SameScope(binding,c.FacilityCode,c.ProgramId);Consequential(context.Principal,Permissions.QmsManage,[await evidence.ResolveAsync(uow,context.Principal,binding.Subject.Reference,true,ct)]);}},ct);
    public async Task<CommandResult> DecideDispositionAsync(DecideDisposition c,CommandContext context,CancellationToken ct)
    {
        var saved=(QmsDispositionRecord)await ReadAsync(context.Principal,"Disposition",c.Id,ct);var permission=c.Action=="Approve"?Permissions.QmsApprove:c.Action is "Record" or "Withdraw"?Permissions.QmsPublish:Permissions.QmsManage;
        return await ExecuteScopeAsync("Disposition"+c.Action,c,saved.FacilityCode,saved.ProgramId,saved.Markings,permission,context,async(uow,now)=>
        {
            await uow.Store<IQmsPlanningStore>().LockEvidenceAsync("retention-governance",ct);var old=(QmsDispositionRecord)await LoadedAsync(uow,context.Principal,"Disposition",c.Id,ct);Version(old.Version,c.ExpectedVersion);Text(c.Note,"Disposition human basis");
            var sources=await work.CaptureAsync(uow,context.Principal,c.Evidence??[],ct);var state=c.Action switch
            {"DryRun" when old.State is QmsDispositionState.Proposed or QmsDispositionState.DryRunFrozen=>QmsDispositionState.DryRunFrozen,"Approve" when old.State==QmsDispositionState.DryRunFrozen=>QmsDispositionState.Approved,"Record" when old.State==QmsDispositionState.Approved=>QmsDispositionState.Recorded,"Withdraw" when old.State is QmsDispositionState.Proposed or QmsDispositionState.DryRunFrozen or QmsDispositionState.Approved=>QmsDispositionState.Withdrawn,_=>throw new CommandRejectedException("retention_state","Follow proposal, exact dry-run, independent approval and attributed observation.")};
            var manifest=old.Manifest;
            if(c.Action=="DryRun") {manifest=await ManifestAsync(uow,context.Principal,old,now,ct);await uow.Store<IQmsPlanningStore>().AppendManifestAsync(old.Id,manifest,ct);}
            if(c.Action is "Approve" or "Record")
            {
                var current=await ManifestAsync(uow,context.Principal,old,now,ct);
                Require(old.Manifest is{Eligible:true}&&current.Eligible&&current.Fingerprint==old.Manifest.Fingerprint,"retention_manifest_changed","The exact eligible manifest is stale or blocked; create a new proposal/dry-run.");
                if(c.Action=="Approve")Independent(context.Actor,new[]{old.ProposedBy}.Concat(old.History.Where(x=>x.Action=="DryRun").Select(x=>x.Actor)),old.Manifest.Candidates.Select(x=>x.Subject).Concat(sources));
                if(c.Action=="Record") {Require(c.ObservationKind is{} observation&&Enum.IsDefined(observation),"retention_observation","Declare administrative record or attributed external physical observation.");Require(sources.Count>0,"retention_evidence","Record actual disposition/observation evidence; no Forge deletion is performed.");}
            }
            var next=old with{Version=old.Version+1,State=state,Manifest=manifest,Markings=Marks(old.Markings,(manifest?.Candidates.Select(x=>x.Subject)??[]).Concat(sources)),ApprovedBy=c.Action=="Approve"?context.Actor:old.ApprovedBy,ApprovedAt=c.Action=="Approve"?now:old.ApprovedAt,
                ObservationKind=c.Action=="Record"?c.ObservationKind:old.ObservationKind,Observation=c.Action=="Record"?c.Note:old.Observation,ObservedBy=c.Action=="Record"?context.Actor:old.ObservedBy,ObservedAt=c.Action=="Record"?now:old.ObservedAt,
                Evidence=old.Evidence.Concat(sources).DistinctBy(x=>x.Reference).ToArray(),History=Append(old.History,c.Action,old.Version+1,old.State.ToString(),state.ToString(),c.Note,context,now)};
            await uow.Store<IQmsPlanningStore>().SaveDispositionAsync(next,old.Version,ct);return ScopeOutcome("QmsDisposition",next.Id,next.Version,c.Action,state.ToString(),old,next,next.FacilityCode,next.ProgramId,next.Markings,context);
        },async(uow,now)=>
        {
            // Current source/hold/schedule authority precedes original-key replay. Withdraw cannot weaken protection.
            var current=(QmsDispositionRecord)await LoadedAsync(uow,context.Principal,"Disposition",c.Id,ct);
            foreach(var id in current.BindingIds){var binding=(QmsRetentionBinding)await LoadedAsync(uow,context.Principal,"Binding",id,ct);SameScope(binding,current.FacilityCode,current.ProgramId);Consequential(context.Principal,permission,[await evidence.ResolveAsync(uow,context.Principal,binding.Subject.Reference,true,ct)]);}
            if(c.Action is "Approve" or "Record") {var manifest=await ManifestAsync(uow,context.Principal,current,now,ct);Require(current.Manifest is{Eligible:true}&&manifest.Eligible&&manifest.Fingerprint==current.Manifest.Fingerprint,"retention_manifest_changed","Current retention protection changed; original approval/observation cannot be replayed.");}
            await work.CaptureAsync(uow,context.Principal,c.Evidence??[],ct);
        },ct);
    }
    internal async Task<QmsDispositionManifest> ManifestAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsDispositionRecord record,DateTimeOffset now,CancellationToken ct)
    {
        var candidates=new List<QmsDispositionCandidate>();
        foreach(var id in record.BindingIds.Order())
        {
            var binding=(QmsRetentionBinding)await LoadedAsync(uow,principal,"Binding",id,ct);var blockers=new List<string>();
            var header=await planning.RecordAsync(uow,binding.Schedule.Code,ct);var revision=Revision(header,binding.Schedule.Revision);var schedule=revision.Content.RetentionSchedule!;
            if(!InEffect(revision,now))blockers.Add("schedule_not_current_effective");else await planning.SourcesAsync(uow,principal,header.Code,header.Kind,revision.Content,true,now,ct);
            if(binding.State!=QmsRetentionBindingState.Active)blockers.Add("binding_not_active");
            var source=await evidence.ResolveAsync(uow,principal,binding.Subject.Reference,true,ct);if(source.Fingerprint!=binding.Subject.Fingerprint)blockers.Add("source_changed");
            DateTimeOffset? until=null;
            if(schedule.Indefinite==true)blockers.Add("indefinite_retention");else if(binding.TriggerAt is null)blockers.Add("trigger_unknown");else until=binding.TriggerAt.Value.AddDays(schedule.DurationDays!.Value);
            if(until>now)blockers.Add("retention_not_elapsed");
            var chain=new HashSet<Guid>{binding.Id};var previous=binding.PreviousBindingId;
            while(previous is{} previousId)
            {
                Require(chain.Add(previousId),"retention_binding_cycle","The retained classification history contains a cycle.");
                var prior=(QmsRetentionBinding)await LoadedAsync(uow,principal,"Binding",previousId,ct);
                var priorHeader=await planning.RecordAsync(uow,prior.Schedule.Code,ct);var priorSchedule=Revision(priorHeader,prior.Schedule.Revision).Content.RetentionSchedule!;
                if(priorSchedule.Indefinite==true)blockers.Add("prior_indefinite_retention");
                else
                {
                    // A previously unknown owner trigger can become known from its actual owner; client override remains forbidden.
                    var priorTrigger=prior.TriggerAt??(priorSchedule.Trigger==QmsRetentionTrigger.HumanVerifiedExternalOccurrence?null:Trigger(priorSchedule,source,null,0));
                    if(priorTrigger is null)blockers.Add("prior_trigger_unknown");
                    else {var floor=priorTrigger.Value.AddDays(priorSchedule.DurationDays!.Value);if(until is null||floor>until)until=floor;if(floor>now)blockers.Add("prior_retention_not_elapsed");}
                }
                previous=prior.PreviousBindingId;
            }
            var holds=(await uow.Store<IQmsPlanningLookup>().HoldsAsync(binding.Subject.Reference,ct)).Where(x=>x.State!=QmsRetentionHoldState.Released).ToList();
            var owners=source.FileSha256 is{} sha ? await evidence.SharedFileOwnersAsync(uow,principal,sha,ct) : Array.Empty<QmsPlanningSourceSnapshot>();
            foreach(var owner in owners)
            {
                holds.AddRange((await uow.Store<IQmsPlanningLookup>().HoldsAsync(owner.Reference,ct)).Where(x=>x.State!=QmsRetentionHoldState.Released));
                var other=(await uow.Store<IQmsPlanningLookup>().BindingsForSourceAsync(owner.Reference,ct)).Where(x=>x.State==QmsRetentionBindingState.Active).ToArray();
                if(other.Length!=1||!record.BindingIds.Contains(other[0].Id))blockers.Add("shared_owner_not_in_exact_manifest");
            }
            if(holds.Count>0)blockers.Add("active_legal_or_quality_hold");
            candidates.Add(new(id,binding.Version,binding.Schedule,revision.Version,source,until,blockers.Count==0,blockers.Distinct().Order().ToArray(),holds.Select(x=>x.Id).Distinct().Order().ToArray(),owners.Select(x=>x.OwnerContext+"/"+x.OwnerType+"/"+x.OwnerId+"/"+x.OwnerRevision).Distinct().Order().ToArray()));
        }
        // CapturedAt/EvaluatedAt are observations, not source identity; exclude them from reproducible current-state manifest hash.
        var hash=ManifestFingerprint(candidates);
        return new(ForgeIds.New(pipeline.Time),(record.Manifest?.Version??0)+1,now,candidates,candidates.Count>0&&candidates.All(x=>x.Eligible),hash);
    }
    public static string ManifestFingerprint(IEnumerable<QmsDispositionCandidate> candidates)=>CanonicalJson.Fingerprint(candidates.OrderBy(x=>x.BindingId).Select(x=>new{x.BindingId,x.BindingVersion,x.Schedule,x.ScheduleVersion,x.Subject.OwnerContext,x.Subject.OwnerType,x.Subject.OwnerId,x.Subject.OwnerRevision,x.Subject.Fingerprint,x.Subject.FileSha256,x.Subject.Sources,x.RetainUntil,x.Eligible,x.Blockers,x.ActiveHoldIds,x.SharedFileOwnerIds}));
    public static DateTimeOffset? Trigger(QmsRetentionScheduleContent schedule,QmsPlanningSourceSnapshot source,DateTimeOffset? external,int evidenceCount)
    {
        if(schedule.Trigger!=QmsRetentionTrigger.HumanVerifiedExternalOccurrence)Require(external is null,"retention_trigger","Owner-controlled trigger times cannot be overridden by the client.");
        else Require(external is not null&&external!=default&&evidenceCount>0,"retention_trigger","An external trigger requires an explicit occurrence time and actual evidence for independent approval.");
        return schedule.Trigger switch{QmsRetentionTrigger.SourceCreatedAt=>source.CreatedAt,QmsRetentionTrigger.SourceApprovedAt=>source.ApprovedAt,QmsRetentionTrigger.SourceClosedAt=>source.ClosedAt,QmsRetentionTrigger.HumanVerifiedExternalOccurrence=>external,_=>throw new CommandRejectedException("retention_trigger","Unknown trigger.")};
    }
    private async Task<QmsPlanningRevision> ScheduleAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsPlanningRecord record,int revision,DateTimeOffset now,CancellationToken ct)
    {Require(record.Kind==QmsPlanningKind.RetentionSchedule,"retention_schedule","Reference an exact retention schedule.");return await work.ActiveAsync(uow,principal,record,revision,now,ct);}
    private void Consequential(ForgePrincipal principal,string permission,IEnumerable<QmsPlanningSourceSnapshot> sources)
    {foreach(var resource in sources.SelectMany(x=>x.Sources).Select(x=>x.Resource).Distinct())planning.Permit(principal,permission,resource with{Type="QmsRetention"});}
    private static void SameScope(QmsRetentionBinding binding,string facility,Guid? program)=>Require(binding.FacilityCode==facility&&binding.ProgramId==program,"retention_scope","An exact disposition contains bindings of its actual immutable facility/program scope.");
    private async Task<object> LoadedAsync(IKernelUnitOfWork uow,ForgePrincipal principal,string type,Guid id,CancellationToken ct)
    {
        var store=uow.Store<IQmsPlanningLookup>();object result;string organization,facility;Guid? program;DataMarking marks;IEnumerable<QmsPlanningSourceSnapshot> sources;
        switch(type)
        {
            case "Binding":var b=await store.FindBindingAsync(id,ct)??throw Unavailable();result=b;organization=b.OrganizationCode;facility=b.FacilityCode;program=b.ProgramId;marks=b.Markings;sources=new[]{b.Subject}.Concat(b.TriggerEvidence);await planning.AuthorityAsync(uow,principal,await planning.RecordAsync(uow,b.Schedule.Code,ct),ct);break;
            case "Hold":var h=await store.FindHoldAsync(id,ct)??throw Unavailable();result=h;organization=h.OrganizationCode;facility=h.FacilityCode;program=h.ProgramId;marks=h.Markings;sources=new[]{h.Subject}.Concat(h.Evidence);break;
            case "Disposition":var d=await store.FindDispositionAsync(id,ct)??throw Unavailable();result=d;organization=d.OrganizationCode;facility=d.FacilityCode;program=d.ProgramId;marks=d.Markings;sources=d.Evidence;foreach(var bindingId in d.BindingIds)await LoadedAsync(uow,principal,"Binding",bindingId,ct);break;
            default:throw Unavailable();
        }
        Require(organization==options.OrganizationCode,"planning_not_found","The retention record is unavailable.");planning.Permit(principal,Permissions.QmsRead,new("QmsRetention"+type,id.ToString(),Program(program),marks,FacilityId:facility));
        await planning.CurrentEvidenceAsync(uow,principal,sources,false,ct);return result;
    }
    private Task<CommandResult> ExecuteScopeAsync<T>(string name,T c,string facility,Guid? program,DataMarking marks,string permission,CommandContext context,Func<IKernelUnitOfWork,DateTimeOffset,Task<CommandOutcome>> body,Func<IKernelUnitOfWork,DateTimeOffset,Task> authority,CancellationToken ct)=>
        pipeline.ExecuteAsync(new("Quality",name,permission,new("QmsRetention",null,Program(program),marks,FacilityId:facility),true),c,context,async(uow,now)=>{await planning.HeaderAsync(uow,facility,program,marks,ct);return await body(uow,now);},ct,async(uow,now)=>{await uow.Store<IQmsPlanningStore>().LockEvidenceAsync("retention-governance",ct);await planning.HeaderAsync(uow,facility,program,marks,ct);await authority(uow,now);return new(new("QmsRetention",null,Program(program),marks,FacilityId:facility),options.OrganizationCode+":"+facility);});
    private CommandOutcome ScopeOutcome(string type,Guid id,int version,string action,string state,object? old,object next,string facility,Guid? program,DataMarking marks,CommandContext context)=>
        new(CommandResult.Ok(id.ToString(),"Retention governance recorded; Forge source and audit bytes preserved."),[pipeline.Event(new QmsPlanningChanged(type,id.ToString(),version,action,state),context,"Quality",type,id.ToString(),Program(program),marks)],new(type,id.ToString(),old is null?null:CanonicalJson.Serialize(old),CanonicalJson.Serialize(next),Program(program),marks));
}
