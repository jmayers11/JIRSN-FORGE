using Forge.Host.Sales;
using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Security;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

/// <summary>Stock reservations and promise dates (owner decision 2026-10-31).</summary>
internal static class ReservationEndpoints
{
    public static void MapReservationEndpoints(this IEndpointRouteBuilder app)
    {
        var inventory = app.MapGroup("/api/inventory").RequireAuthorization();
        inventory.MapGet("/reservations", (HttpContext http, string? part, long? order, long? demand, CancellationToken _) =>
            Read(http, Permissions.InventoryRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var balances = await StockReservationQueries.OutstandingAsync(uow, string.IsNullOrWhiteSpace(part) ? null : part.Trim().ToUpperInvariant(), http.RequestAborted);
                return balances.Where(b => (order is null || b.Reservation.OrderNumber == order) && (demand is null || b.Reservation.DemandId == demand))
                    .Select(b => new { reservation = b.Reservation, b.Drawn, b.Outstanding, b.Holder }).ToList();
            }));
        inventory.MapPost("/reservations/{id:long}/release", (HttpContext http, long id, ReasonRequest b, StockReservationService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReleaseAsync(new ReleaseReservation(id), c, ct)));
        inventory.MapPost("/reservations/orders/{order:long}/sync", (HttpContext http, long order, ReasonRequest b, StockReservationService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SyncOrderAsync(new SyncOrderReservations(order), c, ct)));

        app.MapGroup("/api/sales").RequireAuthorization()
            .MapGet("/promise", (HttpContext http, string part, decimal quantity, PromiseDateService s, CancellationToken _) =>
                Read(http, Permissions.SalesRead, async principal => await s.PromiseAsync(part, quantity, http.RequestAborted)));
    }
}
