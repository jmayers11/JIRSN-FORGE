using Forge.Kernel.Access;
using Forge.Kernel.Security;
using System.Text.Json.Serialization;

namespace Forge.Quality.Contracts;

public enum CommercialRecordKind { CustomerRequirements, DesignApplicability, ProviderEvaluation, PropertyAgreement, PropertyIncident, Complaint, CustomerFeedback, PostDelivery, Communication }
public enum CommercialState { Draft, Submitted, Reviewed, Approved, Effective, Superseded, Withdrawn, Closed }
public enum CommercialReferenceKind { Supplier, Contract, Delivery, PartRevision, DocumentRevision, EngineeringChange, PurchaseOrder, Receipt, Shipment, CustomerReturn, Order, Lot, Tool, Machine, AcceptanceInspection, ProductReleaseDecision, Ncr, Capa, CommercialRecord, Location, InventoryTransaction, ToolEvent, Item, WorkCenter, QmsRevision, Inspection, Hold, Program }
public sealed record CommercialReference(CommercialReferenceKind Kind, string Key, int? LineNumber = null, string? Revision = null);
public sealed record CommercialReadSource(string Permission, ResourceRef Resource);
public sealed record CommercialSourceFact(CommercialReference Reference, string Description, IReadOnlyList<ResourceRef> Resources, string Fingerprint);
public sealed record CommercialScope(string OrganizationCode, string FacilityCode, string? Customer, string? SupplierCode,
    IReadOnlyList<ResourceRef> Ownership, IReadOnlyList<CommercialReadSource> Sources, IReadOnlyList<CommercialSourceFact> Facts,
    string Fingerprint, IReadOnlyList<string> Gaps);

public enum CustomerRequirementKind { Product, Contract, Delivery, Quality, Regulatory, Configuration, SpecialCharacteristic, Acceptance, Preservation, Communication }
public sealed record CustomerRequirementItem(string Code, CustomerRequirementKind Kind, string Requirement, string AcceptanceBasis,
    IReadOnlyList<CommercialReference> Evidence);
public sealed record CustomerRequirementsContent(long? ContractId, long? DeliveryId, string? PartNumber, string? PartRevision,
    IReadOnlyList<CustomerRequirementItem> Requirements, IReadOnlyList<CommercialReference> Applicability,
    [property:JsonRequired] bool EnforceAtOrderRelease, [property:JsonRequired] bool EnforceAtShipment,
    [property:JsonRequired] bool RequireIndependentComplaintClosure, string ReviewBasis,
    [property:JsonRequired] bool? RequireCustomerConcessionForNonconformingRelease = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] CommercialReference? CustomerSource = null);

public enum DesignControlStage { Planning, Inputs, Review, Verification, Validation, Outputs, Changes }
public sealed record DesignControlEvidence(DesignControlStage Stage, string Basis, IReadOnlyList<CommercialReference> Evidence);
public sealed record DesignApplicabilityContent(string PartNumber, string Revision, [property:JsonRequired] bool Applicable, string DeterminationBasis,
    IReadOnlyList<DesignControlEvidence> Controls, IReadOnlyList<CommercialReference> Evidence);

public enum ProviderCriterionKind { Numeric, Attribute }
public sealed record ProviderCriterion(string Code, string Description, ProviderCriterionKind Kind, [property:JsonRequired] bool Mandatory,
    decimal Weight, decimal? Measured, decimal? Minimum, decimal? Maximum, bool? Accepted, string EvidenceBasis);
public sealed record ProviderApprovalScope(string? PartNumber, string? PartRevision, string? ProcessCode,
    string? WorkCenterCode, [property:JsonRequired] bool EnforceAtPurchaseApproval, [property:JsonRequired] bool EnforceAtExternalDispatch, string PurchasingRequirements);
public sealed record ProviderEvaluationContent(string SupplierCode, ProviderApprovalScope Scope,
    IReadOnlyList<ProviderCriterion> Criteria, [property:JsonRequired] decimal MinimumWeightedPassFraction, DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo, DateTimeOffset ReevaluateBy, string EvaluationBasis, IReadOnlyList<CommercialReference> Evidence);
public sealed record ProviderEvaluationResult(decimal WeightedPassFraction, bool MandatoryCriteriaPassed,
    bool MeetsApprovedCriteria, IReadOnlyList<string> FailedCriteria);

