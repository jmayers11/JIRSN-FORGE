using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Forge.Host.Identification;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using ZXing;
using ZXing.Common;

namespace Forge.Integration.Tests;

public sealed class LabelRenderingTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid LabelId = Guid.Parse("0199eaf0-0100-7000-8000-000000000001");
    internal static LabelTemplateView Template() => new("MATERIAL",1,2,LabelTemplateState.Approved,1200,320,["material","roll"],
        [LabelSymbology.Code128,LabelSymbology.QR,LabelSymbology.DataMatrix],"PLANT1",[new("COPY.PROCEDURE","A")],"person:author","person:author","person:approver",At,At,[]);
    internal static LabelCopyView Copy(LabelSymbology symbology) => new(LabelId, Guid.Parse("0199eaf0-0100-7000-8000-000000000002"),
        "forge:v1:EXAMPLE:label:"+LabelId.ToString("D"),"forge:v1:EXAMPLE:material:PART%2F1:LOT%2F1",
        new("forge:v1:EXAMPLE:material:PART%2F1:LOT%2F1","EXAMPLE","material",null,null,"PART/1","A","Recorded","LOT/1"),
        "MATERIAL",1,symbology,"PLANT1",LabelCopyState.Active,2,"person:author",At,"person:approver",At,null,LabelRelationship.Original,
        "POLICY",1,"policy-hash",new(new("forge:v1:EXAMPLE:material:PART%2F1:LOT%2F1","EXAMPLE","material",null,null,"PART/1","A","Recorded","LOT/1"),
            "forge:v1:EXAMPLE:material:PART%2F1:LOT%2F1",Permissions.InventoryRead,[new("Lot","PART%2F1:LOT%2F1")]),[]);

    [Theory]
    [InlineData(LabelSymbology.Code128,BarcodeFormat.CODE_128)]
    [InlineData(LabelSymbology.QR,BarcodeFormat.QR_CODE)]
    [InlineData(LabelSymbology.DataMatrix,BarcodeFormat.DATA_MATRIX)]
    public void Actual_exported_SVG_bars_decode_to_the_exact_opaque_copy_code(LabelSymbology symbology,BarcodeFormat expected)
    {
        var copy=Copy(symbology);var renderer=new LabelRendering();var artifact=renderer.Render(copy,Template());
        var decoded=Decode(artifact.Bytes,expected);
        Assert.Equal((copy.Code,expected),(decoded.Text,decoded.BarcodeFormat));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(artifact.Bytes)).ToLowerInvariant(),artifact.Sha256);
        Assert.Equal(artifact.Bytes,renderer.Render(copy,Template()).Bytes);
        Assert.Contains("PART/1 lot LOT/1 rev A",Encoding.UTF8.GetString(artifact.Bytes),StringComparison.Ordinal);
        Assert.DoesNotContain("<script",Encoding.UTF8.GetString(artifact.Bytes),StringComparison.OrdinalIgnoreCase);
    }

    internal static Result Decode(byte[] svgBytes,BarcodeFormat format)
    {
        // Rasterize the exported SVG rectangles, not a second writer or the renderer's internal matrix.
        var svg=XDocument.Parse(Encoding.UTF8.GetString(svgBytes));var root=svg.Root!;XNamespace ns="http://www.w3.org/2000/svg";
        var width=int.Parse(root.Attribute("width")!.Value,CultureInfo.InvariantCulture);var height=int.Parse(root.Attribute("height")!.Value,CultureInfo.InvariantCulture);
        var rgb=new byte[checked(width*height*3)];Array.Fill(rgb,(byte)255);
        foreach(var rect in root.Elements(ns+"rect").Where(rect=>rect.Attribute("fill")?.Value=="black"))
        {
            int Value(string name)=>int.Parse(rect.Attribute(name)!.Value,CultureInfo.InvariantCulture);
            var x=Value("x");var y=Value("y");var w=Value("width");var h=Value("height");
            Assert.InRange(x,0,width-1);Assert.InRange(y,0,height-1);Assert.True(x+w<=width&&y+h<=height);
            for(var row=y;row<y+h;row++)for(var column=x;column<x+w;column++)
            {var offset=(row*width+column)*3;rgb[offset]=rgb[offset+1]=rgb[offset+2]=0;}
        }
        var reader=new BarcodeReaderGeneric{Options=new DecodingOptions{TryHarder=true,PossibleFormats=[format]}};
        var result=reader.Decode(new RGBLuminanceSource(rgb,width,height,RGBLuminanceSource.BitmapFormat.RGB24));
        Assert.NotNull(result);return result;
    }

    [Fact]
    public void Human_reconciliation_does_not_accept_spool_acknowledgement_or_infer_printing_from_attachment()
    {
        foreach(var outcome in new[]{LabelPrintState.Requested,LabelPrintState.Sent,LabelPrintState.SpoolAccepted})
            Assert.Equal("print_observation",Assert.Throws<CommandRejectedException>(()=>LabelService.ValidateObservation(new(Guid.Empty,1,outcome,"Dialog opened",false,false))).Code);
        Assert.Throws<CommandRejectedException>(()=>LabelService.ValidateObservation(new(Guid.Empty,1,LabelPrintState.Confirmed,"Only spool acknowledgement",false,false)));
        Assert.Throws<CommandRejectedException>(()=>LabelService.ValidateObservation(new(Guid.Empty,1,LabelPrintState.Uncertain,"Uncertain",false,true)));
        LabelService.ValidateObservation(new(Guid.Empty,1,LabelPrintState.Uncertain,"Printer jam; physical outcome unconfirmed",false,false));
        LabelService.ValidateObservation(new(Guid.Empty,2,LabelPrintState.Confirmed,"Observed correct printing and attachment to actual subject",true,true));
    }

    [Fact]
    public void One_unreconciled_duplicate_attempt_blocks_an_otherwise_confirmed_copy_and_does_not_turn_acknowledgement_into_attachment()
    {
        var copy=Copy(LabelSymbology.QR);var template=Template();
        var life=new QmsLabelLifecycleRules(QmsActiveCopyMode.SingleActive,1,true,true,true,true,true,
            QmsLabelConfirmation.OperatorObservedPrintingAndAttachment,["material"],[new("MATERIAL",1)],[QmsLabelSymbology.QR],true,true,true);
        var policy=new QmsAdoptedPolicy("ADOPTION",1,5,"POLICY",1,5,"EXAMPLE",new("PLANT1"),new(),null,null,null,At,null,DataMarking.None,
            new(LabelCopy:new(true,1,true,life)),null,"current-hash",[]);
        var confirmed=new LabelPrintAttemptView(Guid.NewGuid(),copy.LabelId,2,LabelPrintState.Confirmed,null,"person:author",At,"Observed printing only","person:author",At,"sha",true,false,"template-hash","current-hash");
        Assert.Equal("label_confirmation_required",Assert.Throws<CommandRejectedException>(()=>PresentationReadRules.RequireUsable(copy,template,policy,[confirmed])).Code);
        confirmed=confirmed with{ObservedAttached=true};PresentationReadRules.RequireUsable(copy,template,policy,[confirmed]);
        var uncertain=confirmed with{AttemptId=Guid.NewGuid(),State=LabelPrintState.Uncertain,ObservedPrinted=false,ObservedAttached=false};
        Assert.Equal("print_reconciliation_required",Assert.Throws<CommandRejectedException>(()=>PresentationReadRules.RequireUsable(copy,template,policy,[confirmed,uncertain])).Code);
        PresentationReadRules.RequireUsable(copy,template,policy,[confirmed,uncertain with{State=LabelPrintState.Failed}]);
        Assert.Equal("label_inactive",Assert.Throws<CommandRejectedException>(()=>PresentationReadRules.RequireUsable(copy with{State=LabelCopyState.Voided},template,policy,[confirmed])).Code);
        Assert.Equal("label_inactive",Assert.Throws<CommandRejectedException>(()=>PresentationReadRules.RequireUsable(copy,template with{State=LabelTemplateState.Withdrawn},policy,[confirmed])).Code);
    }

    [Fact]
    public void Printed_source_configuration_cannot_silently_follow_a_changed_owner_revision_but_status_progress_is_allowed()
    {
        var copy=Copy(LabelSymbology.QR);var source=new IdentificationSubject(copy.Subject with{Status="Held"},copy.SubjectCode,"Lot","PART%2F1:LOT%2F1",Permissions.InventoryRead,copy.Snapshot.Resources);
        PresentationReadRules.RequireCurrentIdentity(copy,source);
        Assert.Equal("label_source_changed",Assert.Throws<CommandRejectedException>(()=>PresentationReadRules.RequireCurrentIdentity(copy,source with{Display=source.Display with{Revision="B"}})).Code);
        var traveler=copy with{Snapshot=copy.Snapshot with{Display=copy.Snapshot.Display with{SourceFingerprint="recorded-routing-bom"}}};
        Assert.Equal("label_source_changed",Assert.Throws<CommandRejectedException>(()=>PresentationReadRules.RequireCurrentIdentity(traveler,source with{Display=source.Display with{SourceFingerprint="different-routing-bom"}})).Code);
    }
}
