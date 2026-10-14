using Forge.Kernel.Commands;

namespace Forge.Host.Identification;

public sealed class ScannerOptions(IConfiguration configuration)
{
    public ScannerConfiguration View()
    {
        var adapters = new List<ScannerAdapter>();
        foreach (var section in configuration.GetSection("Forge:Identification:ScannerAdapters").GetChildren())
        {
            var id = section["Id"]; var name = section["DisplayName"];
            var symbologies = section.GetSection("Symbologies").GetChildren().Select(child => child.Value).ToArray();
            if (!Safe(id, 80) || !Safe(name, 120) || !Enum.TryParse<ScanInputMode>(section["InputMode"], out var mode)
                || mode != ScanInputMode.ConfiguredAdapter || !bool.TryParse(section["Enabled"], out var enabled)
                || symbologies.Length == 0 || symbologies.Any(symbology => !Safe(symbology, 40))) continue;
            adapters.Add(new(id!, name!, mode, symbologies.Select(symbology => symbology!).ToArray(), enabled, enabled ? "Configured" : "Disabled"));
        }
        return new(adapters.Count != 0, adapters, Enum.GetValues<ScanInputMode>());
    }

    public void Require(ScanInputMode mode, string? adapterId, string? symbology, bool manualAllowed)
    {
        if (!Enum.IsDefined(mode) || mode == ScanInputMode.Manual && !manualAllowed)
            throw new CommandRejectedException("scan_input_mode_unavailable", "This input mode is unavailable under the adopted policy.");
        if (symbology is not null && !Safe(symbology, 40))
            throw new CommandRejectedException("scan_symbology_invalid", "Supply a bounded declared symbology.");
        if (mode == ScanInputMode.ConfiguredAdapter)
        {
            var adapter = View().Adapters.FirstOrDefault(candidate => candidate.Id == adapterId && candidate.Enabled);
            if (adapter is null || symbology is not null && !adapter.Symbologies.Contains(symbology, StringComparer.Ordinal))
                throw new CommandRejectedException("scan_adapter_unavailable", "The declared adapter or symbology is not currently configured.");
        }
        else if (adapterId is not null)
            throw new CommandRejectedException("scan_adapter_invalid", "A configured adapter ID applies only to configured adapter input.");
    }

    private static bool Safe(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
}
