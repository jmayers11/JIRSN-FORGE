using System.Text.Json;
using Forge.Inventory.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Inventory.Infrastructure;

internal sealed partial class PostgresInventoryStore : IOutsideProcessLookup, IOutsideProcessStore
{
    public Task<IReadOnlyList<OutsideProcessDispatchInfo>> ListDispatchesAsync(string? key, CancellationToken ct) =>
        ReadAsync("SELECT snapshot::text FROM inventory.outside_process_dispatch WHERE $1::text IS NULL OR process_key = $1 ORDER BY transaction_id",
            r => JsonSerializer.Deserialize<OutsideProcessDispatchInfo>(r.GetString(0), ResultJson)!, ct, Text(key));

    public async Task<OutsideProcessReturnInfo?> FindReturnAsync(long transactionId, CancellationToken ct) =>
        (await ReadAsync("SELECT snapshot::text FROM inventory.outside_process_return WHERE transaction_id = $1",
            r => JsonSerializer.Deserialize<OutsideProcessReturnInfo>(r.GetString(0), ResultJson)!, ct, transactionId)).SingleOrDefault();

    public Task<IReadOnlyList<OutsideProcessReturnInfo>> ListReturnsAsync(string? key, CancellationToken ct) =>
        ReadAsync("SELECT snapshot::text FROM inventory.outside_process_return WHERE $1::text IS NULL OR process_key = $1 ORDER BY transaction_id",
            r => JsonSerializer.Deserialize<OutsideProcessReturnInfo>(r.GetString(0), ResultJson)!, ct, Text(key));

    public async Task InsertDispatchAsync(OutsideProcessDispatchInfo dispatch, CancellationToken ct)
    {
        await using var cmd = uow.Command("INSERT INTO inventory.outside_process_dispatch(transaction_id, process_key, snapshot) VALUES ($1, $2, $3)",
            dispatch.TransactionId, dispatch.Key, new NpgsqlParameter { Value = JsonSerializer.Serialize(dispatch, ResultJson), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertReturnAsync(OutsideProcessReturnInfo returned, CancellationToken ct)
    {
        await using var cmd = uow.Command("INSERT INTO inventory.outside_process_return(transaction_id, process_key, snapshot) VALUES ($1, $2, $3)",
            returned.TransactionId, returned.Key, new NpgsqlParameter { Value = JsonSerializer.Serialize(returned, ResultJson), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
