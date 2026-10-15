using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;

namespace Forge.Quality.Review;

public static class QmsReviewRules
{
    public static void Validate(QmsReviewDefinitionKind kind, QmsReviewDefinitionContent content)
    {
        if (!Enum.IsDefined(kind) || content is null || (content.Program is null) == (content.Mapping is null)
            || kind == QmsReviewDefinitionKind.ManagementReviewProgram && content.Program is null
            || kind == QmsReviewDefinitionKind.StandardEvidenceMapping && content.Mapping is null)
            Reject("review_definition_kind", "Supply exactly the typed content belonging to this definition.");
        if (content.Program is { } program)
        {
            Text(program.Name, 240); Person(program.OwnerPersonId); Text(program.Frequency); Text(program.Scope);
            Text(program.RequiredParticipants); Text(program.DecisionAuthority); Text(program.ActionVerificationCriteria); Text(program.ClosureCriteria);
            foreach (var participant in List(program.RequiredParticipantPersonIds, true)) Person(participant);
            if (program.RequiredParticipantPersonIds.Distinct().Count() != program.RequiredParticipantPersonIds.Count)
                Reject("review_duplicate_participant", "Required participants must identify distinct registered people.");
            var agenda = List(program.Agenda, true);
            if (agenda.Select(item => BusinessCode.Require(item.Code, "Agenda item")).Distinct(StringComparer.Ordinal).Count() != agenda.Count)
                Reject("review_duplicate_agenda", "Agenda codes must identify distinct human-defined review inputs.");
            foreach (var item in agenda)
            {
                Text(item.Topic, 240); Text(item.RequiredEvidence); Text(item.AdequacyCriteria);
                List(item.Sources); List(item.Metrics);
                foreach (var metric in item.Metrics) Reference(metric);
            }
        }
        if (content.Mapping is { } mapping)
        {
            Text(mapping.Title, 240); Person(mapping.ProcessOwnerPersonId); Text(mapping.ApplicabilityBasis);
            Text(mapping.AssessmentMethod); Text(mapping.ReviewInterval);
            if (!Enum.IsDefined(mapping.Applicability)) Reject("mapping_applicability", "Record an explicit applicability state and basis.");
            foreach (var criterion in List(mapping.Criteria, true)) Standard(criterion);
            foreach (var process in List(mapping.Processes)) Reference(process);
            foreach (var requirement in List(mapping.Requirements)) Reference(requirement);
            foreach (var procedure in List(mapping.ImplementingProcedures)) Document(procedure);
            foreach (var capability in List(mapping.ForgeCapabilities, true)) Text(capability, 240);
            foreach (var evidenceType in List(mapping.RequiredEvidenceTypes, true)) Text(evidenceType, 120);
        }
        foreach (var document in List(content.Documents ?? [])) Document(document);
    }

    public static void Period(DateTimeOffset from, DateTimeOffset to, DateTimeOffset cutoff)
    {
        if (from >= to || to > cutoff || cutoff - from > TimeSpan.FromDays(3660))
            Reject("review_period", "Select a finite closed half-open period ending on or before the actual capture cutoff.");
    }

    public static void ValidateAssessments(QmsReviewInputManifest manifest, IReadOnlyList<QmsReviewInputAssessment> assessments)
    {
        List(assessments);
        if (assessments.Select(item => item.AgendaCode).Distinct(StringComparer.Ordinal).Count() != assessments.Count)
            Reject("review_duplicate_assessment", "Assess each actual frozen agenda input once.");
        foreach (var assessment in assessments)
        {
            if (!manifest.Inputs.Any(input => input.AgendaCode == assessment.AgendaCode) || !Enum.IsDefined(assessment.Adequacy))
                Reject("review_input_unavailable", "An assessment must refer to an actual input in this frozen manifest.");
            Text(assessment.Assessment); Text(assessment.Decision); List(assessment.AdditionalEvidence);
            var input = manifest.Inputs.Single(item => item.AgendaCode == assessment.AgendaCode);
            if (input.Gaps.Count != 0 || input.Metrics.Any(metric => metric.Measurement.Availability != QmsMetricAvailability.Complete))
            {
                Text(assessment.Limits);
                if (assessment.Adequacy == QmsEvidenceAdequacy.Adequate)
                    Reject("review_incomplete_input", "Unavailable or partial recorded inputs cannot be declared complete evidence by filling an assessment field.");
            }
        }
        foreach (var input in manifest.Inputs.Where(input => input.Required))
            if (!assessments.Any(item => item.AgendaCode == input.AgendaCode && item.Adequacy != QmsEvidenceAdequacy.NotAssessed))
                Reject("review_required_assessment", "An authorized human must assess every required frozen review input.");
    }

