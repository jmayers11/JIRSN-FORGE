using Forge.Host.Identification;
using Forge.Kernel.Commands;
using Microsoft.Extensions.Configuration;

namespace Forge.Integration.Tests;

public sealed class ScannerConfigurationTests
{
    [Fact]
    public void Explicit_server_adapter_settings_are_loaded_without_a_client_supplied_availability_field()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forge:Identification:ScannerAdapters:0:Id"] = "actual-configured-reader",
            ["Forge:Identification:ScannerAdapters:0:DisplayName"] = "Configured example reader",
            ["Forge:Identification:ScannerAdapters:0:InputMode"] = "ConfiguredAdapter",
            ["Forge:Identification:ScannerAdapters:0:Enabled"] = "true",
            ["Forge:Identification:ScannerAdapters:0:Symbologies:0"] = "QR",
        }).Build();
        var options = new ScannerOptions(configuration);
        var adapter = Assert.Single(options.View().Adapters);
        Assert.True(options.View().Configured); Assert.Equal("Configured", adapter.Availability);
        options.Require(ScanInputMode.ConfiguredAdapter, "actual-configured-reader", "QR", false);
        configuration["Forge:Identification:ScannerAdapters:0:Enabled"] = "false";
        Assert.Equal("Disabled", Assert.Single(options.View().Adapters).Availability);
        Assert.Equal("scan_adapter_unavailable", Assert.Throws<CommandRejectedException>(() =>
            options.Require(ScanInputMode.ConfiguredAdapter, "actual-configured-reader", "QR", false)).Code);
    }

    [Fact]
    public void Missing_or_invalid_adapter_configuration_cannot_become_declared_device_authority()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forge:Identification:ScannerAdapters:0:Id"] = "unsafe\nreader",
            ["Forge:Identification:ScannerAdapters:0:DisplayName"] = "Unsafe input",
            ["Forge:Identification:ScannerAdapters:0:InputMode"] = "ConfiguredAdapter",
            ["Forge:Identification:ScannerAdapters:0:Enabled"] = "true",
            ["Forge:Identification:ScannerAdapters:0:Symbologies:0"] = "QR",
        }).Build();
        var options = new ScannerOptions(configuration);
        Assert.Empty(options.View().Adapters); Assert.False(options.View().Configured);
        Assert.Equal("scan_adapter_unavailable", Assert.Throws<CommandRejectedException>(() =>
            options.Require(ScanInputMode.ConfiguredAdapter, "body-cannot-register-a-device", "QR", false)).Code);
    }
}
