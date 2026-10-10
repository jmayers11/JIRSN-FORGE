using Forge.Engineering;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning;
using Forge.Procurement;
using Forge.Procurement.Contracts;
using Forge.Programs.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ItemPlanningRequest(int LeadTimeDays, decimal SafetyStock, LotPolicy LotPolicy, decimal OrderQuantity, decimal OrderMultiple, string? Reason);

public sealed record LocationRequest(
    string Code,
    string SiteCode,
    string Name,
    LocationKind Kind,
    string? Reason,
    string? ParentCode = null,
    LocationLevel? Level = null,
    bool? Nettable = null,
    string? WorkCenterCode = null,
    string? MachineCode = null,
    string? CustodianSupplier = null);

public sealed record LocationChangeRequest(
    string Name,
    string? ParentCode,
    LocationLevel Level,
    bool Nettable,
    string? Reason,
    string? WorkCenterCode = null,
    string? MachineCode = null,
    string? CustodianSupplier = null);

public sealed record ReceiptRequest(string PartNumber, decimal Quantity, string LocationCode, string? LotNumber, string? SupplierLot, long? PoNumber, int? PoLine, bool Inspect, string? Reason, string? Revision = null, IdentityKind? IdentityKind = null);

public sealed record SplitRequest(string PartNumber, string LotNumber, string LocationCode, StockStatus? Status, InstanceShare[] Results, string? Reason);

public sealed record MergeRequest(string PartNumber, InstanceShare[] Sources, string ResultLot, string LocationCode, StockStatus? Status, string? Reason);

public sealed record MaterialTaskRequest(MaterialTaskKind Kind, long? OrderNumber, string PartNumber, decimal Quantity, string ToLocation, string? LotNumber, string? FromLocation, string? KitCode, string? Reason);

public sealed record AllocateRequest(string FromLocation, string? LotNumber, string? Reason);

public sealed record PickRequest(string ScannedLocation, string? ScannedLot, decimal? Quantity, string? Reason);

public sealed record DeliverRequest(string ScannedLocation, string? Reason);

public sealed record CountProgramRequest(string ProgramCode, string Name, string? LocationCode, string? PartNumber, int FrequencyDays, string? Reason);

public sealed record TransformationRequest(long OrderNumber, int? OperationSequence, TransformationEntry[] Entries, string? Reason);

public sealed record YieldToleranceRequest(decimal Percent, string? Reason);

/// <summary>What became of the material issued to an order (V3.3): one row per issued part and lot, and every record.</summary>
public sealed record OrderReconciliationView(long OrderNumber, string PartNumber, IReadOnlyList<MaterialReconciliation> Rows, IReadOnlyList<TransformationInfo> Transformations);

public sealed record MergePolicyRequest(bool Allowed, bool SameRevisionRequired, bool SameSupplierLotRequired, string? Reason);

public sealed record OrderMaterialRequest(long OrderNumber, string PartNumber, decimal Quantity, string LocationCode, string? LotNumber, string? Reason, string? AlternateFor = null);

public sealed record TransferRequest(string PartNumber, decimal Quantity, string FromLocation, string ToLocation, string? LotNumber, StockStatus? Status, string? Reason);

public sealed record StockRequest(string PartNumber, decimal Quantity, string LocationCode, string? LotNumber, StockStatus? Status, string? Reason);

public sealed record ProductionReceiptRequest(long OrderNumber, decimal Quantity, string LocationCode, string? LotNumber, string? Reason, string[]? Serials = null);

public sealed record AdjustmentRequestBody(string PartNumber, string LocationCode, string? LotNumber, StockStatus? Status, decimal Change, string? Reason);

public sealed record CountRequest(string PartNumber, string LocationCode, string? LotNumber, StockStatus? Status, decimal Counted, string? Reason);

public sealed record RequisitionRequest(string PartNumber, decimal Quantity, DateOnly NeedDate, PurchaseSourceKind SourceKind, string SourceKey, string SourceLabel, string? RequiredRevision, string? SupplierCode, Guid? ProgramId, string? Reason);

