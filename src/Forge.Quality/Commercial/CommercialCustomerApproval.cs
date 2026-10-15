using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Quality.Commercial;

public sealed partial class CommercialQualityService
{
    public async Task<CommercialCustomerApproval?> RequireAsync(IKernelUnitOfWork uow,ForgePrincipal principal,
        AcceptanceSubjectKey subject,string revision,long ncrId,decimal quantity,Disposition disposition,DateTimeOffset at,CancellationToken ct)
    {
        if(disposition is not(Disposition.UseAsIs or Disposition.Repair)||quantity<=0)
            throw new CommandRejectedException("customer_concession_scope","Customer consent applies to an exact positive use-as-is or repair quantity.");
        var requested=new CustomerConcessionScope(AcceptanceSubjectReference.Normalize(subject),revision,ncrId,quantity,disposition);
        var actual=await sources.CustomerConcessionContextAsync(uow,principal,requested,ct);
        foreach(var source in actual.CustomerSources.OrderBy(r=>r.Kind).ThenBy(r=>r.Key,StringComparer.Ordinal))
            await uow.Store<ICommercialQualityStore>().LockScopeAsync(CommercialRecordKind.CustomerRequirements,
                source.Kind==CommercialReferenceKind.Contract?"contract:"+source.Key:"shipment:"+source.Key,true,ct);
        bool Match(CommercialRevision record)=>record.Content.CustomerRequirements is{} requirement
            &&actual.CustomerSources.Any(s=>requirement.ContractId is{} contract?s.Kind==CommercialReferenceKind.Contract&&s.Key==contract.ToString(CultureInfo.InvariantCulture):s==requirement.CustomerSource)
            &&(requirement.PartNumber is null||requirement.PartNumber==subject.PartNumber)
            &&(requirement.PartRevision is null||requirement.PartRevision==revision);
        var requirements=await ApplicableAsync(uow,principal,CommercialRecordKind.CustomerRequirements,Match,at,ct);
        if(requirements.Count==0)
        {
            // An ever-governed customer cannot escape an explicit configuration by moving to an uncovered revision.
            var known=(await uow.Store<ICommercialQualityLookup>().EverEffectiveAsync(CommercialRecordKind.CustomerRequirements,ct))
                .Any(r=>r.Content.CustomerRequirements is{} c&&actual.CustomerSources.Any(s=>c.ContractId is{} contract?s.Kind==CommercialReferenceKind.Contract&&s.Key==contract.ToString(CultureInfo.InvariantCulture):s==c.CustomerSource));
            if(known)throw new CommandRejectedException("customer_requirements_scope_unapproved","Current governed customer requirements do not cover this physical configuration.");
            return null;
        }
        var determinations=requirements.Select(r=>r.Content.CustomerRequirements!.RequireCustomerConcessionForNonconformingRelease).Distinct().ToArray();
        if(determinations.Length!=1||determinations[0] is null)
            throw new CommandRejectedException("customer_concession_ambiguous","Resolve the governed customer's explicit consent determination.");
        var requirementEvidence=Evidence("CustomerConcessionDetermination",requirements);
        if(determinations[0]==false)return Approval(false,null,null);
        await uow.Store<ICommercialQualityStore>().LockScopeAsync(CommercialRecordKind.Communication,ConcessionScopeKey(requested),true,ct);
        bool CommunicationMatches(CommercialRevision record)=>record.Content.Communication is{Concession:{} offered} communication
            &&offered.Subject==requested.Subject&&offered.Revision==revision&&offered.NcrId==ncrId&&offered.Disposition==disposition&&offered.Quantity==quantity
            &&actual.CustomerSources.Contains(communication.PartySource);
        var candidates=await ApplicableAsync(uow,principal,CommercialRecordKind.Communication,CommunicationMatches,at,ct);
        if(candidates.Count!=1)throw new CommandRejectedException("customer_approval_required","Exactly one independently approved current proposal and observed external customer approval must cover this departure.");
        var communication=candidates[0];
        var received=communication.Observations.Where(o=>o.Kind==CommercialObservationKind.CustomerApprovalReceived&&o.ObservedAt<=at)
            .OrderBy(o=>o.Number).LastOrDefault()??throw new CommandRejectedException("customer_approval_required","Internal approval is not the customer's received concession.");
        await sources.ValidateObservationAsync(uow,principal,communication,received.Kind,received.Evidence,received.ObservedAt,ct);
        return Approval(true,communication,received);

        CommercialCustomerApproval Approval(bool required,CommercialRevision? communication,CommercialObservation? observation)
        {
            var readSources=actual.Sources.Concat(requirementEvidence.Sources).Concat(communication?.Sources??[]).Concat(observation?.Sources??[]).Distinct().ToArray();
            var ownership=actual.Ownership.Concat(requirementEvidence.Ownership).Concat(communication?.Ownership??[]).Concat(observation?.Ownership??[]).Distinct().ToArray();
            var evidence=new{required,requested,ContextFingerprint=actual.Fingerprint,Requirements=requirements,Communication=communication,ReceivedApproval=observation,Sources=readSources,Ownership=ownership};
            return new(required,requested,requirements,communication,observation,readSources,ownership,CanonicalJson.Fingerprint(evidence));
        }
    }
}
