using Forge.Execution.Contracts;
using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ScrapRequestBody(
    string PartNumber,
    string LocationCode,
    string? LotNumber,
    StockStatus? Status,
    decimal Quantity,
    ScrapReasonCode ReasonCode,
    string? Reason,
    string? Condition = null,
    string? Notes = null,
    long? LinkedOrderNumber = null,
    long? LinkedPoNumber = null,
    long? LinkedWorkOrderId = null,
    string? LinkedNcr = null,
    string? LinkedSupplier = null,
    bool ExecuteDirectly = false);

public sealed record ScrapPreviewBody(string PartNumber, string LocationCode, string? LotNumber, StockStatus? Status, decimal Quantity);

public sealed record ScrapDecisionBody(string? Reason, string? Note = null);

public sealed record ScrapDispositionBody(bool Scrap, string? Reason, string? Note = null);

public sealed record ScrapExecuteBody(string? Reason, bool ReleaseAllocations = false);

public sealed record ScrapReversalBody(decimal Quantity, string? Reason);

public sealed record ScrapPolicyBody(string RoleName, ScrapReasonCode? ReasonCode, decimal? MaxQuantity, decimal? MaxValue, string? Reason);

public sealed record ScrapPolicyDecisionBody(bool Approve, string? Reason);

/// <summary>Planner-initiated inventory scrap with controlled disposition (Sprint 10 §20).</summary>
public static class ScrapEndpoints
{
    public static void MapScrap(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup("/api/inventory").RequireAuthorization();

        api.MapGet("/scrap-requests", (HttpContext http, bool? open, string? part, CancellationToken _) =>
            Read(http, Permissions.InventoryRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var hidden = await HiddenOrdersAsync(uow, principal, http.RequestAborted);
                return (await uow.Store<IScrapLookup>().ListScrapRequestsAsync(open ?? false, string.IsNullOrWhiteSpace(part) ? null : part.Trim().ToUpperInvariant(), http.RequestAborted))
                    .Where(r => r.LinkedOrderNumber is not { } n || !hidden.Contains(n))
                    .OrderByDescending(r => r.RequestId)
                    .Take(500)
                    .ToList();
            }));
        api.MapGet("/scrap-policies", (HttpContext http, CancellationToken _) =>
            ReadStore<IScrapLookup>(http, Permissions.InventoryRead, async (l, ct) => await l.ListScrapPoliciesAsync(ct)));

        // What could be scrapped, what it is worth (when a purchase price is known) and which visible released orders it would affect.
        api.MapPost("/scrap-preview", (HttpContext http, ScrapPreviewBody b, MaterialScrapImpactPreviewService impact, CancellationToken ct) =>
            Read(http, Permissions.InventoryRead, async principal =>
            {
                var part = BusinessCode.Require(b.PartNumber, "Part");
                var location = BusinessCode.Require(b.LocationCode, "Location");
                var lot = string.IsNullOrWhiteSpace(b.LotNumber) ? null : b.LotNumber.Trim().ToUpperInvariant();
                var status = b.Status ?? StockStatus.Released;
                ScrapEligibility eligibility;
                (decimal? UnitValue, string? Currency) price;
                IReadOnlyList<MaterialTask> allocated;
                await using (var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(ct))
                {
                    eligibility = await InventoryService.ScrapEligibilityAsync(uow, part, location, lot, status, null, ct);
                    price = await InventoryService.LastPurchasePriceAsync(uow, part, ct);
                    var hidden = await HiddenOrdersAsync(uow, principal, ct);
                    allocated = (await uow.Store<IInventoryLookup>().ListMaterialTasksAsync(true, null, ct))
                        .Where(t => eligibility.AllocatedTaskIds.Contains(t.TaskId) && (t.OrderNumber is not { } n || !hidden.Contains(n)))
                        .ToList();
                }

                MaterialScrapImpactPreview? planning = null;
                if (b.Quantity > 0 && principal.HasPermission(Permissions.ScheduleRead))
                    planning = await impact.PreviewAsync(principal, part, location, status, b.Quantity, ct);
                return new
                {
                    eligibility,
                    estimatedUnitValue = price.UnitValue,
                    currency = price.Currency,
                    estimatedValue = price.UnitValue is { } unit && b.Quantity > 0 ? decimal.Round(unit * b.Quantity, 2, MidpointRounding.AwayFromZero) : (decimal?)null,
                    valueBasis = price.UnitValue is null
                        ? "No purchase price is recorded for this part; Forge has no costing module."
                        : "Estimated from the part's last recorded purchase price; Forge has no costing module.",
                    affectedTasks = allocated.Select(t => new { t.TaskId, t.OrderNumber, t.Quantity, t.ToLocation, status = t.Status.ToString() }),
                    planning,
                    planningRestricted = planning is null && b.Quantity > 0,
                };
            }));

