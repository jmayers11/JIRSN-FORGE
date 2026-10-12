namespace Forge.Execution.Contracts;

/// <summary>An explicitly recorded production-order commitment to an existing contract line.</summary>
public sealed record OrderCommitment(long OrderNumber, long? ContractId, string? Clin, DateTimeOffset RecordedAt, string Actor);

public interface IOrderCommitmentLookup
{
    Task<IReadOnlyList<OrderCommitment>> AsOfAsync(DateTimeOffset at, CancellationToken ct);
}

public interface IOrderCommitmentStore : IOrderCommitmentLookup
{
    Task LockAsync(long orderNumber, CancellationToken ct);
    Task SetAsync(long orderNumber, long? contractId, string? clin, DateTimeOffset at, string actor, CancellationToken ct);
}
