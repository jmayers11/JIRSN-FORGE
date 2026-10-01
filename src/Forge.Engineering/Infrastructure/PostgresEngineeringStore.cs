using Forge.Engineering.Contracts;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Engineering.Infrastructure;

internal sealed class PostgresEngineeringStore(PostgresUnitOfWork uow) : IEngineeringStore, IEngineeringLookup
{
    private const string ItemSql = "SELECT part_number, description, unit_of_measure, make_buy, lead_time_days, lot_tracked, serialized FROM engineering.item";
    private const string RevisionSql = "SELECT part_number, revision, status, effective_from, effective_to FROM engineering.revision";

    public async Task<bool> InsertItemAsync(ItemInfo item, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        await using var cmd = uow.Command(
            "INSERT INTO engineering.item (part_number, description, unit_of_measure, make_buy, lead_time_days, lot_tracked, serialized, created_at, created_by) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) ON CONFLICT DO NOTHING",
            item.PartNumber, item.Description, item.UnitOfMeasure, item.MakeBuy.ToString(), item.LeadTimeDays, item.LotTracked, item.Serialized, at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> InsertRevisionAsync(string partNumber, string revision, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO engineering.revision (part_number, revision, status, created_at, created_by, contributors) VALUES ($1, $2, 'Draft', $3, $4, ARRAY[$4]) ON CONFLICT DO NOTHING",
            partNumber, revision, at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> InsertBomLineAsync(string partNumber, string revision, BomLineInfo line, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(line);
        await using var cmd = uow.Command(
            "INSERT INTO engineering.bom_line (part_number, revision, line_number, component, quantity_per, scrap_percent) VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT DO NOTHING",
            partNumber, revision, line.LineNumber, line.ComponentPartNumber, line.QuantityPer, line.ScrapPercent);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> DeleteBomLineAsync(string partNumber, string revision, int lineNumber, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM engineering.bom_line WHERE part_number = $1 AND revision = $2 AND line_number = $3", partNumber, revision, lineNumber);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> InsertOperationAsync(string partNumber, string revision, RoutingOperationInfo operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var cmd = uow.Command(
            """
            INSERT INTO engineering.routing_operation (part_number, revision, sequence, description, work_center_code, activity_code,
                eligible_machines, required_qualifications, setup_minutes, run_minutes_per_unit, crew_size)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) ON CONFLICT DO NOTHING
            """,
            partNumber, revision, operation.Sequence, operation.Description, operation.WorkCenterCode, operation.ActivityCode,
            TextArray(operation.EligibleMachines), TextArray(operation.RequiredQualifications), operation.SetupMinutes, operation.RunMinutesPerUnit, operation.CrewSize);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> DeleteOperationAsync(string partNumber, string revision, int sequence, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM engineering.routing_operation WHERE part_number = $1 AND revision = $2 AND sequence = $3", partNumber, revision, sequence);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<(string Parent, string Component)>> ListBomEdgesAsync(CancellationToken ct) =>
        await Read("SELECT DISTINCT part_number, component FROM engineering.bom_line ORDER BY part_number, component", r => (r.GetString(0), r.GetString(1)), ct);

    public async Task AddContributorAsync(string partNumber, string revision, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE engineering.revision SET contributors = array_append(contributors, $3) WHERE part_number = $1 AND revision = $2 AND NOT ($3 = ANY (contributors))",
            partNumber, revision, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetContributorsAsync(string partNumber, string revision, CancellationToken ct) =>
        (await Read("SELECT unnest(contributors) FROM engineering.revision WHERE part_number = $1 AND revision = $2", r => r.GetString(0), ct, partNumber, revision)).ToList();

    public async Task SetEffectivityAsync(string partNumber, string revision, RevisionStatus status, DateTimeOffset? from, DateTimeOffset? to, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE engineering.revision SET status = $3, effective_from = $4, effective_to = $5, released_by = COALESCE(released_by, $6) WHERE part_number = $1 AND revision = $2",
            partNumber, revision, status.ToString(), Time(from), Time(to), actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ItemInfo?> FindItemAsync(string partNumber, CancellationToken ct) =>
        (await Read(ItemSql + " WHERE part_number = $1", ReadItem, ct, partNumber)).SingleOrDefault();

    public Task<IReadOnlyList<ItemInfo>> ListItemsAsync(CancellationToken ct) => Read(ItemSql + " ORDER BY part_number", ReadItem, ct);

    public Task<IReadOnlyList<RevisionInfo>> ListRevisionsAsync(string partNumber, CancellationToken ct) =>
        Read(RevisionSql + " WHERE part_number = $1 ORDER BY created_at, revision", ReadRevision, ct, partNumber);

    public async Task<RevisionInfo?> GetEffectiveRevisionAsync(string partNumber, DateTimeOffset at, CancellationToken ct) =>
        (await Read(
            RevisionSql + " WHERE part_number = $1 AND status = 'Released' AND effective_from <= $2 AND (effective_to IS NULL OR effective_to > $2)",
            ReadRevision,
            ct,
            partNumber,
            at)).SingleOrDefault();

    public async Task<RevisionDetail?> GetRevisionDetailAsync(string partNumber, string revision, CancellationToken ct)
    {
        var item = await FindItemAsync(partNumber, ct);
        var rev = (await Read(RevisionSql + " WHERE part_number = $1 AND revision = $2", ReadRevision, ct, partNumber, revision)).SingleOrDefault();
        if (item is null || rev is null)
        {
            return null;
        }

        var bom = await Read(
            "SELECT line_number, component, quantity_per, scrap_percent FROM engineering.bom_line WHERE part_number = $1 AND revision = $2 ORDER BY line_number",
            r => new BomLineInfo(r.GetInt32(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3)),
            ct,
            partNumber,
            revision);
        var routing = await Read(
            """
            SELECT sequence, description, work_center_code, activity_code, eligible_machines, required_qualifications, setup_minutes, run_minutes_per_unit, crew_size
            FROM engineering.routing_operation WHERE part_number = $1 AND revision = $2 ORDER BY sequence
            """,
            r => new RoutingOperationInfo(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<string[]>(4), r.GetFieldValue<string[]>(5), r.GetDecimal(6), r.GetDecimal(7), r.GetInt32(8)),
            ct,
            partNumber,
            revision);
        return new RevisionDetail(item, rev, bom, routing);
    }

    private static ItemInfo ReadItem(NpgsqlDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), Enum.Parse<MakeBuy>(r.GetString(3)), r.GetInt32(4), r.GetBoolean(5), r.GetBoolean(6));

    private static RevisionInfo ReadRevision(NpgsqlDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        Enum.Parse<RevisionStatus>(r.GetString(2)),
        r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3),
        r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4));

    private static NpgsqlParameter TextArray(IEnumerable<string> values) =>
        new() { Value = values.ToArray(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text };

    private static NpgsqlParameter Time(DateTimeOffset? value) =>
        new() { Value = (object?)value?.ToUniversalTime() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz };

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

public static class EngineeringRegistration
{
    public static IServiceCollection AddForgeEngineering(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(EngineeringRegistration).Assembly, "engineering");
        services.AddForgeStore<IEngineeringStore>(uow => new PostgresEngineeringStore(uow));
        services.AddForgeStore<IEngineeringLookup>(uow => new PostgresEngineeringStore(uow));
        services.AddSingleton<EngineeringService>();
        return services;
    }
}
