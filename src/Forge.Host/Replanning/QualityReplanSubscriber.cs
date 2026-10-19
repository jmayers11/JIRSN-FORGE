using System.Globalization;
using System.Text.Json;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Planning;
using Forge.Quality;
using Forge.Quality.Contracts;

namespace Forge.Host.Replanning;

/// <summary>
/// ADR 0057 §2.4: when an inspection fails (which raises an NCR) or an NCR is raised on an order, Forge re-plans at
/// once and records which orders' projected completions move, by how much and why. Delivered at least once; the
/// command's idempotency key (the NCR) makes it happen once.
/// </summary>
public sealed class QualityReplanSubscriber(ScheduleService schedules, IKernelUnitOfWorkFactory unitOfWork, TimeProvider time, ILogger<QualityReplanSubscriber> logger) : IEventSubscriber
{
    private static readonly ForgePrincipal SystemPrincipal = new(
        new PersonId(Guid.Empty),
        "Re-plan after quality events",
        true,
        new HashSet<string>(StringComparer.Ordinal) { Roles.QualityReplan },
        new HashSet<ProgramId>(),
        DataMarking.ControlledUnclassified | DataMarking.ExportControlled | DataMarking.Proprietary);

    public string Name => "quality-replan";

    /// <summary>A quality event older than this is history, not news: the subscriber's first run on an existing database does not re-plan for it.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromHours(12);

    public bool Handles(string eventType) => eventType == nameof(NcrChanged);

    public async Task HandleAsync(long sequence, EventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var e = JsonSerializer.Deserialize<NcrChanged>(envelope.Payload, CanonicalJson.Options)!;
        if (e.Status != nameof(NcrStatus.Open) || e.OrderNumber is not { } order || envelope.OccurredAt < time.GetUtcNow() - Fresh)
        {
            return;
        }

        NcrInfo? ncr;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            ncr = await read.Store<IQualityLookup>().FindNcrAsync(e.NcrId, ct);
        }

        if (ncr is not { Status: NcrStatus.Open or NcrStatus.AwaitingApproval })
        {
            return; // decided already: the next proposal plans its re-inspection
        }

        // Raised once; a rejected disposition that reopens it is the same quality event, not a new one.
        var id = e.NcrId.ToString(CultureInfo.InvariantCulture);
        var cause = ncr is { Sequence: { } s } && ncr.Defect == "Failed inspection"
            ? $"the inspection after operation {s} of order {order} failed (NCR {id})"
            : $"NCR {id} ({ncr?.Defect ?? "nonconformance"}) on order {order}";
        var context = new CommandContext(SystemPrincipal, envelope.CorrelationId, $"system:quality-replan:ncr:{id}", CommandSource.System, $"Re-plan after {cause}", SystemActors.QualityReplan);
        try
        {
            await schedules.ReplanAfterQualityAsync(new ReplanAfterQuality(order, cause), context, ct);
        }
        catch (CommandRejectedException ex)
        {
            logger.LogWarning("Re-plan after NCR {Ncr} was refused: {Code} {Message}", e.NcrId, ex.Code, ex.Message);
        }
    }
}
