using Forge.Host.Identification;

namespace Forge.Integration.Tests;

public sealed class CanonicalIdentificationTests
{
    [Fact]
    public void Namespace_requires_exact_bounded_ASCII_without_trimming_or_a_trailing_newline()
    {
        foreach (var value in new[] { "A", "0", "Plant_1.test-2", new string('A', 64) })
            Assert.True(CanonicalIdentification.IsValidNamespace(value), value);
        string?[] invalid = [null, "", " A", "A ", "A\n", "A\r\n", ".A", "_A", "-A", "A/B", "A:B", "é", "A\u00A0", "A\0", new string('A', 65)];
        foreach (var value in invalid)
            Assert.False(CanonicalIdentification.IsValidNamespace(value), value);
    }

    [Fact]
    public void Canonical_reference_retains_exact_code_namespace_kind_and_typed_numeric_boundaries()
    {
        const string ns = "Plant_1.test-2";
        Assert.Equal(new CanonicalIdentification("forge:v1:Plant_1.test-2:order:1", ns, "order", 1, null),
            CanonicalIdentification.Parse("forge:v1:Plant_1.test-2:order:1", ns));
        Assert.Equal(new CanonicalIdentification("forge:v1:Plant_1.test-2:operation:1:1", ns, "operation", 1, 1),
            CanonicalIdentification.Parse("forge:v1:Plant_1.test-2:operation:1:1", ns));
        const string maximum = "forge:v1:Plant_1.test-2:operation:9223372036854775807:2147483647";
        Assert.Equal(new CanonicalIdentification(maximum, ns, "operation", long.MaxValue, int.MaxValue),
            CanonicalIdentification.Parse(maximum, ns));
        var longestNamespace = new string('N', 64);
        var code = "forge:v1:" + longestNamespace + ":order:9223372036854775807";
        Assert.Equal(new CanonicalIdentification(code, longestNamespace, "order", long.MaxValue, null),
            CanonicalIdentification.Parse(code, longestNamespace));
    }

    [Fact]
    public void Malformed_unknown_noncanonical_and_out_of_range_codes_have_no_typed_reference()
    {
        string?[] invalid =
        [
            null, "", " forge:v1:N:order:1", "forge:v1:N:order:1 ", "forge:v1:N:order:1\n", "forge:v1:N:order:1\0",
            "forge:v1:N:order:\u00A01", "forge:v1:N:order:١", "forge:v1:N:order:１", "forge:v1:N:order:1\t",
            "Forge:v1:N:order:1", "forge:V1:N:order:1", "forge:v2:N:order:1", "forge:v01:N:order:1", "forge:v1:N:lot:1",
            "forge:v1:N:Order:1", "forge:v1:N:order", "forge:v1::order:1", "forge:v1:.N:order:1", "forge:v1:N:order:1:10",
            "forge:v1:N:operation:1", "forge:v1:N:operation:1:10:extra", "forge:v1:N:order:0", "forge:v1:N:order:01",
            "forge:v1:N:order:+1", "forge:v1:N:order:-1", "forge:v1:N:order:1.0", "forge:v1:N:order:1e2",
            "forge:v1:N:order:9223372036854775808", "forge:v1:N:operation:1:0", "forge:v1:N:operation:1:01",
            "forge:v1:N:operation:1:+10", "forge:v1:N:operation:1:-10", "forge:v1:N:operation:1:2147483648",
            "https://example.test/forge:v1:N:order:1", "file:///forge:v1:N:order:1", "forge:v1:N:order:1?next=/orders",
            new string('x', 256), new string('x', 257), "forge:v1:" + new string('N', 65) + ":order:1",
        ];
        foreach (var code in invalid)
            Assert.Equal("invalid_identification", Assert.Throws<IdentificationException>(() => CanonicalIdentification.Parse(code, "N")).Code);
    }

    [Fact]
    public void Configuration_is_required_first_and_valid_foreign_namespaces_use_ordinal_comparison()
    {
        foreach (var configured in new string?[] { null, "", "bad namespace", "N\n", new string('N', 65) })
        {
            Assert.Equal("identification_not_configured", Assert.Throws<IdentificationException>(() => CanonicalIdentification.Parse("forge:v1:N:order:1", configured)).Code);
            Assert.Equal("identification_not_configured", Assert.Throws<IdentificationException>(() => CanonicalIdentification.Parse("malformed", configured)).Code);
        }
        Assert.Equal("foreign_namespace", Assert.Throws<IdentificationException>(() => CanonicalIdentification.Parse("forge:v1:PlantA:order:1", "planta")).Code);
        Assert.Equal("foreign_namespace", Assert.Throws<IdentificationException>(() => CanonicalIdentification.Parse("forge:v1:Other:operation:1:10", "N")).Code);
        Assert.Equal("invalid_identification", Assert.Throws<IdentificationException>(() => CanonicalIdentification.Parse("forge:v1:Other:order:01", "N")).Code);
    }
}
