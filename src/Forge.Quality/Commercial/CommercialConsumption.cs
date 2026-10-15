using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Quality.Commercial;

public sealed record CommercialConsumerContext(string OwnerType,string OwnerId,int? LineNumber,string? ProcessCode,string? WorkCenterCode);
public sealed record CommercialUseEvidence(string Activity,IReadOnlyList<CommercialRevision> Records,
    IReadOnlyList<CommercialReadSource> Sources,IReadOnlyList<ResourceRef> Ownership,string Fingerprint,
    IReadOnlyList<CommercialConsumerContext>? Contexts=null);
public sealed record PropertyUseRequest(PropertySubject Subject,string Activity,string? WorkCenterCode,long? OrderNumber,bool Consumptive,string? FacilityCode=null);
public sealed partial class CommercialQualityService
{
    public static Task LockProviderScopeAsync(IKernelUnitOfWork uow,string supplier,CancellationToken ct)=>
        uow.Store<ICommercialQualityStore>().LockScopeAsync(CommercialRecordKind.ProviderEvaluation,"supplier:"+supplier,true,ct);
    public async Task<CommercialUseEvidence?> RequireProviderAsync(IKernelUnitOfWork uow,ForgePrincipal principal,string supplier,
        string part,string? revision,string? process,string? center,bool external,DateTimeOffset at,CancellationToken ct)
    {
        await uow.Store<ICommercialQualityStore>().LockScopeAsync(CommercialRecordKind.ProviderEvaluation,"supplier:"+supplier,true,ct);
        bool Match(CommercialRevision r)=>r.Content.ProviderEvaluation is{} p&&p.SupplierCode==supplier
            &&(external?p.Scope.EnforceAtExternalDispatch:p.Scope.EnforceAtPurchaseApproval)
            &&(p.Scope.PartNumber is null||p.Scope.PartNumber==part)&&(p.Scope.PartRevision is null||revision is null||p.Scope.PartRevision==revision)
            &&(p.Scope.ProcessCode is null||process is null||p.Scope.ProcessCode==process)&&(p.Scope.WorkCenterCode is null||center is null||p.Scope.WorkCenterCode==center);
        var applicable=await ApplicableAsync(uow,principal,CommercialRecordKind.ProviderEvaluation,Match,at,ct);
        if(applicable.Count==0)
        {
            if((await uow.Store<ICommercialQualityLookup>().EverEffectiveAsync(CommercialRecordKind.ProviderEvaluation,ct)).Any(r=>r.Content.ProviderEvaluation is{} prior&&prior.SupplierCode==supplier&&(external?prior.Scope.EnforceAtExternalDispatch:prior.Scope.EnforceAtPurchaseApproval)))
                throw new CommandRejectedException("provider_scope_not_approved","The governed supplier has no approved qualification for this actual scope.");
            return null;
        }
        if(applicable.Count!=1)throw new CommandRejectedException("provider_scope_ambiguous","Exactly one current provider qualification must match the actual purchase/process scope.");
        var provider=applicable[0].Content.ProviderEvaluation!;
        if(provider.Scope.PartRevision is not null&&revision is null||provider.Scope.ProcessCode is not null&&process is null||provider.Scope.WorkCenterCode is not null&&center is null)
            throw new CommandRejectedException("provider_scope_unknown","Supply the actual governed process/work-center context and recorded configuration required by this provider scope.");
        if(at<provider.ValidFrom||at>=provider.ValidTo||at>=provider.ReevaluateBy||!CommercialRules.Evaluate(provider).MeetsApprovedCriteria)
            throw new CommandRejectedException("provider_reevaluation_required","Current provider qualification does not permit this scope and time.");
        return Evidence(external?"ExternalDispatch":"PurchaseApproval",applicable);
    }
    public async Task<CommercialUseEvidence?> RequireCustomerAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long contractId,
        string part,string revision,bool shipment,DateTimeOffset at,CancellationToken ct)
    {
        await uow.Store<ICommercialQualityStore>().LockScopeAsync(CommercialRecordKind.CustomerRequirements,"contract:"+contractId.ToString(System.Globalization.CultureInfo.InvariantCulture),true,ct);
        bool Match(CommercialRevision r)=>r.Content.CustomerRequirements is{} c&&c.ContractId==contractId
            &&(shipment?c.EnforceAtShipment:c.EnforceAtOrderRelease)&&(c.PartNumber is null||c.PartNumber==part)
            &&(c.PartRevision is null||c.PartRevision==revision);
        var applicable=await ApplicableAsync(uow,principal,CommercialRecordKind.CustomerRequirements,Match,at,ct);
        if(applicable.Count==0&&(await uow.Store<ICommercialQualityLookup>().EverEffectiveAsync(CommercialRecordKind.CustomerRequirements,ct))
            .Any(r=>r.Content.CustomerRequirements is{} prior&&prior.ContractId==contractId&&(shipment?prior.EnforceAtShipment:prior.EnforceAtOrderRelease)))
            throw new CommandRejectedException("customer_requirements_scope_unapproved","The governed contract has no accepted requirements for this actual product configuration.");
        return applicable.Count==0?null:Evidence(shipment?"Shipment":"OrderRelease",applicable);
    }
    public async Task<CommercialUseEvidence?> RequireDesignAsync(IKernelUnitOfWork uow,ForgePrincipal principal,string part,string revision,DateTimeOffset at,CancellationToken ct)
    {
        await uow.Store<ICommercialQualityStore>().LockScopeAsync(CommercialRecordKind.DesignApplicability,"part:"+part,true,ct);
        var applicable=await ApplicableAsync(uow,principal,CommercialRecordKind.DesignApplicability,r=>r.Content.DesignApplicability is{} d&&d.PartNumber==part&&d.Revision==revision,at,ct);
        if(applicable.Count==0&&(await uow.Store<ICommercialQualityLookup>().EverEffectiveAsync(CommercialRecordKind.DesignApplicability,ct)).Any(r=>r.Content.DesignApplicability?.PartNumber==part))
            throw new CommandRejectedException("design_applicability_required","Record the actual design applicability/control decision for this new governed configuration.");
        if(applicable.Count>1)throw new CommandRejectedException("design_applicability_ambiguous","Resolve the overlapping design applicability decisions.");
        return applicable.Count==0?null:Evidence("DesignApplicability",applicable);
    }
    public async Task<CommercialUseEvidence?> RequirePropertyAsync(IKernelUnitOfWork uow,ForgePrincipal principal,PropertyUseRequest use,DateTimeOffset at,CancellationToken ct)
    {
        CommercialRules.Subject(use.Subject);
        await uow.Store<ICommercialQualityStore>().LockScopeAsync(CommercialRecordKind.PropertyAgreement,PropertyKey(use.Subject),true,ct);
        await uow.Store<ICommercialQualityStore>().LockScopeAsync(CommercialRecordKind.PropertyIncident,"all-incidents",true,ct);
        var applicable=await ApplicableAsync(uow,principal,CommercialRecordKind.PropertyAgreement,r=>r.Content.PropertyAgreement?.Subject==use.Subject,at,ct);
        if(applicable.Count==0)return null;
        if(applicable.Count!=1)throw new CommandRejectedException("property_agreement_ambiguous","Resolve overlapping legal property agreements for this existing identity.");
        var record=applicable[0];var agreement=record.Content.PropertyAgreement!;
        if(use.FacilityCode is null||use.FacilityCode!=record.FacilityCode)
            throw new CommandRejectedException("property_scope_unknown","Use must resolve to the actual facility named by this legal property agreement.");
        if(at<agreement.ValidFrom||at>=agreement.ValidTo||!agreement.PermittedActivities.Contains(use.Activity,StringComparer.Ordinal)
            ||agreement.PermittedWorkCenters.Count>0&&(use.WorkCenterCode is null||!agreement.PermittedWorkCenters.Contains(use.WorkCenterCode,StringComparer.Ordinal))
            ||agreement.PermittedOrders.Count>0&&(use.OrderNumber is null||!agreement.PermittedOrders.Contains(use.OrderNumber.Value))
            ||use.Consumptive&&!agreement.AllowConsumptiveUse)
            throw new CommandRejectedException("property_use_not_agreed","Current legal property terms do not authorize this exact activity, center, order or consumptive use.");
        var incidents=(await uow.Store<ICommercialQualityLookup>().EffectiveAsync(CommercialRecordKind.PropertyIncident,ct))
            .Where(r=>r.Content.PropertyIncident is{} incident&&incident.AgreementCode==record.Code&&incident.AgreementRevision==record.Revision).ToArray();
        if(incidents.Length>0)throw new CommandRejectedException("property_incident_open","Resolve the controlled loss, damage, unsuitability or preservation incident before further use.");
        var custody=record.Observations.Where(o=>o.ObservedAt<=at&&(o.Kind is CommercialObservationKind.PropertyReceipt or CommercialObservationKind.PropertyReturn))
            .OrderBy(o=>o.ObservedAt).ThenBy(o=>o.Number).LastOrDefault();
        if(custody is null)throw new CommandRejectedException("property_receipt_evidence_required","Record receipt of this exact legal property against its actual owning event before use.");
        if(custody.Kind==CommercialObservationKind.PropertyReturn)
            throw new CommandRejectedException("property_returned","The recorded property has been returned to its legal owner.");
        if(agreement.Preservation.Count>0)
        {
            var observation=record.Observations.Where(o=>o.Kind==CommercialObservationKind.PreservationObservation&&o.ObservedAt>=custody.ObservedAt&&o.ObservedAt<=at)
                .OrderBy(o=>o.ObservedAt).ThenBy(o=>o.Number).LastOrDefault();
            foreach(var control in agreement.Preservation)
            {
                var reading=observation?.Preservation?.FirstOrDefault(r=>r.ControlCode==control.Code);
                if(reading is null||reading.Accepted==false||reading.Value is{} value&&(control.Minimum is{} minimum&&value<minimum||control.Maximum is{} maximum&&value>maximum))
                    throw new CommandRejectedException("property_preservation_not_met","Current recorded preservation evidence does not permit use of the property.");
            }
        }
        return Evidence("Property:"+use.Activity,applicable);
    }
    private async Task<IReadOnlyList<CommercialRevision>> ApplicableAsync(IKernelUnitOfWork uow,ForgePrincipal principal,CommercialRecordKind kind,
        Func<CommercialRevision,bool> matches,DateTimeOffset at,CancellationToken ct)
    {
        principal=await CurrentPrincipalAsync(uow,principal,ct);
        var ever=(await uow.Store<ICommercialQualityLookup>().EverEffectiveAsync(kind,ct)).Where(matches).Select(r=>r.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var result=new List<CommercialRevision>();
        foreach(var code in ever)
        {
            var revisions=await uow.Store<ICommercialQualityLookup>().RevisionsAsync(kind,code,ct);
            var current=revisions.SingleOrDefault(r=>r.State==CommercialState.Effective&&r.EffectiveFrom<=at&&(r.EffectiveTo is null||at<r.EffectiveTo));
            if(current is null||!matches(current))throw new CommandRejectedException("commercial_binding_unavailable","An applicable governed binding has no current authorized effective successor.");
            await RequireReadSourcesAsync(uow,principal,current,ct);
            var resolved=await sources.ResolveAsync(uow,principal,current.FacilityCode,current.Kind,current.Content,ct);
            if(resolved.Fingerprint!=current.SourceFingerprint)throw new CommandRejectedException("commercial_source_changed","Actual governed source configuration changed; independently review a new revision before use.");
            PermitAll(principal,Permissions.QualityRead,await ResourcesAsync(uow,"CommercialQuality",Key(current.Kind,current.Code,current.Revision),ct)??throw Missing());
            result.Add(current);
        }
        return result;
    }
    public static CommercialUseEvidence Combine(string activity,IEnumerable<CommercialUseEvidence?> pieces)
    {
        var actual=pieces.Where(p=>p is not null).Cast<CommercialUseEvidence>().ToArray();
        var records=actual.SelectMany(p=>p.Records).DistinctBy(r=>Key(r.Kind,r.Code,r.Revision)).ToArray();
        var reads=actual.SelectMany(p=>p.Sources).Distinct().ToArray();var ownership=actual.SelectMany(p=>p.Ownership).Distinct().ToArray();
        var contexts=actual.SelectMany(p=>p.Contexts??[]).Distinct().ToArray();
        return new(activity,records,reads,ownership,CanonicalJson.Fingerprint(new{activity,Records=records.Select(r=>new{r.Kind,r.Code,r.Revision,r.Version,r.SourceFingerprint}),Sources=reads,Ownership=ownership,Contexts=contexts}),contexts);
    }
    private static CommercialUseEvidence Evidence(string activity,IReadOnlyList<CommercialRevision> records)=>Combine(activity,[new(activity,records,
        records.SelectMany(r=>r.Sources).Concat(records.SelectMany(r=>r.Observations.SelectMany(o=>o.Sources))).Distinct().ToArray(),
        records.SelectMany(r=>r.Ownership).Distinct().ToArray(),string.Empty)]);
}

