using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Quality.Audit;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

/// <summary>Typed human command data plus an attributed reason. Route owner IDs replace submitted IDs.</summary>
public sealed record AuditCapaRequest<T>(T Data,string? Reason);
public static class AuditCapaEndpoints
{
    public static void MapAuditCapaEndpoints(this IEndpointRouteBuilder app)
    {
        var audits=app.MapGroup("/api/quality/audits").RequireAuthorization();
        audits.MapGet("/status",async(HttpContext h,QmsAuditService s)=>await PrincipalAsync(h)is null?NotRegistered():Results.Ok(s.Status));
        audits.MapGet("/programs",(HttpContext h,QmsAuditService s,int? take,string? after,CancellationToken ct)=>ReadOwner(h,p=>s.ListAsync(p,"Program",take??30,after,ct)));
        audits.MapGet("/programs/{code}",(HttpContext h,QmsAuditService s,string code,CancellationToken ct)=>ReadOwner(h,p=>s.ReadAsync(p,"Program",code,ct)));
        audits.MapPost("/programs",(HttpContext h,QmsAuditService s,AuditCapaRequest<DefineAuditProgram> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.DefineAsync(r.Data,c,ct))));
        audits.MapPost("/programs/{code}/revisions",(HttpContext h,QmsAuditService s,string code,AuditCapaRequest<ReviseAuditProgram> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.ReviseAsync(r.Data with{Code=code,Revision=null},c,ct))));
        audits.MapPost("/programs/{code}/revisions/{revision:int}/edit",(HttpContext h,QmsAuditService s,string code,int revision,AuditCapaRequest<ReviseAuditProgram> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.ReviseAsync(r.Data with{Code=code,Revision=revision},c,ct))));
        foreach(var action in new[]{"Submit","Review","Approve","Effective","Withdraw"})
        {
            var selected=action;
            audits.MapPost("/programs/{code}/revisions/{revision:int}/"+action.ToLowerInvariant(),(HttpContext h,QmsAuditService s,string code,int revision,AuditCapaRequest<AuditProgramDecision> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.DecideProgramAsync(r.Data with{Code=code,Revision=revision,Action=selected},c,ct))));
        }
        audits.MapPost("/competence",(HttpContext h,QmsAuditService s,AuditCapaRequest<AssessAuditor> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.AssessAsync(r.Data,c,ct))));
        audits.MapGet("/competence/{id:guid}",(HttpContext h,QmsAuditService s,Guid id,CancellationToken ct)=>ReadOwner(h,p=>s.ReadAsync(p,"Competence",id.ToString(),ct)));
        audits.MapPost("/competence/{id:guid}/approve",(HttpContext h,QmsAuditService s,Guid id,AuditCapaRequest<ApproveAuditorAssessment> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.ApproveCompetenceAsync(r.Data with{Id=id},c,ct))));
        audits.MapGet("/runs",(HttpContext h,QmsAuditService s,int? take,string? after,CancellationToken ct)=>ReadOwner(h,p=>s.ListAsync(p,"Audit",take??30,after,ct)));
        audits.MapGet("/runs/{id:guid}",(HttpContext h,QmsAuditService s,Guid id,CancellationToken ct)=>ReadOwner(h,p=>s.ReadAsync(p,"Audit",id.ToString(),ct)));
        audits.MapPost("/runs",(HttpContext h,QmsAuditService s,AuditCapaRequest<ScheduleAudit> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.ScheduleAsync(r.Data,c,ct))));
        foreach(var action in new[]{"Start","SubmitReport","ApproveReport","FollowUp","Close","Reopen","Cancel"})
        {
            var selected=action;
            audits.MapPost("/runs/{id:guid}/"+action.ToLowerInvariant(),(HttpContext h,QmsAuditService s,Guid id,AuditCapaRequest<DecideAudit> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.DecideAsync(r.Data with{Id=id,Action=selected},c,ct))));
        }
        audits.MapPost("/runs/{id:guid}/findings",(HttpContext h,QmsAuditService s,Guid id,AuditCapaRequest<RecordAuditFinding> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.FindingAsync(r.Data with{AuditId=id},c,ct))));
        audits.MapPost("/runs/{id:guid}/report",(HttpContext h,QmsAuditService s,Guid id,AuditCapaRequest<WriteAuditReport> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.ReportAsync(r.Data with{Id=id},c,ct))));
        foreach(var action in new[]{"Record","Verify"})
        {
            var selected=action;
            audits.MapPost("/findings/{id:guid}/"+action.ToLowerInvariant(),(HttpContext h,QmsAuditService s,Guid id,AuditCapaRequest<FollowUpAuditFinding> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.FollowUpAsync(r.Data with{Id=id,Action=selected},c,ct))));
        }
        var capas=app.MapGroup("/api/quality/capa-governance").RequireAuthorization();
        capas.MapGet("",(HttpContext h,QmsCapaService s,int? take,string? after,CancellationToken ct)=>ReadOwner(h,p=>s.ListAsync(p,take??30,after,ct)));
        capas.MapGet("/{id:long}",(HttpContext h,QmsCapaService s,long id,CancellationToken ct)=>ReadOwner(h,p=>s.ReadAsync(p,id,ct)));
        capas.MapPost("",(HttpContext h,QmsCapaService s,AuditCapaRequest<CreateGovernedCapa> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.CreateAsync(r.Data,c,ct))));
        capas.MapPost("/{id:long}/investigations",(HttpContext h,QmsCapaService s,long id,AuditCapaRequest<WriteCapaInvestigation> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.InvestigateAsync(r.Data with{CapaId=id},c,ct))));
        capas.MapPost("/{id:long}/investigations/{revision:int}/edit",(HttpContext h,QmsCapaService s,long id,int revision,AuditCapaRequest<WriteCapaInvestigation> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.InvestigateAsync(r.Data with{CapaId=id,Revision=revision},c,ct))));
        foreach(var action in new[]{"Submit","Review","Approve","Withdraw"})
        {
            var selected=action;
            capas.MapPost("/{id:long}/investigations/{revision:int}/"+action.ToLowerInvariant(),(HttpContext h,QmsCapaService s,long id,int revision,AuditCapaRequest<DecideCapaInvestigation> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.DecideInvestigationAsync(r.Data with{CapaId=id,Revision=revision,Action=selected},c,ct))));
            capas.MapPost("/effectiveness/{id:guid}/"+action.ToLowerInvariant(),(HttpContext h,QmsCapaService s,Guid id,AuditCapaRequest<DecideCapaEffectiveness> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.DecideEffectivenessAsync(r.Data with{Id=id,Action=selected},c,ct))));
        }
        capas.MapPost("/{id:long}/actions",(HttpContext h,QmsCapaService s,long id,AuditCapaRequest<CreateCapaAction> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.CreateActionAsync(r.Data with{CapaId=id},c,ct))));
        foreach(var action in new[]{"Complete","Verify","Reopen","Withdraw"})
        {
            var selected=action;
            capas.MapPost("/actions/{id:guid}/"+action.ToLowerInvariant(),(HttpContext h,QmsCapaService s,Guid id,AuditCapaRequest<DecideCapaAction> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.DecideActionAsync(r.Data with{Id=id,Action=selected},c,ct))));
        }
        capas.MapPost("/{id:long}/effectiveness",(HttpContext h,QmsCapaService s,long id,AuditCapaRequest<RecordCapaEffectiveness> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.EffectivenessAsync(r.Data with{CapaId=id},c,ct))));
        foreach(var action in new[]{"Implemented","Close","Reopen","Withdraw"})
        {
            var selected=action;
            capas.MapPost("/{id:long}/"+action.ToLowerInvariant(),(HttpContext h,QmsCapaService s,long id,AuditCapaRequest<DecideGovernedCapa> r,CancellationToken ct)=>Run(()=>Command(h,r.Reason,c=>s.DecideAsync(r.Data with{CapaId=id,Action=selected},c,ct))));
        }
    }
    private static async Task<IResult> ReadOwner<T>(HttpContext h,Func<ForgePrincipal,Task<T>> read){var p=await PrincipalAsync(h);return p is null?NotRegistered():await Run(async()=>Results.Ok(await read(p)));}
    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try{return await action();}catch(CommandRejectedException e)when(e.Code=="qms_not_configured"){return Results.Problem(statusCode:503,title:"QMS organization required",detail:e.Message,extensions:new Dictionary<string,object?>{{"code",e.Code}});}
    }
}
