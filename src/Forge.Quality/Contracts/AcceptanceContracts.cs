using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Kernel.Holds;
using Forge.Kernel.Identity;
using Forge.Kernel.Commands;
using System.Globalization;
using Forge.Resources.Contracts;

namespace Forge.Quality.Contracts;

public enum AcceptanceCoverageKind { FullQuantity, ApprovedSampling }
public enum AcceptanceCriteriaState { Draft, Submitted, Approved, Effective, Superseded, Withdrawn }
public sealed record AcceptanceSubjectKey(string PartNumber,string LotNumber,string LocationCode,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? ReturnCohortId=null);
public static class AcceptanceSubjectReference
{
    public static AcceptanceSubjectKey Normalize(AcceptanceSubjectKey subject)
    {
        if(subject is null)throw new CommandRejectedException("inspection_subject_required","Supply the actual physical subject.");
        var lot=(subject.LotNumber??string.Empty).Trim().ToUpperInvariant();
        if(lot.Length is<1 or>40||lot.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.' or '/')))throw new CommandRejectedException("invalid_lot","Use the actual owning Inventory lot/serial key.");
        if(subject.ReturnCohortId is<=0)throw new CommandRejectedException("invalid_return_cohort","Use the actual positive return cohort key.");
        return new(BusinessCode.Require(subject.PartNumber,"Part"),lot,BusinessCode.Require(subject.LocationCode,"Location"),subject.ReturnCohortId);
    }
    public static string Format(AcceptanceSubjectKey subject)
    {
        var exact=Normalize(subject);var physical=string.Join(":",Uri.EscapeDataString(exact.PartNumber),Uri.EscapeDataString(exact.LotNumber),Uri.EscapeDataString(exact.LocationCode));return exact.ReturnCohortId is{} cohort?physical+":return:"+cohort.ToString(CultureInfo.InvariantCulture):physical;
    }
    public static bool TryParse(string? key,out AcceptanceSubjectKey? subject)
    {
        subject=null;var values=key?.Split(':');if(values is null||values.Length is not(3 or 5))return false;
        long? cohort=null;if(values.Length==5){if(values[3]!="return"||!long.TryParse(values[4],NumberStyles.None,CultureInfo.InvariantCulture,out var parsed)||parsed<=0||parsed.ToString(CultureInfo.InvariantCulture)!=values[4])return false;cohort=parsed;}
        try{subject=new(Uri.UnescapeDataString(values[0]),Uri.UnescapeDataString(values[1]),Uri.UnescapeDataString(values[2]),cohort);return Format(subject)==key;}
        catch(UriFormatException){return false;}
        catch(CommandRejectedException){return false;}
    }
}
public sealed record InspectionSourceContext(QmsPolicyContext Context,IReadOnlyList<QmsPolicyReadSource> Sources,IReadOnlyList<string> ProductionActors,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ResourceRef>? ActionResources=null);
public sealed record InspectionSubject(AcceptanceSubjectKey Key,string Revision,string Unit,decimal InspectionQuantity,decimal HoldQuantity,
    bool Serialized,InspectionSourceContext Source,IReadOnlyList<int> OperationSequences,IReadOnlyList<long> OriginTransactionIds,IReadOnlyList<long> InstanceLinkIds,
    IReadOnlyList<HoldLot> PhysicalSourceLots,IReadOnlyList<long> OriginOrderNumbers,string ConfigurationFingerprint);
public sealed record AcceptanceCoverage(AcceptanceCoverageKind Kind,decimal MinimumSampleQuantity,decimal MaximumRepresentedQuantity,string Method,string HumanApprovalBasis);
public sealed record AcceptanceCriteriaContent(string PartNumber,string Revision,int? OperationSequence,string Title,
    AcceptanceCoverage Coverage,IReadOnlyList<CharacteristicInfo> Characteristics,IReadOnlyList<QmsDocumentReference> Documents);
public sealed record AcceptanceCriteriaRevision(string Code,int Revision,int Version,AcceptanceCriteriaState State,string FacilityCode,Guid? ProgramId,
    DataMarking Markings,AcceptanceCriteriaContent Content,string CreatedBy,string LastEditedBy,string? ApprovedBy,
    DateTimeOffset? EffectiveFrom,DateTimeOffset? EffectiveTo,IReadOnlyList<QmsSourceReference> Sources,IReadOnlyList<QmsTransition> History);
public sealed record AcceptanceFinding(int CharacteristicNumber,int SampleNumber,decimal? Measured,bool? Accepted,string? GaugeCode);
public sealed record AcceptanceResult(CharacteristicInfo Criterion,int SampleNumber,decimal? Measured,bool? Accepted,bool Passed,
    string? GaugeCode,long? ToolUseEventId,ToolValidity? HistoricValidity);
public sealed record AcceptanceInspection(long InspectionId,int Version,InspectionSubject Subject,string CriteriaCode,int CriteriaRevision,int CriteriaVersion,
    AcceptanceCriteriaContent FrozenCriteria,decimal Quantity,decimal SampleQuantity,bool Passed,string Inspector,DateTimeOffset At,string Evidence,
    IReadOnlyList<AcceptanceResult> Results,long? NcrId,long? HoldTransactionId,string PolicyFingerprint,QmsAdoptedPolicy Policy);
public sealed record ProductReleaseDecision(long DecisionId,int Version,IReadOnlyList<long> InspectionIds,AcceptanceSubjectKey Subject,string Revision,decimal Quantity,
    string ApprovedBy,DateTimeOffset ApprovedAt,string Basis,string EvidenceFingerprint,string PolicyFingerprint,QmsAdoptedPolicy Policy);
public sealed record ProductReleaseWithdrawal(long DecisionId,int Version,string Actor,DateTimeOffset At,string Basis);
public static class AcceptanceSubjectResources
{
    public static ResourceRef Of(InspectionSubject subject,string type,long? id)=>All(subject,type,id)[0];
    /// <summary>Each actual production/procurement owner remains an action scope; linked document/policy reads stay separate.</summary>
    public static IReadOnlyList<ResourceRef> All(InspectionSubject subject,string type,long? id)
    {
        var ownType=id is null?"AcceptanceSubject":type;var key=id is{} number?number.ToString(CultureInfo.InvariantCulture):AcceptanceSubjectReference.Format(subject.Key);
        var marks=subject.Source.Sources.Aggregate(DataMarking.None,(value,source)=>value|source.Resource.Markings);
        var origins=subject.Source.Sources.Where(source=>source.Permission is Permissions.OrdersRead or Permissions.PurchasingRead).Select(source=>source.Resource).Concat(subject.Source.ActionResources??[]).Distinct().ToArray();
        if(origins.Length==0)origins=[new(ownType,key,subject.Source.Context.ProgramId is{} program?new ProgramId(program):null)];
        return origins.Select(origin=>origin with{Type=ownType,Id=key,Markings=origin.Markings|marks,FacilityId=subject.Source.Context.FacilityCode,WorkCenterId=subject.Source.Context.WorkCenterCode??origin.WorkCenterId}).Distinct().ToArray();
    }
}
public interface IProductReleaseDecisionValidator
{
    Task ValidateCurrentDecisionAsync(IKernelUnitOfWork uow,ForgePrincipal principal,ProductReleaseDecision decision,InspectionSubject subject,QmsAdoptedPolicy policy,DateTimeOffset now,CancellationToken ct);
}
public interface IProductReleaseConsumptionLookup
{
    Task<decimal> ConsumedAsync(long decisionId,CancellationToken ct);
}
/// <summary>Additional current/captured acceptance sources for a child-only existing NCR and linked corrective action.</summary>
public interface IAcceptanceSourceAccess
{
    Task<IReadOnlyList<ResourceRef>?> ResourcesForNcrAsync(IKernelUnitOfWork uow,long ncrId,string type,long? id,CancellationToken ct);
    Task RequireNcrSourceReadAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long ncrId,CancellationToken ct);
    Task<bool> CanReadNcrSourceAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long ncrId,CancellationToken ct);
}

