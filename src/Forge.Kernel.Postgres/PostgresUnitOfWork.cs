using Forge.Kernel.Access;
using Npgsql;

namespace Forge.Kernel.Postgres;

/// <summary>How a bounded context's store is created for a unit of work.</summary>
public sealed record StoreRegistration(Type StoreType, Func<PostgresUnitOfWork, object> Create);

public sealed class PostgresUnitOfWorkFactory(NpgsqlDataSource dataSource, IEnumerable<StoreRegistration> stores, TimeProvider time) : IKernelUnitOfWorkFactory
{
    private readonly Dictionary<Type, Func<PostgresUnitOfWork, object>> registrations =
        stores.ToDictionary(s => s.StoreType, s => s.Create);

    public Task<IKernelUnitOfWork> BeginAsync(CancellationToken ct) => BeginAsync(System.Data.IsolationLevel.ReadCommitted, false, ct);

    public Task<IKernelUnitOfWork> BeginSnapshotAsync(CancellationToken ct) => BeginAsync(System.Data.IsolationLevel.RepeatableRead, true, ct);

    private async Task<IKernelUnitOfWork> BeginAsync(System.Data.IsolationLevel isolation, bool readOnly, CancellationToken ct)
    {
        var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(isolation, ct);
            if (readOnly)
            {
                await using var cmd = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            return new PostgresUnitOfWork(connection, transaction, registrations, time.GetUtcNow());
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

/// <summary>A single connection and transaction shared by every store for one command.</summary>
public sealed class PostgresUnitOfWork : IKernelUnitOfWork
{
    private readonly NpgsqlConnection connection;
    private readonly NpgsqlTransaction transaction;
    private readonly IReadOnlyDictionary<Type, Func<PostgresUnitOfWork, object>> registrations;
    private readonly Dictionary<Type, object> created = [];
    private bool completed;

    internal PostgresUnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyDictionary<Type, Func<PostgresUnitOfWork, object>> registrations, DateTimeOffset now)
    {
        this.connection = connection;
        this.transaction = transaction;
        this.registrations = registrations;
        Now = now;
        People = new PostgresPeopleStore(this);
        Outbox = new PostgresOutbox(this);
        Audit = new PostgresAuditWriter(this);
        Idempotency = new PostgresIdempotencyStore(this);
    }

    public IPeopleStore People { get; }

    public DateTimeOffset Now { get; }

    public IEventOutbox Outbox { get; }

    public IAuditWriter Audit { get; }

    public IIdempotencyStore Idempotency { get; }

    public TStore Store<TStore>()
        where TStore : class
    {
        if (!created.TryGetValue(typeof(TStore), out var store))
        {
            if (!registrations.TryGetValue(typeof(TStore), out var create))
            {
                throw new InvalidOperationException($"No store registered for {typeof(TStore).Name}.");
            }

            store = create(this);
            created[typeof(TStore)] = store;
        }

        return (TStore)store;
    }

    /// <summary>A command bound to this unit of work's connection and transaction. Parameters are positional ($1, $2, ...).</summary>
    public NpgsqlCommand Command(string sql, params object?[] parameters)
    {
        ObjectDisposedException.ThrowIf(completed, this);
        var cmd = new NpgsqlCommand(sql, connection, transaction);
        foreach (var p in parameters)
        {
            cmd.Parameters.Add(p is NpgsqlParameter np ? np : new NpgsqlParameter { Value = p ?? DBNull.Value });
        }

        return cmd;
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(completed, this);
        await transaction.CommitAsync(ct);
        completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!completed)
        {
            try
            {
                await transaction.RollbackAsync();
            }
            catch (InvalidOperationException)
            {
                // Connection already broken; nothing was committed.
            }
            catch (NpgsqlException)
            {
                // Connection already broken; nothing was committed.
            }

            completed = true;
        }

        await transaction.DisposeAsync();
        await connection.DisposeAsync();
    }
}
