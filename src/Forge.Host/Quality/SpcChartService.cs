using System.Globalization;
using System.Text.Json;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality;
using Forge.Quality.Contracts;

namespace Forge.Host.Quality;

/// <summary>One charted characteristic in the overview: its capability and whether anything signals.</summary>
public sealed record SpcSummary(SpcCharacteristicKey Key, string Name, string? Unit, bool Critical, int Points, decimal? Cpk, decimal RequiredCpk, bool? Capable, int Violations, int OpenSignals, DateTimeOffset? Last);

/// <summary>Builds control charts from recorded inspection measurements, a year back, against the current inspection plan's limits.</summary>
public sealed class SpcChartService(IKernelUnitOfWorkFactory unitOfWork, TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(366);

    public async Task<IReadOnlyList<SpcSummary>> SummariesAsync(string? partNumber, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var (charts, _) = await ChartsAsync(uow, partNumber is null ? null : BusinessCode.Require(partNumber, "Part"), null, ct);
        var open = (await uow.Store<ISpcLookup>().ListSignalsAsync(true, ct)).GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.Count());
        return charts.Select(c => new SpcSummary(c.Key, c.Name, c.Unit, c.Critical, c.Points.Count, c.Cpk, c.RequiredCpk, c.Capable, c.Violations.Count, open.GetValueOrDefault(c.Key),
                c.Points.Count == 0 ? null : c.Points[^1].At))
            .OrderByDescending(s => s.OpenSignals).ThenBy(s => s.Capable ?? true).ThenBy(s => s.Key.PartNumber, StringComparer.Ordinal).ThenBy(s => s.Key.Sequence).ThenBy(s => s.Key.Number).ToList();
    }

    public async Task<SpcChart> ChartAsync(SpcCharacteristicKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var (charts, _) = await ChartsAsync(uow, BusinessCode.Require(key.PartNumber, "Part"), key, ct);
        return charts.SingleOrDefault() ?? throw new CommandRejectedException("characteristic_not_found", $"{key.PartNumber} has no measured characteristic {key.Number} after operation {key.Sequence}.");
    }

    public async Task<(SpcPolicy Policy, IReadOnlyList<SpcCharacteristicKey> Critical)> PolicyAsync(CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var spc = uow.Store<ISpcLookup>();
        return (await spc.PolicyAsync(ct), await spc.ListCriticalAsync(ct));
    }

    public async Task<IReadOnlyList<SpcSignal>> SignalsAsync(bool openOnly, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return await uow.Store<ISpcLookup>().ListSignalsAsync(openOnly, ct);
    }

    /// <summary>The signals a just-recorded inspection raises: rules broken at that inspection's own point.</summary>
    public async Task<(IReadOnlyList<SpcSignal> Signals, bool Notify)> SignalsForAsync(long inspectionId, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var inspection = await uow.Store<IQualityLookup>().FindInspectionAsync(inspectionId, ct);
        if (inspection is null) return ([], false);
        var (charts, policy) = await ChartsAsync(uow, inspection.PartNumber, null, ct);
        var signals = new List<SpcSignal>();
        foreach (var chart in charts.Where(c => c.Key.Sequence == inspection.Sequence))
            foreach (var v in chart.Violations.Where(v => v.InspectionId == inspectionId))
                signals.Add(new SpcSignal(0, chart.Key, chart.Name, inspectionId, inspection.OrderNumber, v.Rule, v.Description, chart.Points[v.PointIndex].Value,
                    time.GetUtcNow(), SpcSignalStatus.Open, null, null, null));
        return (signals, policy.NotifyOnSignal);
    }

    private async Task<(IReadOnlyList<SpcChart> Charts, SpcPolicy Policy)> ChartsAsync(IKernelUnitOfWork uow, string? partNumber, SpcCharacteristicKey? only, CancellationToken ct)
    {
        var quality = uow.Store<IQualityLookup>();
        var spc = uow.Store<ISpcLookup>();
        var policy = await spc.PolicyAsync(ct);
        var critical = (await spc.ListCriticalAsync(ct)).ToHashSet();
        var now = time.GetUtcNow();
        var measurements = await spc.ListMeasurementsAsync(partNumber, now - Window, ct);
        var plans = await quality.ListPlansAsync(partNumber, ct);
        var charts = new List<SpcChart>();
        foreach (var group in measurements.GroupBy(m => m.Key))
        {
            if (only is not null && group.Key != only) continue;
            // The current plan's limits; without a plan, the limits recorded with the latest measurement.
            var latest = group.OrderBy(x => x.Point.At).ThenBy(x => x.Point.InspectionId).Last();
            var planned = plans.Where(p => p.PartNumber == group.Key.PartNumber && p.Sequence == group.Key.Sequence)
                .SelectMany(p => p.Characteristics).FirstOrDefault(c => c.Number == group.Key.Number);
            var characteristic = planned ?? new CharacteristicInfo(group.Key.Number, latest.Name, CharacteristicKind.Variable, null, latest.Lower, latest.Upper, null);
            charts.Add(SpcEngine.Chart(group.Key, characteristic, group.Select(x => x.Point).ToList(), critical.Contains(group.Key), policy));
        }

        return (charts, policy);
    }
}

/// <summary>
/// Owner decision 2026-11-01: when a new measurement breaks a control-chart rule, quality engineers are told (through the notification
/// rule on SpcSignalDetected). Nothing is held. Delivered at least once; a signal is recorded once per inspection, characteristic and rule.
/// </summary>
public sealed class SpcSubscriber(SpcChartService charts, SpcService spc, ILogger<SpcSubscriber> logger) : IEventSubscriber
{
    private static readonly ForgePrincipal SystemPrincipal = new(
        new PersonId(Guid.Empty),
        "Statistical process control",
        true,
        new HashSet<string>(StringComparer.Ordinal) { Roles.SpcMonitor },
        new HashSet<ProgramId>(),
        DataMarking.None);

    public string Name => "spc-signals";

    public bool Handles(string eventType) => eventType == nameof(InspectionRecorded);

    public async Task HandleAsync(long sequence, EventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var e = JsonSerializer.Deserialize<InspectionRecorded>(envelope.Payload, CanonicalJson.Options)!;
        var (signals, notify) = await charts.SignalsForAsync(e.InspectionId, ct);
        if (signals.Count == 0 || !notify) return;
        var context = new CommandContext(SystemPrincipal, envelope.CorrelationId, string.Create(CultureInfo.InvariantCulture, $"system:spc:{e.InspectionId}"), CommandSource.System,
            string.Create(CultureInfo.InvariantCulture, $"Control-chart check of inspection {e.InspectionId}"), SystemActors.SpcMonitor);
        try
        {
            await spc.RecordSignalsAsync(new RecordSpcSignals(signals), context, ct);
        }
        catch (CommandRejectedException ex)
        {
            logger.LogWarning("Recording SPC signals for inspection {Inspection} was refused: {Code} {Message}", e.InspectionId, ex.Code, ex.Message);
        }
    }
}
