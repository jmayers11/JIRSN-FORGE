using System.Globalization;
using System.Text.Json;
using Forge.Execution;
using Forge.Execution.Contracts;
using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Planning;
using Forge.Sales.Contracts;

namespace Forge.Host.Sales;

/// <summary>
/// Owner decision 2026-10-31: releasing a production order or approving a sales order reserves free stock for it. This keeps reservations
/// in step with order status, amendments and customer-order demand. Delivered at least once; each sync recomputes from current facts.
/// </summary>
public sealed class StockReservationSubscriber(StockReservationService reservations, IKernelUnitOfWorkFactory unitOfWork, ILogger<StockReservationSubscriber> logger) : IEventSubscriber
{
    private static readonly ForgePrincipal SystemPrincipal = new(
        new PersonId(Guid.Empty),
        "Stock reservations",
        true,
        new HashSet<string>(StringComparer.Ordinal) { Roles.StockReservations },
        new HashSet<ProgramId>(),
        DataMarking.ControlledUnclassified | DataMarking.ExportControlled | DataMarking.Proprietary);

    public string Name => "stock-reservations";

    public bool Handles(string eventType) => eventType is nameof(ProductionOrderStatusChanged) or nameof(OrderAmended) or nameof(DemandChanged);

    public async Task HandleAsync(long sequence, EventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        CommandContext Context(string what, Guid? program = null) => new(program is { } p ? SystemPrincipal with { Programs = new HashSet<ProgramId> { new(p) } } : SystemPrincipal,
            envelope.CorrelationId, string.Create(CultureInfo.InvariantCulture, $"system:stock-reservations:{sequence}"), CommandSource.System, what, SystemActors.StockReservations);
        // A program's order is synced with access to that program only (it would otherwise be refused, and retried for ever).
        async Task<Guid?> ProgramOfAsync(long orderNumber)
        {
            await using var read = await unitOfWork.BeginSnapshotAsync(ct);
            return (await read.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct))?.ProgramId;
        }

        try
        {
            switch (envelope.EventType)
            {
                case nameof(ProductionOrderStatusChanged):
                {
                    var e = JsonSerializer.Deserialize<ProductionOrderStatusChanged>(envelope.Payload, CanonicalJson.Options)!;
                    if (e.To == e.From) return;
                    if (e.To is not ("Released" or "Held" or "Cancelled" or "Completed") && e.From is not ("Released" or "Held")) return;
                    await reservations.SyncOrderAsync(new SyncOrderReservations(e.OrderNumber), Context($"Order {e.OrderNumber} {e.From} → {e.To}", await ProgramOfAsync(e.OrderNumber)), ct);
                    break;
                }
                case nameof(OrderAmended):
                {
                    var e = JsonSerializer.Deserialize<OrderAmended>(envelope.Payload, CanonicalJson.Options)!;
                    await reservations.SyncOrderAsync(new SyncOrderReservations(e.OrderNumber), Context($"Order {e.OrderNumber} amended", await ProgramOfAsync(e.OrderNumber)), ct);
                    break;
                }
                case nameof(DemandChanged):
                {
                    var e = JsonSerializer.Deserialize<DemandChanged>(envelope.Payload, CanonicalJson.Options)!;
                    DemandInfo? demand;
                    decimal shipped;
                    await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
                    {
                        demand = await read.Store<IDemandStore>().FindAsync(e.DemandId, ct);
                        shipped = (await read.Store<IInventoryLookup>().ListShippedByDemandAsync(ct)).Where(s => s.DemandId == e.DemandId && s.PartNumber == e.PartNumber).Sum(s => s.Shipped);
                    }

                    // Only customer-order demand from approved sales orders holds finished stock.
                    if (demand is null || demand.Kind != DemandKind.CustomerOrder || SalesOrderInfo.ParseDemandSource(demand.SourceKey) is null) return;
                    var toShip = demand.Status == DemandStatus.Open ? Math.Max(0m, demand.Quantity - shipped) : 0m;
                    await reservations.SyncDemandAsync(new SyncDemandReservation(demand.DemandId, demand.PartNumber, toShip), Context($"Demand {demand.DemandId} {e.Change}"), ct);
                    break;
                }
            }
        }
        catch (CommandRejectedException ex)
        {
            logger.LogWarning("Stock reservation sync for event {Sequence} was refused: {Code} {Message}", sequence, ex.Code, ex.Message);
        }
        catch (ForgeAuthorizationException ex)
        {
            logger.LogWarning("Stock reservation sync for event {Sequence} was not authorized: {Message}", sequence, ex.Message);
        }
    }
}
