using Forge.Inventory.Contracts;
using Npgsql;

namespace Forge.Inventory.Infrastructure;

/// <summary>Approved WIP storage rules and partial builds (Sprint 10 §17–18). Occupancy itself is the ledger balance, never stored here.</summary>
internal sealed partial class PostgresInventoryStore : IWipStorageStore, IWipStorageLookup
{
    public async Task<long> InsertStorageRuleAsync(StorageCapacityRule rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.storage_capacity_rule (location_code, part_number, max_units, max_utilization_percent, max_dwell_hours, safety_reference, state, proposed_by, proposed_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            RETURNING rule_id
            """,
            rule.LocationCode, rule.PartNumber, rule.MaxUnits, Numeric(rule.MaxUtilizationPercent), Int(rule.MaxDwellHours), rule.SafetyReference, rule.State.ToString(),
            rule.ProposedBy, rule.ProposedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> DecideStorageRuleAsync(long ruleId, StorageRuleState expected, StorageRuleState state, string actor, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE inventory.storage_capacity_rule SET state = $3, decided_by = COALESCE(decided_by, $4), decided_at = COALESCE(decided_at, $5) WHERE rule_id = $1 AND state = $2",
            ruleId, expected.ToString(), state.ToString(), actor, at);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<long> InsertPartialBuildAsync(PartialBuild build, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(build);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.partial_build (order_number, sequence, quantity, storage_location, storage_quantity, part_number, evidence, state, approved_by, approved_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            RETURNING build_id
            """,
            build.OrderNumber, build.Sequence, build.Quantity, Text(build.StorageLocation), build.StorageQuantity, build.PartNumber, build.Evidence, build.State.ToString(),
            build.ApprovedBy, build.ApprovedAt);
        var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        foreach (var material in build.Materials)
        {
            await using var line = uow.Command(
                "INSERT INTO inventory.partial_build_material (build_id, part_number, quantity, issued_at_approval) VALUES ($1, $2, $3, $4)",
                id, material.PartNumber, material.Quantity, material.IssuedAtApproval);
            await line.ExecuteNonQueryAsync(ct);
        }

        return id;
    }

    public async Task<bool> ClosePartialBuildAsync(long buildId, PartialBuildState state, string actor, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE inventory.partial_build SET state = $2, closed_by = $3, closed_at = $4 WHERE build_id = $1 AND state = 'Active'",
            buildId, state.ToString(), actor, at);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public Task<IReadOnlyList<StorageCapacityRule>> ListStorageRulesAsync(CancellationToken ct) =>
        ReadAsync(
            """
            SELECT rule_id, location_code, part_number, max_units, max_utilization_percent, max_dwell_hours, safety_reference, state, proposed_by, proposed_at, decided_by, decided_at
            FROM inventory.storage_capacity_rule ORDER BY rule_id
            """,
            r => new StorageCapacityRule(
                r.GetInt64(0), r.GetString(1), r.GetString(2), Dec(r, 3), r.IsDBNull(4) ? null : Dec(r, 4), r.IsDBNull(5) ? null : r.GetInt32(5), r.GetString(6),
                Enum.Parse<StorageRuleState>(r.GetString(7)), r.GetString(8), r.GetFieldValue<DateTimeOffset>(9), NullableText(r, 10),
                r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11)),
            ct);

    public async Task<IReadOnlyList<PartialBuild>> ListPartialBuildsAsync(bool activeOnly, long? orderNumber, CancellationToken ct)
    {
        var materials = (await ReadAsync(
                """
                SELECT m.build_id, m.part_number, m.quantity, m.issued_at_approval
                FROM inventory.partial_build_material m JOIN inventory.partial_build b ON b.build_id = m.build_id
                WHERE (NOT $1 OR b.state = 'Active') AND ($2::bigint IS NULL OR b.order_number = $2)
                ORDER BY m.build_id, m.part_number
                """,
                r => (Build: r.GetInt64(0), Material: new PartialBuildMaterial(r.GetString(1), Dec(r, 2), Dec(r, 3))),
                ct,
                activeOnly, Long(orderNumber)))
            .ToLookup(m => m.Build, m => m.Material);
        return await ReadAsync(
            """
            SELECT build_id, order_number, sequence, quantity, storage_location, storage_quantity, part_number, evidence, state, approved_by, approved_at, closed_by, closed_at
            FROM inventory.partial_build
            WHERE (NOT $1 OR state = 'Active') AND ($2::bigint IS NULL OR order_number = $2)
            ORDER BY build_id
            """,
            r => new PartialBuild(
                r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), Dec(r, 3), NullableText(r, 4), Dec(r, 5), r.GetString(6), r.GetString(7),
                Enum.Parse<PartialBuildState>(r.GetString(8)), r.GetString(9), r.GetFieldValue<DateTimeOffset>(10), NullableText(r, 11),
                r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12), [.. materials[r.GetInt64(0)]]),
            ct,
            activeOnly, Long(orderNumber));
    }

    private static NpgsqlParameter Int(int? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer };
}
