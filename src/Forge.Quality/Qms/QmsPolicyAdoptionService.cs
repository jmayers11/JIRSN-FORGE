using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Programs.Contracts;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Quality.Qms;

public sealed record CreateQmsPolicyAdoption(string Code,string FacilityCode,Guid? ProgramId,DataMarking Markings,QmsPolicyAdoptionContent Content);
public sealed record ReviseQmsPolicyAdoption(string Code,int ExpectedVersion,QmsPolicyAdoptionContent Content);
public sealed record EditQmsPolicyAdoption(string Code,int Revision,int ExpectedVersion,QmsPolicyAdoptionContent Content);
public sealed record TransitionQmsPolicyAdoption(string Code,int Revision,int ExpectedVersion,string Action,string Note);
public sealed record QmsPolicyAdoptionChanged(string Code,int Revision,int Version,string Action,QmsState State):IDomainEvent
{
    public static string EventType=>nameof(QmsPolicyAdoptionChanged);
    public static int SchemaVersion=>1;
}

/// <summary>Only independently approved Effective bindings establish operational controls. Content/history remain immutable after submission.</summary>
public sealed class QmsPolicyAdoptionService(CommandPipeline pipeline,IKernelUnitOfWorkFactory factory,QmsOptions options,QmsService qms,IForgeAuthorizer authorizer)
{
    private readonly QmsPolicyCurrentAccess access=new(qms,authorizer);

