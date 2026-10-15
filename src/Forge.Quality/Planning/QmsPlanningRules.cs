using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;

namespace Forge.Quality.Planning;

/// <summary>Human criteria are explicit. These rules validate decisions; they do not invent acceptance criteria.</summary>
public static class QmsPlanningRules
{
    public static void Validate(QmsPlanningKind kind, QmsPlanningContent c)
    {
        Require(c is not null,"planning_content","Supply typed planning content.");
        List(c.Documents??[],"Controlled document references",200);List(c.Requirements??[],"Exact requirement references",200);List(c.Evidence??[],"Evidence references",500);
        var sections = new object?[] { c.EvaluationMethod, c.RiskOpportunity, c.MetricDefinition, c.Objective, c.Change, c.RetentionSchedule, c.Improvement };
        Require(Enum.IsDefined(kind) && sections.Count(x => x is not null) == 1 && sections[(int)kind] is not null,
            "planning_content", "Supply exactly the typed content section selected by the record kind.");
        foreach (var d in c.Documents ?? []) { QmsContentRules.DocumentNumber(d.DocumentNumber); QmsContentRules.DocumentRevision(d.Revision); }
        foreach (var r in c.Requirements ?? []) Reference(r);
        foreach (var e in Evidence(c)) EvidenceReference(e);
        switch (kind)
        {
            case QmsPlanningKind.EvaluationMethod:
                var method = c.EvaluationMethod!;
                Text(method.Name, "Method name"); Text(method.Method, "Evaluation method"); Text(method.DecisionCriteria, "Decision criteria"); Text(method.ReviewInterval, "Review interval");
                Require(Enum.IsDefined(method.Kind), "planning_method", "Choose an evaluation method kind.");
                Require(method.Scale is not null && method.Scale.Select(x => x.Code).Distinct(StringComparer.Ordinal).Count() == method.Scale.Count,
                    "planning_scale", "Scale codes must be distinct.");
                List(method.Scale,"Explicit scale",200);foreach (var s in method.Scale) { BusinessCode.Require(s.Code, "Scale code"); Text(s.Description, "Scale description"); }
                Require(method.Kind != QmsEvaluationMethodKind.Ordinal || method.Scale.Count > 0, "planning_scale", "An ordinal method needs its explicit scale.");
                break;
            case QmsPlanningKind.RiskOpportunity:
                var risk = c.RiskOpportunity!; Require(Enum.IsDefined(risk.Kind), "planning_kind", "Choose risk or opportunity.");
                Text(risk.Context, "Organizational context"); Reference(risk.Process); Reference(risk.EvaluationMethod);
                Text(risk.Description, "Description"); Text(risk.ConsequenceOrBenefit, "Consequence or benefit"); Person(risk.OwnerPersonId);
                Text(risk.PlannedAction, "Planned action"); Instant(risk.DueAt); Text(risk.Resources, "Resources");
                Text(risk.ReviewInterval, "Review interval"); Text(risk.EffectivenessCriteria, "Effectiveness criteria"); break;
            case QmsPlanningKind.MetricDefinition: Metric(c.MetricDefinition!); break;
            case QmsPlanningKind.Objective:
                var objective = c.Objective!; Text(objective.Title, "Objective"); Reference(objective.Metric); Person(objective.OwnerPersonId);
                List(objective.BaselineEvidence,"Baseline evidence",500);
                Require(Enum.IsDefined(objective.BaselineStatus), "planning_baseline", "Declare a recorded or unknown baseline.");
                Require(objective.BaselineStatus == QmsBaselineStatus.Recorded ? objective.Baseline is not null && objective.BaselineEvidence.Count > 0 : objective.Baseline is null,
                    "planning_baseline", "A recorded baseline needs a value and actual evidence; an unknown baseline has no value.");
                Text(objective.BaselineBasis, "Baseline basis"); Range(objective.PeriodFrom, objective.PeriodTo); Instant(objective.DueAt);
                Require(Enum.IsDefined(objective.Comparison) && objective.Tolerance >= 0, "planning_target", "Declare target comparison and nonnegative tolerance.");
                Require(objective.Comparison == QmsTargetComparison.WithinRange ? objective.UpperTarget >= objective.Target : objective.UpperTarget is null,
                    "planning_target", "A range needs its ordered upper bound; other comparisons have no upper bound.");
                Text(objective.ReviewInterval, "Review interval"); Text(objective.PlannedActions, "Actions"); Text(objective.Resources, "Resources"); break;
            case QmsPlanningKind.Change:
                var change = c.Change!; Text(change.ProposedChange, "Proposed change"); Text(change.ReasonForChange, "Reason for change"); Text(change.IntendedResult, "Intended result");
                List(change.AffectedProcesses,"Affected processes",200);List(change.RisksAndOpportunities,"Risk and opportunity definitions",200);List(change.EngineeringChanges,"Actual Engineering change references",500);
                Require(change.AffectedProcesses.Count > 0, "planning_process", "Identify the actual affected processes.");
                foreach (var r in change.AffectedProcesses.Concat(change.RisksAndOpportunities)) Reference(r);
                foreach (var value in new[] { change.ResourceImplications, change.ResponsibilityChanges, change.RequiredApprovals, change.ImplementationPlan, change.VerificationCriteria, change.EffectivenessCriteria }) Text(value, "Change impact and decision");
                Person(change.OwnerPersonId); Instant(change.DueAt); break;
            case QmsPlanningKind.RetentionSchedule:
                var schedule = c.RetentionSchedule!; Text(schedule.RecordCategory, "Record category"); Text(schedule.RetentionRequirement, "Retention requirement");
                Require(Enum.IsDefined(schedule.Trigger) && schedule.Indefinite is not null, "retention_trigger", "Declare the trigger and indefinite-retention choice.");
                Require(schedule.Indefinite == true ? schedule.DurationDays is null : schedule.DurationDays is >= 0 and <= 365250,
                    "retention_duration", "Declare an explicit duration or indefinite retention.");
                Person(schedule.OwnerPersonId); Text(schedule.ContractCustomerLegalBasis, "Contract, customer and legal basis");
                Text(schedule.DispositionAuthority, "Disposition authority"); Text(schedule.DispositionEvidenceCriteria, "Disposition evidence criteria");
                List(schedule.AllowedOwnerTypes,"Allowed owner categories",200);
                Require(schedule.AllowedOwnerTypes.Count > 0 && schedule.AllowedOwnerTypes.Distinct().Count() == schedule.AllowedOwnerTypes.Count,
                    "retention_category", "Declare distinct actual owner types for this category.");
                foreach (var type in schedule.AllowedOwnerTypes) Text(type, "Owner type", 80); break;
            case QmsPlanningKind.Improvement:
                var improvement = c.Improvement!; Person(improvement.OwnerPersonId); Instant(improvement.DueAt);
                List(improvement.Origins,"Actual improvement origins",500);
                foreach (var value in new[] { improvement.Title, improvement.Context, improvement.IntendedBenefit, improvement.EvaluationMethod, improvement.SelectionCriteria, improvement.PlannedActions, improvement.Resources, improvement.BeforeAfterMethod }) Text(value, "Improvement decision");
                Require(improvement.Origins.Count > 0, "planning_origins", "Link the actual audit, feedback, CAPA, objective or performance origin."); break;
        }
    }

