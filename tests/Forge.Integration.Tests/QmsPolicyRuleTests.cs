using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;

namespace Forge.Integration.Tests;

public sealed class QmsPolicyRuleTests
{
    private static readonly Guid Owner=Guid.Parse("9240f862-a645-4300-a307-4bfa2044e009");
    private static QmsContent Policy(QmsPolicyKind kind,QmsPolicyRules rules)=>new(Policy:new(kind,"Explicit human policy",Owner,"Recorded briefing","On change",true,rules));
    private static QmsLabelLifecycleRules Labels()=>new(QmsActiveCopyMode.SingleActive,1,true,true,true,true,true,QmsLabelConfirmation.OperatorObservedPrintingAndAttachment,
        ["material"],[new("MATERIAL",1)],[QmsLabelSymbology.Code128],true,true,true);
    private static QmsOperationScanRule Rule(string code,QmsRevisionReference? process=null,string? risk=null)=>
        new(code,new(WorkCenterCode:"SLIT-WC",ActivityCode:"RUN_SLITTER",OperationSequence:1),process,risk,[QmsRequiredScanKind.Operation,QmsRequiredScanKind.Material],false,true,true);

    [Fact]
    public void Historic_foundation_definitions_remain_valid_but_missing_extensions_never_become_operational_defaults()
    {
        var content=Policy(QmsPolicyKind.LabelCopy,new(LabelCopy:new(true,1,true)));
        QmsContentRules.Validate(QmsRecordKind.Policy,content);
        Assert.Equal("policy_rules_incomplete",Assert.Throws<CommandRejectedException>(()=>QmsPolicyRuleValidation.RequireConsumable(QmsPolicyPurpose.LabelCopy,content)).Code);
        var complete=content with{Policy=content.Policy! with{Rules=new(LabelCopy:new(true,1,true,Labels()))}};
        QmsPolicyRuleValidation.RequireConsumable(QmsPolicyPurpose.LabelCopy,complete);
        Assert.Equal("policy_rules_incomplete",Assert.Throws<CommandRejectedException>(()=>QmsPolicyRuleValidation.ValidateExtensions(new(LabelCopy:new(true,1,true,Labels() with{AllowVoid=null})))).Code);
    }
    [Fact]
    public void Process_and_risk_come_from_the_recorded_adoption_and_multiple_matching_rules_are_not_ranked_by_looseness()
    {
        var process=new QmsRevisionReference("SLITTING",2);
        var binding=new QmsPolicyAdoptionContent(QmsPolicyPurpose.Execution,"EXECUTION",1,new(WorkCenterCode:"SLIT-WC"),process,"HIGH","Human assessment of the actual selected process","Explicit applicability");
        var context=new QmsPolicyContext("PLANT1",WorkCenterCode:"SLIT-WC",ActivityCode:"RUN_SLITTER",OperationSequence:1);
        var exact=Rule("HIGH-RISK",process,"HIGH");
        var presentation=new QmsExecutionPresentationRules(120,true,true,true,QmsProviderAssurance.StepUp,[exact]);
        Assert.Equal(exact,QmsPolicyRuleValidation.MatchOperationRule(presentation,binding,context));
        Assert.Equal("operation_policy_not_defined",Assert.Throws<CommandRejectedException>(()=>QmsPolicyRuleValidation.MatchOperationRule(presentation,binding with{RiskClassCode="LOW"},context)).Code);
        Assert.Equal("operation_policy_ambiguous",Assert.Throws<CommandRejectedException>(()=>QmsPolicyRuleValidation.MatchOperationRule(presentation with{OperationRules=[exact,Rule("LOOSER") with{AllowManualSelection=true}]},binding,context)).Code);
        Assert.False(QmsPolicyRuleValidation.Matches(new(WorkCenterCode:"OTHER"),context));
    }
    [Fact]
    public void Explicit_session_bounds_and_current_authenticated_person_are_required_without_barcode_authentication()
    {
        var presentation=new QmsExecutionPresentationRules(120,true,true,true,QmsProviderAssurance.Authenticated,[Rule("NORMAL")]);
        var content=Policy(QmsPolicyKind.Execution,new(Execution:new(true,true,true,true,presentation)));
        QmsPolicyRuleValidation.RequireConsumable(QmsPolicyPurpose.Execution,content);
        Assert.Equal("session_invariant",Assert.Throws<CommandRejectedException>(()=>QmsPolicyRuleValidation.ValidateExtensions(new(Execution:new(true,true,true,true,presentation with{RequireCurrentAuthenticatedOperator=false,RequireTrustedWorkstation=false})))).Code);
        QmsPolicyRuleValidation.ValidateExtensions(new(Execution:new(true,true,true,true,presentation with{RequireCurrentAuthenticatedOperator=false,RequireTrustedWorkstation=true})));
        Assert.Equal("policy_range",Assert.Throws<CommandRejectedException>(()=>QmsPolicyRuleValidation.ValidateExtensions(new(Execution:new(true,true,true,true,presentation with{SessionExpirySeconds=0})))).Code);
        Assert.Equal("policy_rules_incomplete",Assert.Throws<CommandRejectedException>(()=>QmsPolicyRuleValidation.ValidateExtensions(new(Execution:new(true,true,true,true,presentation with{ProviderAssurance=null})))).Code);
    }
    [Fact]
    public void Logical_copy_lifecycle_is_explicit_and_retention_cannot_select_credential_or_unknown_raw_payload_capture()
    {
        Assert.Equal("copy_limit",Assert.Throws<CommandRejectedException>(()=>QmsPolicyRuleValidation.ValidateExtensions(new(LabelCopy:new(true,1,true,Labels() with{MaximumActiveCopies=2})))).Code);
        Assert.Equal("copy_limit",Assert.Throws<CommandRejectedException>(()=>QmsPolicyRuleValidation.ValidateExtensions(new(LabelCopy:new(true,1,true,Labels() with{ReplacementVoidsPriorCopy=false})))).Code);
        var telemetry=new QmsTelemetryRules(QmsTelemetryPayloadMode.NormalizedReferencesOnly,256,true,true,true);
        var content=Policy(QmsPolicyKind.AttemptRetention,new(AttemptRetention:new(30,365,1000,true,true,true,telemetry)));
        QmsPolicyRuleValidation.RequireConsumable(QmsPolicyPurpose.AttemptRetention,content);
        Assert.Equal("policy_range",Assert.Throws<CommandRejectedException>(()=>QmsPolicyRuleValidation.ValidateExtensions(new(AttemptRetention:new(30,365,1000,true,true,true,telemetry with{MaximumCapturedReferenceCharacters=1025})))).Code);
        Assert.Equal(new[]{"NoPayload","NormalizedReferencesOnly"},Enum.GetNames<QmsTelemetryPayloadMode>());
    }
}
