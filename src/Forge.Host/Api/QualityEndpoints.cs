using Forge.Execution.Contracts;
using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning;
using Forge.Quality;
using Forge.Quality.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record GaugeRequest(string Code, string Description, DateOnly CalibrationDue, string? Certificate, string? Reason);

public sealed record CalibrationRequest(DateOnly CalibratedOn, DateOnly DueDate, string? Certificate, string? Reason);

public sealed record InspectionPlanRequest(string PartNumber, int Sequence, string Title, NewCharacteristic[] Characteristics, string? Reason, string? Revision = null);

public sealed record InspectionRequest(long OrderNumber, int Sequence, decimal Quantity, string? GaugeCode, string? Notes, Finding[] Findings, decimal? RejectedQuantity, string? Reason);

public sealed record NcrRequest(string PartNumber, long? OrderNumber, int? Sequence, string? LotNumber, decimal Quantity, string Defect, string Description, string? Reason);

public sealed record DispositionRequest(Disposition Disposition, string Note, string? Reason);

public sealed record NoteRequest(string Note, string? Reason);

public sealed record CapaRequest(long? NcrId, string Title, string RootCause, string Action, DateOnly DueDate, string? Reason);

public sealed record ShipmentLineRequest(string PartNumber, decimal Quantity, string LocationCode, string? LotNumber);

public sealed record ShipmentRequest(string? Customer, string? Reference, long? DemandId, string? ShipTo, ShipmentLineRequest[] Lines, string? Reason);

public sealed record ShipRequest(string? Carrier, string? Tracking, string? Reason);

/// <summary>An inspection point an order has reached but not yet passed.</summary>
public sealed record InspectionDue(long OrderNumber, string PartNumber, decimal OrderQuantity, int Sequence, string Operation, string OperationStatus, InspectionPlanInfo Plan, bool LastFailed);

public sealed record OrderQualityView(OrderQuality Quality, IReadOnlyList<InspectionPlanInfo> Plans, IReadOnlyList<InspectionInfo> Inspections);

