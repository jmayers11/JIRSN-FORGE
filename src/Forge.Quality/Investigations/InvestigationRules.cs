using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Quality.Contracts;

namespace Forge.Quality.Investigations;

public static class InvestigationRules
{
    public static void Finding(MeasurementFindingContent content,DateTimeOffset recordedAt)
    {
        BusinessCode.Require(content.ToolCode,"Measuring resource");Text(content.AsFoundObservation);Text(content.IntervalBasis);Text(content.Evidence);
        if(content.CalibrationEventId is<=0||content.DiscoveredAt>recordedAt||content.SuspectedFrom>content.SuspectedTo||content.SuspectedFrom>content.DiscoveredAt||content.SuspectedTo>content.DiscoveredAt)
            Reject("suspected_interval","Use an actual discovery time and coherent suspected historic interval; absent bounds remain explicitly unknown.");
        if(content.AffectedRange is{} range)
        {
            Text(range.Basis);
            if(range.From>range.To||((range.From is not null||range.To is not null)&&string.IsNullOrWhiteSpace(range.Unit)))Reject("affected_range","A declared measurement range needs coherent bounds and actual units.");
            if(range.CharacteristicName is{} characteristic)Text(characteristic);
        }
    }
    public static void Root(TraceInvestigationRoot root)
    {
        if(!Enum.IsDefined(root.Kind))Reject("investigation_root","Use a supported authoritative trigger.");
        if(root.Kind==TraceTriggerKind.SupplierLot)
        {
            if(root.FindingId is not null||string.IsNullOrWhiteSpace(root.PartNumber)||string.IsNullOrWhiteSpace(root.LotNumber))Reject("investigation_root","Identify the exact recorded physical supplier material; labels are not another source.");
            BusinessCode.Require(root.PartNumber!,"Part");
        }
        else if(root.FindingId is null or<=0||root.PartNumber is not null||root.LotNumber is not null)Reject("investigation_root","Select the actual reviewed measurement finding.");
    }
    public static bool InInterval(DateTimeOffset at,DateTimeOffset? from,DateTimeOffset? to,DateTimeOffset cutoff)=>(from is null||at>=from)&&(to is null||at<=to)&&at<=cutoff;
    public static void Assessment(TraceImpactDecision decision,string basis,string evidence,string disposition)
    {if(!Enum.IsDefined(decision))Reject("impact_decision","Record a supported human impact decision.");Text(basis);Text(evidence);Text(disposition);}
    public static void Text(string? value){if(string.IsNullOrWhiteSpace(value)||value.Length>4000)Reject("investigation_basis","Record an explicit bounded human observation/decision basis.");}
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(string code,string message)=>throw new CommandRejectedException(code,message);
}
