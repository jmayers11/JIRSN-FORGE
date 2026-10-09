using System.Text.Json;
using Forge.Inventory;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Maintenance;

namespace Forge.Host.Installation;

/// <summary>
/// A part issued to a maintenance work order (or an approved alternate issued in its place) is recorded as
/// installed on the work order's asset when the work order names its position (V2.9, scenario 4). Delivered
/// at least once; the command's idempotency key (the ledger entry) makes it happen once.
/// </summary>
public sealed class InstallationSubscriber(AssetService assets, ILogger<InstallationSubscriber> logger) : IEventSubscriber
{
    private static readonly ForgePrincipal SystemPrincipal = new(
        new PersonId(Guid.Empty),
        "Installation from issues",
        true,
        new HashSet<string>(StringComparer.Ordinal) { Roles.MaintenanceInstallation },
        new HashSet<ProgramId>(),
        DataMarking.ControlledUnclassified | DataMarking.ExportControlled | DataMarking.Proprietary);

    public string Name => "work-order-installation";

    public bool Handles(string eventType) => eventType is nameof(SparesIssued) or nameof(AlternateUsed);

    public async Task HandleAsync(long sequence, EventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        RecordIssuedInstall? command;
        if (envelope.EventType == nameof(SparesIssued))
        {
            var e = JsonSerializer.Deserialize<SparesIssued>(envelope.Payload, CanonicalJson.Options)!;
            command = new RecordIssuedInstall(e.TransactionId, e.WorkOrderId, e.PartNumber, e.Quantity, e.PartRevision, null, null);
        }
        else
        {
            var e = JsonSerializer.Deserialize<AlternateUsed>(envelope.Payload, CanonicalJson.Options)!;
            command = e.WorkOrderId is { } w ? new RecordIssuedInstall(e.TransactionId, w, e.AlternatePart, e.Quantity, e.PartRevision, e.PrimaryPart, e.AlternateId) : null;
        }

        if (command is null)
        {
            return;
        }

        var context = new CommandContext(SystemPrincipal, envelope.CorrelationId, $"system:installation:{command.TransactionId}", CommandSource.System, $"Issued to work order {command.WorkOrderId}", SystemActors.Installation);
        try
        {
            await assets.RecordIssuedInstallAsync(command, context, ct);
        }
        catch (CommandRejectedException ex)
        {
            // A rejection is a fact about the data (the asset was decommissioned since, say); it is logged, not retried forever.
            logger.LogWarning("Issue {Transaction} to work order {WorkOrder} was not recorded as installed: {Code} {Message}", command.TransactionId, command.WorkOrderId, ex.Code, ex.Message);
        }
    }
}
