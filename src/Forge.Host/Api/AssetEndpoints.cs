using Forge.Engineering;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Maintenance;
using Forge.Maintenance.Contracts;
using Forge.Planning;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record AssetRequest(
    string AssetCode,
    string Name,
    AssetType Type,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? Location,
    string? WorkCenterCode,
    string? MachineCode,
    AssetCriticality Criticality,
    int? ExpectedLifeYears,
    decimal? ReplacementCost,
    string? Reason);

public sealed record AssetChangeRequest(string Name, string? Manufacturer, string? Model, string? SerialNumber, string? Location, AssetCriticality Criticality, int? ExpectedLifeYears, decimal? ReplacementCost, string? Reason);

public sealed record AssetStatusRequest(AssetStatus Status, DateOnly? CommissionedOn, string? Reason);

public sealed record DecommissionDecisionRequest(bool Approve, string? Reason);

public sealed record ComponentRequest(string PartNumber, string? Revision, string Position, decimal Quantity, long? WorkOrderId, string? Reason);

public sealed record ComponentRemovalRequest(long? WorkOrderId, string? Reason);

public sealed record MroPolicyRequest(decimal MinQuantity, decimal ReorderPoint, decimal MaxQuantity, bool CriticalSpare, int? ShelfLifeDays, string? Reason);

public sealed record ObsolescenceRequest(string PartNumber, ObsolescenceStatus Status, DateOnly? LastTimeBuy, DateOnly? EndOfLife, DateOnly? EndOfSupport, string Evidence, string? ReplacementPart, string? Reason);

public sealed record ReplacementRequest(string ReplacementPart, string? Reason);

/// <summary>An asset with its history and components, installed and removed.</summary>
public sealed record AssetDetail(AssetInfo Asset, IReadOnlyList<AssetEvent> History, IReadOnlyList<AssetComponentInfo> Components, IReadOnlyList<ObsolescenceInfo> Obsolescence);

/// <summary>An MRO policy beside what is on hand, so the page shows which spares are at or below their reorder point.</summary>
public sealed record MroPolicyView(MroPolicyInfo Policy, string Description, string UnitOfMeasure, decimal Available, bool AtOrBelowReorderPoint, bool BelowMinimum);

/// <summary>V2.8 endpoints: assets, lifecycle and components; MRO levels; obsolescence records and their impact.</summary>
public static class AssetEndpoints
{
    public static void MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var maintenance = app.MapGroup("/api/maintenance").RequireAuthorization();
        maintenance.MapGet("/assets", (HttpContext http, CancellationToken _) =>
            Read(http, Permissions.MaintenanceRead, async _ =>
            {
                await using var uow = await Snapshot(http);
                return await uow.Store<IAssetLookup>().ListAssetsAsync(http.RequestAborted);
            }));
        maintenance.MapGet("/assets/{code}", (HttpContext http, string code, CancellationToken _) =>
            Read(http, Permissions.MaintenanceRead, async _ =>
            {
                await using var uow = await Snapshot(http);
                var ct = http.RequestAborted;
                var key = code.Trim().ToUpperInvariant();
                var lookup = uow.Store<IAssetLookup>();
                var asset = await lookup.FindAssetAsync(key, ct) ?? throw new CommandRejectedException("asset_not_found", $"Asset {key} does not exist.");
                var components = await lookup.ListComponentsAsync(key, null, true, ct);
                var parts = components.Where(c => c.Installed).Select(c => c.PartNumber).ToHashSet(StringComparer.Ordinal);
                var obsolete = (await uow.Store<IObsolescenceLookup>().ListObsolescenceAsync(true, ct)).Where(o => parts.Contains(o.PartNumber)).ToList();
                return new AssetDetail(asset, await lookup.ListAssetHistoryAsync(key, ct), components, obsolete);
            }));
        maintenance.MapPost("/assets", (HttpContext http, AssetRequest b, AssetService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RegisterAsync(new RegisterAsset(b.AssetCode, b.Name, b.Type, b.Manufacturer, b.Model, b.SerialNumber, b.Location, b.WorkCenterCode, b.MachineCode, b.Criticality, b.ExpectedLifeYears, b.ReplacementCost), c, ct)));
        maintenance.MapPost("/assets/{code}/change", (HttpContext http, string code, AssetChangeRequest b, AssetService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ChangeAsync(new ChangeAsset(code, b.Name, b.Manufacturer, b.Model, b.SerialNumber, b.Location, b.Criticality, b.ExpectedLifeYears, b.ReplacementCost), c, ct)));
        maintenance.MapPost("/assets/{code}/status", (HttpContext http, string code, AssetStatusRequest b, AssetService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetStatusAsync(new SetAssetStatus(code, b.Status, b.CommissionedOn), c, ct)));
        maintenance.MapPost("/assets/{code}/decommission", (HttpContext http, string code, ReasonRequest b, AssetService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RequestDecommissionAsync(new RequestDecommission(code), c, ct)));
        maintenance.MapPost("/assets/{code}/decommission/decide", (HttpContext http, string code, DecommissionDecisionRequest b, AssetService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DecideDecommissionAsync(new DecideDecommission(code, b.Approve), c, ct)));
        maintenance.MapPost("/assets/{code}/components", (HttpContext http, string code, ComponentRequest b, AssetService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.InstallAsync(new InstallComponent(code, b.PartNumber, b.Revision, b.Position, b.Quantity, b.WorkOrderId), c, ct)));
        maintenance.MapPost("/components/{id:long}/remove", (HttpContext http, long id, ComponentRemovalRequest b, AssetService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RemoveAsync(new RemoveComponent(id, b.WorkOrderId), c, ct)));