/// <summary>Host bridge: all source facts and writes use the caller's UOW and authoritative owner contracts.</summary>
public interface IInspectionSubjectResolver
{
    Task<IReadOnlyList<ResourceRef>> ResourceContextsAsync(IKernelUnitOfWork uow,InspectionSubject recorded,string type,long? id,CancellationToken ct);
    Task<InspectionSubject> ResolveAsync(IKernelUnitOfWork uow,ForgePrincipal principal,AcceptanceSubjectKey key,bool lockStock,CancellationToken ct);
    Task<InspectionSourceContext> OutputContextAsync(IKernelUnitOfWork uow,ForgePrincipal principal,string part,string? revision,string location,long? order,long? purchaseOrder,CancellationToken ct);
    Task<long> HoldRejectedAsync(IKernelUnitOfWork uow,InspectionSubject subject,decimal quantity,string quarantineLocation,string actor,string reason,DateTimeOffset at,CancellationToken ct);
}
public interface IAcceptanceLookup
{
    Task<IReadOnlyList<AcceptanceCriteriaRevision>> ListCriteriaAsync(CancellationToken ct);
    Task<AcceptanceCriteriaRevision?> FindCriteriaAsync(string code,int revision,CancellationToken ct);
    Task<AcceptanceInspection?> FindInspectionAsync(long inspectionId,CancellationToken ct);
    Task<AcceptanceInspection?> FindInspectionForNcrAsync(long ncrId,CancellationToken ct);
    Task<IReadOnlyList<AcceptanceInspection>> InspectionsForAsync(AcceptanceSubjectKey subject,CancellationToken ct);
    Task<ProductReleaseDecision?> FindDecisionAsync(long decisionId,CancellationToken ct);
    Task<IReadOnlyList<ProductReleaseDecision>> DecisionsForAsync(AcceptanceSubjectKey subject,CancellationToken ct);
    Task<ProductReleaseWithdrawal?> FindWithdrawalAsync(long decisionId,CancellationToken ct);
    Task<bool> HasOpenNcrAsync(AcceptanceSubjectKey subject,CancellationToken ct);
}
internal interface IAcceptanceStore : IAcceptanceLookup
{
    Task LockCriteriaScopeAsync(string facility,string part,string revision,bool shared,CancellationToken ct);
    Task LockInspectionAsync(long inspectionId,CancellationToken ct);
    Task InsertCriteriaAsync(AcceptanceCriteriaRevision revision,CancellationToken ct);
    Task UpdateCriteriaAsync(AcceptanceCriteriaRevision revision,int expectedVersion,CancellationToken ct);
    Task<long> InsertInspectionAsync(AcceptanceInspection inspection,CancellationToken ct);
    Task<long> InsertDecisionAsync(ProductReleaseDecision decision,CancellationToken ct);
    Task InsertWithdrawalAsync(ProductReleaseWithdrawal withdrawal,CancellationToken ct);
}
