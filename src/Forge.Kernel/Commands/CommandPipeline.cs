using Forge.Kernel.Access;
using Forge.Kernel.Audit;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Kernel.Commands;

/// <summary>What a command is, for authorization and audit.</summary>
public sealed record CommandSpec(string SourceContext, string CommandType, string Permission, ResourceRef Resource, bool ReasonRequired = false);

/// <summary>The audited change a command made.</summary>
public sealed record AuditChange(string EntityType, string EntityId, string? OldValue, string? NewValue, ProgramId? Program = null, DataMarking Markings = DataMarking.None);

/// <summary>Current source authority and nonsecret acting-person identities resolved before a replay.</summary>
public sealed record CommandAuthority(ResourceRef Resource, string? SubjectIdentity = null, IReadOnlyList<ResourceRef>? AdditionalResources = null);

/// <summary>Resolves recorded source context in the command transaction, including before idempotent replay.</summary>
public interface ICommandSourceResolver
{
    Task<CommandAuthority> ResolveAsync(IKernelUnitOfWork uow, CommandSpec spec, CommandContext context, DateTimeOffset now, CancellationToken ct);
}

/// <summary>What a command body produced: the result, the events to publish and the change to audit.</summary>
public sealed record CommandOutcome(CommandResult Result, IReadOnlyList<EventEnvelope> Events, AuditChange? Change)
{
    public static CommandOutcome NoChange(CommandResult result) => new(result, [], null);
}

/// <summary>
/// The one path every state-changing command takes: authorize, require a reason where configured,
/// reserve the idempotency key, run the body, then write events, audit and the idempotency result in
/// the same transaction. Denials and business-rule rejections are audited in their own transaction
/// (the rejected change itself is rolled back).
/// </summary>
public sealed class CommandPipeline(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time, ICommandSourceResolver? sources = null, ICommandTransactionParticipant? participant = null, ICommandTransactionParticipantFactory? participants = null)
{
    public TimeProvider Time => time;

    public async Task<CommandResult> ExecuteAsync<TCommand>(
        CommandSpec spec,
        TCommand command,
        CommandContext context,
        Func<IKernelUnitOfWork, DateTimeOffset, Task<CommandOutcome>> body,
        CancellationToken ct,
        Func<IKernelUnitOfWork, DateTimeOffset, Task<CommandAuthority>>? authority = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.IdempotencyKey);

        var decision = authorizer.Authorize(context.Principal, spec.Permission, spec.Resource);
        if (!decision.IsPermitted && (authority is null && sources is null || !MayHaveAuthority(context.Principal, spec.Permission)))
        {
            await RecordFailureAsync(spec, context, AuditOutcomes.Denied, string.Join(" ", decision.Reasons), decision, ct);
            throw new ForgeAuthorizationException(decision);
        }

        CommandRejectedException? rejection = null;
        AuthorizationDecision? rejectedAuthority = null;
        CommandResult? result = null;

        await using (var uow = await unitOfWork.BeginAsync(ct))
        {
            var now = time.GetUtcNow();
            var sourceAuthorityResolved = false;
            try
            {
                var current = authority is not null ? await authority(uow, now)
                    : sources is not null ? await sources.ResolveAsync(uow, spec, context, now, ct)
                    : new CommandAuthority(spec.Resource);
                sourceAuthorityResolved = true;
                spec = spec with { Resource = current.Resource };
                foreach (var resource in new[] { current.Resource }.Concat(current.AdditionalResources ?? []))
                {
                    decision = authorizer.Authorize(context.Principal, spec.Permission, resource);
                    if (!decision.IsPermitted)
                    {
                        spec = spec with { Resource = resource };
                        throw new ForgeAuthorizationException(decision);
                    }
                }
                if (spec.ReasonRequired && string.IsNullOrWhiteSpace(context.Reason))
                    throw new CommandRejectedException("reason_required", "A reason is required for this change.");
                var transactionParticipant = participant ?? participants?.Create();
                var transactionEvidence = transactionParticipant is null ? null
                    : await transactionParticipant.ValidateAsync(uow, spec, command!, context, current, now, ct);
                // Preserve the established fingerprint bytes for ordinary commands.
                var requestHash = context.ScanSession is null && transactionEvidence is null
                    ? CanonicalJson.Fingerprint(new
                    {
                        version = 2, spec.SourceContext, spec.CommandType, command,
                        context.Actor, context.Source, current.SubjectIdentity,
                    })
                    : CanonicalJson.Fingerprint(new
                    {
                        version = 3, spec.SourceContext, spec.CommandType, command,
                        context.Actor, context.Source, current.SubjectIdentity, context.ScanSession,
                        EvidenceFingerprint = transactionEvidence?.Fingerprint,
                    });
                var prior = await uow.Idempotency.ReserveAsync(context.IdempotencyKey, spec.CommandType, requestHash, now, ct);
                if (prior is not null)
                {
                    return prior;
                }

                var outcome = await body(uow, now);
                foreach (var envelope in outcome.Events)
                {
                    await uow.Outbox.EnqueueAsync(envelope, ct);
                }

                Guid? acceptedAuditId = null;
                if (outcome.Change is { } change)
                {
                    acceptedAuditId = ForgeIds.New(time);
                    await uow.Audit.AppendAsync(
                        new AuditEntry
                        {
                            AuditId = acceptedAuditId.Value,
                            RecordedAt = now,
                            Actor = context.Actor,
                            Action = spec.CommandType,
                            Outcome = AuditOutcomes.Succeeded,
                            EntityType = change.EntityType,
                            EntityId = change.EntityId,
                            OldValue = change.OldValue,
                            NewValue = change.NewValue,
                            Reason = context.Reason,
                            Source = context.Source.ToString(),
                            CorrelationId = context.CorrelationId,
                            ProgramId = change.Program ?? spec.Resource.Program,
                            Markings = change.Markings | spec.Resource.Markings,
                            OwningResource = spec.Resource,
                            ContextFingerprint = transactionEvidence?.Fingerprint,
                        },
                        ct);
                }

                if (transactionEvidence is not null)
                    await transactionParticipant!.RecordAcceptedAsync(uow, spec, context, outcome, acceptedAuditId, transactionEvidence, now, ct);

                await uow.Idempotency.CompleteAsync(context.IdempotencyKey, outcome.Result, ct);
                await uow.CommitAsync(ct);
                result = outcome.Result;
            }
            catch (CommandRejectedException ex)
            {
                if (!sourceAuthorityResolved && authority is not null && sources is not null)
                    spec = spec with { Resource = (await sources.ResolveAsync(uow, spec, context, now, ct)).Resource };
                rejection = ex; // the transaction rolls back on dispose
            }
            catch (ForgeAuthorizationException ex)
            {
                if (ex.OwningResource is { } deniedResource)
                    spec = spec with { Resource = deniedResource };
                else if (!sourceAuthorityResolved && authority is not null && sources is not null)
                    spec = spec with { Resource = (await sources.ResolveAsync(uow, spec, context, now, ct)).Resource };
                rejectedAuthority = ex.Decision;
            }
        }

