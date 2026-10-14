using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Quality.Qms;

/// <summary>No request chooses a policy or risk class. Shared locks permit concurrent actions and serialize governance changes.</summary>
public sealed class QmsPolicyReader(QmsOptions options,QmsService qms,IForgeAuthorizer authorizer) : IQmsPolicyReader
{
    private readonly QmsPolicyCurrentAccess access=new(qms,authorizer);

    public async Task<QmsAdoptedPolicy> RequireAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsPolicyPurpose purpose,
        QmsPolicyContext context,DateTimeOffset at,CancellationToken ct)=>
        await TryAsync(uow,principal,purpose,context,at,ct)??throw new CommandRejectedException("governed_policy_not_adopted","Adopt a complete governed policy for this actual source scope before using the feature.");

    public async Task<QmsAdoptedPolicy?> TryAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsPolicyPurpose purpose,
        QmsPolicyContext context,DateTimeOffset at,CancellationToken ct)
    {
        QmsPolicyRuleValidation.Kind(purpose);
        var facility=BusinessCode.Require(context.FacilityCode,"Facility");
        if(facility!=context.FacilityCode) throw new CommandRejectedException("policy_context","Supply canonical facts resolved by the source owner.");
        var store=uow.Store<IQmsPolicyAdoptionStore>();
        var all=await store.ListAsync(ct);
        bool Matches(QmsPolicyAdoptionView adoption,QmsPolicyAdoptionRevision revision)=>QmsPolicyRuleValidation.ScopeMatches(adoption,revision.Content,purpose,context);
        bool Potential(QmsPolicyAdoptionView adoption,QmsPolicyAdoptionRevision revision)=>QmsPolicyRuleValidation.ScopeMayMatch(adoption,revision.Content,purpose,context);
        var known=all.Where(a=>a.Revisions.Any(r=>r.EffectiveFrom is not null&&Matches(a,r))).ToArray();
        var unknown=all.Any(a=>a.Revisions.Any(r=>r.EffectiveFrom is not null&&Potential(a,r)&&!Matches(a,r)));
        // Unconfigured deployment with no binding history supplies no invented organization or operational controls.
        if(!options.Configured)
        {
            if(known.Length==0&&!unknown) return null;
            throw new CommandRejectedException("qms_not_configured","An existing governed binding requires its explicitly configured deployment organization.");
        }
        await store.LockScopeAsync(options.OrganizationCode!,facility,purpose,true,ct);
        all=await store.ListAsync(ct);
        if(all.Any(a=>a.Revisions.Any(r=>r.EffectiveFrom is not null&&Potential(a,r)&&!Matches(a,r))))
            throw new CommandRejectedException("governed_policy_scope_unknown","An unknown/mixed actual source dimension could match a historically effective narrow binding. Resolve that actual context before proceeding.");
        known=all.Where(a=>a.Revisions.Any(r=>r.EffectiveFrom is not null&&Matches(a,r))).ToArray();
        if(known.Length==0) return null;
        if(known.Any(a=>!a.Revisions.Any(r=>Matches(a,r)&&r.State==QmsState.Effective&&r.EffectiveFrom<=at&&(r.EffectiveTo is null||at<r.EffectiveTo))))
            throw new CommandRejectedException("governed_policy_unavailable","A historically effective binding has no valid current successor for this actual scope; another policy cannot silently replace it.");
        var matches=known.SelectMany(a=>a.Revisions.Where(r=>Matches(a,r)&&r.State==QmsState.Effective&&r.EffectiveFrom<=at&&(r.EffectiveTo is null||at<r.EffectiveTo)).Select(r=>(Adoption:a,Revision:r))).ToArray();
        if(matches.Length>1) throw new CommandRejectedException("governed_policy_ambiguous","Multiple effective governance bindings match the actual source. No weaker policy is selected.");
        if(matches.Length==0) throw new CommandRejectedException("governed_policy_unavailable","A known governance binding is unavailable or withdrawn; it cannot be treated as unadopted.");
        principal=await uow.People.LoadPrincipalAsync(principal.PersonId,ct)
            ??throw new CommandRejectedException("governed_policy_consumer_unavailable","The current registered operating person is unavailable.");
        var selected=matches[0];
        await store.LockReadAsync(selected.Adoption.Code,selected.Revision.Revision,ct);
        var header=await store.FindAsync(selected.Adoption.Code,ct)??throw Unavailable();
        var binding=header.Revisions.FirstOrDefault(r=>r.Revision==selected.Revision.Revision)??throw Unavailable();
        if(header.OrganizationCode!=options.OrganizationCode || binding.State!=QmsState.Effective || binding.ReviewedBy is null || binding.ApprovedBy is null
            || !binding.History.Any(h=>h.Action=="Review"&&h.Actor==binding.ReviewedBy)
            || !binding.History.Any(h=>h.Action=="Approve"&&h.Actor==binding.ApprovedBy)) throw Unavailable();
        var resource=QmsPolicyAdoptionService.Resource(header,binding);
        var decision=authorizer.Authorize(principal,Permissions.QmsRead,resource);
        if(!decision.IsPermitted) throw new ForgeAuthorizationException(decision,resource);
        var policy=await access.RequireAsync(uow,principal,binding.Content.PolicyCode,binding.Content.PolicyRevision,QmsRecordKind.Policy,true,true,at,ct);
        if(policy.Record.FacilityCode!=header.FacilityCode || policy.Record.ProgramId is{} program && program!=header.ProgramId) throw Unavailable();
        QmsPolicyRuleValidation.RequireConsumable(purpose,policy.Revision.Content);
        var sources=new List<QmsPolicyReadSource>{new(Permissions.QmsRead,resource)};
        sources.AddRange(policy.Sources);
        foreach(var source in binding.Sources)
        {
            var recorded=new ResourceRef("QmsRevision",source.Key+"/"+source.Revision,source.ProgramId is{} p?new ProgramId(p):null,source.Markings,FacilityId:source.FacilityCode);
            var recordedDecision=authorizer.Authorize(principal,Permissions.QmsRead,recorded);
            if(!recordedDecision.IsPermitted) throw new ForgeAuthorizationException(recordedDecision,recorded);
            sources.Add(new(Permissions.QmsRead,recorded));
        }
        if(binding.Content.Process is{} process)
        {
            var definition=await access.RequireAsync(uow,principal,process.Code,process.Revision,QmsRecordKind.Process,true,true,at,ct);
            if(definition.Record.FacilityCode!=header.FacilityCode || definition.Record.ProgramId is{} p && p!=header.ProgramId) throw Unavailable();
            sources.AddRange(definition.Sources);
        }
        var rules=policy.Revision.Content.Policy!.Rules!;
        QmsOperationScanRule? operation=null;
        if(purpose==QmsPolicyPurpose.Execution) operation=QmsPolicyRuleValidation.MatchOperationRule(rules.Execution!.Presentation!,binding.Content,context);
        var fingerprint=CanonicalJson.Fingerprint(new{organization=header.OrganizationCode,adoptionCode=header.Code,adoptionRevision=binding.Revision,adoptionVersion=binding.Version,
            adoptionContent=binding.Content,policyCode=policy.Record.Code,policyRevision=policy.Revision.Revision,policyVersion=policy.Revision.Version,policyContent=policy.Revision.Content,policy.Revision.Sources,operation});
        return new(header.Code,binding.Revision,binding.Version,policy.Record.Code,policy.Revision.Revision,policy.Revision.Version,header.OrganizationCode,context,
            binding.Content.Selector,binding.Content.Process,binding.Content.RiskClassCode,binding.Content.RiskBasis,binding.EffectiveFrom!.Value,binding.EffectiveTo,
            sources.Aggregate(header.Markings,(marks,source)=>marks|source.Resource.Markings),rules,operation,fingerprint,sources.Distinct().ToArray());
    }
    private static CommandRejectedException Unavailable()=>new("governed_policy_unavailable","The exact approved governance binding is no longer available for this actual source scope.");
}
