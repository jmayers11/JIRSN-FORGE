using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Kernel.Audit;

/// <summary>
/// Business audit: who did what, to what, with old and new values and the reason. Separate from
/// the event log (which drives processing) though the two share correlation IDs.
/// </summary>
public sealed record AuditEntry
{
    public required Guid AuditId { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    public required string Actor { get; init; }

    public string? OnBehalfOf { get; init; }

    public required string Action { get; init; }

    /// <summary>"Succeeded", "Denied" or "Rejected".</summary>
    public required string Outcome { get; init; }

    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    public string? Reason { get; init; }

    public required string Source { get; init; }

    public required Guid CorrelationId { get; init; }

    public ProgramId? ProgramId { get; init; }

    public DataMarking Markings { get; init; }
}

/// <summary>An audit entry as stored, with its position in the tamper-evident hash chain.</summary>
public sealed record ChainedAuditEntry(long Sequence, AuditEntry Entry, string? PreviousHash, string Hash);

/// <summary>
/// Hash chain over audit entries: each hash covers the entry's canonical form and the previous hash.
/// Changing, removing or reordering any stored entry breaks every hash after it.
/// </summary>
public static class AuditChain
{
    public static string ComputeHash(string? previousHash, AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var canonical = CanonicalJson.Serialize(new { previousHash = previousHash ?? string.Empty, entry = Normalize(entry) });
        return CanonicalJson.Sha256Hex(canonical);
    }

    /// <summary>Returns the sequence number of the first entry that fails verification, or null if the chain is intact.</summary>
    public static long? FindFirstBreak(IEnumerable<ChainedAuditEntry> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        string? previous = null;
        var first = true;
        foreach (var link in chain)
        {
            if (!first && !string.Equals(link.PreviousHash, previous, StringComparison.Ordinal))
            {
                return link.Sequence;
            }

            if (!string.Equals(ComputeHash(link.PreviousHash, link.Entry), link.Hash, StringComparison.Ordinal))
            {
                return link.Sequence;
            }

            previous = link.Hash;
            first = false;
        }

        return null;
    }

    // Timestamps are hashed in UTC at microsecond precision, which is what PostgreSQL stores.
    private static AuditEntry Normalize(AuditEntry entry) => entry with
    {
        RecordedAt = new DateTimeOffset(entry.RecordedAt.UtcDateTime.Ticks / 10 * 10, TimeSpan.Zero),
    };
}
