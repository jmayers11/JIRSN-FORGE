using System.Globalization;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Workforce;

namespace Forge.Host.Training;

/// <summary>Opens a reassessment request (and so notifies technical trainers) for every qualification expiring within 30 days. Runs as the system.</summary>
public sealed class TrainingExpiryScanner(TrainingRequestService training, TimeProvider time)
{
    private static readonly ForgePrincipal SystemPrincipal = new(
        new PersonId(Guid.Empty),
        "Training reassessment requests",
        true,
        new HashSet<string>(StringComparer.Ordinal) { Roles.TrainingRequests },
        new HashSet<ProgramId>(),
        DataMarking.None);

    public Task<CommandResult> RunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var run = ForgeIds.New(time);
        var key = "system:training-requests:" + now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ":" + run.ToString("N", CultureInfo.InvariantCulture);
        var context = new CommandContext(SystemPrincipal, run, key, CommandSource.System, "Qualifications expiring soon need reassessment", SystemActors.TrainingRequests);
        return training.OpenExpiryRequestsAsync(new OpenExpiryTrainingRequests(TrainingRequestService.DefaultExpiryWindowDays), context, ct);
    }
}

public sealed class TrainingExpiryMonitor(TrainingExpiryScanner scanner, TimeProvider time, ILogger<TrainingExpiryMonitor> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stoppingToken);
                await scanner.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Training reassessment requests failed; retrying at the next interval.");
            }
        }
    }
}
