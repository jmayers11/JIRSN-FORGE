using System.Globalization;
using System.Text.Json;
using Forge.Engineering.Contracts;
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
using Forge.Resources.Contracts;

namespace Forge.Host.Inventory;

/// <summary>
/// Owner decisions 2026-11-01: reporting good and scrap quantity at an operation deducts the components that operation consumes, for
/// parts set to backflush, from their backflush location (lots oldest first). Each deduction is an ordinary issue to the order, with
/// every issue rule applied. Whatever cannot be deducted (no stock, held, reserved for other work, expired) is recorded as a backflush
/// exception for inventory controllers; production is never stopped or undone. Delivered at least once: each issue and exception is
/// keyed by the production report, so a replay changes nothing.
/// </summary>
public sealed class BackflushSubscriber(IKernelUnitOfWorkFactory unitOfWork, InventoryService inventory, BackflushService backflush, ILogger<BackflushSubscriber> logger) : IEventSubscriber
{
    public string Name => "backflush";

    public bool Handles(string eventType) => eventType == nameof(ProductionReported);

    public async Task HandleAsync(long sequence, EventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var e = JsonSerializer.Deserialize<ProductionReported>(envelope.Payload, CanonicalJson.Options)!;
        var reported = e.GoodQuantity + e.ScrapQuantity;
        if (reported <= 0) return;

        OrderInfo order;
        List<(BackflushSetting Setting, ItemInfo Item, decimal Due, List<StockBalance> Lots)> plan = [];
        await using (var uow = await unitOfWork.BeginSnapshotAsync(ct))
        {
            if (!await uow.IsOnAsync(SiteFeatures.Backflush, ct)) return;
            var settings = (await uow.Store<IBackflushLookup>().ListBackflushSettingsAsync(ct)).Where(s => s.Enabled).ToDictionary(s => s.PartNumber, StringComparer.Ordinal);
            if (settings.Count == 0) return;
            var found = await uow.Store<IExecutionLookup>().FindOrderAsync(e.OrderNumber, ct);
            if (found is null) return;
            order = found;
            var issued = (await uow.Store<IInventoryLookup>().ListOrderIssuesAsync(ct)).Where(i => i.OrderNumber == order.OrderNumber)
                .ToDictionary(i => i.PartNumber, i => i.NetIssued, StringComparer.Ordinal);
            foreach (var (part, quantity) in BackflushMath.Consumed(order, e.Sequence, reported))
            {
                if (!settings.TryGetValue(part, out var setting)) continue;
                var required = order.Materials.Where(m => m.ComponentPartNumber == part).Sum(m => m.RequiredQuantity);
                var due = Math.Min(quantity, Math.Max(0, required - issued.GetValueOrDefault(part)));
                if (due <= 0) continue; // already issued by hand
                var item = await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct);
                if (item is null) continue;
                var lots = (await uow.Store<IInventoryLookup>().ListBalancesAsync(part, ct))
                    .Where(b => b.LocationCode == setting.LocationCode && b.Status == StockStatus.Released && b.Quantity > 0)
                    .OrderBy(b => b.LotNumber ?? string.Empty, StringComparer.Ordinal).ToList();
                plan.Add((setting, item, due, lots));
            }
        }

        if (plan.Count == 0) return;
        var principal = new ForgePrincipal(new PersonId(Guid.Empty), "Backflush", true, new HashSet<string>(StringComparer.Ordinal) { Roles.Backflush },
            order.ProgramId is { } program ? new HashSet<ProgramId> { new(program) } : new HashSet<ProgramId>(), DataMarking.None);
        foreach (var (setting, item, due, lots) in plan)
        {
            var remaining = due;
            var problems = new List<string>();
            foreach (var lot in lots)
            {
                if (remaining <= 0) break;
                var take = Math.Min(remaining, lot.Quantity);
                var key = string.Create(CultureInfo.InvariantCulture, $"system:backflush:{e.ReportId}:{item.PartNumber}:{lot.LotNumber ?? "-"}");
                var context = new CommandContext(principal, envelope.CorrelationId, key, CommandSource.System,
                    string.Create(CultureInfo.InvariantCulture, $"Backflush: order {order.OrderNumber} operation {e.Sequence} reported {e.GoodQuantity} good, {e.ScrapQuantity} scrap"), SystemActors.Backflush);
                try
                {
                    await inventory.IssueToOrderAsync(new IssueToOrder(order.OrderNumber, item.PartNumber, take, setting.LocationCode, lot.LotNumber), context, ct);
                    remaining -= take;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Production is never undone for a deduction: whatever refused it (stock, a hold, a reservation, access) is recorded for inventory.
                    problems.Add($"{(lot.LotNumber is null ? string.Empty : $"lot {lot.LotNumber}: ")}{ex.Message}");
                }
            }

            if (remaining <= 0) continue;
            if (lots.Count == 0) problems.Add($"no released stock of {item.PartNumber} at {setting.LocationCode}");
            var exceptionContext = new CommandContext(principal, envelope.CorrelationId, string.Create(CultureInfo.InvariantCulture, $"system:backflush:{e.ReportId}:{item.PartNumber}:exception"),
                CommandSource.System, "Backflush could not deduct everything", SystemActors.Backflush);
            try
            {
                await backflush.RecordExceptionAsync(new RecordBackflushException(order.OrderNumber, e.Sequence, e.ReportId, item.PartNumber, due, due - remaining, setting.LocationCode,
                    string.Join("; ", problems)), exceptionContext, ct);
            }
            catch (CommandRejectedException ex)
            {
                logger.LogWarning("Recording the backflush exception for order {Order} part {Part} was refused: {Code} {Message}", order.OrderNumber, item.PartNumber, ex.Code, ex.Message);
            }
        }
    }
}
