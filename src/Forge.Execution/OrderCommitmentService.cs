using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Programs.Contracts;

namespace Forge.Execution;

public sealed record AssignOrderCommitment(long OrderNumber, long? ContractId, string? Clin);
public sealed record OrderCommitmentChanged(long OrderNumber, long? ContractId, string? Clin) : IDomainEvent
{
    public static string EventType => nameof(OrderCommitmentChanged);
    public static int SchemaVersion => 1;
}

public sealed class OrderCommitmentService(CommandPipeline pipeline, IKernelUnitOfWorkFactory unitOfWork)
{
    public async Task<CommandResult> AssignAsync(AssignOrderCommitment command, CommandContext context, CancellationToken ct)
    {
        var id = command.OrderNumber.ToString(CultureInfo.InvariantCulture);
        OrderInfo? source;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
            source = await read.Store<IExecutionLookup>().FindOrderAsync(command.OrderNumber, ct);
        var resource = new ResourceRef("ProductionOrder", id, source?.ProgramId is { } program ? new ProgramId(program) : null,
            ContractId: command.ContractId?.ToString(CultureInfo.InvariantCulture));
        return await pipeline.ExecuteAsync(new CommandSpec("Execution", nameof(AssignOrderCommitment), Permissions.OrdersManage, resource, ReasonRequired: true),
            command, context, async (uow, now) =>
            {
                await uow.Store<IOrderCommitmentStore>().LockAsync(command.OrderNumber, ct);
                var order = await uow.Store<IExecutionLookup>().FindOrderAsync(command.OrderNumber, ct)
                    ?? throw new CommandRejectedException("order_not_found", "Production order does not exist.");
                if (order.Status is OrderStatus.Cancelled or OrderStatus.Completed)
                    throw new CommandRejectedException("order_closed", "A completed or cancelled order cannot be assigned a new contract commitment.");
                string? clin = null;
                if (command.ContractId is { } contractId)
                {
                    var contract = (await uow.Store<IProgramsLookup>().ListContractsAsync(order.ProgramId, ct)).SingleOrDefault(record => record.ContractId == contractId);
                    if (contract is null || order.ProgramId != contract.ProgramId || !context.Principal.Programs.Contains(new ProgramId(contract.ProgramId)))
                        throw new CommandRejectedException("contract_not_found", "An accessible contract in the order's program is required.");
                    if (contract.Status != ContractStatus.Active)
                        throw new CommandRejectedException("contract_closed", "The selected contract is not active.");
                    clin = command.Clin?.Trim();
                    var line = contract.Clins.SingleOrDefault(line => string.Equals(line.Clin, clin, StringComparison.Ordinal));
                    if (line is null || line.PartNumber != order.PartNumber)
                        throw new CommandRejectedException("contract_part_mismatch", "Select an existing contract line for the production order's part.");
                }
                else if (!string.IsNullOrWhiteSpace(command.Clin))
                    throw new CommandRejectedException("contract_required", "A line requires its contract; clearing a commitment clears both.");
                if (order.ContractId == command.ContractId && order.ContractClin == clin)
                    return CommandOutcome.NoChange(CommandResult.NoChange(id, "The order already has that contract commitment."));
                await uow.Store<IOrderCommitmentStore>().SetAsync(order.OrderNumber, command.ContractId, clin, now, context.Actor, ct);
                return new CommandOutcome(CommandResult.Ok(id, command.ContractId is null ? "Contract commitment cleared; its history is retained." : "Order committed to the selected contract line."),
                    [pipeline.Event(new OrderCommitmentChanged(order.OrderNumber, command.ContractId, clin), context, "Execution", "ProductionOrder", id, resource.Program)],
                    new AuditChange("ProductionOrder", id, CanonicalJson.Serialize(new { order.ContractId, order.ContractClin }), CanonicalJson.Serialize(new { command.ContractId, ContractClin = clin }), resource.Program));
            }, ct);
    }
}
