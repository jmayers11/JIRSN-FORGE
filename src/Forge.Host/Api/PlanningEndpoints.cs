using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance;
using Forge.Maintenance.Contracts;
using Forge.Planning;
using Forge.Planning.Engines;
using Forge.Resources;
using Forge.Workforce;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ShiftRequest(string Code, string SiteCode, string Name, TimeOnly Start, int DurationMinutes, int[] Days, string? Reason);

public sealed record WorkCenterShiftRequest(string ShiftCode, string? Reason);

public sealed record ClosureRequest(DateOnly Date, string? Note, string? Reason);

public sealed record WorkerShiftRequest(string? ShiftCode, string? Reason);

public sealed record DowntimeRequest(string Description, string? Reason);

public sealed record MaintenanceWindowRequest(DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Description, string? Reason);

public sealed record ProposeScheduleRequest(int? HorizonDays, string? Reason);

public sealed record OrderPriorityPreviewRequest(int Priority);

public sealed record ResolveRequest(string Resolution, string? Reason);

/// <summary>Phase 4 endpoints: calendars, downtime, capacity, scheduling, constraints and drop-dead dates.</summary>
public static class PlanningEndpoints
{
    public static void MapPlanningEndpoints(this IEndpointRouteBuilder app)
    {
        var resources = app.MapGroup("/api/resources").RequireAuthorization();
        resources.MapPost("/shifts", (HttpContext http, ShiftRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DefineShiftAsync(new DefineShift(b.Code, b.SiteCode, b.Name, b.Start, b.DurationMinutes, b.Days), c, ct)));
        resources.MapPost("/work-centers/{workCenter}/shifts", (HttpContext http, string workCenter, WorkCenterShiftRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AssignWorkCenterShiftAsync(new AssignWorkCenterShift(workCenter, b.ShiftCode), c, ct)));
        resources.MapPost("/sites/{site}/closures", (HttpContext http, string site, ClosureRequest b, ResourcesService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddSiteClosureAsync(new AddSiteClosure(site, b.Date, b.Note), c, ct)));

        app.MapPost("/api/workforce/workers/{personId:guid}/shift", (HttpContext http, Guid personId, WorkerShiftRequest b, WorkforceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetWorkerShiftAsync(new SetWorkerShift(new PersonId(personId), b.ShiftCode), c, ct))).RequireAuthorization();

        var maintenance = app.MapGroup("/api/maintenance").RequireAuthorization();
        maintenance.MapGet("/downtime", (HttpContext http, DateTimeOffset? from, DateTimeOffset? to, CancellationToken _) =>
            ReadStore<IMaintenanceLookup>(http, Permissions.MaintenanceRead, async (l, ct) =>
            {
                var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                return await l.ListDowntimeAsync(from ?? now.AddDays(-7), to ?? now.AddDays(14), ct);
            }));
        maintenance.MapPost("/machines/{machine}/down", (HttpContext http, string machine, DowntimeRequest b, MaintenanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReportDownAsync(new ReportMachineDown(machine, b.Description), c, ct)));
        maintenance.MapPost("/machines/{machine}/restore", (HttpContext http, string machine, ReasonRequest b, MaintenanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RestoreAsync(new RestoreMachine(machine), c, ct)));
        maintenance.MapPost("/machines/{machine}/planned", (HttpContext http, string machine, MaintenanceWindowRequest b, MaintenanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.PlanAsync(new PlanMaintenance(machine, b.StartsAt, b.EndsAt, b.Description), c, ct)));

        var planning = app.MapGroup("/api/planning").RequireAuthorization();
        planning.MapGet("/capacity", async (HttpContext http, DateTimeOffset? from, DateTimeOffset? to, string? workCenter, ReadinessService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.EvaluateCapacityAsync(principal, from, to, workCenter, ct));
        });

