using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Quality.Contracts;

namespace Forge.Planning;

internal static class ScheduleQuality
{
    public static async Task<IReadOnlyDictionary<long, OrderQuality>> LoadAsync(IKernelUnitOfWork uow, IReadOnlyList<OrderInfo> orders, CancellationToken ct)
    {
        var quality = uow.Store<IQualityLookup>();
        var result = new Dictionary<long, OrderQuality>();
        foreach (var order in orders)
        {
            result[order.OrderNumber] = await quality.GetOrderQualityAsync(order.OrderNumber, order.PartNumber, ct);
        }

        return result;
    }
}
