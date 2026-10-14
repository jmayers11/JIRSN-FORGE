using Forge.Kernel.Access;

namespace Forge.Kernel.Commands;

/// <summary>A server-owned contextual session reference. It supplies neither identity nor action authority.</summary>
public sealed record CommandSessionReference(Guid Id, int Version);

/// <summary>Evidence populated from validated authentication claims, never from command JSON.</summary>
public sealed record CommandAuthenticationEvidence(string Subject, string Issuer, string? ProviderSessionId,
    string? WorkstationId, string? Assurance, DateTimeOffset? AuthenticatedAt, DateTimeOffset? ValidUntil);

/// <summary>An owner validates this contextual evidence before replay and persists it with an accepted transaction.</summary>
public sealed record CommandTransactionEvidence(string Kind, string Fingerprint, object Data);

/// <summary>
/// Optional owner integration within the command transaction. Implementations retain domain rules and contextual
/// evidence outside Kernel; validation never bypasses ordinary current source authorization or business checks.
/// </summary>
public interface ICommandTransactionParticipant
{
    Task<CommandTransactionEvidence?> ValidateAsync(IKernelUnitOfWork uow, CommandSpec spec, object command,
        CommandContext context, CommandAuthority authority, DateTimeOffset now, CancellationToken ct);

    Task RecordAcceptedAsync(IKernelUnitOfWork uow, CommandSpec spec, CommandContext context,
        CommandOutcome outcome, Guid? auditId, CommandTransactionEvidence evidence, DateTimeOffset now, CancellationToken ct);
}

/// <summary>Resolves optional owner integration after the command service and pipeline have been constructed.</summary>
public interface ICommandTransactionParticipantFactory
{
    ICommandTransactionParticipant Create();
}
