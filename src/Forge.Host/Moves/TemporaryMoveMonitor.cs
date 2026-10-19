using System.Globalization;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Resources.Contracts;
using Forge.Workforce;

namespace Forge.Host.Moves;

/// <summary>
/// ADR 0057 §5: reminds supervisors before a temporary move ends and treats a worker whose move ran out, with nobody
/// extending it, as back home. Runs as the system, on the home site's date.
/// </summary>
public sealed class TemporaryMoveReconciler(WorkforceService workforce, ISiteClock siteClock, TimeProvider time)
{
    private static readonly ForgePrincipal SystemPrincipal = new(
        new PersonId(Guid.Empty),
        "Temporary move reminders",
        true,
        new HashSet<string>(StringComparer.Ordinal) { Roles.WorkforceMoves },
        new HashSet<ProgramId>(),
        DataMarking.None);

    public Task<CommandResult> RunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var today = siteClock.DateAt(now);
        // Each run is its own command: a run that finds nothing new must not replay an earlier run's result.
        var run = ForgeIds.New(time);
        var key = "system:workforce-moves:" + now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ":" + run.ToString("N", CultureInfo.InvariantCulture);
        var context = new CommandContext(SystemPrincipal, run, key, CommandSource.System, "Temporary move reminders and returns", SystemActors.WorkforceMoves);
        return workforce.ReconcileTemporaryMovesAsync(new ReconcileTemporaryMoves(today), context, ct);
    }
}

public sealed class TemporaryMoveMonitor(TemporaryMoveReconciler reconciler, TimeProvider time, ILogger<TemporaryMoveMonitor> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stoppingToken);
                await reconciler.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Temporary move reminders failed; retrying at the next interval.");
            }
        }
    }
}
