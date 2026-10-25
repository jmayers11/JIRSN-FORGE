using System.Globalization;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning;

namespace Forge.Host.Planning;

/// <summary>Checks released orders for due-date risk from material and tells planners about changes (Sprint 10 WW-S10-30). Runs as the system.</summary>
public sealed class MaterialRiskScanner(MaterialRiskService risks, TimeProvider time)
{
    private static readonly ForgePrincipal SystemPrincipal = new(
        new PersonId(Guid.Empty),
        "Material risk check",
        true,
        new HashSet<string>(StringComparer.Ordinal) { Roles.MaterialRisk },
        new HashSet<ProgramId>(),
        DataMarking.None);

    public Task<CommandResult> RunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var run = ForgeIds.New(time);
        var key = "system:material-risk:" + now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ":" + run.ToString("N", CultureInfo.InvariantCulture);
        var context = new CommandContext(SystemPrincipal, run, key, CommandSource.System, "Hourly material risk check", SystemActors.MaterialRisk);
        return risks.ScanAsync(new ScanMaterialRisk(), context, ct);
    }
}

public sealed class MaterialRiskMonitor(MaterialRiskScanner scanner, TimeProvider time, ILogger<MaterialRiskMonitor> logger) : BackgroundService
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
                logger.LogError(ex, "The material risk check failed; retrying at the next interval.");
            }
        }
    }
}