        if (rejectedAuthority is not null)
        {
            await RecordFailureAsync(spec, context, AuditOutcomes.Denied, string.Join(" ", rejectedAuthority.Reasons), rejectedAuthority, ct);
            throw new ForgeAuthorizationException(rejectedAuthority);
        }

        if (rejection is not null)
        {
            await RecordFailureAsync(spec, context, AuditOutcomes.Rejected, $"{rejection.Code}: {rejection.Message}", null, ct);
            throw rejection;
        }

        return result!;
    }

    /// <summary>Creates an event for this command's context.</summary>
    public EventEnvelope Event<TEvent>(TEvent payload, CommandContext context, string sourceContext, string entityType, string entityId, ProgramId? program = null, DataMarking markings = DataMarking.None)
        where TEvent : IDomainEvent =>
        EventFactory.Create(payload, context, time, sourceContext, entityType, entityId, program, markings);

    // This is only an early rejection, never a permission grant. Scoped/threshold-bearing
    // actors still need the fully resolved source decision below; roleless callers cannot
    // use source or badge resolution as a record-existence probe.
    private bool MayHaveAuthority(ForgePrincipal principal, string permission) => principal.IsActive &&
        ((principal.EffectivePermissions is { } direct ? direct.Contains(permission)
            : principal.Roles.Any(role => Roles.PermissionsOf(role).Contains(permission, StringComparer.Ordinal)))
         || (principal.ScopedPermissions?.Any(scope => scope.Permission == permission) ?? false)
         || (principal.DelegatedPermissions?.Any(grant => grant.Permission == permission
             && grant.StartsAt <= time.GetUtcNow() && time.GetUtcNow() < grant.ExpiresAt) ?? false));

    private async Task RecordFailureAsync(CommandSpec spec, CommandContext context, string outcome, string detail, AuthorizationDecision? denial, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var uow = await unitOfWork.BeginAsync(ct);
        await uow.Audit.AppendAsync(
            new AuditEntry
            {
                AuditId = ForgeIds.New(time),
                RecordedAt = now,
                Actor = context.Actor,
                Action = spec.CommandType,
                Outcome = outcome,
                EntityType = spec.Resource.Type,
                EntityId = spec.Resource.Id ?? string.Empty,
                NewValue = detail,
                Reason = context.Reason,
                Source = context.Source.ToString(),
                CorrelationId = context.CorrelationId,
                ProgramId = spec.Resource.Program,
                Markings = spec.Resource.Markings,
                OwningResource = spec.Resource,
            },
            ct);

        if (denial is not null)
        {
            await uow.Outbox.EnqueueAsync(
                EventFactory.Create(new AuthorizationDenied(spec.Permission, spec.Resource.Type, spec.Resource.Id, denial.Reasons), context, time, spec.SourceContext,
                    spec.Resource.Type, spec.Resource.Id ?? string.Empty, spec.Resource.Program, spec.Resource.Markings),
                ct);
        }

        await uow.CommitAsync(ct);
    }
}

public static class AuditOutcomes
{
    public const string Succeeded = "Succeeded";
    public const string Denied = "Denied";
    public const string Rejected = "Rejected";
}
