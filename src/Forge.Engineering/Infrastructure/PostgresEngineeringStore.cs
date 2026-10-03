using Forge.Engineering.Contracts;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Engineering.Infrastructure;

internal sealed class PostgresEngineeringStore(PostgresUnitOfWork uow) : IEngineeringStore, IEngineeringLookup
{
    private const string ItemSql = "SELECT part_number, description, unit_of_measure, make_buy, lead_time_days, lot_tracked, serialized, safety_stock, lot_policy, order_quantity, order_multiple, item_class FROM engineering.item";
    private const string RevisionSql = "SELECT part_number, revision, status, effective_from, effective_to FROM engineering.revision";

    public async Task<bool> InsertItemAsync(ItemInfo item, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        await using var cmd = uow.Command(
            "INSERT INTO engineering.item (part_number, description, unit_of_measure, make_buy, lead_time_days, lot_tracked, serialized, created_at, created_by, item_class, classified_at, classified_by) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $8, $9) ON CONFLICT DO NOTHING",
            item.PartNumber, item.Description, item.UnitOfMeasure, item.MakeBuy.ToString(), item.LeadTimeDays, item.LotTracked, item.Serialized, at, actor, item.Class.ToString());
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
        new(r.GetString(0), r.GetString(1), r.GetString(2), Enum.Parse<MakeBuy>(r.GetString(3)), r.GetInt32(4), r.GetBoolean(5), r.GetBoolean(6), r.GetDecimal(7), Enum.Parse<LotPolicy>(r.GetString(8)), r.GetDecimal(9), r.GetDecimal(10), Enum.Parse<ItemClass>(r.GetString(11)));

    public async Task SetClassAsync(string partNumber, ItemClass itemClass, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE engineering.item SET item_class = $2, classified_at = $3, classified_by = $4 WHERE part_number = $1",
            partNumber, itemClass.ToString(), at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private const string ManufacturerPartSql =
        "SELECT part_number, manufacturer, mpn, manufacturer_revision, lifecycle, status, datasheet, note, proposed_by, proposed_at, approved_by, approved_at FROM engineering.manufacturer_part";

    public Task<IReadOnlyList<ManufacturerPartInfo>> ListManufacturerPartsAsync(string? partNumber, CancellationToken ct) =>
        Read(
            ManufacturerPartSql + " WHERE ($1::text IS NULL OR part_number = $1) ORDER BY part_number, lower(manufacturer), mpn",
            r => new ManufacturerPartInfo(
                r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                Enum.Parse<ManufacturerLifecycle>(r.GetString(4)), Enum.Parse<ManufacturerPartStatus>(r.GetString(5)),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                r.GetString(8), r.GetFieldValue<DateTimeOffset>(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11)),
            ct,
            new NpgsqlParameter { Value = (object?)partNumber ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });

    public async Task<bool> InsertManufacturerPartAsync(ManufacturerPartInfo part, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(part);
        await using var cmd = uow.Command(
            """
            INSERT INTO engineering.manufacturer_part (part_number, manufacturer, mpn, manufacturer_revision, lifecycle, status, datasheet, note, proposed_by, proposed_at)
            VALUES ($1, $2, $3, $4, $5, 'Proposed', $6, $7, $8, $9) ON CONFLICT DO NOTHING
            """,
            part.PartNumber, part.Manufacturer, part.Mpn, Text(part.ManufacturerRevision), part.Lifecycle.ToString(), Text(part.Datasheet), Text(part.Note), part.ProposedBy, part.ProposedAt);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task UpdateManufacturerPartAsync(ManufacturerPartInfo part, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(part);
        await using var cmd = uow.Command(
            """
            UPDATE engineering.manufacturer_part SET
                lifecycle = $4, status = $5, manufacturer_revision = $6, datasheet = $7, note = $8,
                approved_by = $9, approved_at = $10,
                withdrawn_by = CASE WHEN $5 = 'Withdrawn' THEN $12 ELSE withdrawn_by END,
                withdrawn_at = CASE WHEN $5 = 'Withdrawn' THEN $11 ELSE withdrawn_at END,
                lifecycle_changed_at = CASE WHEN lifecycle <> $4 THEN $11 ELSE lifecycle_changed_at END
            WHERE part_number = $1 AND manufacturer = $2 AND mpn = $3
            """,
            part.PartNumber, part.Manufacturer, part.Mpn, part.Lifecycle.ToString(), part.Status.ToString(), Text(part.ManufacturerRevision), Text(part.Datasheet), Text(part.Note),
            Text(part.ApprovedBy), new NpgsqlParameter { Value = (object?)part.ApprovedAt ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz }, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text };

    public async Task UpdatePlanningAsync(ItemInfo item, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        await using var cmd = uow.Command(
            "UPDATE engineering.item SET lead_time_days = $2, safety_stock = $3, lot_policy = $4, order_quantity = $5, order_multiple = $6, planning_updated_at = $7, planning_updated_by = $8 WHERE part_number = $1",
            item.PartNumber, item.LeadTimeDays, item.SafetyStock, item.LotPolicy.ToString(), item.OrderQuantity, item.OrderMultiple, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RevisionStructure>> ListReleasedStructuresAsync(CancellationToken ct)
    {
        var revisions = await Read(RevisionSql + " WHERE status = 'Released' ORDER BY part_number, effective_from", ReadRevision, ct);
        var lines = await Read(
            """
            SELECT b.part_number, b.revision, b.line_number, b.component, b.quantity_per, b.scrap_percent
            FROM engineering.bom_line b JOIN engineering.revision r USING (part_number, revision)
            WHERE r.status = 'Released' ORDER BY b.part_number, b.revision, b.line_number
            """,
            r => (Part: r.GetString(0), Revision: r.GetString(1), Line: new BomLineInfo(r.GetInt32(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5))),
            ct);
        var byRevision = lines.ToLookup(l => (l.Part, l.Revision), l => l.Line);
        return revisions.Select(rev => new RevisionStructure(rev, byRevision[(rev.PartNumber, rev.Revision)].ToList())).ToList();
    }

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
        services.AddForgeStore<IDocumentStore>(uow => new PostgresDocumentStore(uow));
        services.AddForgeStore<IDocumentLookup>(uow => new PostgresDocumentStore(uow));
        services.AddSingleton<EngineeringService>();
        services.AddSingleton<DocumentService>();
        services.AddForgeStore<IChangeStore>(uow => new PostgresChangeStore(uow));
        services.AddForgeStore<IChangeLookup>(uow => new PostgresChangeStore(uow));
        services.AddSingleton<ChangeService>();
        return services;
    }
}
