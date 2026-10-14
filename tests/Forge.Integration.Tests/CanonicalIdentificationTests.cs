using Forge.Host.Identification;
using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Serialization;

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

    [Fact]
    public void Composite_keys_roundtrip_exact_UTF8_and_do_not_normalize_or_alias_separators()
    {
        const string code = "forge:v1:N:material:PART%2F50%25:LOT%2F%C3%A9";
        var material = CanonicalIdentification.Parse(code, "N");
        Assert.Equal(("PART/50%", "LOT/é", "material"), (material.PartNumber, material.LotNumber, material.Kind));
        Assert.Null(material.OrderNumber);
        Assert.Null(material.Sequence);
        Assert.Equal(code, material.Code);
        var location = CanonicalIdentification.Parse("forge:v1:N:location:RACK%2F%25%C3%A9", "N");
        Assert.Equal("RACK/%é", location.LocationCode);
        Assert.Equal("%C3%A9", CanonicalIdentification.EncodeKey("é"));
        Assert.Equal("e%CC%81", CanonicalIdentification.EncodeKey("e\u0301"));
        var part = new string('界', 40);
        var lot = new string('é', 40);
        var longCode = "forge:v1:N:material:" + CanonicalIdentification.EncodeKey(part) + ":" + CanonicalIdentification.EncodeKey(lot);
        Assert.True(longCode.Length > 256);
        var longMaterial = CanonicalIdentification.Parse(longCode, "N");
        Assert.Equal(part, longMaterial.PartNumber);
        Assert.Equal(lot, longMaterial.LotNumber);
        Assert.Equal(1024, CanonicalIdentification.MaxCodeLength);
        Assert.Equal(40, CanonicalIdentification.MaxKeyLength);
    }

    [Fact]
    public void Escaping_rejects_noncanonical_aliases_invalid_UTF8_empty_and_oversized_keys()
    {
        string[] invalid =
        [
            "forge:v1:N:material:P", "forge:v1:N:material::L", "forge:v1:N:material:P:", "forge:v1:N:material:P:L:extra",
            "forge:v1:N:location:", "forge:v1:N:location:A/B", "forge:v1:N:location:A%", "forge:v1:N:location:A%2",
            "forge:v1:N:location:A%2fB", "forge:v1:N:location:%41", "forge:v1:N:location:é", "forge:v1:N:location:%C0%AF",
            "forge:v1:N:location:%ED%A0%80", "forge:v1:N:location:%F4%90%80%80", "forge:v1:N:location:%00",
            "forge:v1:N:location:%20", "forge:v1:N:location:%C2%A0", "forge:v1:N:location:" + new string('A', 41),
            "forge:v1:N:material:" + new string('P', 41) + ":L", "forge:v1:N:location:" + new string('A', 1025),
        ];
        foreach (var code in invalid)
            Assert.Equal("invalid_identification", Assert.Throws<IdentificationException>(() => CanonicalIdentification.Parse(code, "N")).Code);
        Assert.Equal("invalid_identification", Assert.Throws<IdentificationException>(() => CanonicalIdentification.EncodeKey("\uD800")).Code);
        Assert.Equal("foreign_namespace", Assert.Throws<IdentificationException>(() => CanonicalIdentification.Parse("forge:v1:Other:material:P:L", "N")).Code);
    }

    [Fact]
    public void Legacy_pick_fingerprint_is_preserved_and_typed_material_is_bound_into_replay()
    {
        var legacy = new PickMaterial(17, "WH1", "SAME/LOT", 2m);
        Assert.Equal(CanonicalJson.Serialize(new { taskId = 17L, scannedLocation = "WH1", scannedLot = "SAME/LOT", quantity = (decimal?)2m }), CanonicalJson.Serialize(legacy));
        var typed = legacy with { ScannedMaterial = new ScannedMaterialIdentity("PART-A", "SAME/LOT") };
        Assert.NotEqual(CanonicalJson.Fingerprint(legacy), CanonicalJson.Fingerprint(typed));
        Assert.NotEqual(CanonicalJson.Fingerprint(typed), CanonicalJson.Fingerprint(typed with { ScannedMaterial = new("PART-B", "SAME/LOT") }));
    }
}
