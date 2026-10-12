using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;

namespace Forge.Planning.Infrastructure;

internal sealed class PostgresPlanningScenarioStore(PostgresUnitOfWork uow) : IPlanningScenarioStore
{
    public async Task<Guid> InsertAsync(PlanningScenarioInfo scenario, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            INSERT INTO planning.scenario(scenario_id,owner_person_id,name,change_json,created_at,changed_at,created_by,changed_by)
            VALUES($1,$2,$3,$4,$5,$6,$7,$7)
            """, scenario.ScenarioId, scenario.Owner.Value, scenario.Name, CanonicalJson.Serialize(scenario.Change), scenario.CreatedAt, scenario.ChangedAt, "person:" + scenario.Owner);
        await cmd.ExecuteNonQueryAsync(ct);
        return scenario.ScenarioId;
    }

    public async Task<PlanningScenarioInfo?> FindAsync(Guid scenarioId, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            SELECT scenario_id,owner_person_id,name,change_json,created_at,changed_at
            FROM planning.scenario WHERE scenario_id=$1
            """, scenarioId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<PlanningScenarioInfo>> ListAsync(PersonId owner, CancellationToken ct)
    {
        var result = new List<PlanningScenarioInfo>();
        await using var cmd = uow.Command("""
            SELECT scenario_id,owner_person_id,name,change_json,created_at,changed_at
            FROM planning.scenario WHERE owner_person_id=$1 ORDER BY changed_at DESC,scenario_id
            """, owner.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        return result;
    }

    public async Task DeleteAsync(Guid scenarioId, PersonId owner, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM planning.scenario WHERE scenario_id=$1 AND owner_person_id=$2", scenarioId, owner.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static PlanningScenarioInfo Read(Npgsql.NpgsqlDataReader reader) => new(
        reader.GetGuid(0), new PersonId(reader.GetGuid(1)), reader.GetString(2),
        System.Text.Json.JsonSerializer.Deserialize<ScenarioChange>(reader.GetString(3), CanonicalJson.Options)!,
        reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5));
}
