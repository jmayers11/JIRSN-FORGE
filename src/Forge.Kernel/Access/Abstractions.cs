using Forge.Kernel.Audit;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;

namespace Forge.Kernel.Access;

public sealed record PersonRecord(PersonId Id, string IdpSubject, string DisplayName, bool IsActive, DataMarking Markings);

/// <summary>People, roles and program access. Owned by the kernel; identity itself belongs to the IdP.</summary>
public interface IPeopleStore
{
    Task<PersonRecord?> FindAsync(PersonId id, CancellationToken ct);

    Task<PersonRecord?> FindBySubjectAsync(string idpSubject, CancellationToken ct);

    Task InsertAsync(PersonRecord person, DateTimeOffset at, string actor, CancellationToken ct);

    /// <returns>True if the role was newly assigned; false if already held.</returns>
    Task<bool> AddRoleAsync(PersonId id, string role, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> RemoveRoleAsync(PersonId id, string role, CancellationToken ct);

    Task<bool> AddProgramAccessAsync(PersonId id, ProgramId program, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> RemoveProgramAccessAsync(PersonId id, ProgramId program, CancellationToken ct);

    Task<ForgePrincipal?> LoadPrincipalAsync(PersonId id, CancellationToken ct);

    Task<int> CountActiveWithRoleAsync(string role, CancellationToken ct);
}

public interface IEventOutbox
{
    Task EnqueueAsync(EventEnvelope envelope, CancellationToken ct);
}

public interface IAuditWriter
{
    Task<ChainedAuditEntry> AppendAsync(AuditEntry entry, CancellationToken ct);
}

public interface IIdempotencyStore
{
    /// <summary>
    /// Reserves the key for this request. Returns the stored result if the same request already
    /// completed; throws <see cref="IdempotencyConflictException"/> if the key was used for a different request.
    /// </summary>
    Task<CommandResult?> ReserveAsync(string key, string commandType, string requestHash, DateTimeOffset at, CancellationToken ct);

    Task CompleteAsync(string key, CommandResult result, CancellationToken ct);
}

/// <summary>One database transaction spanning state change, outbox, audit and idempotency.</summary>
public interface IKernelUnitOfWork : IAsyncDisposable
{
    IPeopleStore People { get; }

    IEventOutbox Outbox { get; }

    IAuditWriter Audit { get; }

    IIdempotencyStore Idempotency { get; }

    Task CommitAsync(CancellationToken ct);
}

public interface IKernelUnitOfWorkFactory
{
    Task<IKernelUnitOfWork> BeginAsync(CancellationToken ct);
}
