using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Quality.Commercial;
using Forge.Quality.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record CreateCommercialRequest(CommercialRecordKind Kind,string Code,string FacilityCode,CommercialContent Content,string? Reason);
public sealed record CommercialContentRequest(int ExpectedVersion,CommercialContent Content,string? Reason);
public sealed record CommercialTransitionRequest(int ExpectedVersion,string Basis,string? Reason);
public sealed record CommercialObservationRequest(int ExpectedVersion,CommercialObservationKind Observation,DateTimeOffset ObservedAt,
    string Basis,IReadOnlyList<CommercialReference> Evidence,string? RecordedExternalPerson,string? Reason,IReadOnlyList<PreservationReading>? Preservation=null);
public static class CommercialQualityEndpoints
{
    public static void MapCommercialQualityEndpoints(this IEndpointRouteBuilder app)
    {
        var api=app.MapGroup("/api/quality/commercial").RequireAuthorization();
        api.MapGet("/records",(HttpContext http,CommercialQualityService service,CommercialRecordKind? kind,int? take,string? after,CancellationToken ct)=>
            Query(http,principal=>service.ListAsync(principal,kind,take??50,after,ct)));
        api.MapGet("/records/{kind}/{code}/revisions/{revision:int}",(HttpContext http,CommercialQualityService service,CommercialRecordKind kind,string code,int revision,CancellationToken ct)=>
            Query(http,principal=>service.ReadAsync(principal,kind,code,revision,ct)));
        api.MapGet("/shipments/{shipment:long}/evidence",(HttpContext http,CommercialQualityService service,long shipment,int? line,CancellationToken ct)=>
            Query(http,principal=>service.ShipmentEvidenceAsync(principal,shipment,line,ct)));
        api.MapPost("/records",(HttpContext http,CommercialQualityService service,CreateCommercialRequest body,CancellationToken ct)=>
            Execute(()=>Command(http,body.Reason,context=>service.CreateAsync(new(body.Kind,body.Code,body.FacilityCode,body.Content),context,ct))));
        api.MapPost("/records/{kind}/{code}/revisions",(HttpContext http,CommercialQualityService service,CommercialRecordKind kind,string code,CommercialContentRequest body,CancellationToken ct)=>
            Execute(()=>Command(http,body.Reason,context=>service.ReviseAsync(new(kind,code,body.ExpectedVersion,body.Content),context,ct))));
        api.MapPost("/records/{kind}/{code}/revisions/{revision:int}/edit",(HttpContext http,CommercialQualityService service,CommercialRecordKind kind,string code,int revision,CommercialContentRequest body,CancellationToken ct)=>
            Execute(()=>Command(http,body.Reason,context=>service.EditAsync(new(kind,code,revision,body.ExpectedVersion,body.Content),context,ct))));
        foreach(var action in new[]{"Submit","Review","Approve","Effective","Withdraw","Close"})
        {
            var captured=action;
            api.MapPost("/records/{kind}/{code}/revisions/{revision:int}/"+action.ToLowerInvariant(),
                (HttpContext http,CommercialQualityService service,CommercialRecordKind kind,string code,int revision,CommercialTransitionRequest body,CancellationToken ct)=>
                    Execute(()=>Command(http,body.Reason,context=>service.TransitionAsync(new(kind,code,revision,body.ExpectedVersion,captured,body.Basis),context,ct))));
        }
        api.MapPost("/records/{kind}/{code}/revisions/{revision:int}/observations",
            (HttpContext http,CommercialQualityService service,CommercialRecordKind kind,string code,int revision,CommercialObservationRequest body,CancellationToken ct)=>
                Execute(()=>Command(http,body.Reason,context=>service.ObserveAsync(new(kind,code,revision,body.ExpectedVersion,body.Observation,body.ObservedAt,body.Basis,body.Evidence,body.RecordedExternalPerson,body.Preservation),context,ct))));
    }
    private static async Task<IResult> Query<T>(HttpContext http,Func<ForgePrincipal,Task<T>> query)
    {
        var principal=await PrincipalAsync(http);if(principal is null)return NotRegistered();
        try{return Results.Ok(await query(principal));}
        catch(ForgeAuthorizationException){return Unavailable();}
        catch(CommandRejectedException rejected) when(rejected.Code is "commercial_not_found" or "commercial_source_not_found"){return Unavailable();}
        catch(CommandRejectedException rejected) when(rejected.Code=="qms_not_configured"){return Configuration(rejected);}
    }
    private static async Task<IResult> Execute(Func<Task<IResult>> command)
    {
        try{return await command();}
        catch(CommandRejectedException rejected) when(rejected.Code=="qms_not_configured"){return Configuration(rejected);}
    }
    private static IResult Unavailable()=>Results.Problem(statusCode:404,title:"Commercial evidence unavailable",extensions:new Dictionary<string,object?>{["code"]="commercial_not_found"});
    private static IResult Configuration(CommandRejectedException rejection)=>Results.Problem(statusCode:503,title:"Organization configuration required",detail:rejection.Message,extensions:new Dictionary<string,object?>{["code"]=rejection.Code});
}

