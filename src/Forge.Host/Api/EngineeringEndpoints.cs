using Forge.Engineering;
using Forge.Engineering.Contracts;
using Forge.Execution;
using Forge.Execution.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ItemRequest(string PartNumber, string Description, string UnitOfMeasure, MakeBuy MakeBuy, int LeadTimeDays, bool LotTracked, bool Serialized, string? Reason, ItemClass? Class = null);

public sealed record ClassifyRequest(ItemClass Class, string? Reason);

public sealed record ManufacturerPartRequest(string Manufacturer, string Mpn, string? ManufacturerRevision, ManufacturerLifecycle? Lifecycle, string? Datasheet, string? Note, string? Reason);

public sealed record ManufacturerPartRef(string Manufacturer, string Mpn, string? Reason);

public sealed record LifecycleRequest(string Manufacturer, string Mpn, ManufacturerLifecycle Lifecycle, string? Reason);

public sealed record RevisionRequest(string Revision, string? Reason);

public sealed record BomLineRequest(int LineNumber, string ComponentPartNumber, decimal QuantityPer, decimal ScrapPercent, string? Reason);

public sealed record OperationRequest(
    int Sequence,
    string Description,
    string WorkCenterCode,
    string ActivityCode,
    string[]? EligibleMachines,
    string[]? RequiredQualifications,
    decimal SetupMinutes,
    decimal RunMinutesPerUnit,
    int CrewSize,
    string? Reason,
    string? SetupFamily = null,
    decimal? FamilySetupMinutes = null);

public sealed record ReleaseRevisionRequest(DateTimeOffset? EffectiveFrom, string? Reason);

public sealed record OrderRequest(string PartNumber, decimal Quantity, DateOnly DueDate, int Priority, Guid? ProgramId, string? Reason);

public sealed record OrderPriorityRequest(int Priority, int ExpectedPriority, DateTimeOffset PreviewAsOf, string PreviewFingerprint, string? Reason);

public sealed record OrderCommitmentRequest(long? ContractId, string? Clin, string? Reason);

/// <summary>Phase 3 endpoints: engineering definitions, production orders and order readiness.</summary>
public static class EngineeringEndpoints
{
    public static void MapEngineeringEndpoints(this IEndpointRouteBuilder app)
    {
        var eng = app.MapGroup("/api/engineering").RequireAuthorization();

        eng.MapGet("/items", (HttpContext http, CancellationToken _) =>
            ReadStore<IEngineeringLookup>(http, Permissions.EngineeringRead, async (l, ct) => await l.ListItemsAsync(ct)));
        eng.MapGet("/items/{part}/revisions", (HttpContext http, string part, CancellationToken _) =>
            ReadStore<IEngineeringLookup>(http, Permissions.EngineeringRead, async (l, ct) => await l.ListRevisionsAsync(part.ToUpperInvariant(), ct)));
        eng.MapGet("/items/{part}/revisions/{rev}", (HttpContext http, string part, string rev, CancellationToken _) =>
            ReadStore<IEngineeringLookup>(http, Permissions.EngineeringRead, async (l, ct) => await l.GetRevisionDetailAsync(part.ToUpperInvariant(), rev.ToUpperInvariant(), ct)));

        eng.MapPost("/items", (HttpContext http, ItemRequest b, EngineeringService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateItemAsync(new CreateItem(b.PartNumber, b.Description, b.UnitOfMeasure, b.MakeBuy, b.LeadTimeDays, b.LotTracked, b.Serialized, b.Class), c, ct)));
        eng.MapPost("/items/{part}/classification", (HttpContext http, string part, ClassifyRequest b, EngineeringService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ClassifyItemAsync(new ClassifyItem(part, b.Class), c, ct)));
        eng.MapGet("/manufacturer-parts", (HttpContext http, string? part, CancellationToken _) =>
            ReadStore<IEngineeringLookup>(http, Permissions.EngineeringRead, async (l, ct) => await l.ListManufacturerPartsAsync(string.IsNullOrWhiteSpace(part) ? null : part.Trim().ToUpperInvariant(), ct)));
        eng.MapPost("/items/{part}/manufacturer-parts", (HttpContext http, string part, ManufacturerPartRequest b, EngineeringService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ProposeManufacturerPartAsync(new ProposeManufacturerPart(part, b.Manufacturer, b.Mpn, b.ManufacturerRevision, b.Lifecycle ?? ManufacturerLifecycle.Unknown, b.Datasheet, b.Note), c, ct)));
        eng.MapPost("/items/{part}/manufacturer-parts/approve", (HttpContext http, string part, ManufacturerPartRef b, EngineeringService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveManufacturerPartAsync(new ApproveManufacturerPart(part, b.Manufacturer, b.Mpn), c, ct)));
        eng.MapPost("/items/{part}/manufacturer-parts/withdraw", (HttpContext http, string part, ManufacturerPartRef b, EngineeringService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.WithdrawManufacturerPartAsync(new WithdrawManufacturerPart(part, b.Manufacturer, b.Mpn), c, ct)));
        eng.MapPost("/items/{part}/manufacturer-parts/lifecycle", (HttpContext http, string part, LifecycleRequest b, EngineeringService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetManufacturerLifecycleAsync(new SetManufacturerLifecycle(part, b.Manufacturer, b.Mpn, b.Lifecycle), c, ct)));
        eng.MapPost("/items/{part}/revisions", (HttpContext http, string part, RevisionRequest b, EngineeringService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateRevisionAsync(new CreateRevision(part, b.Revision), c, ct)));
        eng.MapPost("/items/{part}/revisions/{rev}/bom", (HttpContext http, string part, string rev, BomLineRequest b, EngineeringService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddBomLineAsync(new AddBomLine(part, rev, b.LineNumber, b.ComponentPartNumber, b.QuantityPer, b.ScrapPercent), c, ct)));
        eng.MapPost("/items/{part}/revisions/{rev}/bom/{line:int}/remove", (HttpContext http, string part, string rev, int line, ReasonRequest b, EngineeringService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RemoveBomLineAsync(new RemoveBomLine(part, rev, line), c, ct)));
        eng.MapPost("/items/{part}/revisions/{rev}/routing", (HttpContext http, string part, string rev, OperationRequest b, EngineeringService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddRoutingOperationAsync(new AddRoutingOperation(part, rev, b.Sequence, b.Description, b.WorkCenterCode, b.ActivityCode, b.EligibleMachines, b.RequiredQualifications, b.SetupMinutes, b.RunMinutesPerUnit, b.CrewSize, b.SetupFamily, b.FamilySetupMinutes), c, ct)));
        eng.MapPost("/items/{part}/revisions/{rev}/routing/{sequence:int}/remove", (HttpContext http, string part, string rev, int sequence, ReasonRequest b, EngineeringService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RemoveRoutingOperationAsync(new RemoveRoutingOperation(part, rev, sequence), c, ct)));
        eng.MapPost("/items/{part}/revisions/{rev}/release", (HttpContext http, string part, string rev, ReleaseRevisionRequest b, EngineeringService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReleaseRevisionAsync(new ReleaseRevision(part, rev, b.EffectiveFrom), c, ct)));
        eng.MapPost("/items/{part}/revisions/{rev}/withdraw", (HttpContext http, string part, string rev, ReasonRequest b, EngineeringService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.WithdrawRevisionAsync(new WithdrawRevision(part, rev), c, ct)));

        var orders = app.MapGroup("/api/orders").RequireAuthorization();

        // Program-segregated orders are listed only for people with access to that program.
        orders.MapGet("/", (HttpContext http, CancellationToken _) =>
            Read(http, Permissions.OrdersRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<Forge.Kernel.Access.IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var all = await uow.Store<IExecutionLookup>().ListOrdersAsync(http.RequestAborted);
                return all.Where(o => Visible(principal, o)).ToList();
            }));