    public async Task<IReadOnlyList<QmsPolicyAdoptionView>> ListAsync(ForgePrincipal principal,CancellationToken ct)
    {
        Configured();
        await using var uow=await factory.BeginSnapshotAsync(ct);
        var visible=new List<QmsPolicyAdoptionView>();
        foreach(var adoption in await uow.Store<IQmsPolicyAdoptionLookup>().ListAsync(ct))
            if(await CanReadAsync(uow,principal,adoption,ct)) visible.Add(adoption);
        return visible;
    }
    public async Task<QmsPolicyAdoptionView> ReadAsync(ForgePrincipal principal,string rawCode,int? revision,CancellationToken ct)
    {
        Configured(); await using var uow=await factory.BeginSnapshotAsync(ct);
        var adoption=await uow.Store<IQmsPolicyAdoptionLookup>().FindAsync(Code(rawCode),ct)??throw NotFound();
        if(!await CanReadAsync(uow,principal,adoption,ct,revision)) throw NotFound();
        return revision is{} number?adoption with{Revisions=adoption.Revisions.Where(r=>r.Revision==number).ToArray()}:adoption;
    }
    public async Task<bool> CanReadAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsPolicyAdoptionView adoption,CancellationToken ct,int? revision=null)
    {
        if(adoption.OrganizationCode!=options.OrganizationCode) return false;
        IReadOnlyList<QmsPolicyAdoptionRevision> revisions=revision is{} number?adoption.Revisions.Where(r=>r.Revision==number).ToArray():adoption.Revisions;
        if(revisions.Count==0) return false;
        try
        {
            foreach(var version in revisions)
            {
                Permit(principal,Permissions.QmsRead,Resource(adoption,version));
                await RecordedSourcesAsync(uow,principal,version,false,false,pipeline.Time.GetUtcNow(),ct);
            }
            return true;
        }
        catch(ForgeAuthorizationException){return false;}
        catch(CommandRejectedException){return false;}
    }

    public Task<CommandResult> CreateAsync(CreateQmsPolicyAdoption c,CommandContext context,CancellationToken ct)
    {
        if (c.Content is null) throw new CommandRejectedException("policy_rules_incomplete", "Supply the explicit governed adoption content.");
        if (c.Content.Selector is null) throw new CommandRejectedException("policy_selector", "Supply the explicit adoption selector.");
        var code=Code(c.Code); var facility=BusinessCode.Require(c.FacilityCode,"Facility");
        var resource=new ResourceRef("QmsPolicyAdoption",code,Program(c.ProgramId),c.Markings,FacilityId:facility,WorkCenterId:WorkCenter(c.Content.Selector.WorkCenterCode));
        var spec=new CommandSpec("Quality",nameof(CreateQmsPolicyAdoption),Permissions.QmsManage,resource,ReasonRequired:true);
        return pipeline.ExecuteAsync(spec,c,context,async(uow,now)=>
        {
            Configured(); QmsPolicyRuleValidation.ValidateAdoption(c.Content);
            Markings(c.Markings);
            var store=uow.Store<IQmsPolicyAdoptionStore>();
            await store.LockScopeAsync(options.OrganizationCode!,facility,c.Content.Purpose,false,ct);
            Permit(await CurrentPrincipalAsync(uow,context.Principal,ct),Permissions.QmsManage,resource);
            if(await store.FindAsync(code,ct) is not null) Reject("adoption_exists","This governed adoption already exists.");
            await SelectorSourcesAsync(uow,facility,c.ProgramId,c.Content.Selector,true,ct);
            var sources=await DefinitionSourcesAsync(uow,context.Principal,facility,c.ProgramId,c.Content,false,true,now,ct);
            var revision=Draft(code,1,c.Content,sources,context,now);
            var adoption=new QmsPolicyAdoptionView(code,options.OrganizationCode!,facility,c.ProgramId,c.Markings,context.Actor,now,[revision]);
            await store.InsertAsync(adoption,ct); await store.InsertRevisionAsync(revision,ct);
            return Outcome(adoption,revision,null,"Create",context,[]);
        },ct,async(uow,now)=>
        {
            Configured();
            Markings(c.Markings);
            QmsPolicyRuleValidation.ValidateAdoption(c.Content);
            await uow.Store<IQmsPolicyAdoptionStore>().LockScopeAsync(options.OrganizationCode!,facility,c.Content.Purpose,false,ct);
            if(await uow.Store<IQmsPolicyAdoptionLookup>().FindAsync(code,ct) is{} existing)
            {
                Permit(await CurrentPrincipalAsync(uow,context.Principal,ct),Permissions.QmsManage,Resource(existing,existing.Revisions[^1]));
                await CurrentAuthorityAsync(uow,context.Principal,existing,null,false,now,ct);
                return new CommandAuthority(Resource(existing,existing.Revisions[^1]),options.OrganizationCode+":"+code);
            }
            Permit(await CurrentPrincipalAsync(uow,context.Principal,ct),Permissions.QmsManage,resource);
            await SelectorSourcesAsync(uow,facility,c.ProgramId,c.Content.Selector,true,ct);
            await DefinitionSourcesAsync(uow,context.Principal,facility,c.ProgramId,c.Content,false,true,now,ct);
            return new CommandAuthority(resource,options.OrganizationCode+":"+code);
        });
    }

    public Task<CommandResult> ReviseAsync(ReviseQmsPolicyAdoption c,CommandContext context,CancellationToken ct)=>
        ExecuteAsync(nameof(ReviseQmsPolicyAdoption),c,c.Code,Permissions.QmsManage,c.Content,context,async(uow,adoption,now)=>
        {
            var latest=adoption.Revisions[^1]; Version(latest,c.ExpectedVersion); Purpose(adoption,c.Content);
            if(adoption.Revisions.Any(r=>r.State is QmsState.Draft or QmsState.Submitted or QmsState.Reviewed or QmsState.Approved)) Reject("open_revision","Finish or withdraw the open adoption revision first.");
            var sources=await DefinitionSourcesAsync(uow,context.Principal,adoption.FacilityCode,adoption.ProgramId,c.Content,false,true,now,ct);
            var next=Draft(adoption.Code,checked(latest.Revision+1),c.Content,sources,context,now);
            await uow.Store<IQmsPolicyAdoptionStore>().InsertRevisionAsync(next,ct);
            return Outcome(adoption,next,null,"CreateRevision",context,[]);
        },ct);
    public Task<CommandResult> EditAsync(EditQmsPolicyAdoption c,CommandContext context,CancellationToken ct)=>
        ExecuteAsync(nameof(EditQmsPolicyAdoption),c,c.Code,Permissions.QmsManage,c.Content,context,async(uow,adoption,now)=>
        {
            var old=Revision(adoption,c.Revision); Version(old,c.ExpectedVersion); Purpose(adoption,c.Content);
            if(old.State!=QmsState.Draft) Reject("immutable_revision","Submitted adoption content is immutable; create a new governed revision.");
            var sources=await DefinitionSourcesAsync(uow,context.Principal,adoption.FacilityCode,adoption.ProgramId,c.Content,false,true,now,ct);
            var next=History(old with{Version=checked(old.Version+1),Content=c.Content,LastEditedBy=context.Actor,Sources=sources},old.State,"Edit","Binding revised",context,now);
            await uow.Store<IQmsPolicyAdoptionStore>().UpdateAsync(next,old.Version,true,ct);
            return Outcome(adoption,next,old,"Edit",context,[]);
        },ct);

    public Task<CommandResult> TransitionAsync(TransitionQmsPolicyAdoption c,CommandContext context,CancellationToken ct)
    {
        var permission=c.Action switch{"Submit"=>Permissions.QmsManage,"Review"=>Permissions.QmsReview,"Approve"=>Permissions.QmsApprove,"Effective" or "Withdraw"=>Permissions.QmsPublish,_=>throw new CommandRejectedException("adoption_action","Unknown adoption action.")};
        return ExecuteAsync("QmsPolicyAdoption"+c.Action,c,c.Code,permission,null,context,async(uow,adoption,now)=>
        {
            var old=Revision(adoption,c.Revision); Version(old,c.ExpectedVersion);
            if(string.IsNullOrWhiteSpace(c.Note)||c.Note.Length>4000) Reject("adoption_note","Record the actual human review or applicability decision.");
            var nextState=c.Action switch
            {
                "Submit" when old.State==QmsState.Draft=>QmsState.Submitted,
                "Review" when old.State==QmsState.Submitted=>QmsState.Reviewed,
                "Approve" when old.State==QmsState.Reviewed=>QmsState.Approved,
                "Effective" when old.State==QmsState.Approved=>QmsState.Effective,
                "Withdraw" when old.State is not(QmsState.Withdrawn or QmsState.Superseded)=>QmsState.Withdrawn,
                _=>throw new CommandRejectedException("invalid_status","This action does not follow the adoption's governed state.")
            };
            if((c.Action is "Review" or "Approve")&&(old.CreatedBy==context.Actor||old.LastEditedBy==context.Actor||old.History.Any(h=>h.Actor==context.Actor&&h.Action is "Create" or "Edit")))
                Reject("separation_of_duties","An adoption author cannot review or approve their own operational binding.");
            await SelectorSourcesAsync(uow,adoption.FacilityCode,adoption.ProgramId,old.Content.Selector,c.Action!="Withdraw",ct);
            await DefinitionSourcesAsync(uow,context.Principal,adoption.FacilityCode,adoption.ProgramId,old.Content,c.Action!="Withdraw",true,now,ct);
            var next=old with{Version=checked(old.Version+1),State=nextState,ReviewedBy=c.Action=="Review"?context.Actor:old.ReviewedBy,
                ApprovedBy=c.Action=="Approve"?context.Actor:old.ApprovedBy,EffectiveFrom=c.Action=="Effective"?now:old.EffectiveFrom,
                EffectiveTo=c.Action=="Withdraw"&&old.EffectiveFrom is not null?now:old.EffectiveTo};
            var closed=new List<QmsPolicyAdoptionRevision>();
            if(c.Action=="Effective")
            {
                foreach(var prior in adoption.Revisions.Where(r=>r.State==QmsState.Effective))
                {
                    var superseded=History(prior with{Version=checked(prior.Version+1),State=QmsState.Superseded,EffectiveTo=now},prior.State,"Supersede","Replaced by exact adoption revision "+next.Revision.ToString(CultureInfo.InvariantCulture),context,now);
                    await uow.Store<IQmsPolicyAdoptionStore>().UpdateAsync(superseded,prior.Version,false,ct); closed.Add(superseded);
                    await uow.Outbox.EnqueueAsync(pipeline.Event(new QmsPolicyAdoptionChanged(adoption.Code,prior.Revision,superseded.Version,"Supersede",superseded.State),context,"Quality","QmsPolicyAdoptionRevision",Key(adoption.Code,prior.Revision),Program(adoption.ProgramId),Marks(adoption,superseded)),ct);
                }
            }
            next=History(next,old.State,c.Action,c.Note.Trim(),context,now);
            await uow.Store<IQmsPolicyAdoptionStore>().UpdateAsync(next,old.Version,false,ct);
            return Outcome(adoption,next,old,c.Action,context,closed);
        },ct);
    }

    private Task<CommandResult> ExecuteAsync<T>(string name,T command,string rawCode,string permission,QmsPolicyAdoptionContent? proposed,
        CommandContext context,Func<IKernelUnitOfWork,QmsPolicyAdoptionView,DateTimeOffset,Task<CommandOutcome>> body,CancellationToken ct)
    {
        var code=Code(rawCode); var spec=new CommandSpec("Quality",name,permission,ResourceRef.Of("QmsPolicyAdoption",code),ReasonRequired:true);
        return pipeline.ExecuteAsync(spec,command,context,async(uow,now)=>
        {
            var store=uow.Store<IQmsPolicyAdoptionStore>();
            var header=await FindAsync(uow,code,ct);
            await store.LockScopeAsync(header.OrganizationCode,header.FacilityCode,header.Revisions[0].Content.Purpose,false,ct);
            await store.LockAsync(code,ct);
            var adoption=await FindAsync(uow,code,ct);
            Permit(await CurrentPrincipalAsync(uow,context.Principal,ct),permission,Resource(adoption,adoption.Revisions[^1]));
            await CurrentAuthorityAsync(uow,context.Principal,adoption,proposed,true,now,ct);
            return await body(uow,adoption,now);
        },ct,async(uow,now)=>
        {
            var adoption=await FindAsync(uow,code,ct);
            await uow.Store<IQmsPolicyAdoptionStore>().LockScopeAsync(adoption.OrganizationCode,adoption.FacilityCode,adoption.Revisions[0].Content.Purpose,false,ct);
            Permit(await CurrentPrincipalAsync(uow,context.Principal,ct),permission,Resource(adoption,adoption.Revisions[^1]));
            await CurrentAuthorityAsync(uow,context.Principal,adoption,proposed,true,now,ct);
            var additional=proposed is null?Array.Empty<ResourceRef>():[Resource(adoption,adoption.Revisions[^1]) with{WorkCenterId=WorkCenter(proposed.Selector.WorkCenterCode)}];
            return new CommandAuthority(Resource(adoption,adoption.Revisions[^1]),adoption.OrganizationCode+":"+adoption.Code,additional);
        });
    }
    private async Task CurrentAuthorityAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsPolicyAdoptionView adoption,QmsPolicyAdoptionContent? proposed,
        bool lockSources,DateTimeOffset now,CancellationToken ct)
    {
        principal=await CurrentPrincipalAsync(uow,principal,ct);
        if(adoption.OrganizationCode!=options.OrganizationCode) throw NotFound();
        foreach(var revision in adoption.Revisions) await RecordedSourcesAsync(uow,principal,revision,false,lockSources,now,ct);
        if(proposed is not null)
        {
            Purpose(adoption,proposed); await SelectorSourcesAsync(uow,adoption.FacilityCode,adoption.ProgramId,proposed.Selector,true,ct);
            await DefinitionSourcesAsync(uow,principal,adoption.FacilityCode,adoption.ProgramId,proposed,false,lockSources,now,ct);
        }
    }
    private async Task RecordedSourcesAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsPolicyAdoptionRevision revision,
        bool effective,bool lockSources,DateTimeOffset now,CancellationToken ct)
    {
        foreach(var source in revision.Sources)
        {
            Permit(principal,Permissions.QmsRead,new("QmsRevision",source.Key+"/"+source.Revision,Program(source.ProgramId),source.Markings,FacilityId:source.FacilityCode));
            var kind=source.Key==BusinessCode.Require(revision.Content.PolicyCode,"Policy")?QmsRecordKind.Policy:QmsRecordKind.Process;
            await access.RequireAsync(uow,principal,source.Key,int.Parse(source.Revision,CultureInfo.InvariantCulture),kind,effective,lockSources,now,ct);
        }
    }
    private async Task<IReadOnlyList<QmsSourceReference>> DefinitionSourcesAsync(IKernelUnitOfWork uow,ForgePrincipal principal,string facility,Guid? program,
        QmsPolicyAdoptionContent content,bool effective,bool lockSources,DateTimeOffset now,CancellationToken ct)
    {
        principal=await CurrentPrincipalAsync(uow,principal,ct);
        QmsPolicyRuleValidation.ValidateAdoption(content);
        var policy=await access.RequireAsync(uow,principal,content.PolicyCode,content.PolicyRevision,QmsRecordKind.Policy,effective,lockSources,now,ct);
        if(policy.Record.FacilityCode!=facility||policy.Record.ProgramId is{} p&&p!=program) Reject("adoption_scope","Adoption cannot widen the policy's recorded facility/program scope.");
        if(policy.Revision.Content.Policy?.PolicyKind!=QmsPolicyRuleValidation.Kind(content.Purpose)) Reject("policy_purpose","The exact policy revision has the wrong purpose.");
        if(effective) QmsPolicyRuleValidation.RequireConsumable(content.Purpose,policy.Revision.Content);
        var sources=new List<QmsSourceReference>{Snapshot(policy.Record,policy.Revision,policy.Sources)};
        if(content.Process is{} process)
        {
            var definition=await access.RequireAsync(uow,principal,process.Code,process.Revision,QmsRecordKind.Process,effective,lockSources,now,ct);
            if(definition.Record.FacilityCode!=facility||definition.Record.ProgramId is{} processProgram&&processProgram!=program) Reject("adoption_scope","The declared process must belong to the adoption's actual source scope.");
            sources.Add(Snapshot(definition.Record,definition.Revision,definition.Sources));
        }
        return sources;
    }
    private static async Task SelectorSourcesAsync(IKernelUnitOfWork uow,string facility,Guid? program,QmsOperationSelector selector,bool active,CancellationToken ct)
    {
        var resources=uow.Store<IResourcesLookup>();
        if(!(await resources.ListSitesAsync(ct)).Any(s=>s.Code==facility)) Reject("facility_not_found","The actual facility is unavailable.");
        if(program is{} id&&await uow.Store<IProgramsLookup>().FindProgramAsync(id,ct) is null) Reject("program_not_found","The actual program is unavailable.");
        if(active&&program is{} activeId&&await uow.Store<IProgramsLookup>().FindProgramAsync(activeId,ct) is not{Status:ProgramStatus.Active}) Reject("program_not_found","A new governed scope requires an active program.");
        if(selector.WorkCenterCode is{} workCenter)
        {
            var canonical=BusinessCode.Require(workCenter,"Work center");
            var wc=(active?await resources.FindWorkCenterAsync(canonical,ct):(await resources.ListWorkCentersAsync(ct)).FirstOrDefault(w=>w.Code==canonical))??throw new CommandRejectedException("work_center_not_found","The actual selector work center is unavailable.");
            var area=await resources.FindSafetyAreaAsync(wc.SafetyAreaCode,ct);
            if(area?.SiteCode!=facility) Reject("adoption_scope","The selector work center is outside the actual facility.");
        }
        if(selector.MachineCode is{} machineCode)
        {
            var canonical=BusinessCode.Require(machineCode,"Machine");
            var machine=(active?await resources.FindMachineAsync(canonical,ct):(await resources.ListMachinesAsync(ct)).FirstOrDefault(m=>m.Code==canonical))??throw new CommandRejectedException("machine_not_found","The actual selector machine is unavailable.");
            if((await resources.FindSafetyAreaAsync(machine.SafetyAreaCode,ct))?.SiteCode!=facility||selector.WorkCenterCode is{} wc&&BusinessCode.Require(wc,"Work center")!=machine.WorkCenterCode) Reject("adoption_scope","The machine must match the actual selector work center/facility.");
        }
        if(selector.ActivityCode is{} activity&&!await resources.ActivityTypeExistsAsync(BusinessCode.Require(activity,"Activity"),ct)) Reject("activity_not_found","The actual selector activity is unavailable.");
        if(selector.PartNumber is{} part)
        {
            var engineering=uow.Store<IEngineeringLookup>();
            if(await engineering.FindItemAsync(BusinessCode.Require(part,"Part"),ct) is null) Reject("item_not_found","The actual selector part is unavailable.");
            if(selector.PartRevision is{} revision&&!(await engineering.ListRevisionsAsync(BusinessCode.Require(part,"Part"),ct)).Any(r=>r.Revision==QmsContentRules.DocumentRevision(revision))) Reject("revision_not_found","The exact selector item revision is unavailable.");
        }
    }
    private async Task<QmsPolicyAdoptionView> FindAsync(IKernelUnitOfWork uow,string code,CancellationToken ct)
    {
        Configured(); var adoption=await uow.Store<IQmsPolicyAdoptionLookup>().FindAsync(code,ct);
        return adoption is not null&&adoption.OrganizationCode==options.OrganizationCode?adoption:throw NotFound();
    }
    public static ResourceRef Resource(QmsPolicyAdoptionView adoption,QmsPolicyAdoptionRevision revision)=>
        new("QmsPolicyAdoption",adoption.Code,Program(adoption.ProgramId),Marks(adoption,revision),FacilityId:adoption.FacilityCode,WorkCenterId:WorkCenter(revision.Content.Selector.WorkCenterCode));
    private CommandOutcome Outcome(QmsPolicyAdoptionView adoption,QmsPolicyAdoptionRevision next,QmsPolicyAdoptionRevision? old,string action,CommandContext context,IReadOnlyList<QmsPolicyAdoptionRevision> closed)=>
        new(CommandResult.Ok(Key(adoption.Code,next.Revision),$"Governed policy adoption {adoption.Code} revision {next.Revision}: {next.State}."),
            [pipeline.Event(new QmsPolicyAdoptionChanged(adoption.Code,next.Revision,next.Version,action,next.State),context,"Quality","QmsPolicyAdoptionRevision",Key(adoption.Code,next.Revision),Program(adoption.ProgramId),Marks(adoption,next))],
            new AuditChange("QmsPolicyAdoptionRevision",Key(adoption.Code,next.Revision),old is null?null:CanonicalJson.Serialize(new{revision=old,priorEffective=adoption.Revisions.Where(r=>r.State==QmsState.Effective)}),CanonicalJson.Serialize(new{revision=next,closed}),Program(adoption.ProgramId),Marks(adoption,next)));
    private static QmsPolicyAdoptionRevision Draft(string code,int revision,QmsPolicyAdoptionContent content,IReadOnlyList<QmsSourceReference> sources,CommandContext context,DateTimeOffset now)=>
        new(code,revision,1,QmsState.Draft,content,context.Actor,context.Actor,null,null,null,null,sources,[new("Create",context.Actor,now,"Binding draft created",context.Reason!.Trim(),null,QmsState.Draft,1)]);
    private static QmsPolicyAdoptionRevision History(QmsPolicyAdoptionRevision revision,QmsState from,string action,string note,CommandContext context,DateTimeOffset now)=>
        revision with{History=revision.History.Append(new QmsTransition(action,context.Actor,now,note,context.Reason!.Trim(),from,revision.State,revision.Version)).ToArray()};
    private static QmsSourceReference Snapshot(QmsRecordView record,QmsRevisionView revision,IReadOnlyList<QmsPolicyReadSource> sources)=>
        new("QmsRevision",record.Code,revision.Revision.ToString(CultureInfo.InvariantCulture),record.ProgramId,sources.Aggregate(record.Markings,(marks,source)=>marks|source.Resource.Markings),record.FacilityCode);
    private void Permit(ForgePrincipal principal,string permission,ResourceRef resource){var decision=authorizer.Authorize(principal,permission,resource);if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision,resource);}
    private void Configured(){if(!options.Configured)Reject("qms_not_configured","Configure the explicit organization before governing policy adoption.");}
    private static void Purpose(QmsPolicyAdoptionView adoption,QmsPolicyAdoptionContent content){QmsPolicyRuleValidation.ValidateAdoption(content);if(content.Purpose!=adoption.Revisions[0].Content.Purpose)Reject("policy_purpose","An adoption retains its purpose; create a separate governed binding for another action.");}
    private static QmsPolicyAdoptionRevision Revision(QmsPolicyAdoptionView adoption,int revision)=>adoption.Revisions.FirstOrDefault(r=>r.Revision==revision)??throw NotFound();
    private static void Version(QmsPolicyAdoptionRevision revision,int expected){if(revision.Version!=expected)Reject("stale_version","The adoption changed; read its current version before deciding.");}
    private static DataMarking Marks(QmsPolicyAdoptionView adoption,QmsPolicyAdoptionRevision revision)=>revision.Sources.Aggregate(adoption.Markings,(marks,source)=>marks|source.Markings);
    private static ProgramId? Program(Guid? id)=>id is{} value?new ProgramId(value):null;
    private static string Key(string code,int revision)=>code+"/"+revision.ToString(CultureInfo.InvariantCulture);
    private static string Code(string code)=>BusinessCode.Require(code,"Adoption");
    private static string? WorkCenter(string? code)=>code is null?null:BusinessCode.Require(code,"Work center");
    private static async Task<ForgePrincipal> CurrentPrincipalAsync(IKernelUnitOfWork uow,ForgePrincipal principal,CancellationToken ct)=>
        await uow.People.LoadPrincipalAsync(principal.PersonId,ct)??throw new CommandRejectedException("governed_policy_consumer_unavailable","The current registered person is unavailable.");
    private static void Markings(DataMarking markings){if((markings&~(DataMarking.ControlledUnclassified|DataMarking.ExportControlled|DataMarking.Proprietary))!=0)Reject("invalid_markings","Select recognized handling markings.");}
    private static CommandRejectedException NotFound()=>new("adoption_not_found","The governed adoption or revision is unavailable.");
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(string code,string message)=>throw new CommandRejectedException(code,message);
}
