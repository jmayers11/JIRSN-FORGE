using Forge.Kernel.Access;
using Npgsql;

namespace Forge.Kernel.Postgres;

public sealed class PostgresUnitOfWorkFactory(NpgsqlDataSource dataSource) : IKernelUnitOfWorkFactory
{
    public async Task<IKernelUnitOfWork> BeginAsync(CancellationToken ct)
    {
        var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            return new PostgresUnitOfWork(connection, transaction);
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
    private bool completed;

    internal PostgresUnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        this.connection = connection;
        this.transaction = transaction;
        People = new PostgresPeopleStore(this);
        Outbox = new PostgresOutbox(this);
        Audit = new PostgresAuditWriter(this);
        Idempotency = new PostgresIdempotencyStore(this);
    }

    public IPeopleStore People { get; }

    public IEventOutbox Outbox { get; }

    public IAuditWriter Audit { get; }

    public IIdempotencyStore Idempotency { get; }

    internal NpgsqlCommand Command(string sql, params object?[] parameters)
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
