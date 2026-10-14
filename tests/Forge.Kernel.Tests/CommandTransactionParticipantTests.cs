using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Kernel.Tests;

public sealed class CommandTransactionParticipantTests
{
    [Fact]
    public async Task Current_participant_validation_runs_before_replay_and_acceptance_links_the_actual_audit_once()
    {
        var kernel = new InMemoryKernel();
        var participant = new Participant();
        var pipeline = new CommandPipeline(kernel, new ForgeAuthorizer(), TimeProvider.System, participant: participant);
        var context = Context("participant-once") with { ScanSession = new(Guid.NewGuid(), 3) };
        var spec = Spec();
        var bodyCalls = 0;
        Task<CommandResult> Execute() => pipeline.ExecuteAsync(spec, new { value = "exact source" }, context,
            (_, _) => { bodyCalls++; return Task.FromResult(new CommandOutcome(CommandResult.Ok("ACTUAL", "Changed."), [],
                new("ActualBusinessRecord", "ACTUAL", null, "saved"))); }, CancellationToken.None);
        var first = await Execute();
        var replay = await Execute();
        Assert.Equal(first, replay);
        Assert.Equal(1, bodyCalls);
        Assert.Equal(2, participant.ValidationCalls);
        Assert.Equal(1, participant.AcceptanceCalls);
        Assert.Equal(Assert.Single(kernel.Audit).Entry.AuditId, participant.AcceptedAuditId);
        Assert.Equal(participant.Fingerprint, Assert.Single(kernel.Audit).Entry.ContextFingerprint);
        Assert.Single(kernel.Idempotency);
        participant.Deny = true;
        await Assert.ThrowsAsync<CommandRejectedException>(Execute);
        Assert.Equal(1, bodyCalls);
        Assert.Equal(1, participant.AcceptanceCalls);
        Assert.Single(kernel.Idempotency);
        Assert.Equal(AuditOutcomes.Rejected, kernel.Audit[^1].Entry.Outcome);
    }

    [Fact]
    public async Task A_failure_while_recording_acceptance_rolls_back_business_events_audit_and_idempotency()
    {
        var kernel = new InMemoryKernel();
        var participant = new Participant { RejectAcceptance = true };
        var pipeline = new CommandPipeline(kernel, new ForgeAuthorizer(), TimeProvider.System, participant: participant);
        var context = Context("participant-rollback") with { ScanSession = new(Guid.NewGuid(), 1) };
        var person = new PersonRecord(new PersonId(Guid.NewGuid()), "transactional-person", "Pending business change", true, DataMarking.None);
        await Assert.ThrowsAsync<CommandRejectedException>(() => pipeline.ExecuteAsync(Spec(), new { value = 1 }, context,
            async (uow, now) =>
            {
                await uow.People.InsertAsync(person, now, context.Actor, CancellationToken.None);
                return new(CommandResult.Ok(person.Id.ToString(), "Changed."),
                    [pipeline.Event(new ParticipantEvent(person.Id.Value), context, "Test", "Person", person.Id.ToString())],
                    new("Person", person.Id.ToString(), null, "pending"));
            }, CancellationToken.None));
        Assert.Empty(kernel.People);
        Assert.Empty(kernel.Outbox);
        Assert.Empty(kernel.Idempotency);
        Assert.Equal(AuditOutcomes.Rejected, Assert.Single(kernel.Audit).Entry.Outcome);
        Assert.Null(Forge.Kernel.Audit.AuditChain.FindFirstBreak(kernel.Audit));
    }

    [Fact]
    public async Task Ordinary_source_denial_precedes_participant_validation()
    {
        var kernel = new InMemoryKernel();
        var participant = new Participant();
        var pipeline = new CommandPipeline(kernel, new ForgeAuthorizer(), TimeProvider.System, participant: participant);
        var context = Context("source-first") with { Principal = Context("unused").Principal with { Roles = new HashSet<string>(StringComparer.Ordinal) } };
        await Assert.ThrowsAsync<ForgeAuthorizationException>(() => pipeline.ExecuteAsync(Spec(), new { value = 1 }, context,
            (_, _) => throw new InvalidOperationException("Denied source cannot run."), CancellationToken.None));
        Assert.Equal(0, participant.ValidationCalls);
        Assert.Empty(kernel.Idempotency);
    }

