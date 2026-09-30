using Forge.Kernel.Audit;

namespace Forge.Kernel.Tests;

public class AuditChainTests
{
    private static AuditEntry Entry(int n) => new()
    {
        AuditId = Guid.Parse($"018f0000-0000-7000-8000-{n:D12}"),
        RecordedAt = new DateTimeOffset(2026, 10, 1, 7, 0, n, TimeSpan.Zero),
        Actor = "person:x",
        Action = "AssignRole",
        Outcome = "Succeeded",
        EntityType = "Person",
        EntityId = "p" + n,
        NewValue = "{\"role\":\"Auditor\"}",
        Reason = "test",
        Source = "Api",
        CorrelationId = Guid.Parse("018f0000-0000-7000-8000-00000000cccc"),
    };

    private static List<ChainedAuditEntry> Chain(int count)
    {
        var chain = new List<ChainedAuditEntry>();
        string? previous = null;
        for (var i = 1; i <= count; i++)
        {
            var entry = Entry(i);
            var hash = AuditChain.ComputeHash(previous, entry);
            chain.Add(new ChainedAuditEntry(i, entry, previous, hash));
            previous = hash;
        }

        return chain;
    }

    [Fact]
    public void Intact_chain_verifies()
    {
        Assert.Null(AuditChain.FindFirstBreak(Chain(5)));
    }

    [Fact]
    public void Edited_entry_is_detected()
    {
        var chain = Chain(5);
        chain[2] = chain[2] with { Entry = chain[2].Entry with { NewValue = "{\"role\":\"SecurityAdministrator\"}" } };
        Assert.Equal(3, AuditChain.FindFirstBreak(chain));
    }

    [Fact]
    public void Removed_entry_is_detected()
    {
        var chain = Chain(5);
        chain.RemoveAt(1);
        Assert.Equal(3, AuditChain.FindFirstBreak(chain));
    }

    [Fact]
    public void Sub_microsecond_differences_do_not_change_the_hash()
    {
        var entry = Entry(1);
        var shifted = entry with { RecordedAt = entry.RecordedAt.AddTicks(3) };
        Assert.Equal(AuditChain.ComputeHash(null, entry), AuditChain.ComputeHash(null, shifted));
    }
}
