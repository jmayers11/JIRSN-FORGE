using Forge.Kernel.Access;
using Forge.Kernel.Audit;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;

namespace Forge.Kernel.Tests;

/// <summary>
/// In-memory kernel store with real transaction semantics: changes are staged per unit of work and
/// applied only on commit, so tests can prove that rejected commands leave nothing behind.
/// </summary>
internal sealed class InMemoryKernel : IKernelUnitOfWorkFactory
{
    public Dictionary<PersonId, PersonRecord> People { get; } = [];

    public HashSet<(PersonId, string)> RoleAssignments { get; } = [];

    public HashSet<(PersonId, ProgramId)> ProgramAccess { get; } = [];

    public List<EventEnvelope> Outbox { get; } = [];

    public List<ChainedAuditEntry> Audit { get; } = [];

    public Dictionary<string, (string Type, string Hash, CommandResult? Result)> Idempotency { get; } = new(StringComparer.Ordinal);

    public Task<IKernelUnitOfWork> BeginAsync(CancellationToken ct) => Task.FromResult<IKernelUnitOfWork>(new Uow(this));

    public Task<IKernelUnitOfWork> BeginSnapshotAsync(CancellationToken ct) => BeginAsync(ct);

    private sealed class Uow(InMemoryKernel k) : IKernelUnitOfWork, IPeopleStore, IEventOutbox, IAuditWriter, IIdempotencyStore
    {
        private readonly List<Action> pending = [];
        private readonly Dictionary<PersonId, PersonRecord> people = new(k.People);
        private readonly HashSet<(PersonId, string)> roles = [.. k.RoleAssignments];
        private readonly HashSet<(PersonId, ProgramId)> programs = [.. k.ProgramAccess];
        private readonly List<ChainedAuditEntry> audit = [.. k.Audit];
        private readonly Dictionary<string, (string Type, string Hash, CommandResult? Result)> idem = new(k.Idempotency, StringComparer.Ordinal);

        public IPeopleStore People => this;

        public IEventOutbox Outbox => this;

        public IAuditWriter Audit => this;

        public IIdempotencyStore Idempotency => this;

        public Task CommitAsync(CancellationToken ct)
        {
            k.People.Clear();
            foreach (var p in people)
            {
                k.People[p.Key] = p.Value;
            }

            k.RoleAssignments.Clear();
            k.RoleAssignments.UnionWith(roles);
            k.ProgramAccess.Clear();
            k.ProgramAccess.UnionWith(programs);
            k.Audit.Clear();
            k.Audit.AddRange(audit);
            k.Idempotency.Clear();
            foreach (var i in idem)
            {
                k.Idempotency[i.Key] = i.Value;
            }

            pending.ForEach(a => a());
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public TStore Store<TStore>()
            where TStore : class =>
            throw new NotSupportedException("The in-memory kernel has no context stores.");

        public Task<PersonRecord?> FindAsync(PersonId id, CancellationToken ct) => Task.FromResult(people.GetValueOrDefault(id));

        public Task<PersonRecord?> FindBySubjectAsync(string idpSubject, CancellationToken ct) =>
            Task.FromResult(people.Values.FirstOrDefault(p => p.IdpSubject == idpSubject));

        public Task InsertAsync(PersonRecord person, DateTimeOffset at, string actor, CancellationToken ct)
        {
            people.Add(person.Id, person);
            return Task.CompletedTask;
        }

        public Task<bool> AddRoleAsync(PersonId id, string role, DateTimeOffset at, string actor, CancellationToken ct) => Task.FromResult(roles.Add((id, role)));

        public Task<bool> RemoveRoleAsync(PersonId id, string role, CancellationToken ct) => Task.FromResult(roles.Remove((id, role)));

        public Task<bool> AddProgramAccessAsync(PersonId id, ProgramId program, DateTimeOffset at, string actor, CancellationToken ct) => Task.FromResult(programs.Add((id, program)));

        public Task<bool> RemoveProgramAccessAsync(PersonId id, ProgramId program, CancellationToken ct) => Task.FromResult(programs.Remove((id, program)));

        public Task<ForgePrincipal?> LoadPrincipalAsync(PersonId id, CancellationToken ct)
        {
            if (!people.TryGetValue(id, out var p))
            {
                return Task.FromResult<ForgePrincipal?>(null);
            }

            return Task.FromResult<ForgePrincipal?>(new ForgePrincipal(
                p.Id, p.DisplayName, p.IsActive,
                roles.Where(r => r.Item1 == id).Select(r => r.Item2).ToHashSet(),
                programs.Where(r => r.Item1 == id).Select(r => r.Item2).ToHashSet(),
                p.Markings));
        }

        public Task<int> CountActiveWithRoleAsync(string role, CancellationToken ct) =>
            Task.FromResult(roles.Count(r => r.Item2 == role && people.TryGetValue(r.Item1, out var p) && p.IsActive));

        public Task EnqueueAsync(EventEnvelope envelope, CancellationToken ct)
        {
            pending.Add(() => k.Outbox.Add(envelope));
            return Task.CompletedTask;
        }

        public Task<ChainedAuditEntry> AppendAsync(AuditEntry entry, CancellationToken ct)
        {
            var previous = audit.LastOrDefault()?.Hash;
            var link = new ChainedAuditEntry(audit.Count + 1, entry, previous, AuditChain.ComputeHash(previous, entry));
            audit.Add(link);
            return Task.FromResult(link);
        }

        public Task<CommandResult?> ReserveAsync(string key, string commandType, string requestHash, DateTimeOffset at, CancellationToken ct)
        {
            if (!idem.TryGetValue(key, out var existing))
            {
                idem[key] = (commandType, requestHash, null);
                return Task.FromResult<CommandResult?>(null);
            }

            if (existing.Type != commandType || existing.Hash != requestHash || existing.Result is null)
            {
                throw new IdempotencyConflictException(key);
            }

            return Task.FromResult<CommandResult?>(existing.Result);
        }

        public Task CompleteAsync(string key, CommandResult result, CancellationToken ct)
        {
            idem[key] = (idem[key].Type, idem[key].Hash, result);
            return Task.CompletedTask;
        }
    }
}
