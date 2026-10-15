using Forge.Kernel.Commands;
using Forge.Quality.Contracts;
using Forge.Quality.Investigations;

namespace Forge.Integration.Tests;

public sealed class TraceInvestigationRulesTests
{
 [Fact]
 public void Complete_forward_graph_handles_deep_recorded_ancestry_cycles_and_every_branch_without_path_copying_or_display_caps()
 {
  var evidence=new TraceEvidence("InstanceLink","actual-record","Split",1,"EA",null,"Recorded paired ledger evidence",[]);
  var links=Enumerable.Range(0,5000).Select(number=>new TraceGraphLink("n"+number,"n"+(number+1),evidence with{Id=number.ToString(System.Globalization.CultureInfo.InvariantCulture)})).Concat([new("n4999","n10",evidence),new("n2500","branch",evidence),new("unrelated","hidden-unrelated",evidence)]).ToArray();
  var result=TraceGraphEngine.Forward(["n0"],links);Assert.Equal(5002,result.Nodes.Count);Assert.Equal(5002,result.Edges.Count);Assert.Contains("n5000",result.Nodes);Assert.Contains("branch",result.Nodes);Assert.DoesNotContain("hidden-unrelated",result.Nodes);
 }
 [Fact]
 public void Suspected_interval_is_inclusive_and_an_unknown_bound_stays_unknown_instead_of_becoming_an_invented_date()
 {
  var begin=DateTimeOffset.Parse("2026-01-01T00:00:00Z",System.Globalization.CultureInfo.InvariantCulture);var end=begin.AddDays(2);
  Assert.True(InvestigationRules.InInterval(begin,begin,end,end));Assert.True(InvestigationRules.InInterval(end,begin,end,end));Assert.False(InvestigationRules.InInterval(begin.AddTicks(-1),begin,end,end));Assert.False(InvestigationRules.InInterval(end.AddTicks(1),begin,end,end));
  var content=new MeasurementFindingContent("ACTUAL-GAUGE",null,end,"Actual as-found concern",null,null,end,"Start bound remains unknown","Actual evidence recorded");InvestigationRules.Finding(content,end);Assert.Null(content.SuspectedFrom);Assert.True(InvestigationRules.InInterval(begin.AddYears(-10),null,end,end));
  Assert.Throws<CommandRejectedException>(()=>InvestigationRules.Finding(content with{SuspectedFrom=end.AddDays(1)},end));
 }
 [Fact]
 public void Human_range_determination_needs_actual_units_and_coherent_bounds_without_claiming_every_product_defective()
 {
  var discovery=DateTimeOffset.UtcNow;var content=new MeasurementFindingContent("GAUGE",42,discovery,"As-found offset",new("Width","mm",9,11,"Quality's actual suspected range"),discovery.AddDays(-2),discovery,"Human interval determination","Actual certificate/observation");
  InvestigationRules.Finding(content,discovery);Assert.Throws<CommandRejectedException>(()=>InvestigationRules.Finding(content with{AffectedRange=content.AffectedRange! with{Unit=null}},discovery));Assert.Throws<CommandRejectedException>(()=>InvestigationRules.Finding(content with{AffectedRange=content.AffectedRange! with{From=12,To=11}},discovery));
 }
}
