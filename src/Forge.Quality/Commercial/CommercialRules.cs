using System.Diagnostics.CodeAnalysis;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Quality.Contracts;

namespace Forge.Quality.Commercial;

public static class CommercialRules
{
    public static void Validate(CommercialRecordKind kind, CommercialContent content)
    {
        if (!Enum.IsDefined(kind) || content is null) Reject("commercial_content", "Supply a known typed record and its content.");
        object?[] variants = [content.CustomerRequirements, content.DesignApplicability, content.ProviderEvaluation, content.PropertyAgreement,
            content.PropertyIncident, content.Complaint, content.CustomerFeedback, content.PostDelivery, content.Communication];
        if (variants.Count(value => value is not null) != 1 || variants[(int)kind] is null)
            Reject("commercial_content", "Supply exactly the content for this record kind.");
        switch (kind)
        {
            case CommercialRecordKind.CustomerRequirements:
                var requirements = content.CustomerRequirements!; CodeRevision(requirements.PartNumber, requirements.PartRevision);
                if ((requirements.ContractId is null) == (requirements.CustomerSource is null)) Reject("customer_source", "Bind requirements to exactly one actual contract or shipment customer.");
                if (requirements.ContractId is { } contract) Positive(contract);
                if (requirements.CustomerSource is { } customerSource)
                { Reference(customerSource); if(customerSource.Kind!=CommercialReferenceKind.Shipment || requirements.DeliveryId is not null || requirements.EnforceAtOrderRelease) Reject("customer_source", "Shipment-bound requirements apply to that exact shipped customer and cannot govern unrelated order release."); }
                if (requirements.RequireCustomerConcessionForNonconformingRelease is null) Reject("customer_concession_determination", "Explicitly determine whether nonconforming release requires the customer's concession.");
                Text(requirements.ReviewBasis); Bounded(requirements.Requirements, 1, 99); References(requirements.Applicability);
                if (requirements.Requirements.Select(requirement => Code(requirement.Code, "Requirement")).Distinct(StringComparer.Ordinal).Count() != requirements.Requirements.Count) Reject("duplicate_requirement", "Requirement codes are unique in a controlled revision.");
                foreach (var requirement in requirements.Requirements) { Known(requirement.Kind); Text(requirement.Requirement); Text(requirement.AcceptanceBasis); References(requirement.Evidence); }
                break;
            case CommercialRecordKind.DesignApplicability:
                var design = content.DesignApplicability!; CodeRevision(design.PartNumber, design.Revision); Text(design.DeterminationBasis); References(design.Evidence);
                Bounded(design.Controls, design.Applicable ? 7 : 0, design.Applicable ? 7 : 0);
                if (design.Applicable && design.Controls.Select(control => control.Stage).Distinct().Count() != 7) Reject("design_controls", "Applicable design requires explicit evidence for planning, inputs, reviews, verification, validation, outputs and changes.");
                foreach (var control in design.Controls) { Known(control.Stage); Text(control.Basis); References(control.Evidence, 1); }
                break;
            case CommercialRecordKind.ProviderEvaluation:
                var provider = content.ProviderEvaluation!; Code(provider.SupplierCode, "Supplier"); Text(provider.EvaluationBasis);
                if (provider.Scope is null) Reject("provider_scope", "Declare the exact scope and purchasing requirements.");
                CodeRevision(provider.Scope.PartNumber, provider.Scope.PartRevision);
                if (provider.Scope.ProcessCode is { } process) Code(process, "Process");
                if (provider.Scope.WorkCenterCode is { } center) Code(center, "Work center");
                Text(provider.Scope.PurchasingRequirements); Interval(provider.ValidFrom, provider.ValidTo);
                if (provider.ReevaluateBy < provider.ValidFrom || provider.ReevaluateBy > provider.ValidTo) Reject("reevaluation_due", "Reevaluation must be due within the approved validity interval.");
                Bounded(provider.Criteria, 1, 99); References(provider.Evidence, 1);
                if (provider.MinimumWeightedPassFraction is < 0 or > 1 || provider.Criteria.Sum(criterion => criterion.Weight) <= 0) Reject("provider_scoring", "Declare a weighted pass fraction and positive criteria weights.");
                if (provider.Criteria.Select(criterion => Code(criterion.Code, "Criterion")).Distinct(StringComparer.Ordinal).Count() != provider.Criteria.Count) Reject("duplicate_criterion", "Each declared criterion appears once.");
                foreach (var criterion in provider.Criteria)
                {
                    Text(criterion.Description); Text(criterion.EvidenceBasis); Known(criterion.Kind);
                    if (criterion.Weight <= 0 || criterion.Weight > 1000000) Reject("provider_scoring", "Use bounded positive weights.");
                    if (criterion.Kind == ProviderCriterionKind.Numeric && (criterion.Measured is null || criterion.Accepted is not null || criterion.Minimum is null && criterion.Maximum is null || criterion.Minimum > criterion.Maximum)) Reject("provider_measurement", "A numeric criterion needs an observed value and coherent limits.");
                    if (criterion.Kind == ProviderCriterionKind.Attribute && (criterion.Accepted is null || criterion.Measured is not null || criterion.Minimum is not null || criterion.Maximum is not null)) Reject("provider_attribute", "An attribute criterion records explicit accepted/rejected evidence.");
                }
                break;
            case CommercialRecordKind.PropertyAgreement:
                var property = content.PropertyAgreement!; Subject(property.Subject); Known(property.TitleKind); Reference(property.LegalOwner);
                if (property.LegalOwner.Kind is not (CommercialReferenceKind.Contract or CommercialReferenceKind.Supplier or CommercialReferenceKind.Shipment)) Reject("property_owner", "Legal owner must reference an actual contract/shipment customer or supplier.");
                Text(property.AgreementBasis); Text(property.ReturnTerms); Interval(property.ValidFrom, property.ValidTo); References(property.Evidence, 1);
                Bounded(property.PermittedActivities, 1, 99); Bounded(property.PermittedWorkCenters, 0, 99); Bounded(property.PermittedOrders, 0, 99);
                foreach (var activity in property.PermittedActivities) Code(activity, "Permitted activity");
                foreach (var centerCode in property.PermittedWorkCenters) Code(centerCode, "Permitted work center");
                foreach (var order in property.PermittedOrders) Positive(order);
                Bounded(property.Preservation, 0, 99);
                foreach (var control in property.Preservation)
                { Code(control.Code, "Preservation control"); Known(control.Kind); Text(control.Instructions); if (control.Minimum > control.Maximum || (control.Minimum is not null || control.Maximum is not null) && string.IsNullOrWhiteSpace(control.Unit)) Reject("preservation_limits", "Measured preservation limits require a unit and coherent bounds."); References(control.Evidence); }
                break;
            case CommercialRecordKind.PropertyIncident:
                var incident = content.PropertyIncident!; Code(incident.AgreementCode, "Agreement"); Positive(incident.AgreementRevision); Known(incident.Kind); Text(incident.Description); Text(incident.ImmediateAction); References(incident.Evidence, 1);
                break;
            case CommercialRecordKind.Complaint:
                var complaint = content.Complaint!; Positive(complaint.ShipmentId); if (complaint.ShipmentLine is <= 0) Reject("shipment_line", "Reference an actual positive shipment line.");
                Text(complaint.ReportedProblem); Text(complaint.ReceivedChannel); Text(complaint.RecordedContact); Text(complaint.InvestigationOwner); References(complaint.Evidence);
                break;
            case CommercialRecordKind.CustomerFeedback:
                var feedback = content.CustomerFeedback!; Customer(feedback.CustomerSource); Known(feedback.Kind); Text(feedback.Method); Text(feedback.RecordedFeedback); References(feedback.Evidence); Bounded(feedback.Measures, 1, 99);
                foreach (var measure in feedback.Measures)
                { Code(measure.Code, "Feedback measure"); Text(measure.Description); Text(measure.CustomerProvidedBasis); if ((measure.Value is null) == (measure.Accepted is null) || measure.Minimum > measure.Maximum) Reject("feedback_measure", "Record a coherent customer-provided numeric or attribute result, without inferring overall satisfaction."); }
                break;
            case CommercialRecordKind.PostDelivery:
                var delivery = content.PostDelivery!; Positive(delivery.ShipmentId); Known(delivery.Kind); Text(delivery.CaseBasis); References(delivery.RelatedEvidence);
                break;
            case CommercialRecordKind.Communication:
                var communication = content.Communication!; Known(communication.Audience); Reference(communication.PartySource); Reference(communication.RelatedCase);
                if (communication.Audience == CommunicationAudience.Customer) Customer(communication.PartySource);
                else if (communication.PartySource.Kind != CommercialReferenceKind.Supplier) Reject("communication_party", "Supplier communications name an actual supplier.");
                if (communication.RelatedCase.Kind != CommercialReferenceKind.CommercialRecord) Reject("communication_case", "The proposed communication links its actual governed case.");
                Text(communication.RecordedRecipient); Text(communication.Subject, 300); Text(communication.Body, 16000); Text(communication.ProposedChannel); References(communication.Evidence);
                if (communication.Concession is { } concession)
                {
                    if (communication.Audience != CommunicationAudience.Customer || concession.Disposition is not (Disposition.UseAsIs or Disposition.Repair) || concession.Quantity <= 0)
                        Reject("customer_concession_scope", "Customer consent covers an exact nonconforming use-as-is or repair scope and bounded positive quantity.");
                    Positive(concession.NcrId); if(AcceptanceSubjectReference.Normalize(concession.Subject)!=concession.Subject) Reject("customer_concession_scope", "Use the exact canonical physical subject."); CodeRevision(concession.Subject.PartNumber, concession.Revision);
                }
                break;
        }
    }