        api.MapPost("/scrap-requests", (HttpContext http, ScrapRequestBody b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RequestScrapAsync(new RequestScrap(b.PartNumber, b.LocationCode, b.LotNumber, b.Status ?? StockStatus.Released, b.Quantity,
                b.ReasonCode, b.Condition, b.Notes, b.LinkedOrderNumber, b.LinkedPoNumber, b.LinkedWorkOrderId, b.LinkedNcr, b.LinkedSupplier, b.ExecuteDirectly), c, ct)));
        api.MapPost("/scrap-requests/{id:long}/approve", (HttpContext http, long id, ScrapDecisionBody b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveScrapAsync(new ApproveScrap(id, b.Note), c, ct)));
        api.MapPost("/scrap-requests/{id:long}/reject", (HttpContext http, long id, ScrapDecisionBody b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RejectScrapAsync(new RejectScrap(id, b.Note), c, ct)));
        api.MapPost("/scrap-requests/{id:long}/disposition", (HttpContext http, long id, ScrapDispositionBody b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DisposeScrapAsync(new DisposeScrap(id, b.Scrap, b.Note), c, ct)));
        api.MapPost("/scrap-requests/{id:long}/cancel", (HttpContext http, long id, ScrapDecisionBody b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelScrapAsync(new CancelScrap(id), c, ct)));
        api.MapPost("/scrap-requests/{id:long}/execute", (HttpContext http, long id, ScrapExecuteBody b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ExecuteScrapAsync(new ExecuteScrap(id, b.ReleaseAllocations), c, ct)));
        api.MapPost("/scrap-requests/{id:long}/reversal", (HttpContext http, long id, ScrapReversalBody b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RequestScrapReversalAsync(new RequestScrapReversal(id, b.Quantity), c, ct)));

        api.MapPost("/scrap-policies", (HttpContext http, ScrapPolicyBody b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ProposeScrapPolicyAsync(new ProposeScrapPolicy(b.RoleName, b.ReasonCode, b.MaxQuantity, b.MaxValue), c, ct)));
        api.MapPost("/scrap-policies/{id:long}/decide", (HttpContext http, long id, ScrapPolicyDecisionBody b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DecideScrapPolicyAsync(new DecideScrapPolicy(id, b.Approve), c, ct)));
        api.MapPost("/scrap-policies/{id:long}/retire", (HttpContext http, long id, ScrapDecisionBody b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RetireScrapPolicyAsync(new RetireScrapPolicy(id), c, ct)));
    }

    /// <summary>Orders in programs the reader is not a member of; their scrap requests and tasks are not shown.</summary>
    private static async Task<HashSet<long>> HiddenOrdersAsync(IKernelUnitOfWork uow, ForgePrincipal principal, CancellationToken ct) =>
        (await uow.Store<IExecutionLookup>().ListOrdersAsync(ct))
            .Where(o => o.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program)))
            .Select(o => o.OrderNumber)
            .ToHashSet();
}
