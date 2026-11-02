namespace Forge.Inventory.Contracts;

/// <summary>A part set to backflush: reporting production at the operation that uses it deducts it from <see cref="LocationCode"/>.</summary>
public sealed record BackflushSetting(string PartNumber, bool Enabled, string LocationCode, string UpdatedBy, DateTimeOffset UpdatedAt);

public enum BackflushExceptionStatus
{
    Open,
    Resolved,
}

/// <summary>
/// What a backflush could not deduct: <see cref="Quantity"/> was due, <see cref="Deducted"/> was taken. Production went on; inventory
/// issues the rest by hand (or corrects the count) and resolves it.
/// </summary>
public sealed record BackflushException(
    long ExceptionId,
    long OrderNumber,
    int Sequence,
    long ReportId,
    string PartNumber,
    decimal Quantity,
    decimal Deducted,
    string LocationCode,
    string Problem,
    BackflushExceptionStatus Status,
    DateTimeOffset RaisedAt,
    string? ResolvedBy,
    DateTimeOffset? ResolvedAt,
    string? ResolutionNote);

public interface IBackflushLookup
{
    Task<IReadOnlyList<BackflushSetting>> ListBackflushSettingsAsync(CancellationToken ct);

    Task<IReadOnlyList<BackflushException>> ListBackflushExceptionsAsync(bool openOnly, CancellationToken ct);

    Task<BackflushException?> FindBackflushExceptionAsync(long exceptionId, CancellationToken ct);
}

/// <summary>How much of each component one production report consumes (owner decision 2026-11-01).</summary>
public static class BackflushMath
{
    /// <summary>
    /// The components an order's operation consumes for <paramref name="reported"/> units (good plus scrap), in proportion to the order's
    /// required quantities. A component without a consuming operation is used from the first one.
    /// </summary>
    public static IReadOnlyList<(string PartNumber, decimal Quantity)> Consumed(Forge.Execution.Contracts.OrderInfo order, int sequence, decimal reported)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (reported <= 0 || order.Quantity <= 0 || order.Operations.Count == 0) return [];
        var first = order.Operations.Min(o => o.Sequence);
        return order.Materials
            .Where(m => (m.ConsumedAtSequence ?? first) == sequence)
            .GroupBy(m => m.ComponentPartNumber, StringComparer.Ordinal)
            .Select(g => (g.Key, decimal.Round(g.Sum(m => m.RequiredQuantity) * reported / order.Quantity, 4, MidpointRounding.AwayFromZero)))
            .Where(x => x.Item2 > 0)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList();
    }
}
