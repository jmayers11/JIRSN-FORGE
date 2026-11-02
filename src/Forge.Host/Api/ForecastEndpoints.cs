using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Planning;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record SimulateHistoryRequest(int? Months, int? Seed, string? Reason);

public sealed record HistoryImportRow(string PartNumber, DateOnly Month, decimal Quantity);

public sealed record HistoryImportRequest(HistoryImportRow[]? Rows, string? Reason);

public sealed record ForecastPolicyRequest(int Horizon, int Holdout, int IntervalPercent, bool UseSimulated, string? Reason);

public sealed record ForecastApproveRequest(long RunId, string PartNumber, string? Reason);

public sealed record AllocationApproveRequest(long OrderNumber, string PartNumber, decimal Quantity, string? AlternateFor, string? Reason);

public sealed record AllocationPolicyRequest(AllocationRule[]? Rules, string? Reason);

/// <summary>The latest forecast run with its parts, for the Forecast page.</summary>
public sealed record ForecastView(ForecastRun? Run, IReadOnlyList<ForecastLine> Lines, ForecastPolicy Policy);

/// <summary>Demand forecasting and the V5 allocation optimizer (owner decisions 2026-11-02). Both only propose; people approve.</summary>
internal static class ForecastEndpoints
{
    public static void MapForecastEndpoints(this IEndpointRouteBuilder app)
    {
        var f = app.MapGroup("/api/forecasting").RequireAuthorization();
        f.MapGet("/history", (HttpContext http, TimeProvider time, CancellationToken _) =>
            Read(http, Permissions.MaterialPlanRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var policy = await uow.Store<IForecastStore>().ForecastPolicyAsync(http.RequestAborted);
                return await ForecastService.HistoryAsync(uow, time.GetUtcNow(), policy.UseSimulated, http.RequestAborted);
            }));
        f.MapGet("/latest", (HttpContext http, CancellationToken _) =>
            ReadStore<IForecastStore>(http, Permissions.MaterialPlanRead, async (s, ct) =>
            {
                var run = await s.LatestRunAsync(ct);
                return new ForecastView(run, run is null ? [] : await s.ListLinesAsync(run.RunId, ct), await s.ForecastPolicyAsync(ct));
            }));
        f.MapPost("/simulate", (HttpContext http, SimulateHistoryRequest b, ForecastService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.GenerateSimulatedAsync(new GenerateSimulatedHistory(b.Months ?? 24, b.Seed ?? 2026), c, ct)));
        f.MapPost("/simulated/clear", (HttpContext http, ReasonRequest b, ForecastService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ClearSimulatedAsync(new ClearSimulatedHistory(), c, ct)));
        f.MapPost("/import", (HttpContext http, HistoryImportRequest b, ForecastService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ImportAsync(new ImportDemandHistory((b.Rows ?? []).Select(r => (r.PartNumber, r.Month, r.Quantity)).ToList()), c, ct)));
        f.MapPost("/policy", (HttpContext http, ForecastPolicyRequest b, ForecastService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetPolicyAsync(new SetForecastPolicy(b.Horizon, b.Holdout, b.IntervalPercent, b.UseSimulated), c, ct)));
        f.MapPost("/run", (HttpContext http, ReasonRequest b, ForecastService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RunAsync(new RunForecast(), c, ct)));
        f.MapPost("/approve", (HttpContext http, ForecastApproveRequest b, ForecastService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveAsync(new ApproveForecast(b.RunId, b.PartNumber), c, ct)));

        var a = app.MapGroup("/api/allocation").RequireAuthorization();
        a.MapGet("/proposals", (HttpContext http, TimeProvider time, CancellationToken _) =>
            Read(http, Permissions.MaterialPlanRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                return await AllocationOptimizer.ProposeAsync(uow, time.GetUtcNow(), http.RequestAborted);
            }));
        a.MapGet("/policy", (HttpContext http, CancellationToken _) =>
            ReadStore<IAllocationLookup>(http, Permissions.MaterialPlanRead, async (s, ct) => await s.AllocationPolicyAsync(ct)));
        a.MapPost("/policy", (HttpContext http, AllocationPolicyRequest b, AllocationService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetPolicyAsync(new SetAllocationPolicy(b.Rules ?? []), c, ct)));
        a.MapPost("/approve", (HttpContext http, AllocationApproveRequest b, AllocationService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveAsync(new ApproveAllocation(b.OrderNumber, b.PartNumber, b.Quantity, b.AlternateFor), c, ct)));
    }
}
