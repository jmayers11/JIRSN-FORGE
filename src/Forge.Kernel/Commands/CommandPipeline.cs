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
public sealed record AuditChange(string EntityType, string EntityId, string? OldValue, string? NewValue, ProgramId? Program = null);

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
public sealed class CommandPipeline(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    public TimeProvider Time => time;

    public async Task<CommandResult> ExecuteAsync<TCommand>(
        CommandSpec spec,
        TCommand command,
        CommandContext context,
        Func<IKernelUnitOfWork, DateTimeOffset, Task<CommandOutcome>> body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.IdempotencyKey);

        var decision = authorizer.Authorize(context.Principal, spec.Permission, spec.Resource);
        if (!decision.IsPermitted)
        {
            await RecordFailureAsync(spec, context, AuditOutcomes.Denied, string.Join(" ", decision.Reasons), decision, ct);
            throw new ForgeAuthorizationException(decision);
        }

        if (spec.ReasonRequired && string.IsNullOrWhiteSpace(context.Reason))
        {
            const string message = "A reason is required for this change.";
            await RecordFailureAsync(spec, context, AuditOutcomes.Rejected, "reason_required: " + message, null, ct);
            throw new CommandRejectedException("reason_required", message);
        }

        var requestHash = CanonicalJson.Fingerprint(new { spec.CommandType, command });
        CommandRejectedException? rejection = null;
        CommandResult? result = null;

        await using (var uow = await unitOfWork.BeginAsync(ct))
        {
            var now = time.GetUtcNow();
            var prior = await uow.Idempotency.ReserveAsync(context.IdempotencyKey, spec.CommandType, requestHash, now, ct);
            if (prior is not null)
            {
                return prior;
            }

            try
            {
                var outcome = await body(uow, now);
                foreach (var envelope in outcome.Events)
                {
                    await uow.Outbox.EnqueueAsync(envelope, ct);
                }

                if (outcome.Change is { } change)
                {
                    await uow.Audit.AppendAsync(
                        new AuditEntry
                        {
                            AuditId = ForgeIds.New(time),
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
                            ProgramId = change.Program,
                        },
                        ct);
                }

                await uow.Idempotency.CompleteAsync(context.IdempotencyKey, outcome.Result, ct);
                await uow.CommitAsync(ct);
                result = outcome.Result;
            }
            catch (CommandRejectedException ex)
            {
                rejection = ex; // the transaction rolls back on dispose
            }
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
            },
            ct);

        if (denial is not null)
        {
            await uow.Outbox.EnqueueAsync(
                EventFactory.Create(new AuthorizationDenied(spec.Permission, spec.Resource.Type, spec.Resource.Id, denial.Reasons), context, time, spec.SourceContext, spec.Resource.Type, spec.Resource.Id ?? string.Empty),
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
