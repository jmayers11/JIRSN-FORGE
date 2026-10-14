using Forge.Host.Identification;

namespace Forge.Integration.Tests;

public sealed class V41PresentationCodecTests
{
    [Theory]
    [InlineData("person:0199eaf0-0100-7000-8000-000000000001","person")]
    [InlineData("order:1","order")][InlineData("operation:1:10","operation")][InlineData("traveler:1","traveler")]
    [InlineData("material:P:L","material")][InlineData("batch:P:L","batch")][InlineData("roll:P:L","roll")][InlineData("serial:P:L","serial")]
    [InlineData("wip:1:10","wip")][InlineData("remnant:P:L","remnant")][InlineData("machine:M-1","machine")]
    [InlineData("workcenter:WC-1","workcenter")][InlineData("tool:T-1","tool")][InlineData("gauge:G-1","gauge")]
    [InlineData("location:WH-1","location")][InlineData("container:C-1","container")][InlineData("package:C-1","package")]
    [InlineData("inspection:1","inspection")][InlineData("shipment:1","shipment")]
    public void Original_nineteen_categories_have_typed_canonical_owner_keys(string suffix,string kind)
    {var code="forge:v1:N:"+suffix;var parsed=CanonicalIdentification.Parse(code,"N");Assert.Equal((code,"N",kind),(parsed.Code,parsed.Namespace,parsed.Kind));}
    [Fact]
    public void Presentation_UUIDs_and_recorded_output_lines_do_not_accept_numeric_aliases_or_mapping_chains()
    {
        foreach(var kind in new[]{"label","alias"})
        {
            var code="forge:v1:N:"+kind+":0199eaf0-0100-7000-8000-000000000001";
            Assert.Equal(Guid.Parse("0199eaf0-0100-7000-8000-000000000001"),CanonicalIdentification.Parse(code,"N").Id);
            foreach(var key in new[]{"1","0199EAF0-0100-7000-8000-000000000001","00000000-0000-0000-0000-000000000000","{0199eaf0-0100-7000-8000-000000000001}"})
                Assert.Throws<IdentificationException>(()=>CanonicalIdentification.Parse("forge:v1:N:"+kind+":"+key,"N"));
        }
        var output=CanonicalIdentification.Parse("forge:v1:N:output:9223372036854775807:2147483647","N");Assert.Equal(((long?)long.MaxValue,(int?)int.MaxValue),(output.RecordNumber,output.LineNumber));
        var instruction=CanonicalIdentification.Parse("forge:v1:N:instruction:PROC%2F1:A","N");Assert.Equal(("PROC/1","A"),(instruction.Key,instruction.Revision));
        foreach(var suffix in new[]{"output:1:0","output:1:01","output:01:1","inspection:0","shipment:9223372036854775808","wip:1","gauge:T%2f1","roll:P:L:extra"})
            Assert.Throws<IdentificationException>(()=>CanonicalIdentification.Parse("forge:v1:N:"+suffix,"N"));
    }
}
