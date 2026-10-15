using Forge.Inventory;
using Forge.Kernel.Access;
using Forge.Quality.Acceptance;
using Forge.Quality.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record DefineAcceptanceCriteriaRequest(string Code,string FacilityCode,Guid? ProgramId,Forge.Kernel.Security.DataMarking Markings,AcceptanceCriteriaContent Content,int? ExpectedVersion,string? Reason);
public sealed record EditAcceptanceCriteriaRequest(int ExpectedVersion,AcceptanceCriteriaContent Content,string? Reason);
public sealed record AcceptanceTransitionRequest(int ExpectedVersion,string Note,string? Reason);
public sealed record AcceptanceInspectionRequest(AcceptanceSubjectKey Subject,string CriteriaCode,int CriteriaRevision,int ExpectedCriteriaVersion,decimal Quantity,decimal SampleQuantity,AcceptanceFinding[] Findings,string Evidence,string? QuarantineLocation,string? Reason);
public sealed record ProductReleaseRequest(AcceptanceSubjectKey Subject,decimal Quantity,long[] InspectionIds,int ExpectedInspectionVersion,string Basis,string? Reason);
public sealed record WithdrawProductReleaseRequest(int ExpectedVersion,string Basis,string? Reason);
public sealed record ConsumeProductReleaseRequest(int ExpectedVersion,decimal Quantity,string? Reason);

public static class ProductAcceptanceEndpoints
{
 public static void MapProductAcceptanceEndpoints(this IEndpointRouteBuilder app)
 {
  var api=app.MapGroup("/api/quality/acceptance").RequireAuthorization();
  api.MapGet("/criteria",async(HttpContext http,AcceptanceCriteriaService service,CancellationToken ct)=>{var p=await PrincipalAsync(http);return p is null?NotRegistered():Results.Ok(await service.ListAsync(p,ct));});
  api.MapGet("/criteria/{code}/revisions/{revision:int}",async(HttpContext http,AcceptanceCriteriaService service,string code,int revision,CancellationToken ct)=>{var p=await PrincipalAsync(http);return p is null?NotRegistered():Results.Ok(await service.ReadAsync(p,code,revision,ct));});
  api.MapPost("/criteria",(HttpContext http,AcceptanceCriteriaService service,DefineAcceptanceCriteriaRequest body,CancellationToken ct)=>Command(http,body.Reason,p=>service.DefineAsync(new(body.Code,body.FacilityCode,body.ProgramId,body.Markings,body.Content,body.ExpectedVersion),p,ct)));
  api.MapPost("/criteria/{code}/revisions/{revision:int}/edit",(HttpContext http,AcceptanceCriteriaService service,string code,int revision,EditAcceptanceCriteriaRequest body,CancellationToken ct)=>Command(http,body.Reason,p=>service.EditAsync(new(code,revision,body.ExpectedVersion,body.Content),p,ct)));
  foreach(var action in new[]{"Submit","Approve","Effective","Withdraw"})
  {var selected=action;api.MapPost("/criteria/{code}/revisions/{revision:int}/"+action.ToLowerInvariant(),(HttpContext http,AcceptanceCriteriaService service,string code,int revision,AcceptanceTransitionRequest body,CancellationToken ct)=>Command(http,body.Reason,p=>service.TransitionAsync(new(code,revision,body.ExpectedVersion,selected,body.Note),p,ct)));}
  api.MapPost("/inspections",(HttpContext http,ProductAcceptanceService service,AcceptanceInspectionRequest body,CancellationToken ct)=>Command(http,body.Reason,p=>service.InspectAsync(new(body.Subject,body.CriteriaCode,body.CriteriaRevision,body.ExpectedCriteriaVersion,body.Quantity,body.SampleQuantity,body.Findings,body.Evidence,body.QuarantineLocation),p,ct)));
  api.MapGet("/inspections/{id:long}",async(HttpContext http,ProductAcceptanceService service,long id,CancellationToken ct)=>{var p=await PrincipalAsync(http);return p is null?NotRegistered():Results.Ok(await service.ReadAsync<AcceptanceInspection>(p,id,false,ct));});
  api.MapPost("/product-releases",(HttpContext http,ProductAcceptanceService service,ProductReleaseRequest body,CancellationToken ct)=>Command(http,body.Reason,p=>service.ReleaseAsync(new(body.Subject,body.Quantity,body.InspectionIds,body.ExpectedInspectionVersion,body.Basis),p,ct)));
  api.MapGet("/product-releases/{id:long}",async(HttpContext http,ProductAcceptanceService service,long id,CancellationToken ct)=>{var p=await PrincipalAsync(http);return p is null?NotRegistered():Results.Ok(await service.ReadAsync<ProductReleaseDecision>(p,id,true,ct));});
  api.MapPost("/product-releases/{id:long}/withdraw",(HttpContext http,ProductAcceptanceService service,long id,WithdrawProductReleaseRequest body,CancellationToken ct)=>Command(http,body.Reason,p=>service.WithdrawAsync(new(id,body.ExpectedVersion,body.Basis),p,ct)));
  app.MapPost("/api/inventory/product-releases/{id:long}/consume",(HttpContext http,ProductReleaseService service,long id,ConsumeProductReleaseRequest body,CancellationToken ct)=>Command(http,body.Reason,p=>service.ConsumeAsync(new(id,body.ExpectedVersion,body.Quantity),p,ct))).RequireAuthorization();
 }
}