public sealed record PlannedRequisitionRequest(string PlannedOrder, string? SupplierCode, string? RequiredRevision, string? Reason);

public sealed record PurchaseLineRequest(string PartNumber, decimal Quantity, DateOnly PromisedDate, long? RequisitionId = null);

public sealed record PurchaseOrderRequest(string Supplier, PurchaseLineRequest[] Lines, string? Reason);

public sealed record PurchaseLineAddRequest(string PartNumber, decimal Quantity, DateOnly PromisedDate, string? Reason, long? RequisitionId = null);

public sealed record PromisedDateRequest(DateOnly PromisedDate, string? Reason);

public sealed record DemandRequest(string PartNumber, decimal Quantity, DateOnly NeedDate, int? Priority, DemandKind Kind, string? Customer, string? Reference, Guid? ProgramId, string? Reason, string? SourceKey = null);

public sealed record DemandChangeRequest(decimal Quantity, DateOnly NeedDate, int Priority, string? Reason);

/// <summary>A demand with what has shipped against it.</summary>
public sealed record DemandView(long DemandId, string PartNumber, decimal Quantity, decimal Shipped, decimal Remaining, DateOnly NeedDate, int Priority, DemandKind Kind, string? Customer, string? Reference, Guid? ProgramId, DemandStatus Status, string CreatedBy, DateTimeOffset CreatedAt, long? DeliveryId = null, string? ContractNote = null, string? SourceKey = null);

/// <summary>A purchase order line with what Inventory has received against it.</summary>
public sealed record PurchaseLineView(int LineNumber, string PartNumber, decimal Quantity, string UnitOfMeasure, DateOnly PromisedDate, PurchaseLineStatus Status, decimal Received, decimal Open, long? RequisitionId = null, PurchaseSourceKind? SourceKind = null, string? SourceKey = null, string? SourceLabel = null);

public sealed record PurchaseOrderView(long PoNumber, string Supplier, PurchaseOrderStatus Status, string CreatedBy, DateTimeOffset CreatedAt, string? ApprovedBy, DateTimeOffset? ApprovedAt, IReadOnlyList<PurchaseLineView> Lines, string? SupplierCode = null);

public sealed record SupplierRequest(string Code, string Name, string? Notes, string? Reason);

public sealed record SupplierChangeRequest(string Name, string? Notes, string? Reason);

public sealed record SupplierStatusRequest(SupplierStatus Status, string? Reason);

public sealed record SourceRequest(string SupplierCode, string? Note, string? Reason);

