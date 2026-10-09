using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Maintenance;
using Forge.Maintenance.Contracts;
using Forge.Planning;
using Forge.Planning.Engines;
using Forge.Procurement;
using Forge.Procurement.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record RequiredPartRequest(string PartNumber, decimal Quantity, string? Position);

public sealed record WorkOrderRequirementsRequest(string? AssetCode, string? ProcedureDocument, string? ProcedureRevision, string[]? Qualifications, RequiredPartRequest[]? Parts, string? Reason);

/// <summary>A proposed outage with the preview a decider sees.</summary>
public sealed record OutageView(OutageInfo Outage, OutagePreview? Preview);

/// <summary>What asking for a work order's missing parts did, line by line.</summary>
public sealed record PartsRequestLine(string PartNumber, decimal Quantity, string Outcome, string? RequisitionId);

/// <summary>
/// V2.9: work order requirements and readiness, parts requested from purchasing for blocked work, and planned
/// outages proposed by one person and approved by another after the schedule preview.
/// </summary>
public static class MaintenanceReadinessEndpoints
{
    public static void MapMaintenanceReadinessEndpoints(this IEndpointRouteBuilder app)
    {
        var maintenance = app.MapGroup("/api/maintenance").RequireAuthorization();

        maintenance.MapGet("/work-orders/{id:long}/requirements", (HttpContext http, long id, CancellationToken _) =>
            ReadStore<IMaintenanceLookup>(http, Permissions.MaintenanceRead, async (l, ct) => await l.ListRequiredPartsAsync(id, ct)));
        maintenance.MapPost("/work-orders/{id:long}/requirements", (HttpContext http, long id, WorkOrderRequirementsRequest b, MaintenanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetRequirementsAsync(new SetWorkOrderRequirements(id, b.AssetCode, b.ProcedureDocument, b.ProcedureRevision, b.Qualifications ?? [],
                (b.Parts ?? []).Select(p => new RequiredPart(p.PartNumber, p.Quantity, p.Position)).ToList()), c, ct)));

        maintenance.MapGet("/readiness", async (HttpContext http, MaintenancePlanningService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.ReadinessAsync(principal, null, ct));
        });
        maintenance.MapGet("/work-orders/{id:long}/readiness", async (HttpContext http, long id, MaintenancePlanningService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok((await s.ReadinessAsync(principal, id, ct)).Single());
        });

        // Scenario 5: what blocked work is short of goes to purchasing as requisitions, sourced to the work order.
        maintenance.MapPost("/work-orders/{id:long}/request-parts", async (HttpContext http, long id, ReasonRequest b, MaintenancePlanningService planning, RequisitionService requisitions, IKernelUnitOfWorkFactory uows, TimeProvider time, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null)
            {
                return NotRegistered();
            }

            var readiness = (await planning.ReadinessAsync(principal, id, ct)).Single();
            WorkOrderInfo order;
            await using (var read = await uows.BeginSnapshotAsync(ct))
            {
                order = (await read.Store<IMaintenanceLookup>().FindWorkOrderAsync(id, ct))!;
            }

            var lines = new List<PartsRequestLine>();
            var need = order.DueDate is { } due && due > DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime) ? due : DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
            foreach (var part in readiness.Parts.Where(p => !p.Covered))
            {
                var quantity = part.StillNeeded - part.OnHand;
                var (context, error) = await ContextAsync(http, b.Reason, "part:" + part.PartNumber, CommandSource.Api);
                if (context is null)
                {
                    return error!;
                }

                try
                {
                    var result = await requisitions.RaiseAsync(new RaiseRequisition(part.PartNumber, quantity, need, PurchaseSourceKind.Maintenance, $"work-order:{id}",
                        $"Work order {id} ({order.Kind}) on {order.Target}: {order.Title}", null, null, null, null, null), context, ct);
                    lines.Add(new PartsRequestLine(part.PartNumber, quantity, "Requested", result.EntityId));
                }
                catch (CommandRejectedException ex) when (ex.Code == "requisition_exists")
                {
                    lines.Add(new PartsRequestLine(part.PartNumber, quantity, ex.Message, null));
                }
            }

            return Results.Ok(new { workOrderId = id, readiness = readiness.Status, lines });
        });

        maintenance.MapGet("/outages", (HttpContext http, bool? includeDecided, CancellationToken _) =>
            ReadStore<IMaintenanceLookup>(http, Permissions.MaintenanceRead, async (l, ct) => await l.ListOutagesAsync(includeDecided ?? false, ct)));
        maintenance.MapGet("/outages/preview", async (HttpContext http, string machine, DateTimeOffset from, DateTimeOffset to, MaintenancePlanningService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.PreviewAsync(principal, machine.Trim().ToUpperInvariant(), from.ToUniversalTime(), to.ToUniversalTime(), ct));
        });
        maintenance.MapGet("/outages/{id:long}", async (HttpContext http, long id, MaintenancePlanningService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null)
            {
                return NotRegistered();
            }

            var (outage, preview) = await s.PreviewOutageAsync(principal, id, ct);
            return Results.Ok(new OutageView(outage, outage.Status == OutageStatus.Proposed ? preview : null));
        });

        // Approval runs the scheduler with and without the outage first; the decision records what was seen.
        maintenance.MapPost("/outages/{id:long}/approve", async (HttpContext http, long id, ReasonRequest b, MaintenanceService s, MaintenancePlanningService planning, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null)
            {
                return NotRegistered();
            }

            var (_, preview) = await planning.PreviewOutageAsync(principal, id, ct);
            return await Command(http, b.Reason, c => s.DecideOutageAsync(new DecideOutage(id, true, preview.Summary), c, ct));
        });
        maintenance.MapPost("/outages/{id:long}/reject", (HttpContext http, long id, ReasonRequest b, MaintenanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DecideOutageAsync(new DecideOutage(id, false, null), c, ct)));
        maintenance.MapPost("/outages/{id:long}/withdraw", (HttpContext http, long id, ReasonRequest b, MaintenanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.WithdrawOutageAsync(new WithdrawOutage(id), c, ct)));
    }
}
