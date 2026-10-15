using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Planning;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record CreatePlanningRequest(string Code,QmsPlanningKind Kind,string FacilityCode,Guid? ProgramId,DataMarking Markings,QmsPlanningContent Content,string? Reason);
public sealed record PlanningRevisionRequest(int ExpectedVersion,QmsPlanningContent Content,string? Reason);
public sealed record PlanningDecisionRequest(int ExpectedVersion,string Note,string? Reason);
public sealed record PlanningAssessmentRequest(string Code,int DefinitionRevision,QmsAssessmentKind Kind,QmsPlanningAssessmentContent Content,IReadOnlyList<QmsPlanningEvidenceReference> Evidence,string? Reason,Guid? CaseId=null,int? ExpectedCaseVersion=null);
public sealed record PlanningTaskRequest(string Code,int DefinitionRevision,string Title,Guid OwnerPersonId,DateTimeOffset DueAt,string Resources,string CompletionCriteria,string? Reason);
public sealed record PlanningTaskDecisionRequest(int ExpectedVersion,string Result,IReadOnlyList<QmsPlanningEvidenceReference> Evidence,string? Reason);
public sealed record PlanningCaseRequest(string Code,int DefinitionRevision,string? Reason);
public sealed record PlanningCaseDecisionRequest(int ExpectedVersion,Guid? AssessmentId,string Note,string? Reason);
public sealed record RetentionBindingRequest(QmsRevisionReference Schedule,QmsPlanningEvidenceReference Subject,DateTimeOffset? ExternalTriggerAt,string TriggerBasis,IReadOnlyList<QmsPlanningEvidenceReference> Evidence,string? Reason,Guid? PreviousBindingId=null);
public sealed record RetentionHoldRequest(string FacilityCode,Guid? ProgramId,DataMarking Markings,QmsRetentionHoldKind Kind,QmsPlanningEvidenceReference Subject,string Basis,string AuthorityBasis,IReadOnlyList<QmsPlanningEvidenceReference> Evidence,string? Reason);
public sealed record RetentionHoldDecisionRequest(int ExpectedVersion,string Note,IReadOnlyList<QmsPlanningEvidenceReference> Evidence,string? Reason);
public sealed record QmsDispositionRequest(string FacilityCode,Guid? ProgramId,DataMarking Markings,IReadOnlyList<Guid> BindingIds,string Purpose,string? Reason);
public sealed record DispositionDecisionRequest(int ExpectedVersion,string Note,QmsDispositionObservation? ObservationKind,IReadOnlyList<QmsPlanningEvidenceReference>? Evidence,string? Reason);

