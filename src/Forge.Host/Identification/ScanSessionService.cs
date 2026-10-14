using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Host.Identification;

/// <summary>Contextual presentation evidence. Actual execution and stock owners retain all consequential rules.</summary>
public sealed partial class ScanSessionService(CommandPipeline pipeline, IKernelUnitOfWorkFactory unitOfWork,
    IdentificationResolver identification, IQmsPolicyReader policies,
    ScannerOptions scanners, ScanSessionAuthority authority)
{
    public const string StagePermission = "identification.scan.stage";

    public async Task<ScanRequirements> RequirementsAsync(ForgePrincipal principal, string? code, long? orderNumber,
        int? sequence, string? machineCode, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        principal = await CurrentPrincipalAsync(uow, principal, ct);
        var facts = await authority.OperationAsync(uow, principal, code, orderNumber, sequence, false, true, ct);
        var machine = await authority.MachineAsync(uow, principal, facts, machineCode, ct);
        var policy = await policies.TryAsync(uow, principal, QmsPolicyPurpose.Execution, facts.PolicyContext with { MachineCode = machine }, pipeline.Time.GetUtcNow(), ct);
        return policy is null ? new(false, false, true, [], null, null)
            : new(true, policy.Rules.Execution!.Presentation!.RequireServerSession == true,
                ScanSessionRules.ManualAllowed(policy), ScanSessionRules.RequiredKinds(policy), policy, ScanSessionRules.Expiry(policy));
    }

    public Task<CommandResult> CreateAsync(CreateScanSession command, CommandContext context, CancellationToken ct) =>
        pipeline.ExecuteAsync(new("Identification", nameof(CreateScanSession), StagePermission,
            ResourceRef.Of("ScanSession"), ReasonRequired: true), command, context, async (uow, now) =>
        {
            var principal = await CurrentPrincipalAsync(uow, context.Principal, ct);
            _ = identification.GetConfiguration();
            var facts = await authority.OperationAsync(uow, principal, command.OperationCode, command.OrderNumber, command.Sequence, false, true, ct);
            var machine = await authority.MachineAsync(uow, principal, facts, command.MachineCode, ct);
            var policy = await policies.RequireAsync(uow, principal, QmsPolicyPurpose.Execution, facts.PolicyContext with { MachineCode = machine }, now, ct);
            var worker = await WorkerAsync(uow, principal, command.Badge, facts.Order.ProgramId, ct);
            ScanSessionRules.RequireAuthentication(policy, principal, worker, context.Authentication, now);
            var channel = ScanSessionRules.Channel(principal, context.Authentication);
            var store = uow.Store<IScanSessionStore>();
            await store.LockChannelAsync(channel, ct);
            var priorId = await store.CurrentAsync(channel, ct);
            if (priorId != command.PreviousSessionId)
                throw new CommandRejectedException("scan_channel_generation_changed", "Another operator generation became current. Reload before opening a session.");
            if (priorId is { } oldId && await store.FindAsync(oldId, ct) is { State: ScanSessionState.Active } old)
                await store.SaveAsync(old with { Version = old.Version + 1, State = ScanSessionState.Superseded,
                    InvalidatedBy = context.Actor, InvalidationReason = "A new operator session generation was opened.", InvalidatedAt = now }, old.Version, ct);
            var id = ForgeIds.New(pipeline.Time);
            var sources = policy.Sources.Select(source => new ScanReadSource(source.Permission, source.Resource))
                .Concat(facts.Sources).Distinct().ToArray();
            var session = new ScanSessionView(id, 1, ScanSessionState.Active, principal.PersonId.Value, worker, channel,
                ScanSessionRules.AuthenticationBinding(principal, context.Authentication!),
                facts.Code, facts.Order.OrderNumber, facts.Operation.Sequence, facts.PolicyContext.FacilityCode,
                facts.Operation.WorkCenterCode, facts.Order.ProgramId, sources.Aggregate(facts.Resource.Markings, (markings, source) => markings | source.Resource.Markings),
                facts.Order.PartNumber, facts.Order.Revision, facts.Fingerprint, machine, [], ScanSessionRules.RequiredKinds(policy),
                ScanSessionRules.ManualAllowed(policy), now, now.AddSeconds(ScanSessionRules.Expiry(policy)), policy, sources,
                CreationRequestFingerprint: ScanSessionRules.CreationFingerprint(command, context));
            await store.SaveAsync(session, null, ct);
            await store.SetCurrentAsync(channel, id, ct);
            return Outcome(session, context, "A server scan session was opened.");
        }, ct, async (uow, _) =>
        {
            var principal = await CurrentPrincipalAsync(uow, context.Principal, ct);
            var facts = await authority.OperationAsync(uow, principal, command.OperationCode, command.OrderNumber, command.Sequence, true, true, ct);
            var worker = await WorkerAsync(uow, principal, command.Badge, facts.Order.ProgramId, ct);
            return new(facts.Resource, "operator:" + worker.ToString("D"));
        });

    public Task<CommandResult> StageAsync(Guid id, StageScan command, CommandContext context, CancellationToken ct) =>
        pipeline.ExecuteAsync(new("Identification", nameof(StageScan), StagePermission, ResourceRef.Of("ScanSession", id)),
            command, context, async (uow, now) =>
        {
            var (session, policy, facts) = await authority.RequireActiveAsync(uow, context, id, command.ExpectedVersion, now, ct);
            scanners.Require(command.InputMode, command.AdapterId, command.Symbology, ScanSessionRules.ManualAllowed(policy));
            var source = await identification.ResolveAuthorityPresentationAsync(uow, context.Principal, command.Code, ct);
            var kind = ScanSessionRules.StageKind(source.PresentedKind ?? source.Display.Kind);
            ScanSessionAuthority.RequireMatchingSource(kind, source, facts);
            if (kind == "ControlledInstruction") await ScanSessionAuthority.RequireInstructionForOperationAsync(uow, source, facts, ct);
            if (kind == "Material") await ScanSessionAuthority.RequireMaterialForOperationAsync(uow, source, facts, session.SelectedMachineCode, ct);
            var selection = new ScanSelection(kind, command.Code, source.CanonicalSubjectCode,
                command.InputMode, command.AdapterId, command.Symbology, now, ScanSessionRules.SourceFingerprint(source),
                (source.ReadSources ?? source.Resources.Select(resource => new PresentationReadSource(source.ReadPermission, resource)).ToArray())
                    .Select(read => new ScanReadSource(read.Permission, read.Resource)).ToArray());
            if (kind == "Machine" && (source.MachineCode ?? source.SourceId) != session.SelectedMachineCode)
                throw new CommandRejectedException("scan_machine_context_changed", "Changing the machine requires a new session and current adopted policy.");
            var updated = session with { Version = session.Version + 1,
                Selections = session.Selections.Where(prior => prior.Kind != kind || kind is "Material" or "ControlledInstruction"
                    && prior.SubjectCode != selection.SubjectCode).Append(selection).ToArray() };
            await uow.Store<IScanSessionStore>().SaveAsync(updated, session.Version, ct);
            return Outcome(updated, context, "The current accessible selection was staged.");
        }, ct, async (uow, now) =>
        {
            var session = await FindAsync(uow, id, ct);
            var source = await authority.SessionAuthorityAsync(uow, context.Principal, id, ct);
            _ = await authority.RequireActiveAsync(uow, context, id, session.Version, now, ct);
            return source;
        });

    public Task<CommandResult> InvalidateAsync(Guid id, InvalidateScanSession command, CommandContext context, CancellationToken ct) =>
        pipeline.ExecuteAsync(new("Identification", nameof(InvalidateScanSession), StagePermission,
            ResourceRef.Of("ScanSession", id), ReasonRequired: true), command, context, async (uow, now) =>
        {
            var store = uow.Store<IScanSessionStore>();
            var session = await FindAsync(uow, id, ct);
            var channel = ScanSessionRules.Channel(context.Principal, context.Authentication);
            await store.LockChannelAsync(channel, ct);
            session = await FindAsync(uow, id, ct);
            if (session.ChannelHash != channel || session.PrincipalPersonId != context.Principal.PersonId.Value)
                throw Unavailable();
            if (session.State != ScanSessionState.Active)
                return CommandOutcome.NoChange(CommandResult.NoChange(id.ToString("D"), "The session is already closed."));
            if (session.Version != command.ExpectedVersion) throw VersionConflict();
            var updated = session with { Version = session.Version + 1, State = ScanSessionState.Invalidated,
                InvalidatedBy = context.Actor, InvalidationReason = context.Reason, InvalidatedAt = now };
            await store.SaveAsync(updated, session.Version, ct);
            return Outcome(updated, context, "The staged operator context was invalidated.");
        }, ct, (uow, _) => authority.SessionAuthorityAsync(uow, context.Principal, id, ct));

    public async Task<ScanSessionView?> ReadAsync(ForgePrincipal principal, Guid id, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        principal = await CurrentPrincipalAsync(uow, principal, ct);
        var session = await uow.Store<IScanSessionStore>().FindAsync(id, ct);
        if (session is null) return null;
        if (!await authority.CanReadAsync(uow, principal, session, ct)) return null;
        var now = pipeline.Time.GetUtcNow();
        var current = await uow.Store<IScanSessionStore>().CurrentAsync(session.ChannelHash, ct);
        var state = session.State == ScanSessionState.Active
            ? session.ExpiresAt <= now ? ScanSessionState.Expired : current != session.Id ? ScanSessionState.Superseded : ScanSessionState.Active
            : session.State;
        var accepted = new List<ScanAccepted>();
        foreach (var entry in await uow.Store<IScanSessionStore>().AcceptedAsync(id, ct))
            if (await authority.CanReadAsync(uow, principal, session with { Selections = entry.Selections, Accepted = [entry] }, ct)) accepted.Add(entry);
        return session with { State = state, Accepted = accepted };
    }

    public async Task<ScanAcceptedRecord?> ReadAcceptedAsync(ForgePrincipal principal, Guid id, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        principal = await CurrentPrincipalAsync(uow, principal, ct);
        var accepted = await uow.Store<IScanSessionStore>().FindAcceptedAsync(id, ct);
        if (accepted is null) return null;
        var session = await uow.Store<IScanSessionStore>().FindAsync(accepted.SessionId, ct);
        return session is not null && await authority.CanReadAsync(uow, principal,
            session with { Selections = accepted.Entry.Selections, Accepted = [accepted.Entry] }, ct) ? accepted : null;
    }

    private CommandOutcome Outcome(ScanSessionView session, CommandContext context, string message) => new(
        CommandResult.Ok(session.Id.ToString("D"), message),
        [pipeline.Event(new ScanSessionChanged(session.Id, session.Version, session.State.ToString(), session.OperatorPersonId), context,
            "Identification", "ScanSession", session.Id.ToString("D"), session.ProgramId is { } program ? new ProgramId(program) : null, session.Markings)],
        new("ScanSession", session.Id.ToString("D"), null, CanonicalJson.Serialize(new
        {
            session.Id, session.Version, session.State, session.PrincipalPersonId, session.OperatorPersonId,
            session.OperationCode, session.OrderNumber, session.Sequence, session.FacilityCode, session.WorkCenterCode,
            session.ProgramId, session.Markings, session.PartNumber, session.PartRevision, session.OperationFingerprint,
            session.SelectedMachineCode, session.Selections, session.RequiredKinds, session.ManualAllowed,
            session.CreatedAt, session.ExpiresAt, session.Policy, session.Sources,
            session.InvalidatedBy, session.InvalidationReason, session.InvalidatedAt,
        }),
            session.ProgramId is { } owner ? new ProgramId(owner) : null, session.Markings));

    private static async Task<ForgePrincipal> CurrentPrincipalAsync(IKernelUnitOfWork uow, ForgePrincipal principal, CancellationToken ct) =>
        await uow.People.LoadPrincipalAsync(principal.PersonId, ct) is { IsActive: true } current ? current : throw Unavailable();

    private static async Task<Guid> WorkerAsync(IKernelUnitOfWork uow, ForgePrincipal principal, Secret? badge, Guid? program, CancellationToken ct)
    {
        var worker = badge is null ? principal.PersonId.Value
            : (await uow.Store<IWorkforceLookup>().FindBadgeHolderAsync(badge.Reveal(), ct))?.PersonId ?? throw Unavailable();
        var current = await uow.People.LoadPrincipalAsync(new PersonId(worker), ct);
        if (current is not { IsActive: true } || program is { } owner && !current.Programs.Contains(new ProgramId(owner))) throw Unavailable();
        return worker;
    }

    private static Task<ScanSessionView> FindAsync(IKernelUnitOfWork uow, Guid id, CancellationToken ct) => FindCoreAsync(uow, id, ct);
    private static async Task<ScanSessionView> FindCoreAsync(IKernelUnitOfWork uow, Guid id, CancellationToken ct) =>
        await uow.Store<IScanSessionStore>().FindAsync(id, ct) ?? throw Unavailable();
    private static CommandRejectedException Unavailable() => new("scan_session_unavailable", "The current source or registered operator context is unavailable.");
    private static CommandRejectedException VersionConflict() => new("scan_session_version_conflict", "The staged context changed. Reload its current version.");
}
