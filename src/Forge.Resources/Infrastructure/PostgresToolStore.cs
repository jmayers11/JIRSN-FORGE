using Forge.Kernel.Postgres;
using Forge.Resources.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Resources.Infrastructure;

/// <summary>Tool types, tools and their history (V3.8).</summary>
internal sealed partial class PostgresToolStore(PostgresUnitOfWork uow) : IToolLookup, IToolStore
{
    public async Task LockToolAsync(string code, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT code FROM resources.tool WHERE code=$1 FOR UPDATE", code);
        await command.ExecuteScalarAsync(ct);
    }

    private const string ToolSql =
        """
        SELECT code, type_code, description, serial, location, custody, custodian, condition, eligible_machines, eligible_activities, usage_count, uses_since_service,
               calibration_due, certificate, status, changed_at, changed_by
        FROM resources.tool
        """;

    public Task<IReadOnlyList<ToolTypeInfo>> ListToolTypesAsync(CancellationToken ct) =>
        Read(
            "SELECT code, name, kind, calibration_days, usage_limit, service_uses, active FROM resources.tool_type ORDER BY code",
            r => new ToolTypeInfo(r.GetString(0), r.GetString(1), Enum.Parse<ToolKind>(r.GetString(2)), r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetInt32(5), r.GetBoolean(6)),
            ct);

    public Task<IReadOnlyList<ToolInfo>> ListToolsAsync(CancellationToken ct) => Read(ToolSql + " ORDER BY code", ReadTool, ct);

    public async Task<ToolInfo?> FindToolAsync(string code, CancellationToken ct) => (await Read(ToolSql + " WHERE code = $1", ReadTool, ct, code)).SingleOrDefault();

