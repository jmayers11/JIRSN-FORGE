using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Forge.Planning.Reporting;

namespace Forge.Planning.Infrastructure;

internal sealed class PostgresReportStore(PostgresUnitOfWork uow) : IReportDefinitionStore
{
    private const string Select = "SELECT definition_id, name, report_id, parameters, shared, owner, created_at, updated_at FROM planning.report_definition WHERE deleted_at IS NULL";

    public async Task<long> InsertAsync(ReportDefinition definition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await using var cmd = uow.Command(
            """
            INSERT INTO planning.report_definition (name, report_id, parameters, shared, owner, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $6) RETURNING definition_id
            """,
            definition.Name, definition.ReportId, CanonicalJson.Serialize(definition.Parameters), definition.Shared, definition.Owner, definition.CreatedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateAsync(ReportDefinition definition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await using var cmd = uow.Command(
            "UPDATE planning.report_definition SET name = $2, parameters = $3, shared = $4, updated_at = $5 WHERE definition_id = $1",
            definition.DefinitionId, definition.Name, CanonicalJson.Serialize(definition.Parameters), definition.Shared, definition.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(long definitionId, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE planning.report_definition SET deleted_at = $2 WHERE definition_id = $1", definitionId, at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ReportDefinition?> FindAsync(long definitionId, CancellationToken ct) =>
        (await ReadAsync(Select + " AND definition_id = $1", ct, definitionId)).SingleOrDefault();

    public Task<IReadOnlyList<ReportDefinition>> ListAsync(string owner, CancellationToken ct) =>
        ReadAsync(Select + " AND (shared OR owner = $1) ORDER BY lower(name), definition_id", ct, owner);

    public async Task<bool> NameTakenAsync(string owner, string name, long exceptId, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "SELECT EXISTS (SELECT 1 FROM planning.report_definition WHERE deleted_at IS NULL AND owner = $1 AND lower(name) = lower($2) AND definition_id <> $3)",
            owner, name, exceptId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<IReadOnlyList<ReportDefinition>> ReadAsync(string sql, CancellationToken ct, params object?[] args)
    {
        var list = new List<ReportDefinition>();
        await using var cmd = uow.Command(sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new ReportDefinition(
                r.GetInt64(0),
                r.GetString(1),
                r.GetString(2),
                System.Text.Json.JsonSerializer.Deserialize<ReportParameters>(r.GetString(3), CanonicalJson.Options) ?? new ReportParameters(),
                r.GetBoolean(4),
                r.GetString(5),
                r.GetFieldValue<DateTimeOffset>(6),
                r.GetFieldValue<DateTimeOffset>(7)));
        }

        return list;
    }
}