        orders.MapGet("/{orderNumber:long}", (HttpContext http, long orderNumber, CancellationToken _) =>
            Read(http, Permissions.OrdersRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<Forge.Kernel.Access.IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, http.RequestAborted);
                return order is not null && Visible(principal, order)
                    ? order
                    : throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
            }));

        orders.MapPost("/", (HttpContext http, OrderRequest b, ProductionOrderService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateAsync(new CreateProductionOrder(b.PartNumber, b.Quantity, b.DueDate, b.Priority, b.ProgramId), c, ct)));
        orders.MapPost("/{orderNumber:long}/contract-commitment", (HttpContext http, long orderNumber, OrderCommitmentRequest body, OrderCommitmentService service, CancellationToken ct) =>
            Command(http, body.Reason, context => service.AssignAsync(new AssignOrderCommitment(orderNumber, body.ContractId, body.Clin), context, ct)));
        orders.MapPost("/{orderNumber:long}/priority-preview", (HttpContext http, long orderNumber, OrderPriorityPreviewRequest b, PriorityGovernanceService s, CancellationToken ct) =>
            Read(http, Permissions.OrdersRead, async principal => await s.PreviewAsync(orderNumber, b.Priority, principal, ct)));
        orders.MapPost("/{orderNumber:long}/priority", (HttpContext http, long orderNumber, OrderPriorityRequest b, PriorityGovernanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ChangeAsync(new ChangeOrderPriority(orderNumber, b.Priority, b.ExpectedPriority, b.PreviewAsOf, b.PreviewFingerprint), c, ct)));
        orders.MapPost("/{orderNumber:long}/firm", (HttpContext http, long orderNumber, ReasonRequest b, ProductionOrderService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.FirmAsync(new FirmProductionOrder(orderNumber), c, ct)));
        orders.MapPost("/{orderNumber:long}/release", (HttpContext http, long orderNumber, ReasonRequest b, ProductionOrderService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReleaseAsync(new ReleaseProductionOrder(orderNumber), c, ct)));
        orders.MapPost("/{orderNumber:long}/hold", (HttpContext http, long orderNumber, ReasonRequest b, ProductionOrderService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.HoldAsync(new HoldProductionOrder(orderNumber), c, ct)));
        orders.MapPost("/{orderNumber:long}/resume", (HttpContext http, long orderNumber, ReasonRequest b, ProductionOrderService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ResumeAsync(new ResumeProductionOrder(orderNumber), c, ct)));
        orders.MapPost("/{orderNumber:long}/cancel", (HttpContext http, long orderNumber, ReasonRequest b, ProductionOrderService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelAsync(new CancelProductionOrder(orderNumber), c, ct)));

        orders.MapGet("/{orderNumber:long}/readiness", async (HttpContext http, long orderNumber, ReadinessService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.EvaluateOrderAsync(principal, orderNumber, ct));
        });
    }

    private static bool Visible(ForgePrincipal principal, OrderInfo order) =>
        order.ProgramId is not { } program || principal.Programs.Contains(new ProgramId(program));
}
