using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;

namespace Forge.Inventory.Contracts;

public sealed record ReturnAcceptanceAuthority(QmsAdoptedPolicy Policy,IReadOnlyList<ResourceRef> Resources,
    IReadOnlyList<QmsPolicyReadSource> Sources,string SubjectIdentity);
public sealed record ReturnInspectionPrepared(long CohortId,int CohortVersion,long RmaId,long OriginalReturnTransactionId,
    long PreparationTransactionId,AcceptanceSubjectKey Subject,string Revision,decimal Quantity,string LocationCode);
public sealed record ReturnReleaseApplied(long CohortId,long DecisionId,long TransactionId,long RmaId,decimal Quantity,
    decimal CumulativeRmaRestockedQuantity,decimal RmaRecordedQuantity,bool ReturnComplete);

/// <summary>
/// Same owner UOW only. Null authority means truly never-governed; known unavailable/ambiguous/missing sources reject.
/// Restock retains the commercial caller's QualityDisposition/current property requirements and independently requires InventoryTransact.
/// Generic product-release consumption must reject return-bound decisions so the controlled Restock path cannot be bypassed.
/// </summary>
public interface IReturnAcceptanceBridge
{
    Task<ReturnAcceptanceAuthority> PreparationAuthorityAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long rmaId,
        string? locationCode,DateTimeOffset at,CancellationToken ct);
    Task<ReturnAcceptanceAuthority?> AuthorityAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long rmaId,
        long? decisionId,int? expectedDecisionVersion,DateTimeOffset at,CancellationToken ct);
    Task<ReturnInspectionPrepared> PrepareAsync(IKernelUnitOfWork uow,CommandContext context,long rmaId,
        decimal quantity,string? locationCode,DateTimeOffset at,CancellationToken ct);
    Task<ReturnReleaseApplied> ConsumeReturnReleaseAsync(IKernelUnitOfWork uow,CommandContext context,long rmaId,
        long decisionId,int expectedVersion,decimal quantity,DateTimeOffset at,CancellationToken ct);
}

public sealed record ReturnSourceContext(IReadOnlyList<ResourceRef> Resources,IReadOnlyList<QmsPolicyReadSource> Sources,string Fingerprint);
public interface IReturnAcceptanceSourceResolver
{
    Task<ReturnSourceContext> ResolveAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long rmaId,string facility,CancellationToken ct);
    Task<IReadOnlyList<ResourceRef>> ResourceContextsAsync(IKernelUnitOfWork uow,long rmaId,string facility,CancellationToken ct);
}
public interface IReturnAcceptanceInventoryLookup
{
    Task<decimal> PreparedForRmaAsync(long rmaId,CancellationToken ct);
    Task<decimal> ConsumedForRmaAsync(long rmaId,CancellationToken ct);
    Task<decimal> ConsumedForCohortAsync(long cohortId,CancellationToken ct);
    Task<decimal> RejectedForCohortAsync(long cohortId,CancellationToken ct);
}
public interface IReturnAcceptanceInventoryLock
{
    Task LockRmaAsync(long rmaId,CancellationToken ct);
}
internal interface IReturnAcceptanceInventoryStore:IReturnAcceptanceInventoryLookup,IReturnAcceptanceInventoryLock
{
    Task InsertCohortConsumptionAsync(long cohortId,long rmaId,long decisionId,long transactionId,decimal quantity,CancellationToken ct);
}
public static class ReturnCohortIdentity
{
    public static string Fingerprint(CustomerReturnInfo rma)=>Forge.Kernel.Serialization.CanonicalJson.Fingerprint(new{rma.RmaId,rma.ShipmentId,rma.Customer,rma.PartNumber,rma.LotNumber,rma.Quantity,rma.LocationCode,rma.Reason,rma.ReceiptTransactionId,rma.ReceivedBy,rma.ReceivedAt});
}
