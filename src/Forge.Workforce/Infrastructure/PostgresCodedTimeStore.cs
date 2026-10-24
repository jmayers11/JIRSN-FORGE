using Forge.Kernel.Postgres;
using Forge.Workforce.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Workforce.Infrastructure;

/// <summary>Coded time (Sprint 10 §16.1); the table's trigger keeps every entry's facts immutable.</summary>
internal sealed class PostgresCodedTimeStore(PostgresUnitOfWork uow) : ICodedTimeStore, ICodedTimeLookup
{
    private const string Columns = "entry_id, person_id, starts_at, ends_at, code, paid, work_center_code, machine_code, order_number, sequence, note, state, recorded_by, recorded_at, reviewed_by, reviewed_at, supersedes_entry_id";

    public async Task InsertCodedTimeAsync(CodedTimeEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var cmd = uow.Command(
            $"INSERT INTO workforce.coded_time ({Columns}) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17)",
            entry.EntryId, entry.PersonId, entry.StartsAt, entry.EndsAt, entry.Code.ToString(), entry.Paid, Text(entry.WorkCenterCode), Text(entry.MachineCode),
            new NpgsqlParameter { Value = (object?)entry.OrderNumber ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint },
            new NpgsqlParameter { Value = (object?)entry.Sequence ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer },
            entry.Note, entry.State.ToString(), entry.RecordedBy, entry.RecordedAt, Text(entry.ReviewedBy),
            new NpgsqlParameter { Value = (object?)entry.ReviewedAt ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz },
            new NpgsqlParameter { Value = (object?)entry.SupersedesEntryId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> SetCodedTimeStateAsync(Guid entryId, CodedTimeState expected, CodedTimeState state, string? reviewer, DateTimeOffset? at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE workforce.coded_time SET state = $3, reviewed_by = COALESCE(reviewed_by, $4), reviewed_at = COALESCE(reviewed_at, $5) WHERE entry_id = $1 AND state = $2",
            entryId, expected.ToString(), state.ToString(), Text(reviewer),
            new NpgsqlParameter { Value = (object?)at ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<CodedTimeEntry>> ListCodedTimeAsync(Guid? personId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = new List<CodedTimeEntry>();
        await using var cmd = uow.Command(
            $"SELECT {Columns} FROM workforce.coded_time WHERE ($1::uuid IS NULL OR person_id = $1) AND starts_at < $3 AND ends_at > $2 ORDER BY starts_at, entry_id",
            new NpgsqlParameter { Value = (object?)personId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid }, from, to);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(Read(reader));
        return rows;
    }

    public async Task<CodedTimeEntry?> FindCodedTimeAsync(Guid entryId, CancellationToken ct)
    {
        await using var cmd = uow.Command($"SELECT {Columns} FROM workforce.coded_time WHERE entry_id = $1", entryId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    private static CodedTimeEntry Read(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetGuid(1),
        r.GetFieldValue<DateTimeOffset>(2),
        r.GetFieldValue<DateTimeOffset>(3),
        Enum.Parse<CodedTimeCode>(r.GetString(4)),
        r.GetBoolean(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetInt64(8),
        r.IsDBNull(9) ? null : r.GetInt32(9),
        r.GetString(10),
        Enum.Parse<CodedTimeState>(r.GetString(11)),
        r.GetString(12),
        r.GetFieldValue<DateTimeOffset>(13),
        r.IsDBNull(14) ? null : r.GetString(14),
        r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15),
        r.IsDBNull(16) ? null : r.GetGuid(16));

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
}
