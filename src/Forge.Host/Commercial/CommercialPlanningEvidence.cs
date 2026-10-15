using System.Globalization;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Commercial;
using Forge.Quality.Contracts;

namespace Forge.Host.Commercial;

/// <summary>Exact commercial owner values with current and retained mixed authority, in the reader's UOW.</summary>
public sealed class CommercialPlanningEvidence(IServiceProvider services,TimeProvider time,IForgeAuthorizer authorizer):IQmsPlanningOwnerEvidenceResolver
{
    public bool Owns(string context,string type)=>context=="Quality"&&type is "CommercialQuality" or "CommercialObservation" or "CommercialUse"
        ||context=="Inventory"&&type is "CustomerReturn" or "ReturnInspectionCohort";
    public async Task<QmsPlanningSourceSnapshot> ResolveAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsPlanningEvidenceReference reference,
        bool lockSource,Func<QmsPlanningEvidenceReference,bool,Task<QmsPlanningSourceSnapshot>> resolveSource,CancellationToken ct)
    {
        principal=await uow.People.LoadPrincipalAsync(principal.PersonId,ct) is{IsActive:true} current?current:throw Missing();
        object value;DateTimeOffset? created=null,approved=null,closed=null;
        var sources=new List<QmsPlanningReadSource>();var contributors=new List<string>();
        var quality=services.GetRequiredService<CommercialQualityService>();
        var owner=services.GetRequiredService<ICommercialQualitySourceResolver>();
        if(reference.Type is "CommercialQuality" or "CommercialObservation")
        {
            var fields=reference.Key.Split('/');
            var isObservation=reference.Type=="CommercialObservation";
            if(fields.Length!=(isObservation?4:2)||!Enum.TryParse<CommercialRecordKind>(fields[0],out var kind)||!Enum.IsDefined(kind))throw Missing();
            var revision=Number(isObservation?fields[2]:reference.Revision);
            var record=await uow.Store<ICommercialQualityLookup>().FindAsync(kind,fields[1],revision,ct)??throw Missing();
            if(lockSource)
            {
                await CommercialQualityService.LockEvidenceSourceAsync(uow,record,ct);
                record=await uow.Store<ICommercialQualityLookup>().FindAsync(kind,record.Code,revision,ct)??throw Missing();
            }
            if(isObservation)
            {
                if(reference.Revision is not null||reference.AuditId is not null||reference.EventId is not null)throw Missing();
                var number=Number(fields[3]);var observation=record.Observations.FirstOrDefault(o=>o.Number==number)??throw Missing();
                sources.AddRange((await quality.AuthorizedSnapshotSourcesAsync(uow,principal,record with{Observations=[observation]},ct)).Select(Convert));
                value=new{record.Kind,record.Code,record.Revision,record.Content,Observation=observation};
                created=observation.ObservedAt;contributors.Add(observation.Actor);
            }
            else
            {
                sources.AddRange((await quality.AuthorizedSnapshotSourcesAsync(uow,principal,record,ct)).Select(Convert));value=record;created=record.CreatedAt;
                contributors.AddRange(record.History.Select(h=>h.Actor));contributors.AddRange(record.Observations.Select(o=>o.Actor));
                closed=record.History.LastOrDefault(h=>h.Action=="Close")?.At;
            }
            approved=record.History.LastOrDefault(h=>h.Action=="Approve")?.At;contributors.Add(record.CreatedBy);
        }
        else if(reference.Type=="CommercialUse")
        {
            if(reference.Revision is not null||reference.EventId is not null||reference.AuditId is not null||!Guid.TryParseExact(reference.Key,"D",out var id)||id.ToString("D")!=reference.Key)throw Missing();
            var use=await uow.Store<ICommercialUseLookup>().FindAsync(id,ct)??throw Missing();
            if(!await quality.CanReadAuditAsync(uow,principal,use.AuditId,ct))throw Missing();
            foreach(var record in use.Evidence.Records)
                sources.AddRange((await quality.AuthorizedSnapshotSourcesAsync(uow,principal,record,ct)).Select(Convert));
            await owner.RequireCapturedAsync(uow,principal,use.Evidence.Sources,ct);sources.AddRange(use.Evidence.Sources.Select(Convert));
            // The actual audit belongs to the business owner, not to the supplemental use UUID.
            var linked=await resolveSource(new(Context(use.EntityType),use.EntityType,use.EntityId,AuditId:use.AuditId),lockSource);
            sources.AddRange(linked.Sources);value=use;created=use.At;approved=use.At;contributors.Add(use.Actor);
        }
        else if(reference.Type=="CustomerReturn")
        {
            if(reference.Revision is not null)throw Missing();
            var rma=await uow.Store<IReceivingLookup>().FindCustomerReturnAsync(Long(reference.Key),ct)??throw Missing();
            var location=await uow.Store<IInventoryLookup>().FindLocationAsync(rma.LocationCode,ct)??throw Missing();
            var scope=await owner.ResolveReferencesAsync(uow,principal,location.SiteCode,[new(CommercialReferenceKind.CustomerReturn,reference.Key)],ct);
            sources.AddRange(scope.Sources.Select(Convert));value=rma;created=rma.ReceivedAt;closed=rma.DispositionedAt;contributors.Add(rma.ReceivedBy);
            if(rma.DispositionedBy is{} disposer)contributors.Add(disposer);
        }
        else if(reference.Type=="ReturnInspectionCohort")
        {
            if(reference.Revision is not null)throw Missing();
            var cohort=await uow.Store<IReturnAcceptanceLookup>().FindCohortAsync(Long(reference.Key),ct)??throw Missing();
            var rma=await resolveSource(new("Inventory","CustomerReturn",cohort.RmaId.ToString(CultureInfo.InvariantCulture)),lockSource);
            sources.AddRange(rma.Sources);var captured=cohort.Sources.Select(s=>new CommercialReadSource(s.Permission,s.Resource)).ToArray();
            await owner.RequireCapturedAsync(uow,principal,captured,ct);sources.AddRange(captured.Select(Convert));
            sources.AddRange(rma.Sources.Concat(cohort.Sources.Select(s=>new QmsPlanningReadSource(s.Permission,s.Resource)))
                .Select(s=>new QmsPlanningReadSource(Permissions.QualityRead,s.Resource with{Type="ReturnInspectionCohort",Id=reference.Key})).Distinct());
            value=cohort;created=cohort.PreparedAt;approved=cohort.PreparedAt;contributors.Add(cohort.PreparedBy);
        }
        else throw Missing();
        foreach(var source in sources.Distinct())
        {var decision=authorizer.Authorize(principal,source.Permission,source.Resource);if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision,source.Resource);}
        var canonical=CanonicalJson.Serialize(value);
        return new(reference,reference.Context,reference.Type,reference.Key,reference.Revision,CanonicalJson.Fingerprint(value),time.GetUtcNow(),created,approved,closed,
            null,contributors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),sources.Distinct().ToArray(),false,canonical);
    }
    private static QmsPlanningReadSource Convert(CommercialReadSource source)=>new(source.Permission,source.Resource);
    private static int Number(string? key)=>int.TryParse(key,NumberStyles.None,CultureInfo.InvariantCulture,out var n)&&n>0&&n.ToString(CultureInfo.InvariantCulture)==key?n:throw Missing();
    private static long Long(string key)=>long.TryParse(key,NumberStyles.None,CultureInfo.InvariantCulture,out var n)&&n>0&&n.ToString(CultureInfo.InvariantCulture)==key?n:throw Missing();
    private static string Context(string type)=>type switch
    {"PurchaseOrder"=>"Procurement","Order" or "ProductionOrder" or "OrderOperation"=>"Execution","Tool" or "Machine"=>"Resources",
        "Inspection" or "AcceptanceInspection" or "ProductReleaseDecision" or "Ncr" or "Capa"=>"Quality",_=>"Inventory"};
    private static CommandRejectedException Missing()=>new("planning_evidence_unavailable","The exact commercial owner evidence or its sources are unavailable.");
}
