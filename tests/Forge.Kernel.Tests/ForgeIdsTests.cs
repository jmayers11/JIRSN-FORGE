using Forge.Kernel.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Forge.Kernel.Tests;

public class ForgeIdsTests
{
    [Fact]
    public void Ids_are_version_7_and_ordered_by_time()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var first = ForgeIds.New(time);
        time.Advance(TimeSpan.FromMilliseconds(5));
        var second = ForgeIds.New(time);

        Assert.Equal(7, first.Version);
        Assert.True(string.CompareOrdinal(first.ToString(), second.ToString()) < 0);
    }
}
