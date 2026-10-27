using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Resources.Contracts;

namespace Forge.Planning;

/// <summary>
/// How far out an impact may be for each band (owner decision 2026-10-27): the current shift up to 6 days is High, 7 to 29 days is
/// Medium, 30 days or more is Low. Days are counted in site calendar days from today.
/// </summary>
public sealed record RiskRadarOptions(int HighMaxDays = 6, int MediumMaxDays = 29);

public enum RiskRadarView { Current, SevenDays, ThirtyDays }

public enum RiskBand { High, Medium, Low }

/// <summary>One source-backed signal placed on the radar by when it starts to affect work. No score is computed and nothing is merged.</summary>
public sealed record RiskRadarItem(
    RiskBand Band,
    int DaysOut,
    DateTimeOffset ImpactAt,
    string ImpactBasis,
    OperationalEvidenceSignal Signal);

public sealed record RiskRadarBandCount(RiskBand Band, int Count, string Rule);

public sealed record RiskRadar(
    RiskRadarView View,
    DateOnly Today,
    string TimeZone,
    IReadOnlyList<RiskRadarBandCount> Bands,
    IReadOnlyList<RiskRadarItem> Items,
    int OutsideView,
    OperationalEvidenceResponse Evidence);

/// <summary>
/// The Production Risk Radar (owner request 2026-10-27). It reads the operational evidence in one snapshot and bands each signal by
/// its impact time; views are the current day, the next 7 days and the next 30 days (the 30-day view also lists anything later as Low).
/// </summary>
public sealed class RiskRadarService(OperationalEvidenceService evidence, IKernelUnitOfWorkFactory unitOfWork, RiskRadarOptions options)
{
    public async Task<RiskRadar> BuildAsync(ForgePrincipal principal, RiskRadarView view, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var read = await evidence.ReadAsync(principal, null, 7, view == RiskRadarView.ThirtyDays ? 31 : 7, ct);
        string zone;
        await using (var uow = await unitOfWork.BeginSnapshotAsync(ct))
            zone = await uow.SiteTimeZoneAsync(ct);
        var today = SiteTime.DateAtOrUtc(read.Read.ReadStartedAt, zone);
        var all = read.Signals
            .Where(signal => signal.ImpactAt is not null)
            .Select(signal =>
            {
                var days = Math.Max(0, SiteTime.DateAtOrUtc(signal.ImpactAt!.Value, zone).DayNumber - today.DayNumber);
                return new RiskRadarItem(Band(days, options), days, signal.ImpactAt.Value, signal.ImpactBasis ?? string.Empty, signal);
            })
            .ToList();
        var inView = all.Where(item => view switch
        {
            RiskRadarView.Current => item.DaysOut == 0,
            RiskRadarView.SevenDays => item.DaysOut <= 6,
            _ => true,
        }).OrderBy(item => item.Band).ThenBy(item => item.ImpactAt).ThenBy(item => item.Signal.SignalId, StringComparer.Ordinal).ToList();
        var rules = new Dictionary<RiskBand, string>
        {
            [RiskBand.High] = $"Impact today up to {options.HighMaxDays} days out",
            [RiskBand.Medium] = $"Impact {options.HighMaxDays + 1} to {options.MediumMaxDays} days out",
            [RiskBand.Low] = $"Impact {options.MediumMaxDays + 1} days out or later",
        };
        return new RiskRadar(view, today, zone,
            [.. Enum.GetValues<RiskBand>().Select(band => new RiskRadarBandCount(band, inView.Count(item => item.Band == band), rules[band]))],
            inView, all.Count - inView.Count, read);
    }

    internal static RiskBand Band(int days, RiskRadarOptions options) => days <= options.HighMaxDays ? RiskBand.High : days <= options.MediumMaxDays ? RiskBand.Medium : RiskBand.Low;

    public static RiskRadarView ParseView(string? view) => view?.Trim().ToLowerInvariant() switch
    {
        null or "" or "current" => RiskRadarView.Current,
        "7" or "7d" or "seven" or "sevendays" => RiskRadarView.SevenDays,
        "30" or "30d" or "thirty" or "thirtydays" => RiskRadarView.ThirtyDays,
        _ => throw new CommandRejectedException("invalid_view", "Choose the current, 7-day or 30-day view."),
    };
}