public enum PropertyTitleKind { CustomerOwned, SupplierConsigned, Loaned }
public enum PropertyUseKind { Tool, Machine, Material }
public sealed record PropertySubject(PropertyUseKind Kind, string Key, string? PartNumber = null);
public enum PreservationControlKind { Handling, Storage, Packaging, Contamination, ShelfLife, Environment, Transportation }
public sealed record PreservationControl(string Code, PreservationControlKind Kind, string Instructions,
    decimal? Minimum, decimal? Maximum, string? Unit, IReadOnlyList<CommercialReference> Evidence);
public sealed record PropertyAgreementContent(PropertySubject Subject, PropertyTitleKind TitleKind, CommercialReference LegalOwner,
    string AgreementBasis, DateTimeOffset ValidFrom, DateTimeOffset ValidTo, IReadOnlyList<string> PermittedActivities,
    IReadOnlyList<string> PermittedWorkCenters, IReadOnlyList<long> PermittedOrders, [property:JsonRequired] bool AllowConsumptiveUse,
    [property:JsonRequired] bool RequireOwnerReturnAcceptance, string ReturnTerms, IReadOnlyList<PreservationControl> Preservation,
    IReadOnlyList<CommercialReference> Evidence);
public enum PropertyIncidentKind { Loss, Damage, Unsuitability, PreservationFailure }
public sealed record PropertyIncidentContent(string AgreementCode, int AgreementRevision, PropertyIncidentKind Kind,
    DateTimeOffset ObservedAt, string Description, string ImmediateAction, IReadOnlyList<CommercialReference> Evidence);

public sealed record ComplaintContent(long ShipmentId, int? ShipmentLine, string ReportedProblem, DateTimeOffset ReportedAt,
    string ReceivedChannel, string RecordedContact, string InvestigationOwner, IReadOnlyList<CommercialReference> Evidence);
public enum CustomerFeedbackKind { Scorecard, Survey, SupplierRating, DeliveryEvaluation, ProductAcceptance, Other }
public sealed record CustomerFeedbackMeasure(string Code, string Description, decimal? Value, decimal? Minimum,
    decimal? Maximum, string? Unit, bool? Accepted, string CustomerProvidedBasis);
public sealed record CustomerFeedbackContent(CommercialReference CustomerSource, CustomerFeedbackKind Kind,
    DateTimeOffset ReceivedAt, string Method, IReadOnlyList<CustomerFeedbackMeasure> Measures, string RecordedFeedback,
    IReadOnlyList<CommercialReference> Evidence);
public enum PostDeliveryKind { Warranty, FieldFailure, ProductInvestigation, RecallReview, ReturnReview, CorrectiveActionReview }
public sealed record PostDeliveryContent(long ShipmentId, PostDeliveryKind Kind, string CaseBasis,
    IReadOnlyList<CommercialReference> RelatedEvidence);

public enum CommunicationAudience { Customer, Supplier }
public sealed record CustomerConcessionScope(AcceptanceSubjectKey Subject, string Revision, long NcrId, decimal Quantity, Disposition Disposition);
public sealed record CommunicationContent(CommunicationAudience Audience, CommercialReference PartySource,
    CommercialReference RelatedCase, string RecordedRecipient, string Subject, string Body, string ProposedChannel,
    IReadOnlyList<CommercialReference> Evidence, CustomerConcessionScope? Concession = null);

public sealed record CommercialContent(CustomerRequirementsContent? CustomerRequirements = null,
    DesignApplicabilityContent? DesignApplicability = null, ProviderEvaluationContent? ProviderEvaluation = null,
    PropertyAgreementContent? PropertyAgreement = null, PropertyIncidentContent? PropertyIncident = null,
    ComplaintContent? Complaint = null, CustomerFeedbackContent? CustomerFeedback = null,
    PostDeliveryContent? PostDelivery = null, CommunicationContent? Communication = null);

public enum CommercialObservationKind { Monitoring, Reassessment, Investigation, Correction, CorrectiveAction,
    ComplaintDisposition, PropertyReceipt, PropertyUse, PropertyReturn, OwnerAcceptance, CommunicationObserved, PreservationObservation, CustomerApprovalReceived }
public sealed record PreservationReading(string ControlCode, decimal? Value, bool? Accepted, string? Unit, string Basis);
public sealed record CommercialObservation(int Number, CommercialObservationKind Kind, DateTimeOffset ObservedAt,
    string Actor, string Basis, IReadOnlyList<CommercialReference> Evidence, IReadOnlyList<CommercialReadSource> Sources,
    IReadOnlyList<ResourceRef> Ownership, string? RecordedExternalPerson = null, IReadOnlyList<PreservationReading>? Preservation = null);