    [Fact]
    public async Task Null_contextual_evidence_preserves_the_original_version_two_fingerprint()
    {
        var kernel = new InMemoryKernel();
        var participant = new Participant { NoEvidence = true };
        var pipeline = new CommandPipeline(kernel, new ForgeAuthorizer(), TimeProvider.System, participant: participant);
        var context = Context("legacy-fingerprint") with { Authentication = new("subject", "issuer", "session", "workstation", "Authenticated", null, null) };
        var spec = Spec();
        var command = new { value = "unchanged legacy request" };
        await pipeline.ExecuteAsync(spec, command, context, (_, _) => Task.FromResult(CommandOutcome.NoChange(CommandResult.Ok("LEGACY", "Unchanged."))), CancellationToken.None);
        Assert.Equal(CanonicalJson.Fingerprint(new { version = 2, spec.SourceContext, spec.CommandType, command,
            context.Actor, context.Source, SubjectIdentity = (string?)null }), Assert.Single(kernel.Idempotency).Value.Hash);
        Assert.Equal(0, participant.AcceptanceCalls);
    }

    [Fact]
    public async Task A_different_session_or_policy_fingerprint_cannot_replay_the_same_command_key()
    {
        var kernel = new InMemoryKernel();
        var participant = new Participant();
        var pipeline = new CommandPipeline(kernel, new ForgeAuthorizer(), TimeProvider.System, participant: participant);
        var context = Context("session-fingerprint") with { ScanSession = new(Guid.NewGuid(), 1) };
        Task<CommandResult> Execute(CommandContext current) => pipeline.ExecuteAsync(Spec(), new { value = 1 }, current,
            (_, _) => Task.FromResult(new CommandOutcome(CommandResult.Ok("ACTUAL", "Saved."), [], new("Business", "ACTUAL", null, "saved"))), CancellationToken.None);
        await Execute(context);
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => Execute(context with { ScanSession = new(Guid.NewGuid(), 1) }));
        participant.Fingerprint = "changed approved policy";
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => Execute(context));
        Assert.Equal(1, participant.AcceptanceCalls);
        Assert.Single(kernel.Audit);
    }

    private static CommandSpec Spec() => new("Test", "ObservedCommand", Permissions.AccessManage, ResourceRef.Of("ActualBusinessRecord", "ACTUAL"));
    private static CommandContext Context(string key) => new(new ForgePrincipal(new PersonId(Guid.NewGuid()), "Actor", true,
        new HashSet<string>(StringComparer.Ordinal) { Roles.SecurityAdministrator }, new HashSet<ProgramId>(), DataMarking.None), Guid.NewGuid(), key, CommandSource.Api, "Reason");
    private sealed record ParticipantEvent(Guid Person) : IDomainEvent
    {
        public static string EventType => nameof(ParticipantEvent);
        public static int SchemaVersion => 1;
    }
    private sealed class Participant : ICommandTransactionParticipant
    {
        public int ValidationCalls { get; private set; }
        public int AcceptanceCalls { get; private set; }
        public Guid? AcceptedAuditId { get; private set; }
        public bool Deny { get; set; }
        public bool RejectAcceptance { get; set; }
        public bool NoEvidence { get; init; }
        public string Fingerprint { get; set; } = "exact approved policy";
        public Task<CommandTransactionEvidence?> ValidateAsync(IKernelUnitOfWork uow, CommandSpec spec, object command,
            CommandContext context, CommandAuthority authority, DateTimeOffset now, CancellationToken ct)
        {
            ValidationCalls++;
            if (Deny) throw new CommandRejectedException("context_withdrawn", "Current context is unavailable.");
            return Task.FromResult(NoEvidence ? null : new CommandTransactionEvidence("Test", Fingerprint, "nonsecret contextual facts"));
        }
        public Task RecordAcceptedAsync(IKernelUnitOfWork uow, CommandSpec spec, CommandContext context, CommandOutcome outcome,
            Guid? auditId, CommandTransactionEvidence evidence, DateTimeOffset now, CancellationToken ct)
        {
            AcceptanceCalls++; AcceptedAuditId = auditId;
            if (RejectAcceptance) throw new CommandRejectedException("provenance_failed", "The transaction cannot persist its contextual evidence.");
            return Task.CompletedTask;
        }
    }
}
