using Forge.Kernel.Serialization;

namespace Forge.Kernel.Tests;

public class CanonicalJsonTests
{
    private sealed record A(int First, string Second);

    private sealed record B(string Second, int First);

    [Fact]
    public void Property_declaration_order_does_not_change_the_output()
    {
        Assert.Equal(CanonicalJson.Serialize(new A(1, "x")), CanonicalJson.Serialize(new B("x", 1)));
    }

    [Fact]
    public void Dictionary_insertion_order_does_not_change_the_fingerprint()
    {
        var one = new Dictionary<string, decimal> { ["b"] = 2.50m, ["a"] = 1m };
        var two = new Dictionary<string, decimal> { ["a"] = 1m, ["b"] = 2.50m };
        Assert.Equal(CanonicalJson.Fingerprint(one), CanonicalJson.Fingerprint(two));
    }

    [Fact]
    public void Nested_objects_are_sorted_at_every_level()
    {
        var json = CanonicalJson.Canonicalize("""{"z":{"b":1,"a":[{"d":1,"c":2}]},"y":null}""");
        Assert.Equal("""{"y":null,"z":{"a":[{"c":2,"d":1}],"b":1}}""", json);
    }

    [Fact]
    public void Different_values_produce_different_fingerprints()
    {
        Assert.NotEqual(CanonicalJson.Fingerprint(new A(1, "x")), CanonicalJson.Fingerprint(new A(2, "x")));
    }
}