public static class QmsPlanningEndpoints
{
    public static void MapQmsPlanningEndpoints(this IEndpointRouteBuilder app)
    {
        var api=app.MapGroup("/api/quality/qms/planning").RequireAuthorization();
        api.MapGet("/status",async(HttpContext http,QmsPlanningService service)=>await PrincipalAsync(http) is null?NotRegistered():Results.Ok(service.Status));
        api.MapGet("/metrics/dashboard",(HttpContext http,QmsPlanningService service,DateTimeOffset from,DateTimeOffset to,int? take,string? after,CancellationToken ct)=>
            Read(http,p=>service.MetricsDashboardAsync(p,from,to,take??100,after,ct)));
        api.MapGet("/records",(HttpContext http,QmsPlanningService service,QmsPlanningKind? kind,int? take,string? after,CancellationToken ct)=>Read(http,p=>service.ListAsync(p,kind,take??30,after,ct)));
        api.MapGet("/records/{code}",(HttpContext http,QmsPlanningService service,string code,CancellationToken ct)=>Read(http,p=>service.ReadAsync(p,code,ct)));
        api.MapGet("/records/{code}/revisions/{revision:int}",(HttpContext http,QmsPlanningService service,string code,int revision,CancellationToken ct)=>Read(http,async p=>
        {var record=await service.ReadAsync(p,code,ct);return record.Revisions.FirstOrDefault(x=>x.Revision==revision)??throw new CommandRejectedException("planning_not_found","The exact revision is unavailable.");}));
        api.MapGet("/records/{code}/revisions/{revision:int}/work",(HttpContext http,QmsPlanningWorkService service,string code,int revision,CancellationToken ct)=>Read(http,p=>service.ReadChildrenAsync(p,code,revision,ct)));
        api.MapPost("/records",(HttpContext http,QmsPlanningService service,CreatePlanningRequest c,CancellationToken ct)=>Run(()=>Command(http,c.Reason,context=>service.CreateAsync(new(c.Code,c.Kind,c.FacilityCode,c.ProgramId,c.Markings,c.Content),context,ct))));
        api.MapPost("/records/{code}/revisions",(HttpContext http,QmsPlanningService service,string code,PlanningRevisionRequest c,CancellationToken ct)=>Run(()=>Command(http,c.Reason,context=>service.ReviseAsync(new(code,null,c.ExpectedVersion,c.Content),context,ct))));
        api.MapPost("/records/{code}/revisions/{revision:int}/edit",(HttpContext http,QmsPlanningService service,string code,int revision,PlanningRevisionRequest c,CancellationToken ct)=>Run(()=>Command(http,c.Reason,context=>service.ReviseAsync(new(code,revision,c.ExpectedVersion,c.Content),context,ct))));
        foreach(var action in new[]{"Submit","Review","Approve","Effective","Withdraw"})
        {
            var selected=action;
            api.MapPost("/records/{code}/revisions/{revision:int}/"+action.ToLowerInvariant(),(HttpContext http,QmsPlanningService service,string code,int revision,PlanningDecisionRequest c,CancellationToken ct)=>Run(()=>Command(http,c.Reason,context=>service.TransitionAsync(new(code,revision,c.ExpectedVersion,selected,c.Note),context,ct))));
        }
        api.MapPost("/assessments",(HttpContext http,QmsPlanningWorkService service,PlanningAssessmentRequest c,CancellationToken ct)=>Run(()=>Command(http,c.Reason,context=>service.CreateAssessmentAsync(new(c.Code,c.DefinitionRevision,c.Kind,c.Content,c.Evidence,c.CaseId,c.ExpectedCaseVersion),context,ct))));
        foreach(var action in new[]{"Submit","Review","Approve","Withdraw"})
        {
            var selected=action;
            api.MapPost("/assessments/{id:guid}/"+action.ToLowerInvariant(),(HttpContext http,QmsPlanningWorkService service,Guid id,PlanningDecisionRequest c,CancellationToken ct)=>Run(()=>Command(http,c.Reason,context=>service.DecideAssessmentAsync(new(id,c.ExpectedVersion,selected,c.Note),context,ct))));
        }
        api.MapPost("/tasks",(HttpContext http,QmsPlanningWorkService service,PlanningTaskRequest c,CancellationToken ct)=>Run(()=>Command(http,c.Reason,context=>service.CreateTaskAsync(new(c.Code,c.DefinitionRevision,c.Title,c.OwnerPersonId,c.DueAt,c.Resources,c.CompletionCriteria),context,ct))));
        foreach(var action in new[]{"Complete","Verify","Reopen","Withdraw"})
        {
            var selected=action;
            api.MapPost("/tasks/{id:guid}/"+action.ToLowerInvariant(),(HttpContext http,QmsPlanningWorkService service,Guid id,PlanningTaskDecisionRequest c,CancellationToken ct)=>Run(()=>Command(http,c.Reason,context=>service.DecideTaskAsync(new(id,c.ExpectedVersion,selected,c.Result,c.Evidence),context,ct))));
        }
        api.MapPost("/cases",(HttpContext http,QmsPlanningWorkService service,PlanningCaseRequest c,CancellationToken ct)=>Run(()=>Command(http,c.Reason,context=>service.CreateCaseAsync(new(c.Code,c.DefinitionRevision),context,ct))));
        foreach(var action in new[]{"Assess","Plan","Implement","RequestVerification","Verify","Accept","Close","Reopen","Withdraw"})
        {
            var selected=action;
            api.MapPost("/cases/{id:guid}/"+action.ToLowerInvariant(),(HttpContext http,QmsPlanningWorkService service,Guid id,PlanningCaseDecisionRequest c,CancellationToken ct)=>Run(()=>Command(http,c.Reason,context=>service.DecideCaseAsync(new(id,c.ExpectedVersion,selected,c.AssessmentId,c.Note),context,ct))));
        }
        api.MapGet("/retention/bindings",(HttpContext http,QmsRetentionService service,int? take,string? after,CancellationToken ct)=>Read(http,p=>service.ListAsync(p,"Binding",take??30,after,ct)));
        api.MapGet("/retention/bindings/{id:guid}",(HttpContext http,QmsRetentionService service,Guid id,CancellationToken ct)=>Read(http,p=>service.ReadAsync(p,"Binding",id,ct)));
        api.MapPost("/retention/bindings",(HttpContext http,QmsRetentionService service,RetentionBindingRequest c,CancellationToken ct)=>Run(()=>Command(http,c.Reason,context=>service.ProposeBindingAsync(new(c.Schedule,c.Subject,c.ExternalTriggerAt,c.TriggerBasis,c.Evidence,c.PreviousBindingId),context,ct))));
        foreach(var action in new[]{"Approve","Withdraw"})
        {
            var selected=action;api.MapPost("/retention/bindings/{id:guid}/"+action.ToLowerInvariant(),(HttpContext http,QmsRetentionService service,Guid id,PlanningDecisionRequest c,CancellationToken ct)=>Run(()=>Command(http,c.Reason,context=>service.DecideBindingAsync(new(id,c.ExpectedVersion,selected,c.Note),context,ct))));
        }
        api.MapGet("/retention/holds/{id:guid}",(HttpContext http,QmsRetentionService service,Guid id,CancellationToken ct)=>Read(http,p=>service.ReadAsync(p,"Hold",id,ct)));
        api.MapPost("/retention/holds",(HttpContext http,QmsRetentionService service,RetentionHoldRequest c,CancellationToken ct)=>Run(()=>Command(http,c.Reason,context=>service.PlaceHoldAsync(new(c.FacilityCode,c.ProgramId,c.Markings,c.Kind,c.Subject,c.Basis,c.AuthorityBasis,c.Evidence),context,ct))));
        foreach(var action in new[]{"ProposeRelease","Release"})
        {
            var selected=action;api.MapPost("/retention/holds/{id:guid}/"+action.ToLowerInvariant(),(HttpContext http,QmsRetentionService service,Guid id,RetentionHoldDecisionRequest c,CancellationToken ct)=>Run(()=>Command(http,c.Reason,context=>service.DecideHoldAsync(new(id,c.ExpectedVersion,selected,c.Note,c.Evidence),context,ct))));
        }
        api.MapGet("/retention/dispositions",(HttpContext http,QmsRetentionService service,int? take,string? after,CancellationToken ct)=>Read(http,p=>service.ListAsync(p,"Disposition",take??30,after,ct)));
        api.MapGet("/retention/dispositions/{id:guid}",(HttpContext http,QmsRetentionService service,Guid id,CancellationToken ct)=>Read(http,p=>service.ReadAsync(p,"Disposition",id,ct)));
        api.MapPost("/retention/dispositions",(HttpContext http,QmsRetentionService service,QmsDispositionRequest c,CancellationToken ct)=>Run(()=>Command(http,c.Reason,context=>service.ProposeDispositionAsync(new(c.FacilityCode,c.ProgramId,c.Markings,c.BindingIds,c.Purpose),context,ct))));
        foreach(var action in new[]{"DryRun","Approve","Record","Withdraw"})
        {
            var selected=action;api.MapPost("/retention/dispositions/{id:guid}/"+action.ToLowerInvariant(),(HttpContext http,QmsRetentionService service,Guid id,DispositionDecisionRequest c,CancellationToken ct)=>Run(()=>Command(http,c.Reason,context=>service.DecideDispositionAsync(new(id,c.ExpectedVersion,selected,c.Note,c.ObservationKind,c.Evidence),context,ct))));
        }
    }
    private static async Task<IResult> Read<T>(HttpContext http,Func<ForgePrincipal,Task<T>> read){var principal=await PrincipalAsync(http);return principal is null?NotRegistered():await Run(async()=>Results.Ok(await read(principal)));}
    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try{return await action();}catch(CommandRejectedException e) when(e.Code=="qms_not_configured")
        {return Results.Problem(statusCode:503,title:"QMS organization required",detail:e.Message,extensions:new Dictionary<string,object?>{{"code",e.Code}});}
    }
}
