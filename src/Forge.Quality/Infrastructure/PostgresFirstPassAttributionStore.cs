using Forge.Kernel.Postgres;
using Forge.Quality.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Quality.Infrastructure;

/// <summary>First-pass cause attribution (Sprint 10 §16.2); the table's trigger keeps every attribution's facts immutable.</summary>
internal sealed class PostgresFirstPassAttributionStore(PostgresUnitOfWork uow) : IFirstPassAttributionStore, IFirstPassAttributionLookup
{
    private const string Columns = "attribution_id, inspection_id, cause, employee_opportunity, failed_quantity, evidence, state, proposed_by, proposed_at, reviewed_by, reviewed_at, supersedes_attribution_id";

    public async Task<long> InsertAttributionAsync(FirstPassAttribution attribution, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        await using var cmd = uow.Command(
            """
            INSERT INTO quality.first_pass_attribution (inspection_id, cause, employee_opportunity, failed_quantity, evidence, state, proposed_by, proposed_at, supersedes_attribution_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            RETURNING attribution_id
            """,
            attribution.InspectionId, attribution.Cause.ToString(), attribution.EmployeeOpportunity,
            new NpgsqlParameter { Value = (object?)attribution.FailedQuantity ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric },
            attribution.Evidence, attribution.State.ToString(), attribution.ProposedBy, attribution.ProposedAt,
            new NpgsqlParameter { Value = (object?)attribution.SupersedesAttributionId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> SetAttributionStateAsync(long attributionId, AttributionState expected, AttributionState state, string? reviewer, DateTimeOffset? at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE quality.first_pass_attribution SET state = $3, reviewed_by = COALESCE(reviewed_by, $4), reviewed_at = COALESCE(reviewed_at, $5) WHERE attribution_id = $1 AND state = $2",
            attributionId, expected.ToString(), state.ToString(),
            new NpgsqlParameter { Value = (object?)reviewer ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text },
            new NpgsqlParameter { Value = (object?)at ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<FirstPassAttribution>> ListAttributionsAsync(IReadOnlyCollection<long> inspectionIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inspectionIds);
        var rows = new List<FirstPassAttribution>();
        if (inspectionIds.Count == 0) return rows;
        await using var cmd = uow.Command($"SELECT {Columns} FROM quality.first_pass_attribution WHERE inspection_id = ANY($1) ORDER BY attribution_id", inspectionIds.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(Read(reader));
        return rows;
    }

    public async Task<FirstPassAttribution?> FindAttributionAsync(long attributionId, CancellationToken ct)
    {
        await using var cmd = uow.Command($"SELECT {Columns} FROM quality.first_pass_attribution WHERE attribution_id = $1", attributionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    private static FirstPassAttribution Read(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        r.GetInt64(1),
        Enum.Parse<DefectCause>(r.GetString(2)),
        r.GetBoolean(3),
        r.IsDBNull(4) ? null : r.GetDecimal(4) / 1.0000000000000000000000000000m,
        r.GetString(5),
        Enum.Parse<AttributionState>(r.GetString(6)),
        r.GetString(7),
        r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10),
        r.IsDBNull(11) ? null : r.GetInt64(11));
}
