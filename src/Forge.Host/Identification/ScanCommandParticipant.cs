using Forge.Execution;
using Forge.Execution.Contracts;
using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;

namespace Forge.Host.Identification;

/// <summary>The Kernel invokes this before replay and records accepted provenance in its existing transaction.</summary>
public sealed class ScanCommandParticipant(ScanSessionAuthority sessionAuthority, IQmsPolicyReader policies,
    QmsOptions options, ScanAttemptRetentionOptions attemptRetention, TimeProvider time) : ICommandTransactionParticipant
{
    public async Task<CommandTransactionEvidence?> ValidateAsync(IKernelUnitOfWork uow, CommandSpec spec, object command,
        CommandContext context, CommandAuthority authority, DateTimeOffset now, CancellationToken ct)
    {
        if (spec.SourceContext == "Identification")
        {
            if (command is CreateScanSession create)
            {
                var createWorker = Operator(authority.SubjectIdentity);
                var createFacts = await sessionAuthority.OperationAsync(uow, context.Principal, create.OperationCode, create.OrderNumber, create.Sequence, false, true, ct);
                var createMachine = await sessionAuthority.MachineAsync(uow, context.Principal, createFacts, create.MachineCode, ct);
                var createPolicy = await policies.RequireAsync(uow, context.Principal, QmsPolicyPurpose.Execution, createFacts.PolicyContext with { MachineCode = createMachine }, now, ct);
                ScanSessionRules.RequireAuthentication(createPolicy, context.Principal, createWorker, context.Authentication, now);
                var store = uow.Store<IScanSessionStore>();
                var channel = ScanSessionRules.Channel(context.Principal, context.Authentication);
                await store.LockChannelAsync(channel, ct);
                var currentId = await store.CurrentAsync(channel, ct);
                var current = currentId is { } id ? await store.FindAsync(id, ct) : null;
                if (current?.CreationRequestFingerprint == ScanSessionRules.CreationFingerprint(create, context))
                    _ = await sessionAuthority.RequireActiveAsync(uow, context, current.Id, current.Version, now, ct);
                else if (currentId != create.PreviousSessionId)
                    throw new CommandRejectedException("scan_channel_generation_changed", "Another operator generation became current. Reload before opening a session.");
            }
            else if (command is StageScan && Guid.TryParse(authority.Resource.Id, out var stageId))
            {
                var current = await uow.Store<IScanSessionStore>().FindAsync(stageId, ct)
                    ?? throw new CommandRejectedException("scan_session_unavailable", "The session is unavailable.");
                _ = await sessionAuthority.RequireActiveAsync(uow, context, stageId, current.Version, now, ct,
                    allowPendingMaterial: true);
            }
            else if (command is InvalidateScanSession && Guid.TryParse(authority.Resource.Id, out var invalidateId))
            {
                var current = await uow.Store<IScanSessionStore>().FindAsync(invalidateId, ct)
                    ?? throw new CommandRejectedException("scan_session_unavailable", "The session is unavailable.");
                if (current.ChannelHash != ScanSessionRules.Channel(context.Principal, context.Authentication)
                    || current.AuthenticationBindingHash != ScanSessionRules.AuthenticationBinding(context.Principal, context.Authentication!))
                    throw new CommandRejectedException("scan_authenticated_session_changed", "The authenticated provider session changed.");
            }
            return null;
        }
        if (command is PickMaterial or DeliverMaterial)
        {
            if (context.ScanSession is null) return null;
            return await ValidateMaterialMovementAsync(uow, command, context, now, ct);
        }
        if (command is not (StartOperation or ReportProduction))
        {
            if (context.ScanSession is not null)
                throw new CommandRejectedException("scan_session_command_unsupported", "This command has no declared scan-context binding.");
            return null;
        }
        if (!options.Configured && context.ScanSession is null
            && !(await uow.Store<IQmsPolicyAdoptionLookup>().ListAsync(ct)).Any(adoption => adoption.Revisions.Any(revision =>
                revision.Content.Purpose == QmsPolicyPurpose.Execution && revision.EffectiveFrom is not null))) return null;
        var (orderNumber, sequence) = command switch
        {
            StartOperation requestedStart => (requestedStart.OrderNumber, requestedStart.Sequence),
            ReportProduction report => (report.OrderNumber, report.Sequence),
            _ => throw new InvalidOperationException("The command discriminator changed.")
        };
        var facts = await sessionAuthority.OperationAsync(uow, context.Principal, null, orderNumber, sequence, false, false, ct);
        var worker = Operator(authority.SubjectIdentity);
        var machine = command is StartOperation startCommand ? ResolveRequestedMachine(facts.Operation, startCommand.MachineCode)
            : (await uow.Store<IShopFloorStore>().FindOpenLaborAsync(worker, ct))?.MachineCode;
        if (context.ScanSession is { } supplied && command is ReportProduction)
            machine ??= (await uow.Store<IScanSessionStore>().FindAsync(supplied.Id, ct))?.SelectedMachineCode;
        var policy = await policies.TryAsync(uow, context.Principal, QmsPolicyPurpose.Execution,
            facts.PolicyContext with { MachineCode = machine }, now, ct);
        if (policy is null)
        {
            if (context.ScanSession is not null)
                throw new CommandRejectedException("scan_policy_not_adopted", "The scan session no longer has an applicable approved adoption.");
            return null;
        }
        ScanSessionRules.RequireAuthentication(policy, context.Principal, worker, context.Authentication, now);
        if (context.ScanSession is null)
        {
            if (policy.Rules.Execution!.Presentation!.RequireServerSession == true)
                throw new CommandRejectedException("scan_session_required", "The current adopted policy requires a server scan session for this actual operation.");
            return null;
        }
        var reference = context.ScanSession;
        var (session, currentPolicy, sessionFacts) = await sessionAuthority.RequireActiveAsync(uow, context,
            reference.Id, reference.Version, now, ct);
        if (session.OperatorPersonId != worker || session.OrderNumber != orderNumber || session.Sequence != sequence
            || session.SelectedMachineCode != machine || currentPolicy.Fingerprint != policy.Fingerprint)
            throw new CommandRejectedException("scan_command_context_mismatch", "The command differs from its current authenticated operator, machine, operation or approved policy.");
        ScanSessionRules.RequireSelections(session, currentPolicy);
        await ScanSessionAuthority.RequireAllInstructionsAsync(uow, session, currentPolicy, sessionFacts, ct);
        if (command is StartOperation start && session.Selections.FirstOrDefault(selection => selection.Kind is "Tool" or "Gauge") is { } tool)
        {
            var components = CanonicalIdentification.Parse(tool.SubjectCode, tool.SubjectCode.Split(':')[2]);
            if (start.ToolCode != components.Key)
                throw new CommandRejectedException("scan_tool_context_mismatch", "The actual start command does not use the staged tool or gauge.");
        }
        var binding = new ScanTransactionBinding(session, currentPolicy, worker, sessionFacts.Fingerprint);
        return new("IdentificationScanSession", CanonicalJson.Fingerprint(new { session.Id, session.Version,
            PolicyFingerprint = currentPolicy.Fingerprint, Operator = worker, OperationFingerprint = sessionFacts.Fingerprint,
            Selections = session.Selections }), binding);
    }

    public async Task RecordAcceptedAsync(IKernelUnitOfWork uow, CommandSpec spec, CommandContext context,
        CommandOutcome outcome, Guid? auditId, CommandTransactionEvidence evidence, DateTimeOffset now, CancellationToken ct)
    {
        if (evidence.Data is not ScanTransactionBinding binding)
            throw new InvalidOperationException("Unexpected scan transaction evidence.");
        if (auditId is null && outcome.Events.Count == 0) return;
        var accepted = new ScanAccepted(ForgeIds.New(time), spec.CommandType, outcome.Change?.EntityType,
            outcome.Result.EntityId, context.CorrelationId, auditId, outcome.Events.Select(envelope => envelope.EventId).ToArray(),
            binding.OperatorPersonId, context.Actor, now, binding.Session.Version, binding.Policy.Fingerprint, binding.Session.Selections, evidence.Fingerprint);
        await uow.Store<IScanSessionStore>().AppendAcceptedAsync(binding.Session.Id, accepted, ct);
    }

    public async Task RecordRejectedAsync(IKernelUnitOfWork uow, CommandSpec spec, object command, CommandContext context,
        CommandAuthority authority, string outcome, string validationCode, Guid auditId, DateTimeOffset now,
        CancellationToken ct)
    {
        var retention = attemptRetention.Current();
        if (retention is null || outcome is not (AuditOutcomes.Rejected or AuditOutcomes.Denied)) return;
        Guid? sessionId = context.ScanSession?.Id;
        int? requestedVersion = context.ScanSession?.Version;
        if (sessionId is null && spec.SourceContext == "Identification" && command is StageScan stage
            && Guid.TryParse(authority.Resource.Id, out var stagedSession))
        {
            sessionId = stagedSession;
            requestedVersion = stage.ExpectedVersion;
        }
        if (sessionId is not { } id || !AttemptCommand(spec, context)) return;

        var store = uow.Store<IScanSessionStore>();
        var session = await store.FindAsync(id, ct);
        if (session is null || session.PrincipalPersonId != context.Principal.PersonId.Value) return;
        var safeCode = SafeValidationCode(validationCode);
        var workstation = context.Authentication?.WorkstationId;
        var attempt = new ScanAttempt(ForgeIds.New(time), id, requestedVersion ?? session.Version, spec.CommandType,
            outcome, safeCode, now, context.CorrelationId, auditId, session.OperatorPersonId, context.Actor,
            SafeSummary(context.Authentication?.Assurance, 40), string.IsNullOrWhiteSpace(workstation)
                ? null : CanonicalJson.Fingerprint(new { workstation }), spec.Resource.Type, spec.Resource.Id);
        await store.AppendAttemptAsync(attempt, retention, ct);
    }

    private async Task<CommandTransactionEvidence> ValidateMaterialMovementAsync(IKernelUnitOfWork uow, object command,
        CommandContext context, DateTimeOffset now, CancellationToken ct)
    {
        var reference = context.ScanSession!;
        var taskId = command switch
        {
            PickMaterial pick => pick.TaskId,
            DeliverMaterial deliver => deliver.TaskId,
            _ => throw new InvalidOperationException("The material-movement discriminator changed.")
        };
        var task = await uow.Store<IInventoryLookup>().FindMaterialTaskAsync(taskId, ct)
            ?? throw new CommandRejectedException("material_task_unavailable", "The actual material task is unavailable.");
        var session = await uow.Store<IScanSessionStore>().FindAsync(reference.Id, ct)
            ?? throw new CommandRejectedException("scan_session_unavailable", "The current scan session is unavailable.");
        if (task.Kind is not (MaterialTaskKind.Stage or MaterialTaskKind.Kit)
            || task.OrderNumber != session.OrderNumber || task.OrderNumber is null || task.LotNumber is null
            || task.FromLocation is null
            || command is PickMaterial && task.Status is not (MaterialTaskStatus.Allocated or MaterialTaskStatus.Picked)
            || command is DeliverMaterial && task.Status is not (MaterialTaskStatus.Picked or MaterialTaskStatus.InTransit or MaterialTaskStatus.Delivered))
            throw new CommandRejectedException("scan_material_task_mismatch", "The session must match an active, exact-lot task for its actual order and operation.");

        var (active, currentPolicy, currentFacts) = await sessionAuthority.RequireActiveAsync(uow, context,
            reference.Id, reference.Version, now, ct, materialTaskId: taskId);
        var worker = active.OperatorPersonId;
        ScanSessionRules.RequireAuthentication(currentPolicy, context.Principal, worker, context.Authentication, now);
        if (active.OrderNumber != task.OrderNumber || active.OrderNumber != currentFacts.Order.OrderNumber
            || active.Sequence != currentFacts.Operation.Sequence)
            throw new CommandRejectedException("scan_command_context_mismatch", "The material task differs from its current authenticated operation session or approved policy.");
        ScanSessionRules.RequireSelections(active, currentPolicy);
        await ScanSessionAuthority.RequireAllInstructionsAsync(uow, active, currentPolicy, currentFacts, ct);

        var material = active.Selections.SingleOrDefault(selection => selection.Kind == "Material");
        var location = active.Selections.SingleOrDefault(selection => selection.Kind == "Location");
        if (material is null || location is null)
            throw new CommandRejectedException("scan_material_location_required", "Stage the exact material lot and the task's current source or destination location.");
        var materialIdentity = ParseSelection(material, "material");
        if (materialIdentity.PartNumber != task.PartNumber || materialIdentity.LotNumber != task.LotNumber)
            throw new CommandRejectedException("scan_material_task_mismatch", "The staged material identity differs from the actual task part and lot.");
        var locationIdentity = ParseSelection(location, "location");
        var expectedLocation = command switch
        {
            PickMaterial => task.FromLocation,
            DeliverMaterial => task.ToLocation,
            _ => throw new InvalidOperationException("The material-movement discriminator changed.")
        };
        var scannedLocation = command switch
        {
            PickMaterial pick => pick.ScannedLocation,
            DeliverMaterial deliver => deliver.ScannedLocation,
            _ => throw new InvalidOperationException("The material-movement discriminator changed.")
        };
        if (locationIdentity.LocationCode != expectedLocation || scannedLocation != expectedLocation)
            throw new CommandRejectedException("scan_material_location_mismatch", "The staged and submitted location must equal the actual task source or destination.");
        if (command is PickMaterial selectedPick && (selectedPick.ScannedLot != task.LotNumber
            || selectedPick.ScannedMaterial is { } typed && (typed.PartNumber != task.PartNumber || typed.LotNumber != task.LotNumber)))
            throw new CommandRejectedException("scan_material_identity_mismatch", "The pick command must preserve the task's exact scanned lot identity.");

        var binding = new ScanTransactionBinding(active, currentPolicy, worker, currentFacts.Fingerprint);
        return new("IdentificationScanSession", CanonicalJson.Fingerprint(new { active.Id, active.Version,
            PolicyFingerprint = currentPolicy.Fingerprint, Operator = worker, OperationFingerprint = currentFacts.Fingerprint,
            Task = new { task.TaskId, task.OrderNumber, task.PartNumber, task.LotNumber, task.FromLocation, task.ToLocation },
            Selections = active.Selections }), binding);
    }

    private static CanonicalIdentification ParseSelection(ScanSelection selection, string expectedKind)
    {
        var parts = selection.SubjectCode.Split(':');
        if (parts.Length < 3) throw new CommandRejectedException("scan_selection_changed", "The staged canonical source is unavailable.");
        var parsed = CanonicalIdentification.Parse(selection.SubjectCode, parts[2]);
        if (parsed.Kind != expectedKind)
            throw new CommandRejectedException("scan_selection_changed", "The staged canonical source has the wrong identity kind.");
        return parsed;
    }

    private static Guid Operator(string? identity)
    {
        var value = identity?.Split(';')[0];
        return value?.StartsWith("operator:", StringComparison.Ordinal) == true && Guid.TryParse(value.AsSpan(9), out var person)
            ? person : throw new CommandRejectedException("scan_operator_unresolved", "The ordinary owner did not resolve the actual acting operator.");
    }

    private static string? ResolveRequestedMachine(OrderOperationInfo operation, string? requested) =>
        string.IsNullOrWhiteSpace(requested) ? operation.EligibleMachines.Count == 1 ? operation.EligibleMachines[0] : null
            : BusinessCode.Require(requested, "Machine");

    private static bool AttemptCommand(CommandSpec spec, CommandContext context) =>
        spec.SourceContext == "Identification" && spec.CommandType == nameof(StageScan)
        || context.ScanSession is not null && (spec.CommandType is nameof(StartOperation) or nameof(ReportProduction)
            or nameof(PickMaterial) or nameof(DeliverMaterial));

    private static string SafeValidationCode(string value) => value.Length is > 0 and <= 100
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
        ? value : "validation_failed";

    private static string? SafeSummary(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl) ? null : value;
}