        planning.MapPost("/schedules", (HttpContext http, ProposeScheduleRequest b, ScheduleService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ProposeAsync(new ProposeSchedule(b.HorizonDays ?? 14), c, ct)));
        planning.MapPost("/schedules/{versionId:long}/approve", (HttpContext http, long versionId, ReasonRequest b, ScheduleService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveAsync(new ApproveSchedule(versionId), c, ct)));
        planning.MapGet("/schedules/{selector}", (HttpContext http, string selector, CancellationToken _) =>
            ReadStore<IPlanningStore>(http, Permissions.ScheduleRead, async (store, ct) =>
            {
                var version = selector switch
                {
                    "proposed" => await store.LatestAsync(ScheduleStatus.Proposed, true, ct),
                    "approved" => await store.LatestAsync(ScheduleStatus.Approved, true, ct),
                    _ when long.TryParse(selector, out var id) => await store.FindVersionAsync(id, true, ct),
                    _ => null,
                } ?? throw new Forge.Kernel.Commands.CommandRejectedException("schedule_not_found", $"No schedule '{selector}'.");
                var principal = (await PrincipalAsync(http))!;
                return version.Result is null ? version : version with { Result = Visible(principal, version.Result) };
            }));

        planning.MapGet("/constraints", (HttpContext http, bool? includeClosed, CancellationToken _) =>
            ReadStore<IPlanningStore>(http, Permissions.ScheduleRead, async (store, ct) =>
            {
                var principal = (await PrincipalAsync(http))!;
                var hidden = await HiddenOrdersAsync(http, principal, ct);
                return (await store.ListConstraintsAsync(includeClosed ?? false, ct))
                    .Where(c => c.AffectedOrders.Count == 0 || c.AffectedOrders.Any(o => !hidden.Contains(o)))
                    .Select(c => c with
                    {
                        AffectedOrders = c.AffectedOrders.Where(o => !hidden.Contains(o)).ToList(),
                        AffectedOperations = c.AffectedOperations.Where(o => !hidden.Contains(long.Parse(o.Split('/')[0], System.Globalization.CultureInfo.InvariantCulture))).ToList(),
                    })
                    .ToList();
            }));
        planning.MapPost("/constraints/{id:long}/investigate", (HttpContext http, long id, ReasonRequest b, ScheduleService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.InvestigateAsync(new InvestigateConstraint(id), c, ct)));
        planning.MapPost("/constraints/{id:long}/resolve", (HttpContext http, long id, ResolveRequest b, ScheduleService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ResolveAsync(new ResolveConstraint(id, b.Resolution), c, ct)));
        planning.MapPost("/constraints/{id:long}/verify", (HttpContext http, long id, ReasonRequest b, ScheduleService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.VerifyAsync(new VerifyConstraint(id), c, ct)));

        app.MapGet("/api/orders/{orderNumber:long}/drop-dead-dates", async (HttpContext http, long orderNumber, int? shipDays, ReadinessService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.EvaluateDropDeadDatesAsync(principal, orderNumber, shipDays ?? 1, ct));
        }).RequireAuthorization();
    }

    /// <summary>Removes orders of programs the caller cannot see.</summary>
    private static ScheduleResult Visible(ForgePrincipal principal, ScheduleResult result) => result with
    {
        Orders = result.Orders.Where(o => o.ProgramId is not { } p || principal.Programs.Contains(new ProgramId(p))).ToList(),
        Operations = result.Operations.Where(o => o.ProgramId is not { } p || principal.Programs.Contains(new ProgramId(p))).ToList(),
    };

    private static async Task<HashSet<long>> HiddenOrdersAsync(HttpContext http, ForgePrincipal principal, CancellationToken ct)
    {
        await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(ct);
        var orders = await uow.Store<Forge.Execution.Contracts.IExecutionLookup>().ListOrdersAsync(ct);
        return orders.Where(o => o.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p))).Select(o => o.OrderNumber).ToHashSet();
    }
}