/// <summary>Phase 6 endpoints: inventory, purchase orders, demand, item planning and MRP.</summary>
public static class MaterialsEndpoints
{
    public static void MapMaterialsEndpoints(this IEndpointRouteBuilder app)
    {
        MapInventory(app.MapGroup("/api/inventory").RequireAuthorization());
        MapProcurement(app.MapGroup("/api/procurement").RequireAuthorization());

        var planning = app.MapGroup("/api/planning").RequireAuthorization();
        planning.MapGet("/demand", (HttpContext http, bool? includeClosed, CancellationToken _) =>
            Read(http, Permissions.MaterialPlanRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var shipped = (await uow.Store<IInventoryLookup>().ListShippedByDemandAsync(http.RequestAborted)).ToDictionary(d => (d.DemandId, d.PartNumber), d => d.Shipped);
                var programs = uow.Store<IProgramsLookup>();
                var views = new List<DemandView>();
                foreach (var d in (await uow.Store<IDemandStore>().ListAsync(includeClosed ?? false, http.RequestAborted))
                    .Where(d => d.ProgramId is not { } p || principal.Programs.Contains(new ProgramId(p))))
                {
                    var got = shipped.GetValueOrDefault((d.DemandId, d.PartNumber));
                    long? deliveryId = d.SourceKey is { } key && key.StartsWith("delivery:", StringComparison.Ordinal) && long.TryParse(key.AsSpan(9), System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
                    string? note = null;
                    if (deliveryId is { } did && d.Status == DemandStatus.Open && await programs.FindDeliveryAsync(did, http.RequestAborted) is { } contract)
                    {
                        note = contract.Delivery.Cancelled ? "Cancelled on the contract"
                            : contract.Delivery.Quantity != d.Quantity || contract.Delivery.DueDate != d.NeedDate
                                ? $"Contract now says {contract.Delivery.Quantity:0.###} by {contract.Delivery.DueDate:yyyy-MM-dd}"
                                : null;
                    }

                    views.Add(new DemandView(d.DemandId, d.PartNumber, d.Quantity, got, Math.Max(0, d.Quantity - got), d.NeedDate, d.Priority, d.Kind, d.Customer, d.Reference, d.ProgramId, d.Status, d.CreatedBy, d.CreatedAt, deliveryId, note, d.SourceKey));
                }

                return views;
            }));
        planning.MapPost("/demand", (HttpContext http, DemandRequest b, DemandService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordAsync(new RecordDemand(b.PartNumber, b.Quantity, b.NeedDate, b.Priority ?? 5, b.Kind, b.Customer, b.Reference, b.ProgramId, b.SourceKey), c, ct)));
        planning.MapPost("/demand/{id:long}/change", (HttpContext http, long id, DemandChangeRequest b, DemandService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ChangeAsync(new ChangeDemand(id, b.Quantity, b.NeedDate, b.Priority), c, ct)));
        planning.MapPost("/demand/{id:long}/close", (HttpContext http, long id, ReasonRequest b, DemandService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CloseAsync(new CloseDemand(id, false), c, ct)));
        planning.MapPost("/demand/{id:long}/cancel", (HttpContext http, long id, ReasonRequest b, DemandService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CloseAsync(new CloseDemand(id, true), c, ct)));

        planning.MapGet("/mrp", async (HttpContext http, string? part, MaterialPlanningService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.EvaluateMrpAsync(principal, part, ct));
        });
        // V2.7: a requisition from a planned purchase, carrying the source its pegging leads back to.
        planning.MapPost("/mrp/requisitions", async (HttpContext http, PlannedRequisitionRequest b, MaterialPlanningService mrp, RequisitionService s, IKernelUnitOfWorkFactory uows, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null)
            {
                return NotRegistered();
            }

            var run = (await mrp.EvaluateMrpAsync(principal, null, ct)).Output;
            var planned = run.PlannedOrders.FirstOrDefault(p => p.Key == (b.PlannedOrder ?? string.Empty).Trim())
                ?? throw new CommandRejectedException("planned_order_not_found", $"The material plan has no planned order {b.PlannedOrder} now; run it again.");
            if (planned.MakeBuy != Forge.Engineering.Contracts.MakeBuy.Buy)
            {
                throw new CommandRejectedException("not_purchased", $"{planned.Key} is a production order recommendation; set up an order instead.");
            }

            IReadOnlyDictionary<long, DemandInfo> demands;
            await using (var uow = await uows.BeginSnapshotAsync(ct))
            {
                demands = (await uow.Store<IDemandStore>().ListAsync(true, ct)).ToDictionary(d => d.DemandId);
            }

            var source = PurchaseTraceService.SourceOf(planned, run.PlannedOrders, demands);
            var program = source.DemandId is { } d && demands.TryGetValue(d, out var demand) ? demand.ProgramId : null;
            return await Command(http, b.Reason, c => s.RaiseAsync(new RaiseRequisition(planned.PartNumber, planned.Quantity, planned.DueDate, source.Kind, source.Key, source.Label, source.DemandId, planned.Key,
                b.RequiredRevision, b.SupplierCode, program), c, ct));
        });
        planning.MapGet("/material", async (HttpContext http, MaterialPlanningService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.EvaluateMaterialAsync(principal, ct));
        });

        app.MapPost("/api/engineering/items/{part}/planning", (HttpContext http, string part, ItemPlanningRequest b, EngineeringService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetItemPlanningAsync(new SetItemPlanning(part, b.LeadTimeDays, b.SafetyStock, b.LotPolicy, b.OrderQuantity, b.OrderMultiple), c, ct)))
            .RequireAuthorization();
    }

    private static void MapInventory(RouteGroupBuilder api)
    {
        api.MapGet("/locations", (HttpContext http, CancellationToken _) =>
            ReadStore<IInventoryLookup>(http, Permissions.InventoryRead, async (l, ct) => await l.ListLocationsAsync(ct)));
        api.MapGet("/stock", (HttpContext http, string? part, CancellationToken _) =>
            ReadStore<IInventoryLookup>(http, Permissions.InventoryRead, async (l, ct) => await l.ListBalancesAsync(Part(part), ct)));
        // V3.1: every balance with its dimensions kept apart: quality status, location path, custody, nettable, point of use.
        api.MapGet("/positions", (HttpContext http, string? part, string? location, string? lot, CancellationToken _) =>
            ReadStore<IInventoryLookup>(http, Permissions.InventoryRead, async (l, ct) =>
            {
                var positions = StockPosition.From(await l.ListBalancesAsync(Part(part), ct), await l.ListLocationsAsync(ct));
                var at = string.IsNullOrWhiteSpace(location) ? null : location.Trim().ToUpperInvariant();
                var lotCode = string.IsNullOrWhiteSpace(lot) ? null : lot.Trim().ToUpperInvariant();
                return positions.Where(p => (at is null || p.LocationCode == at || p.LocationPath.Split(" / ").Contains(at)) && (lotCode is null || p.LotNumber == lotCode)).ToList();
            }));
        api.MapGet("/summary", async (HttpContext http, MaterialPlanningService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.StockSummaryAsync(principal, ct));
        });
        api.MapGet("/transactions/{id:long}/why", async (HttpContext http, long id, string part, PurchaseTraceService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.WhyTransactionAsync(principal, id, part, ct));
        });
        api.MapGet("/transactions", (HttpContext http, string? part, long? order, int? take, CancellationToken _) =>
            Read(http, Permissions.InventoryRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var hidden = await HiddenOrdersAsync(uow, principal, http.RequestAborted);
                return (await uow.Store<IInventoryLookup>().ListTransactionsAsync(Part(part), order, take ?? 200, http.RequestAborted))
                    .Where(t => t.OrderNumber is not { } n || !hidden.Contains(n))
                    .ToList();
            }));
        api.MapGet("/lots", (HttpContext http, string? part, CancellationToken _) =>
            ReadStore<IInventoryLookup>(http, Permissions.InventoryRead, async (l, ct) => await l.ListLotsAsync(Part(part), ct)));
        // V3.2: an instance's lineage (what it was split or merged from, what came of it, and its first instances).
        api.MapGet("/lots/{part}/{lot}/lineage", (HttpContext http, string part, string lot, CancellationToken _) =>
            ReadStore<IInventoryLookup>(http, Permissions.InventoryRead, async (l, ct) =>
            {
                var p = part.Trim().ToUpperInvariant();
                var code = lot.Trim().ToUpperInvariant();
                if (!(await l.ListLotsAsync(p, ct)).Any(x => x.LotNumber == code))
                {
                    throw new CommandRejectedException("lot_not_found", $"{p} lot {code} does not exist.");
                }

                return InstanceLineage.From(p, code, await l.ListInstanceLinksAsync(p, null, ct));
            }));
        api.MapGet("/merge-policy/{part}", (HttpContext http, string part, CancellationToken _) =>
            ReadStore<IInventoryLookup>(http, Permissions.InventoryRead, async (l, ct) =>
                await l.FindMergePolicyAsync(part.Trim().ToUpperInvariant(), ct) ?? new MergePolicy(part.Trim().ToUpperInvariant(), false, true, true, "No policy set: lots are never merged.", "system", DateTimeOffset.MinValue)));
        // V3.3: what became of material issued to an order, and the yield tolerances in force.
        api.MapGet("/orders/{order:long}/reconciliation", (HttpContext http, long order, CancellationToken _) =>
            Read(http, Permissions.InventoryRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var info = await uow.Store<IExecutionLookup>().FindOrderAsync(order, http.RequestAborted);
                if (info is null || (await HiddenOrdersAsync(uow, principal, http.RequestAborted)).Contains(order))
                {
                    throw new CommandRejectedException("order_not_found", $"Order {order} does not exist.");
                }

                var inventory = uow.Store<IInventoryLookup>();
                var tolerances = (await inventory.ListYieldTolerancesAsync(http.RequestAborted)).ToDictionary(t => t.PartNumber, t => t.Percent, StringComparer.Ordinal);
                var transformations = await inventory.ListTransformationsAsync(order, null, null, http.RequestAborted);
                var rows = MaterialReconciliation.For(order, await inventory.ListTransactionsAsync(null, order, 100_000, http.RequestAborted), transformations, p => tolerances.GetValueOrDefault(p));
                return new OrderReconciliationView(order, info.PartNumber, rows, transformations);
            }));
        // V3.4: material tasks, an order's material flow, cycle-count programs and counts due.
        api.MapGet("/tasks", (HttpContext http, bool? open, long? order, CancellationToken _) =>
            Read(http, Permissions.InventoryRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var hidden = await HiddenOrdersAsync(uow, principal, http.RequestAborted);
                return (await uow.Store<IInventoryLookup>().ListMaterialTasksAsync(open ?? true, order, http.RequestAborted))
                    .Where(t => t.OrderNumber is not { } n || !hidden.Contains(n))
                    .ToList();
            }));
        api.MapGet("/orders/{order:long}/material-flow", async (HttpContext http, long order, MaterialFlowService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.OrderAsync(principal, order, ct));
        });
        api.MapGet("/count-programs", (HttpContext http, CancellationToken _) =>
            ReadStore<IInventoryLookup>(http, Permissions.InventoryRead, async (l, ct) => await l.ListCountProgramsAsync(ct)));
        api.MapGet("/counts-due", async (HttpContext http, MaterialFlowService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.CountsDueAsync(principal, ct));
        });
        api.MapGet("/yield-tolerances", (HttpContext http, CancellationToken _) =>
            ReadStore<IInventoryLookup>(http, Permissions.InventoryRead, async (l, ct) => await l.ListYieldTolerancesAsync(ct)));
        api.MapGet("/requests", (HttpContext http, bool? pending, CancellationToken _) =>
            ReadStore<IInventoryLookup>(http, Permissions.InventoryRead, async (l, ct) => await l.ListRequestsAsync(pending ?? true, ct)));

        api.MapPost("/locations", (HttpContext http, LocationRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateLocationAsync(new CreateLocation(b.Code, b.SiteCode, b.Name, b.Kind, b.ParentCode, b.Level, b.Nettable, b.WorkCenterCode, b.MachineCode, b.CustodianSupplier), c, ct)));
        api.MapPost("/locations/{code}", (HttpContext http, string code, LocationChangeRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ChangeLocationAsync(new ChangeLocation(code, b.Name, b.ParentCode, b.Level, b.Nettable, b.WorkCenterCode, b.MachineCode, b.CustodianSupplier), c, ct)));
        api.MapPost("/locations/{code}/retire", (HttpContext http, string code, ReasonRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RetireLocationAsync(new RetireLocation(code), c, ct)));
        api.MapPost("/receipts", (HttpContext http, ReceiptRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReceiveAsync(new ReceiveStock(b.PartNumber, b.Quantity, b.LocationCode, b.LotNumber, b.SupplierLot, b.PoNumber, b.PoLine, b.Inspect, b.Revision, b.IdentityKind), c, ct)));
        api.MapPost("/issues", (HttpContext http, OrderMaterialRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.IssueToOrderAsync(new IssueToOrder(b.OrderNumber, b.PartNumber, b.Quantity, b.LocationCode, b.LotNumber, b.AlternateFor), c, ct)));
        api.MapPost("/returns", (HttpContext http, OrderMaterialRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReturnFromOrderAsync(new ReturnFromOrder(b.OrderNumber, b.PartNumber, b.Quantity, b.LocationCode, b.LotNumber, b.AlternateFor), c, ct)));
        api.MapPost("/transfers", (HttpContext http, TransferRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.TransferAsync(new TransferStock(b.PartNumber, b.Quantity, b.FromLocation, b.ToLocation, b.LotNumber, b.Status ?? StockStatus.Released), c, ct)));
        api.MapPost("/scrap", (HttpContext http, StockRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ScrapAsync(new ScrapStock(b.PartNumber, b.Quantity, b.LocationCode, b.LotNumber, b.Status ?? StockStatus.Released), c, ct)));
        api.MapPost("/holds", (HttpContext http, StockRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.HoldAsync(new HoldStock(b.PartNumber, b.Quantity, b.LocationCode, b.LotNumber), c, ct)));
        api.MapPost("/releases", (HttpContext http, StockRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReleaseAsync(new ReleaseStock(b.PartNumber, b.Quantity, b.LocationCode, b.LotNumber, b.Status ?? StockStatus.Hold), c, ct)));
        api.MapPost("/production-receipts", (HttpContext http, ProductionReceiptRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReceiveFromProductionAsync(new ReceiveFromProduction(b.OrderNumber, b.Quantity, b.LocationCode, b.LotNumber, b.Serials), c, ct)));
        api.MapPost("/adjustments", (HttpContext http, AdjustmentRequestBody b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RequestAdjustmentAsync(new RequestAdjustment(b.PartNumber, b.LocationCode, b.LotNumber, b.Status ?? StockStatus.Released, b.Change), c, ct)));
        api.MapPost("/counts", (HttpContext http, CountRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordCountAsync(new RecordCount(b.PartNumber, b.LocationCode, b.LotNumber, b.Status ?? StockStatus.Released, b.Counted), c, ct)));
        api.MapPost("/tasks", (HttpContext http, MaterialTaskRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RequestMaterialAsync(new RequestMaterial(b.Kind, b.OrderNumber, b.PartNumber, b.Quantity, b.ToLocation, b.LotNumber, b.FromLocation, b.KitCode), c, ct)));
        api.MapPost("/tasks/{id:long}/allocate", (HttpContext http, long id, AllocateRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AllocateMaterialAsync(new AllocateMaterial(id, b.FromLocation, b.LotNumber), c, ct)));
        api.MapPost("/tasks/{id:long}/pick", (HttpContext http, long id, PickRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.PickMaterialAsync(new PickMaterial(id, b.ScannedLocation, b.ScannedLot, b.Quantity), c, ct)));
        api.MapPost("/tasks/{id:long}/deliver", (HttpContext http, long id, DeliverRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DeliverMaterialAsync(new DeliverMaterial(id, b.ScannedLocation), c, ct)));
        api.MapPost("/tasks/{id:long}/cancel", (HttpContext http, long id, ReasonRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelMaterialTaskAsync(new CancelMaterialTask(id), c, ct)));
        api.MapPost("/count-programs", (HttpContext http, CountProgramRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateCountProgramAsync(new CreateCountProgram(b.ProgramCode, b.Name, b.LocationCode, b.PartNumber, b.FrequencyDays), c, ct)));
        api.MapPost("/count-programs/{code}/retire", (HttpContext http, string code, ReasonRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RetireCountProgramAsync(new RetireCountProgram(code), c, ct)));
        api.MapPost("/transformations", (HttpContext http, TransformationRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordTransformationAsync(new RecordTransformation(b.OrderNumber, b.OperationSequence, b.Entries ?? []), c, ct)));
        api.MapPost("/yield-tolerance/{part}", (HttpContext http, string part, YieldToleranceRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetYieldToleranceAsync(new SetYieldTolerance(part, b.Percent), c, ct)));
        api.MapPost("/splits", (HttpContext http, SplitRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SplitAsync(new SplitInstance(b.PartNumber, b.LotNumber, b.LocationCode, b.Status ?? StockStatus.Released, b.Results ?? []), c, ct)));
        api.MapPost("/merges", (HttpContext http, MergeRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.MergeAsync(new MergeInstances(b.PartNumber, b.Sources ?? [], b.ResultLot, b.LocationCode, b.Status ?? StockStatus.Released), c, ct)));
        api.MapPost("/merge-policy/{part}", (HttpContext http, string part, MergePolicyRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetMergePolicyAsync(new SetMergePolicy(part, b.Allowed, b.SameRevisionRequired, b.SameSupplierLotRequired), c, ct)));
        api.MapPost("/requests/{id:long}/approve", (HttpContext http, long id, ReasonRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveAdjustmentAsync(new ApproveAdjustment(id), c, ct)));
        api.MapPost("/requests/{id:long}/reject", (HttpContext http, long id, ReasonRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RejectAdjustmentAsync(new RejectAdjustment(id), c, ct)));
    }

    private static void MapProcurement(RouteGroupBuilder api)
    {
        api.MapGet("/purchase-orders", (HttpContext http, CancellationToken _) =>
            Read(http, Permissions.PurchasingRead, async _ =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var received = (await uow.Store<IInventoryLookup>().ListPurchaseReceiptsAsync(http.RequestAborted)).ToDictionary(r => (r.PoNumber, r.LineNumber), r => r.Received);
                return (await uow.Store<IProcurementLookup>().ListPurchaseOrdersAsync(http.RequestAborted))
                    .Select(po => new PurchaseOrderView(po.PoNumber, po.Supplier, po.Status, po.CreatedBy, po.CreatedAt, po.ApprovedBy, po.ApprovedAt, po.Lines.Select(l =>
                    {
                        var got = received.GetValueOrDefault((po.PoNumber, l.LineNumber));
                        var open = po.Status == PurchaseOrderStatus.Approved && l.Status == PurchaseLineStatus.Open ? Math.Max(0, l.Quantity - got) : 0;
                        return new PurchaseLineView(l.LineNumber, l.PartNumber, l.Quantity, l.UnitOfMeasure, l.PromisedDate, l.Status, got, open, l.RequisitionId, l.SourceKind, l.SourceKey, l.SourceLabel);
                    }).ToList(), po.SupplierCode))
                    .ToList();
            }));

        // V2.7: requisitions from every source, and why each purchase was made.
        api.MapGet("/requisitions", (HttpContext http, bool? open, CancellationToken _) =>
            Read(http, Permissions.RequisitionRaise, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                return (await uow.Store<IRequisitionLookup>().ListRequisitionsAsync(open != false, http.RequestAborted))
                    .Where(r => r.ProgramId is not { } p || principal.Programs.Contains(new ProgramId(p))).ToList();
            }));
        api.MapPost("/requisitions", (HttpContext http, RequisitionRequest b, RequisitionService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RaiseAsync(new RaiseRequisition(b.PartNumber, b.Quantity, b.NeedDate, b.SourceKind, b.SourceKey, b.SourceLabel, null, null, b.RequiredRevision, b.SupplierCode, b.ProgramId), c, ct)));
        api.MapPost("/requisitions/{id:long}/cancel", (HttpContext http, long id, ReasonRequest b, RequisitionService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelAsync(new CancelRequisition(id), c, ct)));
        api.MapGet("/purchase-orders/{po:long}/lines/{line:int}/why", async (HttpContext http, long po, int line, PurchaseTraceService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.WhyLineAsync(principal, po, line, ct));
        });

        // V2.5: the technical package each line was approved with. Documents the reader may not see stay counted, unnamed.
        api.MapGet("/purchase-orders/{po:long}/lines/{line:int}/package", (HttpContext http, long po, int line, CancellationToken _) =>
            Read(http, Permissions.PurchasingRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var package = await uow.Store<ITechnicalPackageLookup>().FindPackageAsync(po, line, http.RequestAborted)
                    ?? throw new CommandRejectedException("package_not_found", $"No technical package was recorded for PO {po} line {line} (it is recorded when the order is approved).");
                var documents = uow.Store<Forge.Engineering.Contracts.IDocumentLookup>();
                var shown = new List<PackageDocument>();
                foreach (var d in package.Documents)
                {
                    var document = await documents.FindDocumentAsync(d.DocumentNumber, http.RequestAborted);
                    shown.Add(document is not null && Forge.Engineering.Contracts.DocumentAccess.CanSee(principal, document)
                        ? d
                        : d with { DocumentNumber = Forge.Planning.DocumentationService.Restricted, Title = string.Empty, Revision = null, FileSha256 = null, FileName = null });
                }

                return package with { Documents = shown };
            }));

        api.MapGet("/suppliers", (HttpContext http, CancellationToken _) =>
            ReadStore<IProcurementLookup>(http, Permissions.PurchasingRead, async (l, ct) => await l.ListSuppliersAsync(ct)));
        api.MapPost("/suppliers", (HttpContext http, SupplierRequest b, SupplierService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateAsync(new CreateSupplier(b.Code, b.Name, b.Notes), c, ct)));
        api.MapPost("/suppliers/{code}", (HttpContext http, string code, SupplierChangeRequest b, SupplierService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ChangeAsync(new ChangeSupplier(code, b.Name, b.Notes), c, ct)));
        api.MapPost("/suppliers/{code}/status", (HttpContext http, string code, SupplierStatusRequest b, SupplierService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetStatusAsync(new SetSupplierStatus(code, b.Status), c, ct)));
        api.MapGet("/approved-sources", (HttpContext http, string? part, CancellationToken _) =>
            ReadStore<IProcurementLookup>(http, Permissions.PurchasingRead, async (l, ct) => await l.ListApprovedSourcesAsync(Part(part), ct)));
        api.MapPost("/items/{part}/approved-sources", (HttpContext http, string part, SourceRequest b, SupplierService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveSourceAsync(new ApproveSource(part, b.SupplierCode, b.Note), c, ct)));
        api.MapPost("/items/{part}/approved-sources/{code}/remove", (HttpContext http, string part, string code, ReasonRequest b, SupplierService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RemoveSourceAsync(new RemoveSource(part, code), c, ct)));

        api.MapPost("/purchase-orders", (HttpContext http, PurchaseOrderRequest b, PurchasingService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateAsync(new CreatePurchaseOrder(b.Supplier, (b.Lines ?? []).Select(l => new NewPurchaseLine(l.PartNumber, l.Quantity, l.PromisedDate, l.RequisitionId)).ToList()), c, ct)));
        api.MapPost("/purchase-orders/{po:long}/lines", (HttpContext http, long po, PurchaseLineAddRequest b, PurchasingService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddLineAsync(new AddPurchaseLine(po, b.PartNumber, b.Quantity, b.PromisedDate, b.RequisitionId), c, ct)));
        api.MapPost("/purchase-orders/{po:long}/approve", (HttpContext http, long po, ReasonRequest b, PurchasingService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveAsync(new ApprovePurchaseOrder(po), c, ct)));
        api.MapPost("/purchase-orders/{po:long}/lines/{line:int}/date", (HttpContext http, long po, int line, PromisedDateRequest b, PurchasingService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ChangePromisedDateAsync(new ChangePromisedDate(po, line, b.PromisedDate), c, ct)));
        api.MapPost("/purchase-orders/{po:long}/lines/{line:int}/cancel", (HttpContext http, long po, int line, ReasonRequest b, PurchasingService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelLineAsync(new CancelPurchaseLine(po, line), c, ct)));
        api.MapPost("/purchase-orders/{po:long}/close", (HttpContext http, long po, ReasonRequest b, PurchasingService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CloseAsync(new ClosePurchaseOrder(po), c, ct)));
        api.MapPost("/purchase-orders/{po:long}/cancel", (HttpContext http, long po, ReasonRequest b, PurchasingService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelAsync(new CancelPurchaseOrder(po), c, ct)));
    }

    private static string? Part(string? part) => string.IsNullOrWhiteSpace(part) ? null : part.Trim().ToUpperInvariant();

    private static async Task<HashSet<long>> HiddenOrdersAsync(IKernelUnitOfWork uow, ForgePrincipal principal, CancellationToken ct) =>
        (await uow.Store<IExecutionLookup>().ListOrdersAsync(ct))
            .Where(o => o.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p)))
            .Select(o => o.OrderNumber)
            .ToHashSet();
}
