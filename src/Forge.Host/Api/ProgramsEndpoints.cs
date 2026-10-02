using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance;
using Forge.Maintenance.Contracts;
using Forge.Planning;
using Forge.Programs;
using Forge.Programs.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ProgramRequest(string Code, string Name, string? Customer, string? Description, string? Reason);

public sealed record ProgramUpdateRequest(string Name, string? Customer, string? Description, string? Reason);

public sealed record ContractRequest(string Number, string Customer, string Title, DateOnly PeriodStart, DateOnly PeriodEnd, decimal? Ceiling, string? Reason);

public sealed record ContractModRequest(string Title, DateOnly PeriodStart, DateOnly PeriodEnd, decimal? Ceiling, string? Reason);

public sealed record ContractStatusRequest(ContractStatus Status, string? Reason);

public sealed record ClinRequest(string Clin, string? ParentClin, string Description, string? PartNumber, decimal? Quantity, decimal? UnitPrice, string? Reason);

public sealed record DeliveryRequest(string Clin, decimal Quantity, DateOnly DueDate, string? Reason);

public sealed record DeliveryChangeRequest(decimal Quantity, DateOnly DueDate, string? Reason);

public sealed record RequirementRequest(string Code, string Text, string? Reason);

public sealed record MilestoneRequest(string Title, DateOnly DueDate, string? Reason);

public sealed record MilestoneDoneRequest(DateOnly CompletedOn, string? Reason);

public sealed record PlanDeliveryRequest(int? Priority, string? Reason);

public sealed record PmPlanRequest(string MachineCode, string Title, int IntervalDays, DateOnly? FirstDue, int DurationMinutes, string[] Tasks, bool LockOutWhenOverdue, int GraceDays, string? Reason);

public sealed record PmPlanChangeRequest(string Title, int IntervalDays, int DurationMinutes, string[] Tasks, bool LockOutWhenOverdue, int GraceDays, string? Reason);

public sealed record WorkOrderRequest(string MachineCode, string Title, string? Description, DateOnly DueDate, string? Reason);

public sealed record WorkOrderWindowRequest(DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Reason);

public sealed record WorkOrderCompleteRequest(string Findings, bool[] TasksDone, string? Reason);

/// <summary>A program's code and name only: what a security administrator needs to grant access.</summary>
public sealed record ProgramDirectoryEntry(Guid ProgramId, string Code, string Name, ProgramStatus Status);

/// <summary>Phase 7b endpoints: programs and contracts, Program Horizon, preventive maintenance and work orders.</summary>
public static class ProgramsEndpoints
{
    public static void MapProgramsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/programs").RequireAuthorization();

        // Codes and names of every program, for granting access. No contract or delivery data.
        api.MapGet("/directory", (HttpContext http, CancellationToken _) =>
            ReadStore<IProgramsLookup>(http, Permissions.ProgramsDirectory, async (l, ct) =>
                (await l.ListProgramsAsync(ct)).Select(p => new ProgramDirectoryEntry(p.ProgramId, p.Code, p.Name, p.Status)).ToList()));