    public static void Metric(QmsMetricDefinitionContent d)
    {
        Require(d is not null,"metric_definition","Supply the actual metric definition.");List(d.OrderNumbers,"Required order cohort",10000);List(d.OperationSequences,"Required operation cohort",10000);
        foreach (var value in new[] { d.Name, d.Unit, d.NumeratorDefinition, d.DenominatorDefinition, d.MeasurementMethod, d.CohortBasis, d.CompletenessCriteria, d.ReviewInterval }) Text(value, "Metric definition");
        Require(Enum.IsDefined(d.SourceKind) && d.IncludeAllOperations is not null, "metric_source", "Declare the measurement source and operation cohort choice.");
        Require(d.OrderNumbers.Count > 0 && d.OrderNumbers.All(x => x > 0) && d.OrderNumbers.Distinct().Count() == d.OrderNumbers.Count,
            "metric_cohort", "Declare distinct actual orders; an organization-wide cohort is never inferred.");
        Require(d.IncludeAllOperations == true ? d.OperationSequences.Count == 0 : d.OperationSequences.Count > 0 && d.OperationSequences.All(x => x > 0) && d.OperationSequences.Distinct().Count() == d.OperationSequences.Count,
            "metric_cohort", "Declare all operations explicitly or identify the exact operation sequences.");
        Require(d.MaximumRows is > 0 and <= 100000, "metric_limit", "Declare a bounded evidence limit; exceeding it returns incomplete measurement.");
        Require(d.SourceKind == QmsMetricSourceKind.RecordedProductionReports ? d.ProductionMeasure is { } m && Enum.IsDefined(m) : d.ProductionMeasure is null,
            "metric_measure", "Choose a supported recorded-production measure or an attributed human measurement.");
        Require(d.ProductionMeasure!=QmsRecordedProductionMeasure.ScrapFraction||d.Unit=="fraction","metric_unit","A recorded scrap fraction is a dimensionless fraction, not a percent or first-pass-yield claim.");
        Require(d.ProductionMeasure!=QmsRecordedProductionMeasure.ReportCount||d.Unit=="reports","metric_unit","A recorded report count uses reports as its explicit unit.");
        Range(d.PeriodFrom, d.PeriodTo);
    }

