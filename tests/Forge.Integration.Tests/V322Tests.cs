using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Forge.Host.History;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V322Tests(PostgresFixture postgres)
{
    [Fact]
    public async Task Cause_map_follows_recorded_causation_and_does_not_infer_from_correlation()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        var dataSource = app.Services.GetRequiredService<NpgsqlDataSource>();
        var now = DateTimeOffset.UtcNow;
        var from = now.AddHours(-2);
        var correlation = Guid.NewGuid();
        var root = Guid.NewGuid();
        var effect = Guid.NewGuid();
        var coincident = Guid.NewGuid();

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO kernel.event_log(event_id,event_type,schema_version,occurred_at,recorded_at,entity_type,entity_id,correlation_id,causation_id,program_id,envelope)
            VALUES
              ($1,'EngineeringChangeReleased',1,$4,$4,'EngineeringChange','ECN-CAUSEMAP',$3,NULL,NULL,$7),
              ($2,'OrderRevisionPropagated',1,$5,$5,'ProductionOrder','123',$3,$1,NULL,$8),
              ($6,'ConstraintRecorded',1,$5 + interval '1 minute',$5 + interval '1 minute','Constraint','same-window',$3,NULL,NULL,$9)
            """, connection);
        cmd.Parameters.AddWithValue(root);
        cmd.Parameters.AddWithValue(effect);
        cmd.Parameters.AddWithValue(correlation);
        cmd.Parameters.AddWithValue(from.AddMinutes(30));
        cmd.Parameters.AddWithValue(from.AddMinutes(60));
        cmd.Parameters.AddWithValue(coincident);
        cmd.Parameters.AddWithValue("""{"sourceContext":"Engineering","markings":"None","reason":"Released change"}""");
        cmd.Parameters.AddWithValue("""{"sourceContext":"Propagation","markings":"None","reason":"Applied revision"}""");
        cmd.Parameters.AddWithValue("""{"sourceContext":"Planning","markings":"None","reason":"Observed constraint"}""");
        Assert.Equal(3, await cmd.ExecuteNonQueryAsync());

        var principal = new ForgePrincipal(new PersonId(Guid.NewGuid()), "CauseMap test", true,
            new HashSet<string>(), new HashSet<ProgramId>(), DataMarking.None);
        var causeMap = await app.Services.GetRequiredService<CauseMapReadStore>().LoadAsync(principal, from, now, CancellationToken.None);
        Assert.Contains(causeMap.Steps, s => s.EventId == root);
        Assert.Contains(causeMap.Steps, s => s.EventId == effect);
        Assert.Contains(causeMap.Steps, s => s.EventId == coincident);

        var link = Assert.Single(causeMap.Links);
        Assert.Equal(root, link.CauseEventId);
        Assert.Equal(effect, link.EffectEventId);
        Assert.Contains(coincident, causeMap.EventsWithoutVisibleCause);
    }
}
