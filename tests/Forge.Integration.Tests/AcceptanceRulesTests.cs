using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Quality.Acceptance;
using Forge.Quality.Contracts;

namespace Forge.Integration.Tests;

public sealed class AcceptanceRulesTests
{
 private static AcceptanceCriteriaContent Criteria(AcceptanceCoverageKind kind)=>new("PRODUCT","A",10,"Approved exact criteria",new(kind,1,1000,"Defined representative cohort method","Human Quality approval for the represented quantity"),[new(1,"Width",CharacteristicKind.Variable,10,9,11,"mm")],[new("ACCEPTANCE.INSTRUCTION","A")]);
 [Fact]
 public void Full_quantity_and_explicit_approved_sampling_are_distinct_and_freeze_unit_nominal_limits()
 {
  var full=Criteria(AcceptanceCoverageKind.FullQuantity);AcceptanceRules.Validate(full);
  Assert.Equal("full_quantity_required",Assert.Throws<CommandRejectedException>(()=>AcceptanceRules.Coverage(full.Coverage,920,92,false)).Code);
  var sampled=Criteria(AcceptanceCoverageKind.ApprovedSampling);AcceptanceRules.Validate(sampled);AcceptanceRules.Coverage(sampled.Coverage,920,92,false);
  var results=AcceptanceRules.Findings(sampled,2,1,false,[new(1,1,10,null,"GAUGE")]);
  var result=Assert.Single(results);Assert.True(result.Pass);Assert.Equal(("mm",10m,9m,11m),(result.Criterion.Unit,result.Criterion.Nominal,result.Criterion.Lower,result.Criterion.Upper));
  Assert.False(Assert.Single(AcceptanceRules.Findings(sampled,2,1,false,[new(1,1,12,null,"GAUGE")])).Pass);
 }
 [Fact]
 public void Missing_sample_results_unknown_equipment_and_duplicate_results_never_imply_acceptance()
 {
  var criteria=Criteria(AcceptanceCoverageKind.FullQuantity);
  Assert.Equal("complete_results_required",Assert.Throws<CommandRejectedException>(()=>AcceptanceRules.Findings(criteria,2,2,false,[new(1,1,10,null,"GAUGE")])).Code);
  Assert.Equal("complete_results_required",Assert.Throws<CommandRejectedException>(()=>AcceptanceRules.Findings(criteria,2,2,false,[new(1,1,10,null,"GAUGE"),new(1,1,10,null,"GAUGE")])).Code);
  Assert.Equal("measurement_required",Assert.Throws<CommandRejectedException>(()=>AcceptanceRules.Findings(criteria,1,1,false,[new(1,1,10,null,null)])).Code);
  Assert.Equal("serial_coverage",Assert.Throws<CommandRejectedException>(()=>AcceptanceRules.Coverage(criteria.Coverage,2,2,true)).Code);
 }
 [Fact]
 public void Physical_source_keys_preserve_delimiters_and_reject_ambiguous_or_noncanonical_encodings()
 {
  var subject=new AcceptanceSubjectKey("PRODUCT","LOT/1.OBSERVED","STORES");var key=AcceptanceSubjectReference.Format(subject);
  Assert.True(AcceptanceSubjectReference.TryParse(key,out var parsed));Assert.Equal(subject,parsed);
  Assert.False(AcceptanceSubjectReference.TryParse("PRODUCT:LOT:EXTRA:STORES",out _));Assert.False(AcceptanceSubjectReference.TryParse("PRODUCT:LOT%2f1:STORES",out _));
 }
 [Fact]
 public void Mixed_product_owners_preserve_every_action_scope_and_contract_while_linked_document_permissions_stay_separate()
 {
  var first=Guid.Parse("11111111-1111-1111-1111-111111111111");var second=Guid.Parse("22222222-2222-2222-2222-222222222222");
  var source=new InspectionSourceContext(new("PLANT1",WorkCenterCode:"WC"),[
   new(Permissions.OrdersRead,new("Order","10001",new ProgramId(first),ContractId:"CONTRACT-A",CustomerId:"CUSTOMER-A",ShiftId:"SHIFT-A",Amount:50,Currency:"USD")),
   new(Permissions.OrdersRead,new("Order","10002",new ProgramId(second),ContractId:"CONTRACT-B")),
   new(Permissions.EngineeringRead,new("DocumentRevision","CONTROL/A",Markings:DataMarking.ControlledUnclassified))],[]);
  var subject=new InspectionSubject(new("PRODUCT","LOT","STORES"),"A","EA",1,0,false,source,[10],[1,2],[],[],[10001,10002],"exact-config");
  var actions=AcceptanceSubjectResources.All(subject,"ProductReleaseDecision",30001);Assert.Equal(2,actions.Count);
  Assert.Equal((first,"CONTRACT-A","CUSTOMER-A","SHIFT-A",50m,"USD"),(actions[0].Program!.Value.Value,actions[0].ContractId,actions[0].CustomerId,actions[0].ShiftId,actions[0].Amount,actions[0].Currency));
  Assert.Equal((second,"CONTRACT-B"),(actions[1].Program!.Value.Value,actions[1].ContractId));
  Assert.All(actions,action=>{Assert.Equal("ProductReleaseDecision",action.Type);Assert.Equal("30001",action.Id);Assert.Equal("PLANT1",action.FacilityId);Assert.Equal("WC",action.WorkCenterId);Assert.Equal(DataMarking.ControlledUnclassified,action.Markings);});
  Assert.Contains(source.Sources,read=>read.Permission==Permissions.EngineeringRead&&read.Resource.Type=="DocumentRevision");
 }
}
