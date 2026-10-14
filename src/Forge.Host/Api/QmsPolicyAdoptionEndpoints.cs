using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record CreateQmsPolicyAdoptionRequest(string Code,string FacilityCode,Guid? ProgramId,DataMarking Markings,QmsPolicyAdoptionContent Content,string? Reason);
public sealed record QmsPolicyAdoptionContentRequest(int ExpectedVersion,QmsPolicyAdoptionContent Content,string? Reason);

public static class QmsPolicyAdoptionEndpoints
{
    public static void MapQmsPolicyAdoptionEndpoints(this IEndpointRouteBuilder app)
    {
        var api=app.MapGroup("/api/quality/qms").RequireAuthorization();
        api.MapGet("/adoptions",(HttpContext http,QmsPolicyAdoptionService service,CancellationToken ct)=>Query(http,p=>service.ListAsync(p,ct)));
        api.MapGet("/adoptions/{code}",(HttpContext http,QmsPolicyAdoptionService service,string code,CancellationToken ct)=>Query(http,p=>service.ReadAsync(p,code,null,ct)));
        api.MapGet("/adoptions/{code}/revisions/{revision:int}",(HttpContext http,QmsPolicyAdoptionService service,string code,int revision,CancellationToken ct)=>
            Query(http,async p=>(await service.ReadAsync(p,code,revision,ct)).Revisions.Single()));
        api.MapPost("/adoptions",(HttpContext http,QmsPolicyAdoptionService service,CreateQmsPolicyAdoptionRequest body,CancellationToken ct)=>
            Execute(()=>Command(http,body.Reason,p=>service.CreateAsync(new(body.Code,body.FacilityCode,body.ProgramId,body.Markings,body.Content),p,ct))));
        api.MapPost("/adoptions/{code}/revisions",(HttpContext http,QmsPolicyAdoptionService service,string code,QmsPolicyAdoptionContentRequest body,CancellationToken ct)=>
            Execute(()=>Command(http,body.Reason,p=>service.ReviseAsync(new(code,body.ExpectedVersion,body.Content),p,ct))));
        api.MapPost("/adoptions/{code}/revisions/{revision:int}/edit",(HttpContext http,QmsPolicyAdoptionService service,string code,int revision,QmsPolicyAdoptionContentRequest body,CancellationToken ct)=>
            Execute(()=>Command(http,body.Reason,p=>service.EditAsync(new(code,revision,body.ExpectedVersion,body.Content),p,ct))));
        foreach(var action in new[]{"Submit","Review","Approve","Effective","Withdraw"})
        {
            var captured=action;
            api.MapPost("/adoptions/{code}/revisions/{revision:int}/"+action.ToLowerInvariant(),
                (HttpContext http,QmsPolicyAdoptionService service,string code,int revision,QmsTransitionRequest body,CancellationToken ct)=>
                    Execute(()=>Command(http,body.Reason,p=>service.TransitionAsync(new(code,revision,body.ExpectedVersion,captured,body.Note),p,ct))));
        }
        // A governance preview gives no business authorization. Consequential callers supply facts resolved by source owners.
        api.MapGet("/policy-current/{purpose}",async(HttpContext http,IQmsPolicyReader reader,QmsPolicyPurpose purpose,string facilityCode,Guid? programId,
            string? workCenterCode,string? activityCode,string? machineCode,int? operationSequence,string? partNumber,string? partRevision,CancellationToken ct)=>
        {
            var principal=await PrincipalAsync(http); if(principal is null)return NotRegistered();
            await using var uow=await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginAsync(ct);
            http.Response.Headers["X-Qms-Governance-Preview"]="true";
            return await Execute(async()=>Results.Ok(await reader.RequireAsync(uow,principal,purpose,
                new(facilityCode,programId,workCenterCode,activityCode,machineCode,operationSequence,partNumber,partRevision),
                http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow(),ct)));
        });
    }
    private static async Task<IResult> Query<T>(HttpContext http,Func<ForgePrincipal,Task<T>> query)
    {
        var principal=await PrincipalAsync(http); return principal is null?NotRegistered():await Execute(async()=>Results.Ok(await query(principal)));
    }
    private static async Task<IResult> Execute(Func<Task<IResult>> command)
    {
        try{return await command();}
        catch(CommandRejectedException rejected)when(rejected.Code=="qms_not_configured")
        {
            return Results.Problem(title:"QMS organization configuration required",detail:rejected.Message,statusCode:StatusCodes.Status503ServiceUnavailable,
                extensions:new Dictionary<string,object?>{["code"]=rejected.Code});
        }
    }
}
