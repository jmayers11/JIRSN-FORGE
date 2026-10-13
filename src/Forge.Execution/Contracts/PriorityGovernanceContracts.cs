using Forge.Kernel.Access;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;

namespace Forge.Execution.Contracts;

public sealed record PriorityOrderImpact(
    long OrderNumber,
    string PartNumber,
    Guid? ProgramId,
    DateTimeOffset? CompletionBefore,
    DateTimeOffset? CompletionAfter,
    string? StatusBefore,
    string? StatusAfter,
    string ExplanationBefore,
    string ExplanationAfter)
{
    public double? DelayMinutes => CompletionBefore is { } before && CompletionAfter is { } after && after > before
        ? (after - before).TotalMinutes
        : null;
}

public sealed record PriorityChangePreview(
    long OrderNumber,
    int CurrentPriority,
    int ProposedPriority,
    DateTimeOffset AsOf,
    string Fingerprint,
    IReadOnlyList<PriorityOrderImpact> Orders);

/// <summary>Planning computes a time-bounded, deterministic projection from the caller's unit of work.</summary>
public interface IPriorityImpactProvider
{
    Task<PriorityChangePreview> PreviewAsync(IKernelUnitOfWork uow, long orderNumber, int newPriority, DateTimeOffset asOf, CancellationToken ct, Forge.Kernel.Security.ForgePrincipal? viewer = null);
}

public sealed record ChangeOrderPriority(
    long OrderNumber,
    int NewPriority,
    int ExpectedPriority,
    DateTimeOffset PreviewAsOf,
    string PreviewFingerprint);

public sealed record OrderPriorityChanged(
    long OrderNumber,
    int PreviousPriority,
    int NewPriority,
    string PreviewFingerprint,
    IReadOnlyList<PriorityOrderImpact> DelayedOrders) : IDomainEvent
{
    public static string EventType => nameof(OrderPriorityChanged);

    public static int SchemaVersion => 1;
}
