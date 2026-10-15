namespace Forge.Host.Identification;

public sealed record ScanAttemptRetentionPolicy(int RetentionDays, int MaxPerSession);

/// <summary>
/// Raw scan input is never retained. Minimal rejected-attempt metadata is persisted only when an
/// operator explicitly configures both a retention window and a per-session cap.
/// </summary>
public sealed class ScanAttemptRetentionOptions(IConfiguration configuration)
{
    private const string Prefix = "Forge:Identification:ScanAttemptRetention:";

    public ScanAttemptRetentionPolicy? Current()
    {
        if (!bool.TryParse(configuration[Prefix + "Enabled"], out var enabled) || !enabled) return null;
        if (!int.TryParse(configuration[Prefix + "RetentionDays"], out var days) || days is < 1 or > 3650
            || !int.TryParse(configuration[Prefix + "MaxPerSession"], out var maximum) || maximum is < 1 or > 1000)
            return null;
        return new(days, maximum);
    }
}
