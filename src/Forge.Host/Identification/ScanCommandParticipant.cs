using Forge.Execution;
using Forge.Execution.Contracts;
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
    QmsOptions options, TimeProvider time) : ICommandTransactionParticipant
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
                _ = await sessionAuthority.RequireActiveAsync(uow, context, stageId, current.Version, now, ct);
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

    private static Guid Operator(string? identity)
    {
        var value = identity?.Split(';')[0];
        return value?.StartsWith("operator:", StringComparison.Ordinal) == true && Guid.TryParse(value.AsSpan(9), out var person)
            ? person : throw new CommandRejectedException("scan_operator_unresolved", "The ordinary owner did not resolve the actual acting operator.");
    }

    private static string? ResolveRequestedMachine(OrderOperationInfo operation, string? requested) =>
        string.IsNullOrWhiteSpace(requested) ? operation.EligibleMachines.Count == 1 ? operation.EligibleMachines[0] : null
            : BusinessCode.Require(requested, "Machine");
}
