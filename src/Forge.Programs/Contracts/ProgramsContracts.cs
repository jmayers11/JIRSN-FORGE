namespace Forge.Programs.Contracts;

public enum ProgramStatus
{
    Active,
    Closed,
}

/// <summary>
/// A program: the top of program → contract → CLIN → delivery → production. Its id is the
/// <c>ProgramId</c> that segregates orders, demand and audit records.
/// </summary>
public sealed record ProgramInfo(Guid ProgramId, string Code, string Name, string? Customer, string? Description, ProgramStatus Status, string CreatedBy, DateTimeOffset CreatedAt);

public enum ContractStatus
{
    Active,
    Complete,
    Cancelled,
}

/// <summary>A contract line item. A SLIN names its parent CLIN.</summary>
public sealed record ClinInfo(string Clin, string? ParentClin, string Description, string? PartNumber, decimal? Quantity, decimal? UnitPrice);

/// <summary>A scheduled delivery of a CLIN: a quantity by a date. Planning turns it into demand.</summary>
public sealed record DeliveryInfo(long DeliveryId, long ContractId, string Clin, decimal Quantity, DateOnly DueDate, bool Cancelled, string CreatedBy, DateTimeOffset CreatedAt);

public sealed record RequirementInfo(int Number, string Code, string Text);

public sealed record ContractInfo(
    long ContractId,
    Guid ProgramId,
    string Number,
    string Customer,
    string Title,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal? Ceiling,
    ContractStatus Status,
    IReadOnlyList<ClinInfo> Clins,
    IReadOnlyList<DeliveryInfo> Deliveries,
    IReadOnlyList<RequirementInfo> Requirements);

public sealed record MilestoneInfo(long MilestoneId, Guid ProgramId, string Title, DateOnly DueDate, DateOnly? CompletedOn, string? CompletedBy);

/// <summary>Everything Planning needs to turn a delivery into demand.</summary>
public sealed record DeliveryReference(DeliveryInfo Delivery, Guid ProgramId, string ProgramCode, string ContractNumber, string Customer, ContractStatus ContractStatus, string? PartNumber)
{
    /// <summary>The key a demand created from this delivery carries, so it is never planned twice.</summary>
    public string SourceKey => SourceKeyFor(Delivery.DeliveryId);

    public static string SourceKeyFor(long deliveryId) => "delivery:" + deliveryId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>What other contexts may ask Programs. Callers filter by the reader's program access.</summary>
public interface IProgramsLookup
{
    Task<IReadOnlyList<ProgramInfo>> ListProgramsAsync(CancellationToken ct);

    Task<ProgramInfo?> FindProgramAsync(Guid programId, CancellationToken ct);

    /// <summary>Contracts with their CLINs, deliveries and requirements; one program's, or all.</summary>
    Task<IReadOnlyList<ContractInfo>> ListContractsAsync(Guid? programId, CancellationToken ct);

    Task<DeliveryReference?> FindDeliveryAsync(long deliveryId, CancellationToken ct);

    Task<IReadOnlyList<MilestoneInfo>> ListMilestonesAsync(Guid? programId, CancellationToken ct);
}
