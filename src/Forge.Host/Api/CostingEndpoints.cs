using Forge.Host.Costing;
using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Security;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record CostingPolicyRequest(CostingMethod Method, string? Currency, decimal? LaborRatePerHour, decimal? OverheadRatePerHour, string? Reason);

public sealed record StandardCostRequest(string PartNumber, decimal Material, decimal Labor, decimal Overhead, string? Reason);

/// <summary>Sprint 10 WW-S10-24: the costing policy, standard costs and the cost reconciliation of a split NCR.</summary>
internal static class CostingEndpoints
{
    public static void MapCostingEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/costing").RequireAuthorization();
        api.MapGet("/policies", (HttpContext http, CancellationToken _) =>
            ReadStore<ICostingLookup>(http, Permissions.CostingRead, async (store, ct) => await store.ListPoliciesAsync(ct)));
        api.MapPost("/policies", (HttpContext http, CostingPolicyRequest b, CostingService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetPolicyAsync(new SetCostingPolicy(b.Method, b.Currency, b.LaborRatePerHour, b.OverheadRatePerHour), c, ct)));
        api.MapGet("/standard-costs", (HttpContext http, string? part, CancellationToken _) =>
            ReadStore<ICostingLookup>(http, Permissions.CostingRead, async (store, ct) => await store.ListStandardCostsAsync(string.IsNullOrWhiteSpace(part) ? null : part.Trim().ToUpperInvariant(), ct)));
        api.MapPost("/standard-costs", (HttpContext http, StandardCostRequest b, CostingService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetStandardCostAsync(new SetStandardCost(b.PartNumber, b.Material, b.Labor, b.Overhead), c, ct)));
        api.MapGet("/ncrs/{ncrId:long}/reconciliation", (HttpContext http, long ncrId, DispositionCostService s, CancellationToken _) =>
            Read(http, Permissions.CostingRead, async principal => await s.ReconcileAsync(principal, ncrId, http.RequestAborted)));
    }
}
