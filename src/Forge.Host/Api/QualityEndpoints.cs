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

public sealed record InspectionPlanRequest(string PartNumber, int Sequence, string Title, NewCharacteristic[] Characteristics, string? Reason, string? Revision = null, int? PlannedMinutes = null);

public sealed record InspectionRequest(long OrderNumber, int Sequence, decimal Quantity, string? GaugeCode, string? Notes, Finding[] Findings, decimal? RejectedQuantity, string? Reason);

public sealed record NcrRequest(string PartNumber, long? OrderNumber, int? Sequence, string? LotNumber, decimal Quantity, string Defect, string Description, string? Reason);

public sealed record SuspectNcrRequest(string? Reason);
public sealed record SuspectReplacementMaterialRequest(decimal Quantity, string ToLocation, string? Reason);

public sealed record DispositionRequest(Disposition Disposition, string Note, string? Reason);

public sealed record NoteRequest(string Note, string? Reason);

public sealed record CapaRequest(long? NcrId, string Title, string RootCause, string Action, DateOnly DueDate, string? Reason);

public sealed record ShipmentLineRequest(string PartNumber, decimal Quantity, string LocationCode, string? LotNumber);

public sealed record ShipmentRequest(string? Customer, string? Reference, long? DemandId, string? ShipTo, ShipmentLineRequest[] Lines, string? Reason, bool RequirePackaging = false, string? ContainerCode = null);

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
        api.MapGet("/capas", async (HttpContext http, bool? includeVerified, CancellationToken ct) =>
            {
                var principal = await PrincipalAsync(http);
                if (principal is null) return NotRegistered();
                var authorizer = http.RequestServices.GetRequiredService<IForgeAuthorizer>();
                if (!HasQualityRead(principal)) throw new ForgeAuthorizationException(authorizer.Authorize(principal, Permissions.QualityRead, ResourceRef.Of("Capa")));
                await using var uow = await Snapshot(http);
                var visible = new List<CapaInfo>();
                foreach (var capa in await uow.Store<IQualityLookup>().ListCapasAsync(includeVerified ?? false, ct))
                    if ((await QualityService.ResourcesForCapaAsync(uow, capa, ct)).All(resource => authorizer.Authorize(principal, Permissions.QualityRead, resource).IsPermitted)
                        && (capa.NcrId is not { } source || await http.RequestServices.GetRequiredService<IAcceptanceSourceAccess>().CanReadNcrSourceAsync(uow, principal, source, ct))
                        && (!capa.Governed || await http.RequestServices.GetRequiredService<ICapaLegacyGovernanceGuard>().CanReadAsync(uow, principal, capa.CapaId, ct))) visible.Add(capa);
                return Results.Ok(visible);
            });
        api.MapGet("/ncrs", async (HttpContext http, bool? includeClosed, CancellationToken ct) =>
            {
                var principal = await PrincipalAsync(http);
                if (principal is null) return NotRegistered();
                var authorizer = http.RequestServices.GetRequiredService<IForgeAuthorizer>();
                if (!HasQualityRead(principal)) throw new ForgeAuthorizationException(authorizer.Authorize(principal, Permissions.QualityRead, ResourceRef.Of("Ncr")));
                await using var uow = await Snapshot(http);
                var visible = new List<NcrInfo>();
                foreach (var ncr in await uow.Store<IQualityLookup>().ListNcrsAsync(includeClosed ?? false, ct))
                    if ((await QualityService.ResourcesForNcrAsync(uow, ncr, ct)).All(resource => authorizer.Authorize(principal, Permissions.QualityRead, resource).IsPermitted)
                        && await http.RequestServices.GetRequiredService<IAcceptanceSourceAccess>().CanReadNcrSourceAsync(uow, principal, ncr.NcrId, ct)) visible.Add(ncr);
                return Results.Ok(visible);
            });
        api.MapGet("/suspect-scrap", (HttpContext http, CancellationToken _) =>
            Read(http, Permissions.QualityRead, async principal =>
            {
                await using var uow = await Snapshot(http);
                var execution = uow.Store<IExecutionLookup>();
                var allowedOrders = (await execution.ListOrdersAsync(http.RequestAborted))
                    .Where(order => order.ProgramId is not { } program || principal.Programs.Contains(new ProgramId(program)))
                    .ToDictionary(order => order.OrderNumber);
                var tasks = (await uow.Store<IInventoryLookup>().ListMaterialTasksAsync(false, null, http.RequestAborted))
                    .Where(task => task.SourceSuspectReportId is not null).ToDictionary(task => task.SourceSuspectReportId!.Value, task => task.TaskId);
                var reports = await uow.Store<IQualityLookup>().ListSuspectScrapReportsAsync(null, http.RequestAborted);
                var result = new List<object>();
                foreach (var report in reports.Where(report => allowedOrders.ContainsKey(report.OrderNumber)))
                {
                    var order = await execution.FindOrderAsync(report.OrderNumber, http.RequestAborted);
                    var operation = order?.Operations.FirstOrDefault(item => item.Sequence == report.Sequence);
                    var replacementTaskId = tasks.TryGetValue(report.ReportId, out var linkedTaskId) ? linkedTaskId : (long?)null;
                    result.Add(new { report.ReportId, report.Kind, report.OrderNumber, report.Sequence, report.PartNumber, report.Revision, report.LotNumber,
                        report.SerialNumber, report.Quantity, report.MachineCode, report.ReporterPersonId, report.ReasonCode, report.Description,
                        report.ReportedAt, report.NcrId, ReplacementTaskId = replacementTaskId, WorkCenterCode = operation?.WorkCenterCode, OrderStatus = order?.Status.ToString() });
                }
                return result;
            }));
        api.MapPost("/suspect-scrap/{reportId:long}/ncr", (HttpContext http, long reportId, SuspectNcrRequest body, QualityService quality, CancellationToken ct) =>
            Command(http, body.Reason, context => quality.RaiseNcrFromSuspectScrapAsync(new RaiseNcrFromSuspectScrap(reportId), context, ct)));
        api.MapPost("/suspect-scrap/{reportId:long}/replacement-request", async (HttpContext http, long reportId, SuspectReplacementMaterialRequest body,
            InventoryService inventory, IForgeAuthorizer authorizer, IKernelUnitOfWorkFactory uows, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var permission = authorizer.Authorize(principal, Permissions.InventoryMaterialRequest, ResourceRef.Of(Permissions.InventoryMaterialRequest));
            if (!permission.IsPermitted) throw new ForgeAuthorizationException(permission);
            SuspectScrapReport report;
            await using (var uow = await uows.BeginSnapshotAsync(ct))
            {
                report = await uow.Store<IQualityLookup>().FindSuspectScrapReportAsync(reportId, ct)
                    ?? throw new CommandRejectedException("suspect_report_not_found", "The suspect report does not exist.");
                var order = await uow.Store<IExecutionLookup>().FindOrderAsync(report.OrderNumber, ct);
                if (order is null || order.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program)))
                    throw new CommandRejectedException("suspect_report_not_found", "The suspect report does not exist.");
                var scoped = authorizer.Authorize(principal, Permissions.InventoryMaterialRequest,
                    new ResourceRef("ProductionOrder", report.OrderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), order.ProgramId is { } id ? new ProgramId(id) : null));
                if (!scoped.IsPermitted) throw new ForgeAuthorizationException(scoped);
            }
            return await Command(http, body.Reason, context => inventory.RequestMaterialAsync(
                new RequestMaterial(MaterialTaskKind.Stage, report.OrderNumber, report.PartNumber, body.Quantity, body.ToLocation,
                    OperationSequence: report.Sequence, SourceSuspectReportId: reportId), context, ct));
        });
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
        api.MapGet("/orders/{number:long}", async (HttpContext http, long number, CancellationToken ct) =>
            {
                var principal = await PrincipalAsync(http);
                if (principal is null) return NotRegistered();
                await using var uow = await Snapshot(http);
                var order = await uow.Store<IExecutionLookup>().FindOrderAsync(number, ct);
                if (order is null || (order.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p))))
                {
                    throw new CommandRejectedException("order_not_found", $"Order {number} does not exist.");
                }
                var authorizer = http.RequestServices.GetRequiredService<IForgeAuthorizer>();
                var resource = new ResourceRef("ProductionOrder", number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    order.ProgramId is { } program ? new ProgramId(program) : null, ContractId: order.ContractId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var centers = order.Operations.Select(operation => operation.WorkCenterCode).Distinct(StringComparer.Ordinal).ToList();
                var contexts = new List<ResourceRef>();
                foreach (var code in centers)
                {
                    var center = await uow.Store<Forge.Resources.Contracts.IResourcesLookup>().FindWorkCenterAsync(code, ct);
                    var area = center is null ? null : await uow.Store<Forge.Resources.Contracts.IResourcesLookup>().FindSafetyAreaAsync(center.SafetyAreaCode, ct);
                    contexts.Add(resource with { WorkCenterId = code, FacilityId = area?.SiteCode });
                }
                if (contexts.Count == 0) contexts.Add(resource);
                if (contexts.Any(scope => !authorizer.Authorize(principal, Permissions.QualityRead, scope).IsPermitted))
                    throw new CommandRejectedException("order_not_found", $"Order {number} does not exist.");
                var quality = uow.Store<IQualityLookup>();
                var state = await quality.GetOrderQualityAsync(number, order.PartNumber, ct);
                foreach (var ncr in state.OpenNcrs)
                    if ((await QualityService.ResourcesForNcrAsync(uow, ncr, ct)).Any(resource => !authorizer.Authorize(principal, Permissions.QualityRead, resource).IsPermitted)
                        || !await http.RequestServices.GetRequiredService<IAcceptanceSourceAccess>().CanReadNcrSourceAsync(uow, principal, ncr.NcrId, ct))
                        throw new CommandRejectedException("order_not_found", $"Order {number} does not exist.");
                return Results.Ok(new OrderQualityView(state,
                    InspectionPlans.For(await quality.ListPlansAsync(order.PartNumber, ct), order.PartNumber, order.Revision),
                    await quality.ListInspectionsAsync(number, 200, ct)));
            });

        api.MapPost("/gauges", (HttpContext http, GaugeRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DefineGaugeAsync(new DefineGauge(b.Code, b.Description, b.CalibrationDue, b.Certificate), c, ct)));
        api.MapPost("/gauges/{code}/calibrations", (HttpContext http, string code, CalibrationRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordCalibrationAsync(new RecordCalibration(code, b.CalibratedOn, b.DueDate, b.Certificate), c, ct)));
        api.MapPost("/plans", (HttpContext http, InspectionPlanRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetInspectionPlanAsync(new SetInspectionPlan(b.PartNumber, b.Sequence, b.Title, b.Characteristics ?? [], b.Revision, b.PlannedMinutes ?? 0), c, ct)));
        api.MapPost("/plans/{part}/{sequence:int}/remove", (HttpContext http, string part, int sequence, string? revision, ReasonRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RemoveInspectionPlanAsync(new RemoveInspectionPlan(part, sequence, revision), c, ct)));
        api.MapPost("/inspections", (HttpContext http, InspectionRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordInspectionAsync(new RecordInspection(b.OrderNumber, b.Sequence, b.Quantity, b.GaugeCode, b.Notes, b.Findings ?? [], b.RejectedQuantity), c, ct)));
        api.MapPost("/ncrs", (HttpContext http, NcrRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RaiseNcrAsync(new RaiseNcr(b.PartNumber, b.OrderNumber, b.Sequence, b.LotNumber, b.Quantity, b.Defect, b.Description), c, ct)));
        api.MapPost("/ncrs/{id:long}/disposition", (HttpContext http, long id, DispositionRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DispositionAsync(new DispositionNcr(id, b.Disposition, b.Note), c, ct)));
        api.MapPost("/ncrs/{id:long}/approve", (HttpContext http, long id, ReasonRequest b, QualityService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DecideUseAsIsAsync(new DecideUseAsIs(id, true), c, ct)))
            .WithMetadata(new StepUpRequiredAttribute());
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
                new CreateShipment(customer ?? string.Empty, b.Reference, b.DemandId, b.ShipTo, (b.Lines ?? []).Select(l => new NewShipmentLine(l.PartNumber, l.Quantity, l.LocationCode, l.LotNumber)).ToList(), b.RequirePackaging, b.ContainerCode), c, ct));
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

    private static bool HasQualityRead(ForgePrincipal principal) =>
        (principal.EffectivePermissions is { } permissions ? permissions.Contains(Permissions.QualityRead)
            : principal.Roles.Any(role => Roles.PermissionsOf(role).Contains(Permissions.QualityRead, StringComparer.Ordinal)))
        || principal.ScopedPermissions?.Any(grant => grant.Permission == Permissions.QualityRead) == true
        || principal.DelegatedPermissions?.Any(grant => grant.Permission == Permissions.QualityRead) == true;

    private static async Task<HashSet<long>> HiddenOrdersAsync(IKernelUnitOfWork uow, ForgePrincipal principal, CancellationToken ct) =>
        (await uow.Store<IExecutionLookup>().ListOrdersAsync(ct))
            .Where(o => o.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p)))
            .Select(o => o.OrderNumber)
            .ToHashSet();
}
