using System.Globalization;
using Forge.Hazcom;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Resources.Contracts;

namespace Forge.Host.Hazcom;

/// <summary>Daily: SDS whose three-year review falls due within 30 days are flagged once, and the EHS Coordinator is told.</summary>
public sealed class HazcomReviewScanner(HazcomService hazcom, ISiteClock siteClock, TimeProvider time)
{
    private static readonly ForgePrincipal SystemPrincipal = new(
        new PersonId(Guid.Empty), "SDS reviews", true, new HashSet<string>(StringComparer.Ordinal) { Roles.Hazcom }, new HashSet<ProgramId>(), DataMarking.None);

    public Task<CommandResult> RunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var today = siteClock.DateAt(now);
        var key = "system:hazcom:reviews:" + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return hazcom.FlagReviewsAsync(new FlagSdsReviews(today), new CommandContext(SystemPrincipal, ForgeIds.New(time), key, CommandSource.System, "SDS review reminders", SystemActors.Hazcom), ct);
    }
}

public sealed class HazcomReviewMonitor(HazcomReviewScanner scanner, TimeProvider time, ILogger<HazcomReviewMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(6), time, stoppingToken);
                await scanner.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "SDS review reminders failed; retrying later.");
            }
        }
    }
}
