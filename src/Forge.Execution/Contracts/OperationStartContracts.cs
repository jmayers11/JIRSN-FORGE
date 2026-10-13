using Forge.Kernel.Access;
using Forge.Kernel.Security;

namespace Forge.Execution.Contracts;

public sealed record OperationStartEvidence(string Detail, DataMarking Markings, IReadOnlyList<ResourceRef> RequiredDocuments);

/// <summary>
/// Execution owns the point-of-action check. Planning implements it with the same deterministic
/// readiness rules as previews, reading the command's transaction rather than a previous HTTP read.
/// Qualification and trainer authority remain Execution's responsibility.
/// </summary>
public interface IOperationStartCheck
{
    Task<OperationStartEvidence> RequireReadyAsync(IKernelUnitOfWork uow, OrderInfo order, OrderOperationInfo operation,
        string? machineCode, Guid operatorId, Guid? trainerId, ForgePrincipal terminal,
        DateTimeOffset now, CancellationToken ct);
}