    public static void ValidateDecisions(IReadOnlyList<QmsManagementReviewDecision> decisions)
    {
        List(decisions);
        if (decisions.Select(decision => BusinessCode.Require(decision.Code, "Decision")).Distinct(StringComparer.Ordinal).Count() != decisions.Count)
            Reject("review_duplicate_decision", "Decision codes must be unique within the actual review.");
        foreach (var decision in decisions)
        {
            Text(decision.Decision); Text(decision.Basis); Text(decision.IntendedResult); List(decision.SourceActionReferences);
            if (decision.OwnerPersonId is { } owner)
            {
                Person(owner); Text(decision.CompletionCriteria); Text(decision.Resources);
                if (decision.DueAt is null) Reject("review_action_due", "An assigned action needs its actual owner, due time and completion criteria.");
            }
            else if (decision.DueAt is not null) Reject("review_action_owner", "A dated action needs its actual assigned owner.");
        }
    }

    public static void ValidateMappingAssessment(QmsEvidenceAdequacy adequacy, QmsConformityAssessment assessment,
        bool requiresLicensedVerification, string interpretation, string method, string applicabilityBasis,
        string gap, string notes, string limits)
    {
        if (!Enum.IsDefined(adequacy) || !Enum.IsDefined(assessment)) Reject("mapping_assessment", "Use explicit evidence adequacy and human assessment states.");
        Text(interpretation); Text(method); Text(applicabilityBasis); Text(notes);
        if (requiresLicensedVerification && assessment is QmsConformityAssessment.Supported or QmsConformityAssessment.NotApplicable)
            Reject("mapping_licensed_source_pending", "This normative assessment still needs the authorized licensed source verification; an evidence link cannot supply it.");
        if (assessment == QmsConformityAssessment.Supported && adequacy != QmsEvidenceAdequacy.Adequate)
            Reject("mapping_evidence_inadequate", "Supported conformity requires a separately recorded adequate evidence judgment.");
        if (assessment == QmsConformityAssessment.Gap) Text(gap);
        if (adequacy is QmsEvidenceAdequacy.Inadequate or QmsEvidenceAdequacy.Unavailable) Text(limits);
    }

    public static void Independent(string actor, IEnumerable<string> contributors)
    {
        if (contributors.Contains(actor, StringComparer.Ordinal))
            Reject("review_independent_assessment_required", "The author or an actual evidence contributor cannot independently approve this assessment.");
    }

    public static void Edition(QmsEditionMappingAssessment assessment)
    {
        Text(assessment.Standard, 120); Text(assessment.FromEdition, 80); Text(assessment.ToEdition, 80);
        Text(assessment.Basis); Text(assessment.Changes); Text(assessment.ApplicabilityImpact); Text(assessment.RequiredActions);
        if (assessment.FromEdition == assessment.ToEdition || !Enum.IsDefined(assessment.Relationship))
            Reject("edition_relationship", "Select distinct editions and the human-reviewed relationship, including unresolved where appropriate.");
        var from = List(assessment.FromCriteria); var to = List(assessment.ToCriteria);
        foreach (var reference in from.Concat(to)) Standard(reference);
        if (from.Any(reference => reference.Standard != assessment.Standard || reference.Edition != assessment.FromEdition)
            || to.Any(reference => reference.Standard != assessment.Standard || reference.Edition != assessment.ToEdition))
            Reject("edition_criterion_mismatch", "Each exact criterion must belong to its declared standard and edition.");
        if (assessment.Relationship == QmsEditionRelationship.New && (from.Count != 0 || to.Count == 0)
            || assessment.Relationship == QmsEditionRelationship.Removed && (from.Count == 0 || to.Count != 0)
            || assessment.Relationship is not (QmsEditionRelationship.New or QmsEditionRelationship.Removed or QmsEditionRelationship.Unresolved)
                && (from.Count == 0 || to.Count == 0))
            Reject("edition_criterion_set", "Preserve the exact many-to-many criterion sets; new, removed and unresolved mappings have distinct meanings.");
    }

    private static void Standard(QmsStandardReference reference)
    {
        Text(reference.Standard, 120); Text(reference.Edition, 80); Text(reference.Clause, 120);
        if (!Enum.IsDefined(reference.Verification)) Reject("review_standard_verification", "Record the actual authorized-source verification state.");
        if (reference.Verification == QmsStandardVerification.VerifiedAuthorizedCopy && reference.VerificationDocument is null)
            Reject("review_verification_source", "Reference the exact retained source for an authorized-copy verification.");
        if (reference.VerificationDocument is { } document) Document(document);
    }
    private static void Reference(QmsRevisionReference reference)
    { BusinessCode.Require(reference.Code, "Definition reference"); if (reference.Revision < 1) Reject("review_revision", "Reference a positive immutable owner revision."); }
    private static void Document(QmsDocumentReference reference)
    { BusinessCode.Require(reference.DocumentNumber, "Document"); Forge.Quality.Qms.QmsContentRules.DocumentRevision(reference.Revision); }
    private static void Person(Guid id) { if (id == Guid.Empty) Reject("review_person", "Select an actual registered person."); }
    private static void Text(string? value, int maximum = 4000)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t'))) Reject("review_text", "Record bounded explicit human content."); }
    private static IReadOnlyList<T> List<T>(IReadOnlyList<T>? values, bool nonempty = false)
    { if (values is null || values.Count > 200 || nonempty && values.Count == 0 || values.Any(value => value is null)) Reject("review_list", "Use a finite explicit owner-defined list."); return values; }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(string code, string message) => throw new CommandRejectedException(code, message);
}
