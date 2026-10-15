using System.Text.Json;
using Forge.Quality.Contracts;
using static Forge.Integration.Tests.QmsPlanningScenario;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class QmsRetentionRevisionTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Current_schedule_withdrawal_blocks_original_disposition_approval_replay()
    {
        await using var f=await CreateAsync(postgres);await DocumentAsync(f,"CURRENT.SCHEDULE");await DocumentAsync(f,"CURRENT.SOURCE");
        await CreateRecordAsync(f,"SCHEDULE",QmsPlanningKind.RetentionSchedule,Schedule(f,"CURRENT.SCHEDULE"));await EffectiveAsync(f,"SCHEDULE");
        var binding=await BindingAsync(f,"SCHEDULE","CURRENT.SOURCE");var proposal=await DispositionAsync(f,binding);
        await f.Plant.Ok(f.Author,$"{Api}/retention/dispositions/{proposal}/dryrun",Decision(1));var approval=Decision(2);
        await JsonAsync(await f.Reviewer.CommandAsync($"{Api}/retention/dispositions/{proposal}/approve",approval,"current-schedule-original-approval"));
        await f.Plant.Ok(f.Reviewer,Api+"/records/SCHEDULE/revisions/1/withdraw",Decision(5));
        await RejectedAsync(await f.Reviewer.CommandAsync($"{Api}/retention/dispositions/{proposal}/approve",approval,"current-schedule-original-approval"),"retention_manifest_changed");
        var current=await ReadAsync(f.Reviewer,$"{Api}/retention/dispositions/{proposal}");Assert.Equal("Approved",current.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Revised_classification_supersedes_exact_binding_without_discarding_longer_prior_retention()
    {
        await using var f=await CreateAsync(postgres);await DocumentAsync(f,"CLASSIFICATION.BASIS");await DocumentAsync(f,"CLASSIFICATION.SOURCE");
        await CreateRecordAsync(f,"LONG-SCHEDULE",QmsPlanningKind.RetentionSchedule,Schedule(f,"CLASSIFICATION.BASIS",days:365));await EffectiveAsync(f,"LONG-SCHEDULE");var old=await BindingAsync(f,"LONG-SCHEDULE","CLASSIFICATION.SOURCE");
        await CreateRecordAsync(f,"SHORT-SCHEDULE",QmsPlanningKind.RetentionSchedule,Schedule(f,"CLASSIFICATION.BASIS",days:0));await EffectiveAsync(f,"SHORT-SCHEDULE");
        var proposed=await f.Plant.Ok(f.Author,Api+"/retention/bindings",new{schedule=new QmsRevisionReference("SHORT-SCHEDULE",1),subject=new QmsPlanningEvidenceReference("Engineering","DocumentRevision","CLASSIFICATION.SOURCE","A"),externalTriggerAt=(DateTimeOffset?)null,triggerBasis="Actual authored time with preserved prior classification",evidence=Array.Empty<QmsPlanningEvidenceReference>(),previousBindingId=old,reason="Explicit independently reviewed revised classification"});
        var revised=Guid.Parse(proposed.GetProperty("entityId").GetString()!);await f.Plant.Ok(f.Reviewer,$"{Api}/retention/bindings/{revised}/approve",Decision(1));
        var original=await ReadAsync(f.Reviewer,$"{Api}/retention/bindings/{old}");Assert.Equal("Superseded",original.GetProperty("state").GetString());Assert.Equal(3,original.GetProperty("version").GetInt32());
        var current=await ReadAsync(f.Reviewer,$"{Api}/retention/bindings/{revised}");Assert.Equal(old,current.GetProperty("previousBindingId").GetGuid());
        var disposition=await DispositionAsync(f,revised);await f.Plant.Ok(f.Author,$"{Api}/retention/dispositions/{disposition}/dryrun",Decision(1));
        var dryRun=await ReadAsync(f.Reviewer,$"{Api}/retention/dispositions/{disposition}");Assert.False(dryRun.GetProperty("manifest").GetProperty("eligible").GetBoolean());Assert.Contains("prior_retention_not_elapsed",dryRun.GetRawText(),StringComparison.Ordinal);
    }
}
