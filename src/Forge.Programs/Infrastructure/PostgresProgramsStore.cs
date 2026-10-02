using Forge.Kernel.Postgres;
using Forge.Programs.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Programs.Infrastructure;

internal sealed class PostgresProgramsStore(PostgresUnitOfWork uow) : IProgramsStore, IProgramsLookup
{
    private const string ProgramSql = "SELECT program_id, code, name, customer, description, status, created_by, created_at FROM programs.program";

    public async Task<bool> InsertProgramAsync(ProgramInfo program, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(program);
        await using var cmd = uow.Command(
            "INSERT INTO programs.program (program_id, code, name, customer, description, status, created_at, created_by) VALUES ($1, $2, $3, $4, $5, $6, $7, $8) ON CONFLICT DO NOTHING",
            program.ProgramId, program.Code, program.Name, Text(program.Customer), Text(program.Description), program.Status.ToString(), program.CreatedAt, program.CreatedBy);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task UpdateProgramAsync(ProgramInfo program, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(program);
        await using var cmd = uow.Command(
            "UPDATE programs.program SET name = $2, customer = $3, description = $4, status = $5 WHERE program_id = $1",
            program.ProgramId, program.Name, Text(program.Customer), Text(program.Description), program.Status.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long?> InsertContractAsync(ContractInfo contract, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contract);
        await using var cmd = uow.Command(
            """
            INSERT INTO programs.contract (program_id, number, customer, title, period_start, period_end, ceiling, status, created_at, created_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) ON CONFLICT (number) DO NOTHING RETURNING contract_id
            """,
            contract.ProgramId, contract.Number, contract.Customer, contract.Title, contract.PeriodStart, contract.PeriodEnd, Num(contract.Ceiling), contract.Status.ToString(), at, actor);
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    public async Task UpdateContractAsync(ContractInfo contract, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contract);
        await using var cmd = uow.Command(
            "UPDATE programs.contract SET title = $2, period_start = $3, period_end = $4, ceiling = $5, status = $6 WHERE contract_id = $1",
            contract.ContractId, contract.Title, contract.PeriodStart, contract.PeriodEnd, Num(contract.Ceiling), contract.Status.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> InsertClinAsync(long contractId, ClinInfo clin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clin);
        await using var cmd = uow.Command(
            "INSERT INTO programs.clin (contract_id, clin, parent_clin, description, part_number, quantity, unit_price) VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT DO NOTHING",
            contractId, clin.Clin, Text(clin.ParentClin), clin.Description, Text(clin.PartNumber), Num(clin.Quantity), Num(clin.UnitPrice));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<long> InsertDeliveryAsync(DeliveryInfo delivery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        await using var cmd = uow.Command(
            "INSERT INTO programs.delivery (contract_id, clin, quantity, due_date, cancelled, created_at, created_by) VALUES ($1, $2, $3, $4, false, $5, $6) RETURNING delivery_id",
            delivery.ContractId, delivery.Clin, delivery.Quantity, delivery.DueDate, delivery.CreatedAt, delivery.CreatedBy);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateDeliveryAsync(DeliveryInfo delivery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        await using var cmd = uow.Command("UPDATE programs.delivery SET quantity = $2, due_date = $3, cancelled = $4 WHERE delivery_id = $1", delivery.DeliveryId, delivery.Quantity, delivery.DueDate, delivery.Cancelled);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertRequirementAsync(long contractId, RequirementInfo requirement, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        await using var cmd = uow.Command("INSERT INTO programs.requirement (contract_id, number, code, text) VALUES ($1, $2, $3, $4)", contractId, requirement.Number, requirement.Code, requirement.Text);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertMilestoneAsync(MilestoneInfo milestone, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(milestone);
        await using var cmd = uow.Command(
            "INSERT INTO programs.milestone (program_id, title, due_date, created_at, created_by) VALUES ($1, $2, $3, $4, $5) RETURNING milestone_id",
            milestone.ProgramId, milestone.Title, milestone.DueDate, at, actor);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<MilestoneInfo?> FindMilestoneAsync(long milestoneId, CancellationToken ct) =>
        (await MilestonesAsync("WHERE milestone_id = $1", ct, milestoneId)).SingleOrDefault();

    public async Task UpdateMilestoneAsync(MilestoneInfo milestone, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(milestone);
        await using var cmd = uow.Command(
            "UPDATE programs.milestone SET completed_on = $2, completed_by = $3 WHERE milestone_id = $1",
            milestone.MilestoneId, new NpgsqlParameter { Value = (object?)milestone.CompletedOn ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date }, Text(milestone.CompletedBy));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<ProgramInfo>> ListProgramsAsync(CancellationToken ct) => ReadAsync(ProgramSql + " ORDER BY code", ReadProgram, ct);

    public async Task<ProgramInfo?> FindProgramAsync(Guid programId, CancellationToken ct) =>
        (await ReadAsync(ProgramSql + " WHERE program_id = $1", ReadProgram, ct, programId)).SingleOrDefault();

    public async Task<IReadOnlyList<ContractInfo>> ListContractsAsync(Guid? programId, CancellationToken ct)
    {
        var programArg = new NpgsqlParameter { Value = (object?)programId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid };
        var heads = await ReadAsync(
            "SELECT contract_id, program_id, number, customer, title, period_start, period_end, ceiling, status FROM programs.contract WHERE ($1::uuid IS NULL OR program_id = $1) ORDER BY number",
            r => new ContractInfo(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetFieldValue<DateOnly>(5), r.GetFieldValue<DateOnly>(6),
                NDec(r, 7), Enum.Parse<ContractStatus>(r.GetString(8)), [], [], []),
            ct,
            programArg);
        if (heads.Count == 0)
        {
            return heads;
        }

        var ids = heads.Select(h => h.ContractId).ToArray();
        var clins = (await ReadAsync(
            "SELECT contract_id, clin, parent_clin, description, part_number, quantity, unit_price FROM programs.clin WHERE contract_id = ANY($1) ORDER BY contract_id, clin",
            r => (Id: r.GetInt64(0), Clin: new ClinInfo(r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), NDec(r, 5), NDec(r, 6))),
            ct,
            ids)).ToLookup(x => x.Id, x => x.Clin);
        var deliveries = (await ReadAsync(
            "SELECT delivery_id, contract_id, clin, quantity, due_date, cancelled, created_by, created_at FROM programs.delivery WHERE contract_id = ANY($1) ORDER BY due_date, delivery_id",
            ReadDelivery,
            ct,
            ids)).ToLookup(d => d.ContractId);
        var requirements = (await ReadAsync(
            "SELECT contract_id, number, code, text FROM programs.requirement WHERE contract_id = ANY($1) ORDER BY contract_id, number",
            r => (Id: r.GetInt64(0), Requirement: new RequirementInfo(r.GetInt32(1), r.GetString(2), r.GetString(3))),
            ct,
            ids)).ToLookup(x => x.Id, x => x.Requirement);
        return heads.Select(h => h with { Clins = clins[h.ContractId].ToList(), Deliveries = deliveries[h.ContractId].ToList(), Requirements = requirements[h.ContractId].ToList() }).ToList();
    }

    public async Task<DeliveryReference?> FindDeliveryAsync(long deliveryId, CancellationToken ct) =>
        (await ReadAsync(
            """
            SELECT d.delivery_id, d.contract_id, d.clin, d.quantity, d.due_date, d.cancelled, d.created_by, d.created_at,
                   c.program_id, p.code, c.number, c.customer, c.status, l.part_number
            FROM programs.delivery d
            JOIN programs.contract c ON c.contract_id = d.contract_id
            JOIN programs.program p ON p.program_id = c.program_id
            JOIN programs.clin l ON l.contract_id = d.contract_id AND l.clin = d.clin
            WHERE d.delivery_id = $1
            """,
            r => new DeliveryReference(ReadDelivery(r), r.GetGuid(8), r.GetString(9), r.GetString(10), r.GetString(11), Enum.Parse<ContractStatus>(r.GetString(12)), r.IsDBNull(13) ? null : r.GetString(13)),
            ct,
            deliveryId)).SingleOrDefault();

    public Task<IReadOnlyList<MilestoneInfo>> ListMilestonesAsync(Guid? programId, CancellationToken ct) =>
        MilestonesAsync("WHERE ($1::uuid IS NULL OR program_id = $1)", ct, new NpgsqlParameter { Value = (object?)programId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });

    private Task<IReadOnlyList<MilestoneInfo>> MilestonesAsync(string where, CancellationToken ct, params object?[] args) =>
        ReadAsync(
            $"SELECT milestone_id, program_id, title, due_date, completed_on, completed_by FROM programs.milestone {where} ORDER BY due_date, milestone_id",
            r => new MilestoneInfo(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateOnly>(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateOnly>(4), r.IsDBNull(5) ? null : r.GetString(5)),
            ct,
            args);

    private static ProgramInfo ReadProgram(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        Enum.Parse<ProgramStatus>(r.GetString(5)), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7));

    private static DeliveryInfo ReadDelivery(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), Dec(r, 3), r.GetFieldValue<DateOnly>(4), r.GetBoolean(5), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7));

    private static decimal Normal(decimal value) => value / 1.000000000000000000000000000000000m;

    private static decimal Dec(NpgsqlDataReader r, int ordinal) => Normal(r.GetDecimal(ordinal));

    private static decimal? NDec(NpgsqlDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : Normal(r.GetDecimal(ordinal));

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Num(decimal? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric };

    private async Task<IReadOnlyList<T>> ReadAsync<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
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

public static class ProgramsRegistration
{
    public static IServiceCollection AddForgePrograms(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(ProgramsRegistration).Assembly, "programs");
        services.AddForgeStore<IProgramsStore>(uow => new PostgresProgramsStore(uow));
        services.AddForgeStore<IProgramsLookup>(uow => new PostgresProgramsStore(uow));
        services.AddSingleton<ProgramsService>();
        return services;
    }
}
