using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;

namespace Forge.Quality.Acceptance;

public static class AcceptanceRules
{
    public static void Validate(AcceptanceCriteriaContent content)
    {
        if(content is null||content.Coverage is null||content.Characteristics is null||content.Documents is null) Reject("criteria_required","Supply typed criteria, coverage and controlled source revisions.");
        BusinessCode.Require(content.PartNumber,"Part");QmsContentRules.DocumentRevision(content.Revision);Text(content.Title);
        if(content.OperationSequence is<=0) Reject("operation_sequence","Reference an actual positive operation sequence.");
        var coverage=content.Coverage;
        if(!Enum.IsDefined(coverage.Kind)||coverage.MinimumSampleQuantity<=0||coverage.MaximumRepresentedQuantity<coverage.MinimumSampleQuantity||coverage.MaximumRepresentedQuantity>100000000) Reject("coverage_required","Define explicit finite sample and represented-quantity bounds.");
        Text(coverage.Method);Text(coverage.HumanApprovalBasis);
        if(content.Characteristics.Count is<1 or>99||content.Characteristics.Any(c=>c is null)||content.Characteristics.Select(c=>c.Number).Distinct().Count()!=content.Characteristics.Count) Reject("characteristics_required","Define 1-99 unique numbered characteristics.");
        foreach(var c in content.Characteristics)
        {
            Text(c.Name);if(c.Number<=0||!Enum.IsDefined(c.Kind))Reject("characteristic","Use a positive number and known kind.");
            if(c.Kind==CharacteristicKind.Variable&&(string.IsNullOrWhiteSpace(c.Unit)||c.Lower is null&&c.Upper is null||c.Lower>c.Upper||c.Nominal<c.Lower||c.Nominal>c.Upper))Reject("measurement_criteria","Measured criteria require a unit and coherent limits; an optional nominal must lie within them.");
            if(c.Kind==CharacteristicKind.Attribute&&(c.Nominal is not null||c.Lower is not null||c.Upper is not null||c.Unit is not null))Reject("attribute_criteria","Attribute criteria record explicit acceptance rather than numeric limits.");
        }
        if(content.Documents.Count is<1 or>99||content.Documents.Any(d=>d is null))Reject("criteria_source","Use actual controlled documents for the approved acceptance arrangement.");
        foreach(var document in content.Documents){QmsContentRules.DocumentNumber(document.DocumentNumber);QmsContentRules.DocumentRevision(document.Revision);}
        if(content.Documents.GroupBy(d=>(QmsContentRules.DocumentNumber(d.DocumentNumber),QmsContentRules.DocumentRevision(d.Revision))).Any(g=>g.Count()>1))Reject("duplicate_source","Each exact controlled revision is referenced once.");
    }
    public static void Coverage(AcceptanceCoverage rule,decimal quantity,decimal sample,bool serialized)
    {
        if(quantity<=0||quantity>rule.MaximumRepresentedQuantity||sample<=0||sample>quantity||sample<rule.MinimumSampleQuantity||sample!=decimal.Truncate(sample)||sample>10000)Reject("inspection_coverage","The actual identified quantity/sample must satisfy the approved bounded coverage rule.");
        if(rule.Kind==AcceptanceCoverageKind.FullQuantity&&sample!=quantity)Reject("full_quantity_required","Full-quantity inspection cannot be represented by a smaller sample.");
        if(serialized&&(quantity!=1||sample!=1))Reject("serial_coverage","Each exact serial is one identified inspected unit.");
    }
    public static IReadOnlyList<(CharacteristicInfo Criterion,AcceptanceFinding Finding,bool Pass)> Findings(AcceptanceCriteriaContent criteria,decimal quantity,decimal sample,bool serialized,IReadOnlyList<AcceptanceFinding> findings)
    {
        Coverage(criteria.Coverage,quantity,sample,serialized);
        if(findings is null||findings.Count!=criteria.Characteristics.Count*sample||findings.Any(f=>f is null)||findings.Select(f=>(f.CharacteristicNumber,f.SampleNumber)).Distinct().Count()!=findings.Count)Reject("complete_results_required","Record every criterion for every sampled unit once.");
        var results=new List<(CharacteristicInfo,AcceptanceFinding,bool)>();
        foreach(var criterion in criteria.Characteristics)
        for(var sampleNumber=1;sampleNumber<=sample;sampleNumber++)
        {
            var finding=findings.SingleOrDefault(f=>f.CharacteristicNumber==criterion.Number&&f.SampleNumber==sampleNumber)??throw new CommandRejectedException("complete_results_required","A required criterion/sample result is missing.");
            if(criterion.Kind==CharacteristicKind.Variable)
            {
                if(finding.Measured is null||finding.Accepted is not null||string.IsNullOrWhiteSpace(finding.GaugeCode))Reject("measurement_required","Record a measurement and actual gauge for each measured criterion.");
                results.Add((criterion,finding,(criterion.Lower is null||finding.Measured>=criterion.Lower)&&(criterion.Upper is null||finding.Measured<=criterion.Upper)));
            }
            else
            {
                if(finding.Accepted is null||finding.Measured is not null||finding.GaugeCode is not null)Reject("attribute_required","Record explicit accepted/rejected for each attribute.");
                results.Add((criterion,finding,finding.Accepted.Value));
            }
        }
        return results;
    }
    private static void Text(string? text){if(string.IsNullOrWhiteSpace(text)||text.Length>4000)Reject("evidence_basis","Record the actual bounded human basis.");}
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(string code,string message)=>throw new CommandRejectedException(code,message);
}