public sealed record CommercialTransition(string Action, CommercialState From, CommercialState To, int Version,
    string Actor, DateTimeOffset At, string Basis, string Reason);
public sealed record CommercialRevision(CommercialRecordKind Kind, string Code, int Revision, int Version,
    CommercialState State, string OrganizationCode, string FacilityCode, CommercialContent Content,
    string CreatedBy, string LastEditedBy, string? ReviewedBy, string? ApprovedBy, DateTimeOffset CreatedAt,
    DateTimeOffset? EffectiveFrom, DateTimeOffset? EffectiveTo, IReadOnlyList<ResourceRef> Ownership,
    IReadOnlyList<CommercialReadSource> Sources, IReadOnlyList<CommercialSourceFact> Facts, string SourceFingerprint,
    IReadOnlyList<CommercialObservation> Observations, IReadOnlyList<CommercialTransition> History);
public sealed record CommercialPage(IReadOnlyList<CommercialRevision> Items, string? NextCursor);
public sealed record CommercialCaseEvidence(CommercialReference Source, IReadOnlyList<CommercialSourceFact> Facts,
    IReadOnlyList<CommercialReference> RelatedRecords, bool Complete, IReadOnlyList<string> Gaps);

/// <summary>Current actual owner facts; raw caller references are never accepted as authority.</summary>
public interface ICommercialQualitySourceResolver
{
    Task ValidateObservationAsync(IKernelUnitOfWork uow, ForgePrincipal principal, CommercialRevision record,
        CommercialObservationKind observation, IReadOnlyList<CommercialReference> evidence, DateTimeOffset at, CancellationToken ct);
    Task<CommercialCustomerContext> CustomerConcessionContextAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        CustomerConcessionScope concession, CancellationToken ct);
    Task<IReadOnlyList<ResourceRef>> ResourceContextsAsync(IKernelUnitOfWork uow, string facility,
        IReadOnlyList<CommercialReference> references, CancellationToken ct);
    Task<CommercialScope> ResolveAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string facility,
        CommercialRecordKind kind, CommercialContent content, CancellationToken ct);
    Task<CommercialScope> ResolveReferencesAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string facility,
        IReadOnlyList<CommercialReference> references, CancellationToken ct);
    Task RequireCapturedAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        IReadOnlyList<CommercialReadSource> captured, CancellationToken ct);
    Task<CommercialCaseEvidence> ShipmentEvidenceAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        long shipmentId, int? line, CancellationToken ct);
}

public interface ICommercialQualityLookup
{
    Task<CommercialRevision?> FindAsync(CommercialRecordKind kind, string code, int revision, CancellationToken ct);
    Task<CommercialRevision?> FindVersionAsync(CommercialRecordKind kind, string code, int revision, int version, CancellationToken ct);
    Task<IReadOnlyList<CommercialRevision>> RevisionsAsync(CommercialRecordKind kind, string code, CancellationToken ct);
    Task<IReadOnlyList<CommercialRevision>> ListAsync(CommercialRecordKind? kind, int take, string? after, CancellationToken ct);
    Task<IReadOnlyList<CommercialRevision>> EffectiveAsync(CommercialRecordKind kind, CancellationToken ct);
    Task<IReadOnlyList<CommercialRevision>> EverEffectiveAsync(CommercialRecordKind kind, CancellationToken ct);
}

internal interface ICommercialQualityStore : ICommercialQualityLookup
{
    Task LockCodeAsync(CommercialRecordKind kind, string code, CancellationToken ct);
    Task LockScopeAsync(CommercialRecordKind kind, string scope, bool shared, CancellationToken ct);
    Task InsertAsync(CommercialRevision revision, CancellationToken ct);
    Task UpdateAsync(CommercialRevision revision, int expectedVersion, CancellationToken ct);
}

/// <summary>Action checks use all actual current/captured source contexts, without another inventory ledger.</summary>
public interface ICommercialQualityAccess
{
    Task<IReadOnlyList<ResourceRef>?> ResourcesAsync(IKernelUnitOfWork uow, string kind, string key, CancellationToken ct);
    Task<bool> CanReadAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string kind, string key, CancellationToken ct);
}