    public Task<IReadOnlyList<ToolEvent>> ListToolEventsAsync(string? toolCode, CancellationToken ct) =>
        Read(
            """
            SELECT event_id, tool_code, kind, at, actor, location, custodian, condition, calibrated_on, due_on, certificate, uses, reference, note
            FROM resources.tool_event WHERE $1::text IS NULL OR tool_code = $1 ORDER BY event_id
            """,
            r => new ToolEvent(r.GetInt64(0), r.GetString(1), Enum.Parse<ToolEventKind>(r.GetString(2)), r.GetFieldValue<DateTimeOffset>(3), r.GetString(4), S(r, 5), S(r, 6),
                r.IsDBNull(7) ? null : Enum.Parse<ToolCondition>(r.GetString(7)), r.IsDBNull(8) ? null : r.GetFieldValue<DateOnly>(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateOnly>(9),
                S(r, 10), r.IsDBNull(11) ? null : r.GetInt32(11), S(r, 12), S(r, 13)),
            ct,
            Text(toolCode));

    public Task<IReadOnlyList<ToolRequirement>> ListToolRequirementsAsync(CancellationToken ct) =>
        Read(
            "SELECT requirement_id, activity_code, machine_code, tool_type_code, quantity, set_at, set_by FROM resources.tool_requirement ORDER BY activity_code, machine_code NULLS FIRST, tool_type_code",
            r => new ToolRequirement(r.GetInt64(0), r.GetString(1), S(r, 2), r.GetString(3), r.GetInt32(4), r.GetFieldValue<DateTimeOffset>(5), r.GetString(6)),
            ct);

    public async Task<long> SetToolRequirementAsync(ToolRequirement requirement, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        await using var cmd = uow.Command(
            """
            INSERT INTO resources.tool_requirement (activity_code, machine_code, tool_type_code, quantity, set_at, set_by)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (activity_code, coalesce(machine_code, ''), tool_type_code) DO UPDATE SET quantity = EXCLUDED.quantity, set_at = EXCLUDED.set_at, set_by = EXCLUDED.set_by
            RETURNING requirement_id
            """,
            requirement.ActivityCode, Text(requirement.MachineCode), requirement.ToolTypeCode, requirement.Quantity, requirement.SetAt, requirement.SetBy);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> DeleteToolRequirementAsync(long requirementId, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM resources.tool_requirement WHERE requirement_id = $1", requirementId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> InsertToolTypeAsync(ToolTypeInfo type, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(type);
        await using var cmd = uow.Command(
            """
            INSERT INTO resources.tool_type (code, name, kind, calibration_days, usage_limit, service_uses, active, changed_at, changed_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) ON CONFLICT (code) DO NOTHING
            """,
            type.Code, type.Name, type.Kind.ToString(), Int(type.CalibrationDays), Int(type.UsageLimit), Int(type.ServiceUses), type.Active, at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> InsertToolAsync(ToolInfo tool, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tool);
        await using var cmd = uow.Command(
            """
            INSERT INTO resources.tool (code, type_code, description, serial, location, custody, custodian, condition, eligible_machines, eligible_activities, usage_count,
                uses_since_service, calibration_due, certificate, status, created_at, created_by, changed_at, changed_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $16, $17) ON CONFLICT (code) DO NOTHING
            """,
            tool.Code, tool.TypeCode, tool.Description, Text(tool.Serial), Text(tool.Location), tool.Custody.ToString(), Text(tool.Custodian), tool.Condition.ToString(),
            Array(tool.EligibleMachines), Array(tool.EligibleActivities), tool.UsageCount, tool.UsesSinceService, Date(tool.CalibrationDue), Text(tool.Certificate), tool.Status.ToString(), at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task UpdateToolAsync(ToolInfo tool, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tool);
        await using var cmd = uow.Command(
            """
            UPDATE resources.tool SET description = $2, location = $3, custody = $4, custodian = $5, condition = $6, eligible_machines = $7, eligible_activities = $8,
                usage_count = $9, uses_since_service = $10, calibration_due = $11, certificate = $12, status = $13, changed_at = $14, changed_by = $15
            WHERE code = $1
            """,
            tool.Code, tool.Description, Text(tool.Location), tool.Custody.ToString(), Text(tool.Custodian), tool.Condition.ToString(), Array(tool.EligibleMachines), Array(tool.EligibleActivities),
            tool.UsageCount, tool.UsesSinceService, Date(tool.CalibrationDue), Text(tool.Certificate), tool.Status.ToString(), tool.ChangedAt, tool.ChangedBy);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertToolEventAsync(ToolEvent e, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);
        await using var cmd = uow.Command(
            """
            INSERT INTO resources.tool_event (tool_code, kind, at, actor, location, custodian, condition, calibrated_on, due_on, certificate, uses, reference, note)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13) RETURNING event_id
            """,
            e.ToolCode, e.Kind.ToString(), e.At, e.Actor, Text(e.Location), Text(e.Custodian), Text(e.Condition?.ToString()), Date(e.CalibratedOn), Date(e.DueOn), Text(e.Certificate),
            Int(e.Uses), Text(e.Reference), Text(e.Note));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static ToolInfo ReadTool(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), S(r, 3), S(r, 4), Enum.Parse<ToolCustody>(r.GetString(5)), S(r, 6), Enum.Parse<ToolCondition>(r.GetString(7)),
        r.GetFieldValue<string[]>(8), r.GetFieldValue<string[]>(9), r.GetInt32(10), r.GetInt32(11), r.IsDBNull(12) ? null : r.GetFieldValue<DateOnly>(12), S(r, 13),
        Enum.Parse<ToolStatus>(r.GetString(14)), r.GetFieldValue<DateTimeOffset>(15), r.GetString(16));

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Int(int? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer };

    private static NpgsqlParameter Date(DateOnly? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date };

    private static NpgsqlParameter Array(IEnumerable<string> values) => new() { Value = values.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text };

    private async Task<IReadOnlyList<T>> Read<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
    {
        var list = new List<T>();
        await using var cmd = uow.Command(sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(map(reader));
        }

        return list;
    }
}