    public static IEnumerable<(QmsRevisionReference Reference, QmsPlanningKind? PlanningKind, QmsRecordKind? QmsKind)> References(QmsPlanningContent c)
    {
        foreach (var r in c.Requirements ?? []) yield return (r, null, QmsRecordKind.Requirement);
        if (c.RiskOpportunity is { } risk) { yield return (risk.Process, null, QmsRecordKind.Process); yield return (risk.EvaluationMethod, QmsPlanningKind.EvaluationMethod, null); }
        if (c.Objective is { } objective) yield return (objective.Metric, QmsPlanningKind.MetricDefinition, null);
        if (c.Change is { } change)
        {
            foreach (var r in change.AffectedProcesses) yield return (r, null, QmsRecordKind.Process);
            foreach (var r in change.RisksAndOpportunities) yield return (r, QmsPlanningKind.RiskOpportunity, null);
        }
    }
    public static IEnumerable<QmsPlanningEvidenceReference> Evidence(QmsPlanningContent c) =>
        (c.Evidence ?? []).Concat(c.Objective?.BaselineEvidence ?? []).Concat(c.Change?.EngineeringChanges ?? []).Concat(c.Improvement?.Origins ?? []);

    public static QmsState Transition(QmsState state, string action) => action switch
    {
        "Submit" when state == QmsState.Draft => QmsState.Submitted,
        "Review" when state == QmsState.Submitted => QmsState.Reviewed,
        "Approve" when state == QmsState.Reviewed => QmsState.Approved,
        "Effective" when state == QmsState.Approved => QmsState.Effective,
        "Withdraw" when state is not (QmsState.Withdrawn or QmsState.Superseded) => QmsState.Withdrawn,
        _ => throw new CommandRejectedException("planning_state", "The action is unavailable in the current state.")
    };
    public static void Independent(string actor, IEnumerable<string> authors, IEnumerable<QmsPlanningSourceSnapshot> evidence)
    {
        Require(!authors.Concat(evidence.SelectMany(x => x.Contributors)).Contains(actor, StringComparer.Ordinal),
            "separation_of_duties", "A contributor cannot independently review, approve or verify this decision.");
    }
    public static void Version(int actual, int expected) => Require(actual == expected, "stale_version", "Read the current version before deciding.");
    public static bool InEffect(QmsPlanningRevision r, DateTimeOffset now) => r.State == QmsState.Effective && r.EffectiveFrom <= now && (r.EffectiveTo is null || now < r.EffectiveTo);
    public static void Text(string? value, string field, int maximum = 4000) => Require(!string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\r' or '\t')), "planning_field", field + " is required and must fit its recorded bound.");
    public static void Person(Guid person) => Require(person != Guid.Empty, "planning_owner", "Select an actual responsible person.");
    public static void Instant(DateTimeOffset value) => Require(value != default, "planning_date", "Declare an actual date and time.");
    public static void Range(DateTimeOffset from, DateTimeOffset to) => Require(from != default && to > from, "planning_period", "Declare an ordered half-open measurement period.");
    public static void Reference(QmsRevisionReference r) { BusinessCode.Require(r.Code, "Referenced record"); Require(r.Revision > 0, "planning_revision", "Reference an exact positive revision."); }
    public static void EvidenceReference(QmsPlanningEvidenceReference e)
    {
        Require(e is not null,"planning_evidence","Supply an actual evidence reference.");
        Text(e.Context, "Evidence context", 80); Text(e.Type, "Evidence type", 80); Text(e.Key, "Evidence owner key", 160);
        Require(e.EventId != Guid.Empty && e.AuditId != Guid.Empty, "planning_evidence", "Evidence IDs must be actual nonempty IDs when supplied.");
    }
    public static void List<T>(IReadOnlyList<T>? value,string field,int maximum)=>Require(value is not null&&value.Count<=maximum&&value.All(item=>item is not null),"planning_list",field+" must be an explicit bounded list of non-null entries.");
    public static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string code, string message) { if (!condition) throw new CommandRejectedException(code, message); }
}
