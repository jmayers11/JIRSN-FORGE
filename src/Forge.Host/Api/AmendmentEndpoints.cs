using Forge.Execution;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record AmendmentRequest(AmendmentChanges? Changes, string Rationale, string? Reason);

public sealed record AmendmentDecisionRequest(bool Approve, string? Note, string? Reason);

/// <summary>Net quantity of each component issued to an order: issues less returns.</summary>
public sealed class OrderIssueLookup : IOrderIssueLookup
{
    public async Task<IReadOnlyDictionary<string, decimal>> NetIssuedAsync(IKernelUnitOfWork uow, long orderNumber, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return (await uow.Store<IInventoryLookup>().ListTransactionsAsync(null, orderNumber, 100_000, ct))
            .Where(t => t.OrderNumber == orderNumber && t.Kind is InventoryTransactionKind.Issue or InventoryTransactionKind.Return)
            .GroupBy(t => t.PartNumber)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Kind == InventoryTransactionKind.Issue ? t.Quantity : -t.Quantity), StringComparer.Ordinal);
    }
}

/// <summary>Released-order amendments (owner decisions 2026-10-31).</summary>
internal static class AmendmentEndpoints
{
    public static void MapAmendmentEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/orders").RequireAuthorization();
        api.MapGet("/{order:long}/amendments", (HttpContext http, long order, CancellationToken _) =>
            Read(http, Permissions.OrdersRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var info = await uow.Store<IExecutionLookup>().FindOrderAsync(order, http.RequestAborted);
                if (info is null || (info.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p))))
                    throw new Forge.Kernel.Commands.CommandRejectedException("order_not_found", $"Order {order} does not exist.");
                return await uow.Store<IOrderAmendmentStore>().ListAmendmentsAsync(order, http.RequestAborted);
            }));
        api.MapPost("/{order:long}/amendments", (HttpContext http, long order, AmendmentRequest b, OrderAmendmentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ProposeAsync(new ProposeOrderAmendment(order, b.Changes ?? new AmendmentChanges(), b.Rationale), c, ct)));
        api.MapPost("/amendments/{id:long}/decide", (HttpContext http, long id, AmendmentDecisionRequest b, OrderAmendmentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DecideAsync(new DecideOrderAmendment(id, b.Approve, b.Note), c, ct)));
        api.MapPost("/amendments/{id:long}/withdraw", (HttpContext http, long id, ReasonRequest b, OrderAmendmentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.WithdrawAsync(new WithdrawOrderAmendment(id), c, ct)));
    }
}