    public static ProviderEvaluationResult Evaluate(ProviderEvaluationContent evaluation)
    {
        Validate(CommercialRecordKind.ProviderEvaluation, new(ProviderEvaluation: evaluation));
        bool Pass(ProviderCriterion criterion) => criterion.Kind == ProviderCriterionKind.Attribute ? criterion.Accepted == true
            : (criterion.Minimum is null || criterion.Measured >= criterion.Minimum) && (criterion.Maximum is null || criterion.Measured <= criterion.Maximum);
        var failed = evaluation.Criteria.Where(criterion => !Pass(criterion)).Select(criterion => criterion.Code).ToArray();
        var fraction = evaluation.Criteria.Where(Pass).Sum(criterion => criterion.Weight) / evaluation.Criteria.Sum(criterion => criterion.Weight);
        var mandatory = evaluation.Criteria.Where(criterion => criterion.Mandatory).All(Pass);
        return new(fraction, mandatory, mandatory && fraction >= evaluation.MinimumWeightedPassFraction, failed);
    }

    public static void Reference(CommercialReference reference)
    {
        if (reference is null || !Enum.IsDefined(reference.Kind) || string.IsNullOrWhiteSpace(reference.Key) || reference.Key.Length > 1024 || reference.Key.Any(char.IsControl)) Reject("commercial_reference", "Supply a bounded typed owning reference.");
        if (reference.LineNumber is <= 0) Reject("commercial_reference", "A supplied line number is positive.");
        if (reference.Kind == CommercialReferenceKind.Supplier) Code(reference.Key, "Supplier");
    }
    public static void References(IReadOnlyList<CommercialReference> references, int minimum = 0)
    { Bounded(references, minimum, 200); foreach (var reference in references) Reference(reference); if (references.Distinct().Count() != references.Count) Reject("duplicate_reference", "Each exact reference appears once."); }
    public static void Subject(PropertySubject subject)
    {
        if (subject is null || !Enum.IsDefined(subject.Kind)) Reject("property_subject", "Name the existing physical subject.");
        if (subject.Kind == PropertyUseKind.Material)
        {
            Code(subject.PartNumber ?? string.Empty, "Part");
            if (string.IsNullOrEmpty(subject.Key) || subject.Key.Length > 40 || subject.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '/')))
                Reject("property_subject", "Use the exact recorded material lot key.");
        }
        else { Code(subject.Key, "Property identity"); if (subject.PartNumber is not null) Reject("property_subject", "Resource property uses the actual tool/machine code."); }
    }
    public static void Text(string? value, int maximum = 4000)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) Reject("commercial_basis", "Record a bounded human basis."); }
    private static void Customer(CommercialReference source)
    { Reference(source); if (source.Kind is not (CommercialReferenceKind.Contract or CommercialReferenceKind.Shipment)) Reject("customer_source", "Use the actual customer on an existing contract or shipment."); }
    private static void CodeRevision(string? part, string? revision)
    { if (part is null && revision is not null) Reject("configuration", "A revision needs its actual part."); if (part is not null) Code(part, "Part"); if (revision is not null) Qms.QmsContentRules.DocumentRevision(revision); }
    private static void Interval(DateTimeOffset from, DateTimeOffset to) { if (to <= from) Reject("effective_interval", "An approved interval has an exclusive end after its start."); }
    private static void Positive(long number) { if (number <= 0) Reject("commercial_reference", "Use a positive owning identifier."); }
    private static string Code(string value, string label)
    { var exact = BusinessCode.Require(value, label); if (exact != value) Reject("commercial_key_not_canonical", "Use the recorded canonical owning code."); return exact; }
    private static void Known<T>(T value) where T : struct, Enum { if (!Enum.IsDefined(value)) Reject("commercial_kind", "Use a declared typed choice."); }
    private static void Bounded<T>(IReadOnlyList<T> values, int minimum, int maximum)
    { if (values is null || values.Count < minimum || values.Count > maximum || values.Any(value => value is null)) Reject("commercial_count", "Use the bounded complete list for this record."); }
    [DoesNotReturn] private static void Reject(string code, string message) => throw new CommandRejectedException(code, message);
}