/// <summary>Phase 7a endpoints: inspection plans, inspections, NCRs, corrective actions, gauges and shipments.</summary>
public static class QualityEndpoints
{
    public static void MapQualityEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/quality").RequireAuthorization();
        api.MapGet("/plans", (HttpContext http, string? part, CancellationToken _) =>
            ReadStore<IQualityLookup>(http, Permissions.QualityRead, async (l, ct) => await l.ListPlansAsync(string.IsNullOrWhiteSpace(part) ? null : part.Trim().ToUpperInvariant(), ct)));
        api.MapGet("/gauges", (HttpContext http, CancellationToken _) =>
            ReadStore<Forge.Resources.Contracts.IToolLookup>(http, Permissions.QualityRead, async (l, ct) => GaugeInfo.From(await l.ListToolsAsync(ct), await l.ListToolTypesAsync(ct))));
        api.MapGet("/capas", (HttpContext http, bool? includeVerified, CancellationToken _) =>
            ReadStore<IQualityLookup>(http, Permissions.QualityRead, async (l, ct) => await l.ListCapasAsync(includeVerified ?? false, ct)));
        api.MapGet("/ncrs", (HttpContext http, bool? includeClosed, CancellationToken _) =>
            Read(http, Permissions.QualityRead, async principal =>
            {
                await using var uow = await Snapshot(http);
                var hidden = await HiddenOrdersAsync(uow, principal, http.RequestAborted);
                return (await uow.Store<IQualityLookup>().ListNcrsAsync(includeClosed ?? false, http.RequestAborted)).Where(n => n.OrderNumber is not { } o || !hidden.Contains(o)).ToList();
            }));
        api.MapGet("/inspections", (HttpContext http, long? order, int? take, CancellationToken _) =>
            Read(http, Permissions.QualityRead, async principal =>
            {
                await using var uow = await Snapshot(http);
                var hidden = await HiddenOrdersAsync(uow, principal, http.RequestAborted);
                return (await uow.Store<IQualityLookup>().ListInspectionsAsync(order, take ?? 100, http.RequestAborted)).Where(i => !hidden.Contains(i.OrderNumber)).ToList();
            }));
        api.MapGet("/due", (HttpContext http, CancellationToken _) =>
            Read(http, Permissions.QualityRead, async principal =>
            {
                await using var uow = await Snapshot(http);
                var ct = http.RequestAborted;
                var execution = uow.Store<IExecutionLookup>();
                var quality = uow.Store<IQualityLookup>();
                var plans = (await quality.ListPlansAsync(null, ct)).ToLookup(p => p.PartNumber, StringComparer.Ordinal);
                var latest = (await quality.ListInspectionsAsync(null, 1000, ct))
                    .GroupBy(i => (i.OrderNumber, i.Sequence))
                    .ToDictionary(g => g.Key, g => g.MaxBy(i => i.InspectionId)!.Result);
                var due = new List<InspectionDue>();
                foreach (var summary in await execution.ListOrdersAsync(ct))
                {
                    if (summary.Status is not (OrderStatus.Released or OrderStatus.Held) || !plans.Contains(summary.PartNumber)
                        || (summary.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p))))
                    {
                        continue;
                    }

                    var order = await execution.FindOrderAsync(summary.OrderNumber, ct);
                    foreach (var plan in InspectionPlans.For(plans[summary.PartNumber], summary.PartNumber, summary.Revision))
                    {
                        var op = order?.Operations.FirstOrDefault(o => o.Sequence == plan.Sequence);
                        var result = latest.TryGetValue((summary.OrderNumber, plan.Sequence), out var r) ? r : (InspectionResult?)null;
                        if (op is not null && op.Status != OperationStatus.Pending && result != InspectionResult.Pass)
                        {
                            due.Add(new InspectionDue(summary.OrderNumber, summary.PartNumber, summary.Quantity, plan.Sequence, op.Description, op.Status.ToString(), plan, result == InspectionResult.Fail));
                        }
                    }
                }

                return due;
            }));
        api.MapGet("/orders/{number:long}", (HttpContext http, long number, CancellationToken _) =>
            Read(http, Permissions.QualityRead, async principal =>
            {
                await using var uow = await Snapshot(http);
                var ct = http.RequestAborted;
                var order = await uow.Store<IExecutionLookup>().FindOrderAsync(number, ct);
                if (order is null || (order.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p))))
                {
                    throw new CommandRejectedException("order_not_found", $"Order {number} does not exist.");
                }

                var quality = uow.Store<IQualityLookup>();
                return new OrderQualityView(
                    await quality.GetOrderQualityAsync(number, order.PartNumber, ct),
                    InspectionPlans.For(await quality.ListPlansAsync(order.PartNumber, ct), order.PartNumber, order.Revision),
                    await quality.ListInspectionsAsync(number, 200, ct));
            }));

        api.MapPost("/gauges", (HttpContext http, GaugeRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DefineGaugeAsync(new DefineGauge(b.Code, b.Description, b.CalibrationDue, b.Certificate), c, ct)));
        api.MapPost("/gauges/{code}/calibrations", (HttpContext http, string code, CalibrationRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordCalibrationAsync(new RecordCalibration(code, b.CalibratedOn, b.DueDate, b.Certificate), c, ct)));
        api.MapPost("/plans", (HttpContext http, InspectionPlanRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetInspectionPlanAsync(new SetInspectionPlan(b.PartNumber, b.Sequence, b.Title, b.Characteristics ?? [], b.Revision), c, ct)));
        api.MapPost("/plans/{part}/{sequence:int}/remove", (HttpContext http, string part, int sequence, string? revision, ReasonRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RemoveInspectionPlanAsync(new RemoveInspectionPlan(part, sequence, revision), c, ct)));
        api.MapPost("/inspections", (HttpContext http, InspectionRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordInspectionAsync(new RecordInspection(b.OrderNumber, b.Sequence, b.Quantity, b.GaugeCode, b.Notes, b.Findings ?? [], b.RejectedQuantity), c, ct)));
        api.MapPost("/ncrs", (HttpContext http, NcrRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RaiseNcrAsync(new RaiseNcr(b.PartNumber, b.OrderNumber, b.Sequence, b.LotNumber, b.Quantity, b.Defect, b.Description), c, ct)));
        api.MapPost("/ncrs/{id:long}/disposition", (HttpContext http, long id, DispositionRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DispositionAsync(new DispositionNcr(id, b.Disposition, b.Note), c, ct)));
        api.MapPost("/ncrs/{id:long}/approve", (HttpContext http, long id, ReasonRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DecideUseAsIsAsync(new DecideUseAsIs(id, true), c, ct)));
        api.MapPost("/ncrs/{id:long}/reject", (HttpContext http, long id, ReasonRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DecideUseAsIsAsync(new DecideUseAsIs(id, false), c, ct)));
        api.MapPost("/ncrs/{id:long}/close", (HttpContext http, long id, NoteRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CloseNcrAsync(new CloseNcr(id, b.Note), c, ct)));
        api.MapPost("/capas", (HttpContext http, CapaRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.OpenCapaAsync(new OpenCapa(b.NcrId, b.Title, b.RootCause, b.Action, b.DueDate), c, ct)));
        api.MapPost("/capas/{id:long}/implement", (HttpContext http, long id, NoteRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ImplementCapaAsync(new ImplementCapa(id, b.Note), c, ct)));
        api.MapPost("/capas/{id:long}/verify", (HttpContext http, long id, ReasonRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.VerifyCapaAsync(new VerifyCapa(id), c, ct)));

        var shipping = app.MapGroup("/api/inventory").RequireAuthorization();
        shipping.MapGet("/shipments", (HttpContext http, bool? includeClosed, CancellationToken _) =>
            ReadStore<IInventoryLookup>(http, Permissions.InventoryRead, async (l, ct) => await l.ListShipmentsAsync(includeClosed ?? false, ct)));
        shipping.MapPost("/shipments", async (HttpContext http, ShipmentRequest b, InventoryService s, CancellationToken ct) =>
        {
            // The shipment's demand (Planning) is checked here: Inventory cannot see Planning.
            var customer = b.Customer;
            if (b.DemandId is { } demandId)
            {
                var principal = await PrincipalAsync(http);
                if (principal is null)
                {
                    return NotRegistered();
                }

                await using var uow = await Snapshot(http);
                var demand = await uow.Store<IDemandStore>().FindAsync(demandId, ct);
                if (demand is null || (demand.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p))))
                {
                    throw new CommandRejectedException("demand_not_found", $"Demand {demandId} does not exist.");
                }

                if (demand.Status != DemandStatus.Open)
                {
                    throw new CommandRejectedException("demand_not_open", $"Demand {demandId} is {demand.Status}.");
                }

                if ((b.Lines ?? []).Any(l => !string.Equals(l.PartNumber?.Trim(), demand.PartNumber, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new CommandRejectedException("wrong_part", $"Demand {demandId} is for {demand.PartNumber}; every line must ship that part.");
                }

                customer = string.IsNullOrWhiteSpace(customer) ? demand.Customer ?? "Internal" : customer;
            }

            return await Command(http, b.Reason, c => s.CreateShipmentAsync(
                new CreateShipment(customer ?? string.Empty, b.Reference, b.DemandId, b.ShipTo, (b.Lines ?? []).Select(l => new NewShipmentLine(l.PartNumber, l.Quantity, l.LocationCode, l.LotNumber)).ToList()), c, ct));
        });
        shipping.MapPost("/shipments/{id:long}/pack", (HttpContext http, long id, ReasonRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.PackShipmentAsync(new PackShipment(id), c, ct)));
        shipping.MapPost("/shipments/{id:long}/ship", (HttpContext http, long id, ShipRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ShipShipmentAsync(new ShipShipment(id, b.Carrier, b.Tracking), c, ct)));
        shipping.MapPost("/shipments/{id:long}/cancel", (HttpContext http, long id, ReasonRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelShipmentAsync(new CancelShipment(id), c, ct)));
    }

    private static Task<IKernelUnitOfWork> Snapshot(HttpContext http) =>
        http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);

    private static async Task<HashSet<long>> HiddenOrdersAsync(IKernelUnitOfWork uow, ForgePrincipal principal, CancellationToken ct) =>
        (await uow.Store<IExecutionLookup>().ListOrdersAsync(ct))
            .Where(o => o.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p)))
            .Select(o => o.OrderNumber)
            .ToHashSet();
}
