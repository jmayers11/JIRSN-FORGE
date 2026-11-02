using Forge.Execution.Contracts;
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

public sealed record ForecastAnalogRequest(string PartNumber, string? LikePart, decimal? Scale, DateOnly? LaunchMonth, string? Reason);

public sealed record ForecastFamilyRequest(string Code, string? Name, string? DriverPart, ForecastFamilyMember[]? Members, string? Reason);

public sealed record ForecastAdjustmentRequest(string PartNumber, DateOnly FromMonth, DateOnly? ToMonth, ForecastAdjustmentKind Kind, decimal Amount, string? Reason);

public sealed record AllocationApproveRequest(long OrderNumber, string PartNumber, decimal Quantity, string? AlternateFor, string? Reason, long? WipOrderNumber = null, string? LotNumber = null);

public sealed record AllocationPolicyRequest(AllocationRule[]? Rules, string? Reason);

public sealed record LotDimensionRequest(string PartNumber, string LotNumber, decimal? Length, decimal? Width, decimal? Thickness, SizeUnit Unit, string? Reason);

public sealed record PieceSizeRequest(string ParentPart, string ComponentPart, decimal? Length, decimal? Width, decimal? Thickness, SizeUnit Unit, string? Reason);

/// <summary>The latest forecast run with its parts, for the Forecast page.</summary>
public sealed record ForecastView(ForecastRun? Run, IReadOnlyList<ForecastLine> Lines, ForecastPolicy Policy);

/// <summary>Leftover pieces and work in progress the optimizer can use, for the Allocation tab.</summary>
public sealed record AllocationSources(IReadOnlyList<LotDimension> Measured, IReadOnlyList<PieceSizeRule> PieceSizes, IReadOnlyList<LotEarmark> SetAside, IReadOnlyList<WipAllocation> Promised,
    IReadOnlyList<WipSupply> WorkInProgress);

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
        f.MapGet("/setup", (HttpContext http, CancellationToken _) =>
            ReadStore<IForecastStore>(http, Permissions.MaterialPlanRead, async (s, ct) =>
                new ForecastSetup(await s.ListAnalogsAsync(ct), await s.ListFamiliesAsync(ct), await s.ListAdjustmentsAsync(true, ct))));
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
        f.MapPost("/analogs", (HttpContext http, ForecastAnalogRequest b, ForecastService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetAnalogAsync(new SetForecastAnalog(b.PartNumber, b.LikePart, b.Scale, b.LaunchMonth), c, ct)));
        f.MapPost("/families", (HttpContext http, ForecastFamilyRequest b, ForecastService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetFamilyAsync(new SetForecastFamily(b.Code, b.Name, b.DriverPart, b.Members ?? []), c, ct)));
        f.MapPost("/adjustments", (HttpContext http, ForecastAdjustmentRequest b, ForecastService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddAdjustmentAsync(new AddForecastAdjustment(b.PartNumber, b.FromMonth, b.ToMonth, b.Kind, b.Amount), c, ct)));
        f.MapPost("/adjustments/{id:long}/cancel", (HttpContext http, long id, ReasonRequest b, ForecastService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelAdjustmentAsync(new CancelForecastAdjustment(id), c, ct)));

        var a = app.MapGroup("/api/allocation").RequireAuthorization();
        a.MapGet("/proposals", (HttpContext http, TimeProvider time, CancellationToken _) =>
            Read(http, Permissions.MaterialPlanRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                return await AllocationOptimizer.ProposeAsync(uow, time.GetUtcNow(), http.RequestServices.GetService<IWipDemandSource>(), http.RequestAborted);
            }));
        a.MapGet("/sources", (HttpContext http, TimeProvider time, CancellationToken _) =>
            Read(http, Permissions.MaterialPlanRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var ct = http.RequestAborted;
                var lookup = uow.Store<IAllocationLookup>();
                var demand = http.RequestServices.GetService<IWipDemandSource>();
                var parts = (await uow.Store<IExecutionLookup>().ListOrdersAsync(ct)).Where(o => o.Status is OrderStatus.FirmPlanned or OrderStatus.Released or OrderStatus.Held)
                    .Select(o => o.PartNumber).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                var wip = new List<WipSupply>();
                foreach (var part in parts) wip.AddRange(await AllocationOptimizer.WipSupplyAsync(uow, part, time.GetUtcNow(), demand, ct));
                return new AllocationSources(await lookup.ListLotDimensionsAsync(null, ct), await lookup.ListPieceSizeRulesAsync(ct), (await lookup.ListLotEarmarksAsync(true, ct)).Where(e => e.Outstanding > 0).ToList(),
                    await AllocationOptimizer.OpenWipAllocationsAsync(uow, ct), wip);
            }));
        a.MapGet("/policy", (HttpContext http, CancellationToken _) =>
            ReadStore<IAllocationLookup>(http, Permissions.MaterialPlanRead, async (s, ct) => await s.AllocationPolicyAsync(ct)));
        a.MapPost("/policy", (HttpContext http, AllocationPolicyRequest b, AllocationService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetPolicyAsync(new SetAllocationPolicy(b.Rules ?? []), c, ct)));
        a.MapPost("/approve", (HttpContext http, AllocationApproveRequest b, AllocationService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveAsync(new ApproveAllocation(b.OrderNumber, b.PartNumber, b.Quantity, b.AlternateFor, b.WipOrderNumber, b.LotNumber), c, ct)));
        a.MapPost("/promises/{id:long}/release", (HttpContext http, long id, ReasonRequest b, AllocationService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReleaseWipAsync(new ReleaseWipAllocation(id), c, ct)));
        a.MapPost("/set-asides/{id:long}/release", (HttpContext http, long id, ReasonRequest b, AllocationService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReleaseEarmarkAsync(new ReleaseLotEarmark(id), c, ct)));
        a.MapPost("/lot-dimensions", (HttpContext http, LotDimensionRequest b, RemnantService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordDimensionAsync(new RecordLotDimension(b.PartNumber, b.LotNumber, b.Length, b.Width, b.Thickness, b.Unit), c, ct)));
        a.MapPost("/piece-sizes", (HttpContext http, PieceSizeRequest b, RemnantService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetRuleAsync(new SetPieceSizeRule(b.ParentPart, b.ComponentPart, b.Length, b.Width, b.Thickness, b.Unit), c, ct)));
    }
}