        var inventory = app.MapGroup("/api/inventory").RequireAuthorization();
        inventory.MapGet("/mro", (HttpContext http, CancellationToken _) =>
            Read(http, Permissions.InventoryRead, async _ =>
            {
                await using var uow = await Snapshot(http);
                var ct = http.RequestAborted;
                var items = (await uow.Store<IEngineeringLookup>().ListItemsAsync(ct)).ToDictionary(i => i.PartNumber, StringComparer.Ordinal);
                var inventoryLookup = uow.Store<Forge.Inventory.Contracts.IInventoryLookup>();
                var stock = Forge.Inventory.Contracts.StockPosition.From(await inventoryLookup.ListBalancesAsync(null, ct), await inventoryLookup.ListLocationsAsync(ct))
                    .Where(b => b.AvailableForPlanning)
                    .GroupBy(b => b.PartNumber, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Sum(b => b.Quantity), StringComparer.Ordinal);
                return (await uow.Store<IMroLookup>().ListMroPoliciesAsync(ct)).Select(p =>
                {
                    var available = stock.GetValueOrDefault(p.PartNumber);
                    var item = items.GetValueOrDefault(p.PartNumber);
                    return new MroPolicyView(p, item?.Description ?? p.PartNumber, item?.UnitOfMeasure ?? "EA", available, available <= p.ReorderPoint, p.CriticalSpare && available < p.MinQuantity);
                }).ToList();
            }));
        inventory.MapGet("/mro/{part}/history", (HttpContext http, string part, CancellationToken _) =>
            Read(http, Permissions.InventoryRead, async _ =>
            {
                await using var uow = await Snapshot(http);
                return await uow.Store<IMroLookup>().MroHistoryAsync(part.Trim().ToUpperInvariant(), http.RequestAborted);
            }));
        inventory.MapPost("/mro/{part}", (HttpContext http, string part, MroPolicyRequest b, ObsolescenceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetMroPolicyAsync(new SetMroPolicy(part, b.MinQuantity, b.ReorderPoint, b.MaxQuantity, b.CriticalSpare, b.ShelfLifeDays), c, ct)));
        inventory.MapPost("/mro/{part}/clear", (HttpContext http, string part, ReasonRequest b, ObsolescenceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ClearMroPolicyAsync(new ClearMroPolicy(part), c, ct)));

        var eng = app.MapGroup("/api/engineering").RequireAuthorization();
        eng.MapGet("/obsolescence", (HttpContext http, bool? all, CancellationToken _) =>
            Read(http, Permissions.EngineeringRead, async _ =>
            {
                await using var uow = await Snapshot(http);
                return await uow.Store<IObsolescenceLookup>().ListObsolescenceAsync(all != true, http.RequestAborted);
            }));
        eng.MapGet("/obsolescence/{id:long}/impact", async (HttpContext http, long id, ObsolescenceImpactService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.ImpactAsync(principal, id, ct));
        });
        eng.MapPost("/obsolescence", (HttpContext http, ObsolescenceRequest b, ObsolescenceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordAsync(new RecordObsolescence(b.PartNumber, b.Status, b.LastTimeBuy, b.EndOfLife, b.EndOfSupport, b.Evidence, b.ReplacementPart), c, ct)));
        eng.MapPost("/obsolescence/{id:long}/replacement", (HttpContext http, long id, ReplacementRequest b, ObsolescenceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ProposeReplacementAsync(new ProposeReplacement(id, b.ReplacementPart), c, ct)));
        eng.MapPost("/obsolescence/{id:long}/replacement/approve", (HttpContext http, long id, ReasonRequest b, ObsolescenceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveReplacementAsync(new ApproveReplacement(id), c, ct)));
        eng.MapPost("/obsolescence/{id:long}/replacement/reject", (HttpContext http, long id, ReasonRequest b, ObsolescenceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RejectReplacementAsync(new RejectReplacement(id), c, ct)));
    }

    private static Task<IKernelUnitOfWork> Snapshot(HttpContext http) =>
        http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
}
