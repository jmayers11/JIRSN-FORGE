using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

internal static class QmsPlanningScenario
{
    public const string Api="/api/quality/qms/planning";
    internal sealed class Clock(DateTimeOffset now) : TimeProvider
    {public DateTimeOffset Now{get;set;}=now;public override DateTimeOffset GetUtcNow()=>Now;}
    internal sealed class Flow(ForgeApp app,SlittingPlant.Plant plant,Clock clock) : IAsyncDisposable
    {
        public ForgeApp App{get;}=app;public SlittingPlant.Plant Plant{get;}=plant;public Clock Time{get;}=clock;
        public HttpClient Author=>Plant.Engineer;public HttpClient Reviewer=>Plant.Approver;public HttpClient Observer=>Plant.As("planning-observer");
        public ValueTask DisposeAsync()=>App.DisposeAsync();
    }
    public static async Task<Flow> CreateAsync(PostgresFixture postgres,bool engineered=false)
    {
        var clock=new Clock(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var app=new ForgeApp(await postgres.CreateDatabaseAsync(),s=>s.AddSingleton<TimeProvider>(clock),b=>b.UseSetting(QmsService.ConfigurationKey,"PLANNING-EXAMPLE-ORG"));
        try
        {
            SlittingPlant.Plant plant;
            if(engineered)plant=(await SlittingPlant.BuildEngineeredAsync(app)).Plant;
            else
            {
                plant=new(app);await plant.Person("eng",Roles.ManufacturingEngineer);await plant.Person("approver",Roles.DocumentController);
                await plant.Ok(plant.Engineer,"/api/resources/sites",new{code="PLANT1",name="Actual planning example facility",timeZone=TestSite.TimeZone});
            }
            foreach(var subject in new[]{"eng","approver"})
                foreach(var role in new[]{Roles.QmsAuthor,Roles.QmsReviewer,Roles.QmsApprover,Roles.DocumentController})
                    await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People[subject]}/roles",new{role,reason="Explicit planning governance authorities for this example"});
            await plant.Person("planning-observer",Roles.QmsAuthor,Roles.QmsReviewer,Roles.QmsApprover,Roles.DocumentController);
            await plant.Ok(plant.Admin,"/api/kernel/roles",new{role="PlanningActualEvidenceRead",displayName="Actual selected production evidence reading",permissions=new[]{Permissions.OrdersRead,Permissions.ShopFloorRead,Permissions.ResourcesRead,Permissions.QualityRead},reason="Explicit actual owner source visibility"});
            foreach(var subject in new[]{"eng","approver","planning-observer"})await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People[subject]}/roles",new{role="PlanningActualEvidenceRead",reason="Read each actual mixed owner source in this scenario"});
            return new(app,plant,clock);
        }
        catch{await app.DisposeAsync();throw;}
    }
    public static object Decision(int version,string note="Actual independently reviewed criteria, sources and human basis")=>new{expectedVersion=version,note,reason="Explicit human governance decision"};
    public static async Task EffectiveAsync(Flow f,string code,int revision=1)
    {
        var path=$"{Api}/records/{code}/revisions/{revision}";await f.Plant.Ok(f.Author,path+"/submit",Decision(1));await f.Plant.Ok(f.Reviewer,path+"/review",Decision(2));await f.Plant.Ok(f.Reviewer,path+"/approve",Decision(3));await f.Plant.Ok(f.Reviewer,path+"/effective",Decision(4));
    }
    public static Task<JsonElement> CreateRecordAsync(Flow f,string code,QmsPlanningKind kind,QmsPlanningContent content)=>
        f.Plant.Ok(f.Author,Api+"/records",new{code,kind=kind.ToString(),facilityCode="PLANT1",programId=(Guid?)null,markings="None",content,reason="Actual organization owner chose this typed definition"});
    public static async Task<string> DocumentAsync(Flow f,string code,string? bytes=null)
    {
        using var upload=new HttpRequestMessage(HttpMethod.Post,"/api/engineering/document-files"){Content=new ByteArrayContent(Encoding.UTF8.GetBytes(bytes??"Actual source bytes for "+code))};
        upload.Headers.Add("Idempotency-Key","planning-source-"+Guid.NewGuid().ToString("N"));upload.Headers.Add("X-File-Name",code+".txt");
        var file=await JsonAsync(await f.Author.SendAsync(upload));var sha=file.GetProperty("entityId").GetString()!;
        await f.Plant.Ok(f.Author,"/api/engineering/documents",new{number=code,title="Actual explicit organization source "+code,type="Procedure",authority="Internal"});
        await f.Plant.Ok(f.Author,$"/api/engineering/documents/{code}/revisions",new{revision="A",fileSha256=sha,fileName=code+".txt"});
        await f.Plant.Ok(f.Author,$"/api/engineering/documents/{code}/revisions/A/submit",new{});await f.Plant.Ok(f.Reviewer,$"/api/engineering/documents/{code}/revisions/A/approve",new{});await f.Plant.Ok(f.Reviewer,$"/api/engineering/documents/{code}/revisions/A/release",new{reason="Independent actual controlled source release"});return sha;
    }
    public static QmsPlanningContent Method(string document)=>new(EvaluationMethod:new("Actual process consequence/benefit evaluation",QmsEvaluationMethodKind.Qualitative,"Assess actual consequences and recorded evidence","Record a human acceptance/action basis",[],"On change"),Documents:[new(document,"A")]);
    public static QmsPlanningContent Schedule(Flow f,string document,int days=0,bool indefinite=false,QmsRetentionTrigger trigger=QmsRetentionTrigger.SourceCreatedAt)=>new(RetentionSchedule:new("Controlled original source revisions","Explicit example contract record requirement",trigger,indefinite?null:days,indefinite,f.Plant.People["eng"],"Owner-approved contract basis; no universal duration","Independent authorized review","Attributed administrative decision and actual source evidence",["Engineering/DocumentRevision"]),Documents:[new(document,"A")]);
    public static async Task<Guid> BindingAsync(Flow f,string schedule,string subject)
    {
        var result=await f.Plant.Ok(f.Author,Api+"/retention/bindings",new{schedule=new QmsRevisionReference(schedule,1),subject=new QmsPlanningEvidenceReference("Engineering","DocumentRevision",subject,"A"),externalTriggerAt=(DateTimeOffset?)null,triggerBasis="Actual immutable owner's authored timestamp",evidence=Array.Empty<QmsPlanningEvidenceReference>(),reason="Explicit exact-source retention proposal"});var id=Guid.Parse(result.GetProperty("entityId").GetString()!);
        await f.Plant.Ok(f.Reviewer,$"{Api}/retention/bindings/{id}/approve",Decision(1));return id;
    }
    public static async Task<Guid> DispositionAsync(Flow f,params Guid[] bindings)
    {
        var result=await f.Plant.Ok(f.Author,Api+"/retention/dispositions",new{facilityCode="PLANT1",programId=(Guid?)null,markings="None",bindingIds=bindings,purpose="Administrative review of exactly these actual bindings; Forge bytes preserved",reason="Exact controlled proposal"});return Guid.Parse(result.GetProperty("entityId").GetString()!);
    }
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response,HttpStatusCode expected=HttpStatusCode.OK)
    {
        using(response){var text=await response.Content.ReadAsStringAsync();Assert.True(response.StatusCode==expected,$"Expected {(int)expected}, got {(int)response.StatusCode}: {text}");using var json=JsonDocument.Parse(text);return json.RootElement.Clone();}
    }
    public static Task<JsonElement> ReadAsync(HttpClient actor,string route)=>ReadCore(actor,route);
    private static async Task<JsonElement> ReadCore(HttpClient actor,string route)=>await JsonAsync(await actor.GetAsync(route));
    public static async Task RejectedAsync(HttpResponseMessage response,string code,HttpStatusCode status=HttpStatusCode.UnprocessableEntity)
    {var problem=await JsonAsync(response,status);Assert.Equal(code,problem.GetProperty("code").GetString());}
    public static async Task<string[]> SnapshotAsync(Flow f)
    {
        await using var connection=new NpgsqlConnection(f.App.ConnectionString);await connection.OpenAsync();var rows=new List<string>();
        foreach(var sql in new[]{"SELECT row_to_json(t)::text FROM engineering.document_file t ORDER BY sha256","SELECT row_to_json(t)::text FROM engineering.document_revision t ORDER BY document_number,revision","SELECT row_to_json(t)::text FROM execution.production_report t ORDER BY report_id"})
        {await using var cmd=new NpgsqlCommand(sql,connection);await using var reader=await cmd.ExecuteReaderAsync();while(await reader.ReadAsync())rows.Add(reader.GetString(0));}return rows.ToArray();
    }
}
