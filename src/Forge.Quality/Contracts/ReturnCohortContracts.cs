namespace Forge.Quality.Contracts;

/// <summary>Exact reinspection cohort bound to actual return/preparation ledger entries; it owns no independent stock balance.</summary>
public sealed record ReturnInspectionCohort(long CohortId,int Version,long RmaId,string OriginalReturnFingerprint,
    long OriginalReturnTransactionId,long PreparationTransactionId,AcceptanceSubjectKey Subject,string Revision,decimal Quantity,
    string PreparedBy,DateTimeOffset PreparedAt,string Basis,IReadOnlyList<QmsPolicyReadSource> Sources,string Fingerprint);
public interface IReturnAcceptanceLookup
{
    Task<ReturnInspectionCohort?> FindCohortAsync(long cohortId,CancellationToken ct);
    Task<IReadOnlyList<ReturnInspectionCohort>> CohortsForRmaAsync(long rmaId,CancellationToken ct);
}

/// <summary>Owner persistence used only inside the authorized Inventory return transaction; it creates no ledger or balance.</summary>
public interface IReturnCohortWriter
{
    Task<long> AllocateCohortIdAsync(CancellationToken ct);
    Task InsertCohortAsync(ReturnInspectionCohort cohort,CancellationToken ct);
}
