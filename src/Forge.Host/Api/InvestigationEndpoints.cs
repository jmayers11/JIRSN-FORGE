using Forge.Quality.Contracts;
using Forge.Quality.Investigations;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record MeasurementFindingRequest(MeasurementFindingContent Content,string? Reason);
public sealed record EditMeasurementFindingRequest(int ExpectedVersion,MeasurementFindingContent Content,string? Reason);
public sealed record InvestigationVersionRequest(int ExpectedVersion,string? Reason);
public sealed record TraceInvestigationRequest(TraceInvestigationRoot Root,string Title,Guid OwnerPersonId,string TriggerEvidence,string? Reason);
public sealed record TraceAssessmentRequest(int ExpectedVersion,int SnapshotNumber,string CandidateKey,TraceImpactDecision Decision,string Basis,string Evidence,string PlannedDisposition,string? Reason);
public sealed record TraceHoldLinkRequest(int ExpectedVersion,long AssessmentId,int ExpectedAssessmentVersion,long HoldId,int ExpectedHoldVersion,string Basis,string? Reason);
public sealed record CloseTraceInvestigationRequest(int ExpectedVersion,bool AcknowledgedGaps,string Basis,string? Reason);

public static class InvestigationEndpoints
{
 public static void MapInvestigationEndpoints(this IEndpointRouteBuilder app)
 {
  var api=app.MapGroup("/api/quality/investigations").RequireAuthorization();
  api.MapGet("/measurement-findings",async(HttpContext http,InvestigationReadService service,CancellationToken ct)=>{var principal=await PrincipalAsync(http);return principal is null?NotRegistered():Results.Ok(await service.FindingsAsync(principal,ct));});
  api.MapGet("/measurement-findings/{id:long}",async(HttpContext http,InvestigationReadService service,long id,CancellationToken ct)=>{var principal=await PrincipalAsync(http);return principal is null?NotRegistered():Results.Ok(await service.FindingAsync(principal,id,ct));});
  api.MapPost("/measurement-findings",(HttpContext http,InvestigationService service,MeasurementFindingRequest body,CancellationToken ct)=>Command(http,body.Reason,context=>service.CreateFindingAsync(new(body.Content),context,ct)));
  api.MapPost("/measurement-findings/{id:long}/edit",(HttpContext http,InvestigationService service,long id,EditMeasurementFindingRequest body,CancellationToken ct)=>Command(http,body.Reason,context=>service.EditFindingAsync(new(id,body.ExpectedVersion,body.Content),context,ct)));
  foreach(var action in new[]{"Submit","Approve","Withdraw"}){var selected=action;api.MapPost("/measurement-findings/{id:long}/"+selected.ToLowerInvariant(),(HttpContext http,InvestigationService service,long id,InvestigationVersionRequest body,CancellationToken ct)=>Command(http,body.Reason,context=>service.TransitionFindingAsync(new(id,body.ExpectedVersion,selected),context,ct)));}
  api.MapGet("/trace",async(HttpContext http,InvestigationReadService service,CancellationToken ct)=>{var principal=await PrincipalAsync(http);return principal is null?NotRegistered():Results.Ok(await service.CasesAsync(principal,ct));});
  api.MapGet("/trace/{id:long}",async(HttpContext http,InvestigationReadService service,long id,int? snapshot,CancellationToken ct)=>{var principal=await PrincipalAsync(http);return principal is null?NotRegistered():Results.Ok(await service.ReadAsync(principal,id,snapshot,ct));});
  api.MapPost("/trace",(HttpContext http,InvestigationService service,TraceInvestigationRequest body,CancellationToken ct)=>Command(http,body.Reason,context=>service.OpenAsync(new(body.Root,body.Title,body.OwnerPersonId,body.TriggerEvidence),context,ct)));
  api.MapPost("/trace/{id:long}/refresh",(HttpContext http,InvestigationService service,long id,InvestigationVersionRequest body,CancellationToken ct)=>Command(http,body.Reason,context=>service.RefreshAsync(new(id,body.ExpectedVersion),context,ct)));
  api.MapPost("/trace/{id:long}/assessments",(HttpContext http,InvestigationService service,long id,TraceAssessmentRequest body,CancellationToken ct)=>Command(http,body.Reason,context=>service.ProposeAsync(new(id,body.ExpectedVersion,body.SnapshotNumber,body.CandidateKey,body.Decision,body.Basis,body.Evidence,body.PlannedDisposition),context,ct)));
  foreach(var action in new[]{"Approve","Withdraw"}){var selected=action;api.MapPost("/assessments/{id:long}/"+selected.ToLowerInvariant(),(HttpContext http,InvestigationService service,long id,InvestigationVersionRequest body,CancellationToken ct)=>Command(http,body.Reason,context=>service.TransitionAssessmentAsync(new(id,body.ExpectedVersion,selected),context,ct)));}
  api.MapPost("/trace/{id:long}/hold-links",(HttpContext http,InvestigationService service,long id,TraceHoldLinkRequest body,CancellationToken ct)=>Command(http,body.Reason,context=>service.LinkHoldAsync(new(id,body.ExpectedVersion,body.AssessmentId,body.ExpectedAssessmentVersion,body.HoldId,body.ExpectedHoldVersion,body.Basis),context,ct)));
  api.MapPost("/trace/{id:long}/close",(HttpContext http,InvestigationService service,long id,CloseTraceInvestigationRequest body,CancellationToken ct)=>Command(http,body.Reason,context=>service.CloseAsync(new(id,body.ExpectedVersion,body.AcknowledgedGaps,body.Basis),context,ct)));
 }
}
