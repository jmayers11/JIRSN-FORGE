using System.Globalization;
using System.Text.Json;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Planning;
using Forge.Sales;

namespace Forge.Host.Sales;

/// <summary>
/// Owner decision 2026-10-31: an approved sales order's open lines are customer-order demand. Whenever a sales order changes, planning
/// brings that demand in step with it. Delivered at least once; planning is a sync of the order's current state, so a repeat does nothing.
/// </summary>
public sealed class SalesDemandSubscriber(DemandService demand, ILogger<SalesDemandSubscriber> logger) : IEventSubscriber
{
    private static readonly ForgePrincipal SystemPrincipal = new(
        new PersonId(Guid.Empty),
        "Plan sales order demand",
        true,
        new HashSet<string>(StringComparer.Ordinal) { Roles.SalesDemand },
        new HashSet<ProgramId>(),
        DataMarking.None);

    public string Name => "sales-demand";

    public bool Handles(string eventType) => eventType == nameof(SalesOrderChanged);

    public async Task HandleAsync(long sequence, EventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var e = JsonSerializer.Deserialize<SalesOrderChanged>(envelope.Payload, CanonicalJson.Options)!;
        if (e.Change is "Drafted" or "LineAdded") return; // drafts are not planned
        var key = string.Create(CultureInfo.InvariantCulture, $"system:sales-demand:{e.OrderNumber}:{sequence}");
        var context = new CommandContext(SystemPrincipal, envelope.CorrelationId, key, CommandSource.System, $"Sales order {e.OrderNumber} {e.Change}", SystemActors.SalesDemand);
        try
        {
            await demand.PlanSalesOrderAsync(new PlanSalesOrder(e.OrderNumber), context, ct);
        }
        catch (CommandRejectedException ex)
        {
            logger.LogWarning("Planning sales order {Order} was refused: {Code} {Message}", e.OrderNumber, ex.Code, ex.Message);
        }
    }
}
