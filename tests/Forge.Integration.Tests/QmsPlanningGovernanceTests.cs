using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Forge.Integration.Tests.QmsPlanningScenario;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class QmsPlanningGovernanceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Definitions_use_actual_sources_independent_reviews_versions_and_immutable_scope()
    {
        await using var f=await CreateAsync(postgres);await DocumentAsync(f,"PLANNING.BASIS");await CreateRecordAsync(f,"METHOD",QmsPlanningKind.EvaluationMethod,Method("PLANNING.BASIS"));
        await RejectedAsync(await f.Plant.Try(f.Author,Api+"/records/METHOD/revisions/1/effective",Decision(1)),"planning_state");
        await f.Plant.Ok(f.Author,Api+"/records/METHOD/revisions/1/submit",Decision(1));
        await RejectedAsync(await f.Plant.Try(f.Author,Api+"/records/METHOD/revisions/1/review",Decision(2)),"separation_of_duties");
        await RejectedAsync(await f.Plant.Try(f.Reviewer,Api+"/records/METHOD/revisions/1/review",Decision(1)),"stale_version");
        await f.Plant.Ok(f.Reviewer,Api+"/records/METHOD/revisions/1/review",Decision(2));await f.Plant.Ok(f.Reviewer,Api+"/records/METHOD/revisions/1/approve",Decision(3));
        await RejectedAsync(await f.Plant.Try(f.Author,Api+"/records/METHOD/revisions",new{expectedVersion=4,content=Method("PLANNING.BASIS"),reason="Try bypassing an open proposal"}),"planning_open_revision");
        await f.Plant.Ok(f.Reviewer,Api+"/records/METHOD/revisions/1/effective",Decision(4));
        await f.Plant.Ok(f.Author,Api+"/records/METHOD/revisions",new{expectedVersion=5,content=Method("PLANNING.BASIS"),reason="Actual revised method draft"});await EffectiveAsync(f,"METHOD",2);
        var record=await ReadAsync(f.Reviewer,Api+"/records/METHOD");Assert.Equal("PLANNING-EXAMPLE-ORG",record.GetProperty("organizationCode").GetString());Assert.Equal("PLANT1",record.GetProperty("facilityCode").GetString());
        Assert.Equal("Superseded",record.GetProperty("revisions")[0].GetProperty("state").GetString());Assert.Equal("Effective",record.GetProperty("revisions")[1].GetProperty("state").GetString());Assert.NotEqual(JsonValueKind.Null,record.GetProperty("revisions")[0].GetProperty("effectiveTo").ValueKind);
        var source=record.GetProperty("revisions")[1].GetProperty("sources")[0];Assert.Equal("Engineering",source.GetProperty("ownerContext").GetString());Assert.Equal("A",source.GetProperty("ownerRevision").GetString());Assert.Equal("PLANNING.BASIS",source.GetProperty("ownerId").GetString());
    }

    [Fact]
    public async Task Earlier_draft_editor_is_still_a_contributor_after_another_editor_changes_content()
    {
        await using var f=await CreateAsync(postgres);await DocumentAsync(f,"EDITOR.BASIS");await CreateRecordAsync(f,"METHOD",QmsPlanningKind.EvaluationMethod,Method("EDITOR.BASIS"));
        await f.Plant.Ok(f.Reviewer,Api+"/records/METHOD/revisions/1/edit",new{expectedVersion=1,content=Method("EDITOR.BASIS"),reason="Earlier editor records actual criteria"});
        await f.Plant.Ok(f.Author,Api+"/records/METHOD/revisions/1/edit",new{expectedVersion=2,content=Method("EDITOR.BASIS"),reason="A later editor corrects criteria"});
        await f.Plant.Ok(f.Author,Api+"/records/METHOD/revisions/1/submit",Decision(3));
        await RejectedAsync(await f.Plant.Try(f.Reviewer,Api+"/records/METHOD/revisions/1/review",Decision(4)),"separation_of_duties");
        await f.Plant.Ok(f.Observer,Api+"/records/METHOD/revisions/1/review",Decision(4));await f.Plant.Ok(f.Observer,Api+"/records/METHOD/revisions/1/approve",Decision(5));
    }

    [Fact]
    public async Task Withdrawn_exact_governing_definition_blocks_original_task_create_replay_and_invented_source_ids()
    {
        await using var f=await CreateAsync(postgres);await DocumentAsync(f,"REPLAY.BASIS");await CreateRecordAsync(f,"METHOD",QmsPlanningKind.EvaluationMethod,Method("REPLAY.BASIS"));await EffectiveAsync(f,"METHOD");
        var body=new{code="METHOD",definitionRevision=1,title="Verify recorded source bytes",ownerPersonId=f.Plant.People["eng"],dueAt=f.Time.Now.AddDays(7),resources="Actual document controller time",completionCriteria="Record the exact file hash inspected by the assigned owner",reason="Actual owned task"};
        await JsonAsync(await f.Author.CommandAsync(Api+"/tasks",body,"planning-original-task"));
        await f.Plant.Ok(f.Reviewer,Api+"/records/METHOD/revisions/1/withdraw",Decision(5));
        await RejectedAsync(await f.Author.CommandAsync(Api+"/tasks",body,"planning-original-task"),"planning_source_not_effective");
        var invalid=Method("REPLAY.BASIS") with{Evidence=[new("Engineering","DocumentRevision","REPLAY.BASIS","A",AuditId:Guid.NewGuid())]};
        await RejectedAsync(await f.Plant.Try(f.Author,Api+"/records",new{code="BAD-PROOF",kind="EvaluationMethod",facilityCode="PLANT1",markings="None",content=invalid,reason="A random audit ID is not evidence"}),"planning_evidence_unavailable");
    }

    [Fact]
    public async Task Actual_owned_tasks_require_owner_completion_independent_verification_and_frozen_source_evidence()
    {
        await using var f=await CreateAsync(postgres);await DocumentAsync(f,"TASK.BASIS");await CreateRecordAsync(f,"METHOD",QmsPlanningKind.EvaluationMethod,Method("TASK.BASIS"));await EffectiveAsync(f,"METHOD");
        var created=await f.Plant.Ok(f.Author,Api+"/tasks",new{code="METHOD",definitionRevision=1,title="Verify original controlled source byte hash",ownerPersonId=f.Plant.People["eng"],dueAt=f.Time.Now.AddDays(2),resources="Assigned controller review time",completionCriteria="Assigned person records exact source reviewed",reason="Actual owned governance task"});var id=created.GetProperty("entityId").GetString();
        var decision=new{expectedVersion=1,result="The exact controlled source was reviewed by the assigned owner",evidence=new[]{new QmsPlanningEvidenceReference("Engineering","DocumentRevision","TASK.BASIS","A")},reason="Actual attributed owner completion"};
        await RejectedAsync(await f.Plant.Try(f.Reviewer,$"{Api}/tasks/{id}/complete",decision),"planning_task_owner");await f.Plant.Ok(f.Author,$"{Api}/tasks/{id}/complete",decision);
        await RejectedAsync(await f.Plant.Try(f.Author,$"{Api}/tasks/{id}/verify",decision with{expectedVersion=2}),"separation_of_duties");
        await f.Plant.Ok(f.Reviewer,$"{Api}/tasks/{id}/verify",new{expectedVersion=2,result="Independent review confirmed the assigned source review evidence",evidence=Array.Empty<QmsPlanningEvidenceReference>(),reason="Independent actual effectiveness prerequisite"});
        var work=await ReadAsync(f.Reviewer,Api+"/records/METHOD/revisions/1/work");var task=work.GetProperty("tasks")[0];Assert.Equal("Verified",task.GetProperty("state").GetString());Assert.Equal(f.Plant.People["eng"],task.GetProperty("ownerPersonId").GetGuid());Assert.Equal("person:"+f.Plant.People["eng"],task.GetProperty("completedBy").GetString());Assert.Equal("person:"+f.Plant.People["approver"],task.GetProperty("verifiedBy").GetString());
    }

    [Fact]
    public async Task Retention_exact_manifest_rechecks_holds_before_original_approval_retry_and_never_deletes_bytes()
    {
        await using var f=await CreateAsync(postgres);await DocumentAsync(f,"SCHEDULE.BASIS");await DocumentAsync(f,"RETAINED.SOURCE");await CreateRecordAsync(f,"SCHEDULE",QmsPlanningKind.RetentionSchedule,Schedule(f,"SCHEDULE.BASIS"));await EffectiveAsync(f,"SCHEDULE");var binding=await BindingAsync(f,"SCHEDULE","RETAINED.SOURCE");var proposal=await DispositionAsync(f,binding);
        var bytesBefore=await SnapshotAsync(f);await f.Plant.Ok(f.Author,$"{Api}/retention/dispositions/{proposal}/dryrun",new{expectedVersion=1,note="Exact candidate source/retention/hold dry-run",reason="No physical deletion performed"});
        var frozen=await ReadAsync(f.Reviewer,$"{Api}/retention/dispositions/{proposal}");Assert.True(frozen.GetProperty("manifest").GetProperty("eligible").GetBoolean());var fingerprint=frozen.GetProperty("manifest").GetProperty("fingerprint").GetString();
        var approval=Decision(2);await JsonAsync(await f.Reviewer.CommandAsync($"{Api}/retention/dispositions/{proposal}/approve",approval,"planning-original-disposition-approve"));
        await f.Plant.Ok(f.Reviewer,Api+"/retention/holds",new{facilityCode="PLANT1",markings="None",kind="Legal",subject=new QmsPlanningEvidenceReference("Engineering","DocumentRevision","RETAINED.SOURCE","A"),basis="Actual contract review hold",authorityBasis="Authorized legal records controller declaration",evidence=new[]{new QmsPlanningEvidenceReference("Engineering","DocumentRevision","SCHEDULE.BASIS","A")},reason="Prevent administrative disposition while legal review is active"});
        await RejectedAsync(await f.Reviewer.CommandAsync($"{Api}/retention/dispositions/{proposal}/approve",approval,"planning-original-disposition-approve"),"retention_manifest_changed");
        await RejectedAsync(await f.Plant.Try(f.Reviewer,$"{Api}/retention/dispositions/{proposal}/record",new{expectedVersion=3,note="Attempt record while held",observationKind="AdministrativeRecordOnly",evidence=new[]{new QmsPlanningEvidenceReference("Engineering","DocumentRevision","SCHEDULE.BASIS","A")},reason="Held source must remain blocked"}),"retention_manifest_changed");
        Assert.Equal(bytesBefore,await SnapshotAsync(f));var current=await ReadAsync(f.Reviewer,$"{Api}/retention/dispositions/{proposal}");Assert.Equal(fingerprint,current.GetProperty("manifest").GetProperty("fingerprint").GetString());Assert.Equal("Approved",current.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Shared_source_bytes_require_every_actual_file_owner_in_exact_manifest_and_keep_all_record_versions()
    {
        await using var f=await CreateAsync(postgres);await DocumentAsync(f,"SHARED.SCHEDULE");var hashA=await DocumentAsync(f,"SHARED.A","The same actual immutable source bytes");var hashB=await DocumentAsync(f,"SHARED.B","The same actual immutable source bytes");Assert.Equal(hashA,hashB);
        await CreateRecordAsync(f,"SCHEDULE",QmsPlanningKind.RetentionSchedule,Schedule(f,"SHARED.SCHEDULE"));await EffectiveAsync(f,"SCHEDULE");var a=await BindingAsync(f,"SCHEDULE","SHARED.A");var incomplete=await DispositionAsync(f,a);
        await f.Plant.Ok(f.Author,$"{Api}/retention/dispositions/{incomplete}/dryrun",Decision(1));var frozen=await ReadAsync(f.Reviewer,$"{Api}/retention/dispositions/{incomplete}");Assert.False(frozen.GetProperty("manifest").GetProperty("eligible").GetBoolean());Assert.Contains("shared_owner_not_in_exact_manifest",frozen.GetRawText(),StringComparison.Ordinal);
        await RejectedAsync(await f.Plant.Try(f.Reviewer,$"{Api}/retention/dispositions/{incomplete}/approve",Decision(2)),"retention_manifest_changed");
        var b=await BindingAsync(f,"SCHEDULE","SHARED.B");var complete=await DispositionAsync(f,a,b);var before=await SnapshotAsync(f);await f.Plant.Ok(f.Author,$"{Api}/retention/dispositions/{complete}/dryrun",Decision(1));await f.Plant.Ok(f.Reviewer,$"{Api}/retention/dispositions/{complete}/approve",Decision(2));
        await f.Plant.Ok(f.Reviewer,$"{Api}/retention/dispositions/{complete}/record",new{expectedVersion=3,note="Administrative disposition review recorded; all source bytes preserved",observationKind="AdministrativeRecordOnly",evidence=new[]{new QmsPlanningEvidenceReference("Engineering","DocumentRevision","SHARED.SCHEDULE","A")},reason="Attributed administrative observation"});
        Assert.Equal(before,await SnapshotAsync(f));var record=await ReadAsync(f.Reviewer,$"{Api}/retention/dispositions/{complete}");Assert.Equal("Recorded",record.GetProperty("state").GetString());Assert.Equal("AdministrativeRecordOnly",record.GetProperty("observationKind").GetString());
        await using var connection=new NpgsqlConnection(f.App.ConnectionString);await connection.OpenAsync();await using var query=new NpgsqlCommand("SELECT count(*) FROM quality.planning_version WHERE entity_type='Disposition' AND entity_id=$1",connection);query.Parameters.AddWithValue(complete.ToString());Assert.Equal(4L,(long)(await query.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData(true,QmsRetentionTrigger.SourceCreatedAt,"indefinite_retention")]
    [InlineData(false,QmsRetentionTrigger.SourceClosedAt,"trigger_unknown")]
    public async Task Unknown_trigger_and_indefinite_retention_are_explicit_blockers(bool indefinite,QmsRetentionTrigger trigger,string blocker)
    {
        await using var f=await CreateAsync(postgres);await DocumentAsync(f,"UNKNOWN.SCHEDULE");await DocumentAsync(f,"UNKNOWN.SOURCE");await CreateRecordAsync(f,"SCHEDULE",QmsPlanningKind.RetentionSchedule,Schedule(f,"UNKNOWN.SCHEDULE",indefinite:indefinite,trigger:trigger));await EffectiveAsync(f,"SCHEDULE");var binding=await BindingAsync(f,"SCHEDULE","UNKNOWN.SOURCE");var disposition=await DispositionAsync(f,binding);
        await f.Plant.Ok(f.Author,$"{Api}/retention/dispositions/{disposition}/dryrun",Decision(1));var result=await ReadAsync(f.Reviewer,$"{Api}/retention/dispositions/{disposition}");Assert.False(result.GetProperty("manifest").GetProperty("eligible").GetBoolean());Assert.Contains(blocker,result.GetRawText(),StringComparison.Ordinal);
        await RejectedAsync(await f.Plant.Try(f.Reviewer,$"{Api}/retention/dispositions/{disposition}/approve",Decision(2)),"retention_manifest_changed");
    }

    [Fact]
    public async Task Legal_hold_release_requires_actual_evidence_and_distinct_proposer_approver()
    {
        await using var f=await CreateAsync(postgres);await DocumentAsync(f,"HOLD.AUTHORITY");await DocumentAsync(f,"HOLD.SOURCE");var source=new QmsPlanningEvidenceReference("Engineering","DocumentRevision","HOLD.SOURCE","A");var proof=new QmsPlanningEvidenceReference("Engineering","DocumentRevision","HOLD.AUTHORITY","A");
        var hold=await f.Plant.Ok(f.Reviewer,Api+"/retention/holds",new{facilityCode="PLANT1",markings="None",kind="Quality",subject=source,basis="Actual review of source integrity",authorityBasis="Quality records owner control",evidence=new[]{proof},reason="Explicit quality hold"});var id=hold.GetProperty("entityId").GetString();
        await f.Plant.Ok(f.Author,$"{Api}/retention/holds/{id}/proposerelease",new{expectedVersion=1,note="Actual quality review resolved the recorded concern",evidence=new[]{proof},reason="Human proposal with actual retained authority evidence"});
        await RejectedAsync(await f.Plant.Try(f.Author,$"{Api}/retention/holds/{id}/release",new{expectedVersion=2,note="Try self approval",evidence=new[]{proof},reason="Must be rejected"}),"separation_of_duties");
        await RejectedAsync(await f.Plant.Try(f.Reviewer,$"{Api}/retention/holds/{id}/release",new{expectedVersion=2,note="Placer cannot approve own hold release",evidence=new[]{proof},reason="Must be independently reviewed"}),"separation_of_duties");
        await f.Plant.Ok(f.Observer,$"{Api}/retention/holds/{id}/release",new{expectedVersion=2,note="Independent controller reviewed actual resolution and retained evidence",evidence=Array.Empty<QmsPlanningEvidenceReference>(),reason="Independent attributed hold release"});var current=await ReadAsync(f.Observer,$"{Api}/retention/holds/{id}");Assert.Equal("Released",current.GetProperty("state").GetString());Assert.Equal(3,current.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Real_recorded_metric_filters_closed_half_open_period_exact_operations_and_returns_no_filtered_cohort_scalar()
    {
        await using var f=await CreateAsync(postgres,engineered:true);var p=f.Plant;await p.Person("planning-terminal",Roles.ShopFloorTerminal);var terminal=p.As("planning-terminal");
        await p.Ok(p.Admin,$"/api/workforce/people/{p.People["sarah"]}/badge",new{badge="PLANNING-SARAH"});await p.Ok(terminal,"/api/shopfloor/clock-in",new{badge="PLANNING-SARAH"});
        var orderResult=await p.Ok(f.Author,"/api/orders/",new{partNumber="ROLL-100",quantity=10m,dueDate=f.Time.Now.AddDays(10).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),priority=3});var order=long.Parse(orderResult.GetProperty("entityId").GetString()!,CultureInfo.InvariantCulture);
        await p.Ok(p.As("planner"),$"/api/orders/{order}/release",new{reason="Real metric cohort configuration"});await p.Ok(p.As("stores"),"/api/inventory/issues",new{orderNumber=order,partNumber="COIL-AL",quantity=26m,locationCode="STORES",reason="Actual readiness before recorded output"});
        var from=f.Time.Now;await p.Ok(terminal,"/api/shopfloor/start",new{badge="PLANNING-SARAH",orderNumber=order,sequence=10,machineCode="SLITTER-A"});await p.Ok(terminal,"/api/shopfloor/report",new{badge="PLANNING-SARAH",orderNumber=order,sequence=10,good=3m,scrap=0m,complete=false});
        var to=from.AddMinutes(1);f.Time.Now=to;await p.Ok(terminal,"/api/shopfloor/report",new{badge="PLANNING-SARAH",orderNumber=order,sequence=10,good=2m,scrap=0m,complete=false});f.Time.Now=to.AddMinutes(1);
        var metric=new QmsMetricDefinitionContent("Recorded good at actual op10",QmsMetricSourceKind.RecordedProductionReports,QmsRecordedProductionMeasure.GoodQuantity,"EA","Recorded good from op10","Not a ratio; no denominator inferred","Sum actual report quantities for these exact owner records","Exactly the named order and operation",[order],[10],false,from,to,100,"Every recorded op10 report in the closed half-open period","On review");
        var bridge=f.App.Services.GetRequiredService<IQmsPlanningEvidenceReader>();var factory=f.App.Services.GetRequiredService<IKernelUnitOfWorkFactory>();
        await using(var uow=await factory.BeginAsync(default))
        {
            var principal=(await uow.People.LoadPrincipalAsync(new(p.People["eng"]),default))!;var measured=await bridge.MeasureAsync(uow,principal,metric,from,to,default);Assert.Equal(QmsMetricAvailability.Complete,measured.Availability);Assert.Equal(3m,measured.Actual);Assert.Single(measured.Evidence);Assert.Equal(order,measured.Evidence[0].OrderNumber);Assert.Equal(10,measured.Evidence[0].Sequence);Assert.True(measured.Evidence[0].ReportedAt<to);
            var missing=await bridge.MeasureAsync(uow,principal,metric with{OrderNumbers=[order,999999]},from,to,default);Assert.Equal(QmsMetricAvailability.Unavailable,missing.Availability);Assert.Null(missing.Actual);Assert.Empty(missing.Evidence);
            var stillOpen=await bridge.MeasureAsync(uow,principal,metric with{PeriodTo=f.Time.Now.AddHours(1)},from,f.Time.Now.AddHours(1),default);Assert.Equal(QmsMetricAvailability.Partial,stillOpen.Availability);Assert.Null(stillOpen.Actual);
        }
        await p.Person("planning-no-order-access",Roles.QmsReviewer);
        await using(var uow=await factory.BeginAsync(default)){var denied=(await uow.People.LoadPrincipalAsync(new(p.People["planning-no-order-access"]),default))!;var inaccessible=await bridge.MeasureAsync(uow,denied,metric,from,to,default);Assert.Equal(QmsMetricAvailability.Unavailable,inaccessible.Availability);Assert.Null(inaccessible.Actual);Assert.Empty(inaccessible.Evidence);}
    }

    [Fact]
    public async Task Metrics_dashboard_shows_only_effective_authorized_cohorts_and_never_calculates_human_assessments()
    {
        await using var f=await CreateAsync(postgres,engineered:true);var p=f.Plant;
        await p.Person("planning-terminal",Roles.ShopFloorTerminal);var terminal=p.As("planning-terminal");
        await p.Ok(p.Admin,$"/api/workforce/people/{p.People["sarah"]}/badge",new{badge="DASHBOARD-SARAH"});
        await p.Ok(terminal,"/api/shopfloor/clock-in",new{badge="DASHBOARD-SARAH"});
        var orderResult=await p.Ok(f.Author,"/api/orders/",new{partNumber="ROLL-100",quantity=10m,dueDate=f.Time.Now.AddDays(10).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),priority=3});
        var order=long.Parse(orderResult.GetProperty("entityId").GetString()!,CultureInfo.InvariantCulture);
        await p.Ok(p.As("planner"),$"/api/orders/{order}/release",new{reason="Authorize exact dashboard cohort"});
        await p.Ok(p.As("stores"),"/api/inventory/issues",new{orderNumber=order,partNumber="COIL-AL",quantity=26m,locationCode="STORES",reason="Actual readiness for the recorded KPI cohort"});
        var from=f.Time.Now;
        await p.Ok(terminal,"/api/shopfloor/start",new{badge="DASHBOARD-SARAH",orderNumber=order,sequence=10,machineCode="SLITTER-A"});
        await p.Ok(terminal,"/api/shopfloor/report",new{badge="DASHBOARD-SARAH",orderNumber=order,sequence=10,good=3m,scrap=0m,complete=false});
        var to=from.AddMinutes(1);f.Time.Now=to;
        await p.Ok(terminal,"/api/shopfloor/report",new{badge="DASHBOARD-SARAH",orderNumber=order,sequence=10,good=2m,scrap=0m,complete=false});f.Time.Now=to.AddMinutes(1);
        var recorded=new QmsMetricDefinitionContent("Recorded good at operation 10",QmsMetricSourceKind.RecordedProductionReports,QmsRecordedProductionMeasure.GoodQuantity,"EA","Recorded good quantity","Not a ratio; no denominator is inferred","Sum actual production reports","Exactly this order and operation",[order],[10],false,from,to,100,"All reports in the closed half-open period","On review");
        var human=recorded with{Name="Customer assessment input",SourceKind=QmsMetricSourceKind.AttributedHumanObservation,ProductionMeasure=null,Unit="score",NumeratorDefinition="Human-attributed score",DenominatorDefinition="Human-declared basis",MeasurementMethod="Independent human review"};
        await CreateRecordAsync(f,"DASHBOARD-GOOD",QmsPlanningKind.MetricDefinition,new(MetricDefinition:recorded));await EffectiveAsync(f,"DASHBOARD-GOOD");
        await CreateRecordAsync(f,"DASHBOARD-HUMAN",QmsPlanningKind.MetricDefinition,new(MetricDefinition:human));await EffectiveAsync(f,"DASHBOARD-HUMAN");
        var objective=new QmsObjectiveContent("Meet the approved recorded-good target",new("DASHBOARD-GOOD",1),p.People["eng"],
            QmsBaselineStatus.Unknown,null,"No verified baseline exists",[],QmsTargetComparison.AtLeast,4m,null,0m,from,to,
            "At period close","Review actual process performance if below target","Assigned owner review time",to);
        await CreateRecordAsync(f,"DASHBOARD-OBJ",QmsPlanningKind.Objective,new(Objective:objective));await EffectiveAsync(f,"DASHBOARD-OBJ");
        var query=$"?from={Uri.EscapeDataString(from.ToString("O",CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.ToString("O",CultureInfo.InvariantCulture))}&take=100";
        var snapshot=await JsonAsync(await f.Author.GetAsync(Api+"/metrics/dashboard"+query));
        Assert.True(snapshot.GetProperty("definitionsComplete").GetBoolean());Assert.True(snapshot.GetProperty("calculatedMetricsComplete").GetBoolean());
        var items=snapshot.GetProperty("items").EnumerateArray().ToArray();Assert.Equal(2,items.Length);
        var calculated=items.Single(x=>x.GetProperty("code").GetString()=="DASHBOARD-GOOD");
        Assert.Equal("Complete",calculated.GetProperty("status").GetString());Assert.True(calculated.GetProperty("calculated").GetBoolean());
        Assert.Equal(3m,calculated.GetProperty("measurement").GetProperty("actual").GetDecimal());
        Assert.Single(calculated.GetProperty("measurement").GetProperty("evidence").EnumerateArray());
        var target=Assert.Single(calculated.GetProperty("objectives").EnumerateArray());
        Assert.Equal("DASHBOARD-OBJ",target.GetProperty("code").GetString());Assert.Equal("BelowTarget",target.GetProperty("status").GetString());
        var analyst=await JsonAsync(await f.Author.PostAsJsonAsync("/api/analyst/ask",new{question="Which quality objectives are below target?"}));
        Assert.Equal("QualityObjectivesBelowTarget",analyst.GetProperty("interpretation").GetProperty("intent").GetString());
        Assert.Single(analyst.GetProperty("rows").EnumerateArray());
        Assert.Contains(analyst.GetProperty("facts").EnumerateArray(),x=>x.GetProperty("trace").GetString()=="qms-objective:DASHBOARD-OBJ:1");
        var interpreted=await JsonAsync(await f.Author.PostAsJsonAsync("/api/analyst/interpret",new{question="Which quality objectives are below target?"}));
        Assert.Equal("QualityObjectivesBelowTarget",interpreted.GetProperty("intent").GetString());
        var manual=items.Single(x=>x.GetProperty("code").GetString()=="DASHBOARD-HUMAN");
        Assert.Equal("AssessmentRequired",manual.GetProperty("status").GetString());Assert.False(manual.GetProperty("calculated").GetBoolean());
        Assert.Equal(JsonValueKind.Null,manual.GetProperty("measurement").ValueKind);
        await p.Person("dashboard-no-order-access",Roles.QmsReviewer);
        var hidden=await JsonAsync(await p.As("dashboard-no-order-access").GetAsync(Api+"/metrics/dashboard"+query));
        Assert.Empty(hidden.GetProperty("items").EnumerateArray());
    }
}