        // The programs the reader has access to, each with its horizon.
        api.MapGet("/", async (HttpContext http, ProgramHorizonService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.ListAsync(principal, ct));
        });
        api.MapGet("/{programId:guid}", async (HttpContext http, Guid programId, ProgramHorizonService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.GetAsync(principal, programId, ct));
        });
        api.MapGet("/{programId:guid}/contracts", (HttpContext http, Guid programId, CancellationToken _) =>
            Read(http, Permissions.ProgramsRead, async principal =>
            {
                if (!principal.Programs.Contains(new ProgramId(programId)))
                {
                    throw new CommandRejectedException("program_not_found", $"Program {programId} does not exist.");
                }

                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                return await uow.Store<IProgramsLookup>().ListContractsAsync(programId, http.RequestAborted);
            }));

        api.MapPost("/", (HttpContext http, ProgramRequest b, ProgramsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateProgramAsync(new CreateProgram(b.Code, b.Name, b.Customer, b.Description), c, ct)));
        api.MapPost("/{programId:guid}", (HttpContext http, Guid programId, ProgramUpdateRequest b, ProgramsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.UpdateProgramAsync(new UpdateProgram(programId, b.Name, b.Customer, b.Description), c, ct)));
        api.MapPost("/{programId:guid}/close", (HttpContext http, Guid programId, ReasonRequest b, ProgramsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetProgramStatusAsync(new SetProgramStatus(programId, ProgramStatus.Closed), c, ct)));
        api.MapPost("/{programId:guid}/reopen", (HttpContext http, Guid programId, ReasonRequest b, ProgramsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetProgramStatusAsync(new SetProgramStatus(programId, ProgramStatus.Active), c, ct)));
        api.MapPost("/{programId:guid}/contracts", (HttpContext http, Guid programId, ContractRequest b, ProgramsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddContractAsync(new AddContract(programId, b.Number, b.Customer, b.Title, b.PeriodStart, b.PeriodEnd, b.Ceiling), c, ct)));
        api.MapPost("/{programId:guid}/milestones", (HttpContext http, Guid programId, MilestoneRequest b, ProgramsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddMilestoneAsync(new AddMilestone(programId, b.Title, b.DueDate), c, ct)));
        api.MapPost("/milestones/{id:long}/complete", (HttpContext http, long id, MilestoneDoneRequest b, ProgramsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CompleteMilestoneAsync(new CompleteMilestone(id, b.CompletedOn), c, ct)));
        api.MapPost("/contracts/{id:long}/modify", (HttpContext http, long id, ContractModRequest b, ProgramsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ModifyContractAsync(new ModifyContract(id, b.Title, b.PeriodStart, b.PeriodEnd, b.Ceiling), c, ct)));
        api.MapPost("/contracts/{id:long}/status", (HttpContext http, long id, ContractStatusRequest b, ProgramsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetContractStatusAsync(new SetContractStatus(id, b.Status), c, ct)));
        api.MapPost("/contracts/{id:long}/clins", (HttpContext http, long id, ClinRequest b, ProgramsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddClinAsync(new AddClin(id, b.Clin, b.ParentClin, b.Description, b.PartNumber, b.Quantity, b.UnitPrice), c, ct)));
        api.MapPost("/contracts/{id:long}/deliveries", (HttpContext http, long id, DeliveryRequest b, ProgramsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ScheduleDeliveryAsync(new ScheduleDelivery(id, b.Clin, b.Quantity, b.DueDate), c, ct)));
        api.MapPost("/contracts/{id:long}/requirements", (HttpContext http, long id, RequirementRequest b, ProgramsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddRequirementAsync(new AddRequirement(id, b.Code, b.Text), c, ct)));
        api.MapPost("/deliveries/{id:long}/change", (HttpContext http, long id, DeliveryChangeRequest b, ProgramsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ChangeDeliveryAsync(new ChangeDelivery(id, b.Quantity, b.DueDate), c, ct)));
        api.MapPost("/deliveries/{id:long}/cancel", (HttpContext http, long id, ReasonRequest b, ProgramsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelDeliveryAsync(new CancelDelivery(id), c, ct)));

        // A planner turns a contract delivery into demand (or brings that demand back in step).
        app.MapPost("/api/planning/demand/from-delivery/{id:long}", (HttpContext http, long id, PlanDeliveryRequest b, DemandService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.PlanDeliveryAsync(new PlanDelivery(id, b.Priority), c, ct)))
            .RequireAuthorization();

        var maintenance = app.MapGroup("/api/maintenance").RequireAuthorization();
        maintenance.MapGet("/plans", (HttpContext http, CancellationToken _) =>
            ReadStore<IMaintenanceLookup>(http, Permissions.MaintenanceRead, async (l, ct) => await l.ListPlansAsync(ct)));
        maintenance.MapGet("/work-orders", (HttpContext http, bool? includeClosed, CancellationToken _) =>
            ReadStore<IMaintenanceLookup>(http, Permissions.MaintenanceRead, async (l, ct) => await l.ListWorkOrdersAsync(includeClosed ?? false, ct)));
        maintenance.MapPost("/plans", (HttpContext http, PmPlanRequest b, MaintenanceService s, TimeProvider time, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DefinePmPlanAsync(new DefinePmPlan(b.MachineCode, b.Title, b.IntervalDays, b.FirstDue ?? DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime), b.DurationMinutes, b.Tasks ?? [], b.LockOutWhenOverdue, b.GraceDays), c, ct)));
        maintenance.MapPost("/plans/{id:long}", (HttpContext http, long id, PmPlanChangeRequest b, MaintenanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ChangePmPlanAsync(new ChangePmPlan(id, b.Title, b.IntervalDays, b.DurationMinutes, b.Tasks ?? [], b.LockOutWhenOverdue, b.GraceDays), c, ct)));
        maintenance.MapPost("/plans/{id:long}/retire", (HttpContext http, long id, ReasonRequest b, MaintenanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RetirePmPlanAsync(new RetirePmPlan(id), c, ct)));
        maintenance.MapPost("/work-orders", (HttpContext http, WorkOrderRequest b, MaintenanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RaiseWorkOrderAsync(new RaiseWorkOrder(b.MachineCode, b.Title, b.Description, b.DueDate), c, ct)));
        maintenance.MapPost("/work-orders/{id:long}/schedule", (HttpContext http, long id, WorkOrderWindowRequest b, MaintenanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ScheduleWorkOrderAsync(new ScheduleWorkOrder(id, b.StartsAt, b.EndsAt), c, ct)));
        maintenance.MapPost("/work-orders/{id:long}/start", (HttpContext http, long id, ReasonRequest b, MaintenanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.StartWorkOrderAsync(new StartWorkOrder(id), c, ct)));
        maintenance.MapPost("/work-orders/{id:long}/complete", (HttpContext http, long id, WorkOrderCompleteRequest b, MaintenanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CompleteWorkOrderAsync(new CompleteWorkOrder(id, b.Findings, b.TasksDone ?? []), c, ct)));
        maintenance.MapPost("/work-orders/{id:long}/sign-off", (HttpContext http, long id, ReasonRequest b, MaintenanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SignOffWorkOrderAsync(new SignOffWorkOrder(id, true), c, ct)));
        maintenance.MapPost("/work-orders/{id:long}/send-back", (HttpContext http, long id, ReasonRequest b, MaintenanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SignOffWorkOrderAsync(new SignOffWorkOrder(id, false), c, ct)));
        maintenance.MapPost("/work-orders/{id:long}/cancel", (HttpContext http, long id, ReasonRequest b, MaintenanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelWorkOrderAsync(new CancelWorkOrder(id), c, ct)));
    }
}
