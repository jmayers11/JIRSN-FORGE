using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Forge.Host.Identification;
using Forge.Kernel.Security;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V41AllOwnerIdentificationTests(PostgresFixture postgres)
{
    private const string Namespace="ALL.EXAMPLE";
    private static string Code(string kind,string key)=>$"forge:v1:{Namespace}:{kind}:{key}";
    private static long Number(JsonElement value)=>long.Parse(value.GetProperty("entityId").GetString()!,CultureInfo.InvariantCulture);
    private static async Task<JsonElement> Resolve(HttpClient client,string code)
    {using var response=await client.PostAsJsonAsync("/api/identification/resolve",new{code});var text=await response.Content.ReadAsStringAsync();Assert.Equal(HttpStatusCode.OK,response.StatusCode);using var result=JsonDocument.Parse(text);return result.RootElement.Clone();}
    private static async Task Hidden(HttpClient client,string code)
    {using var response=await client.PostAsJsonAsync("/api/identification/resolve",new{code});Assert.Equal(HttpStatusCode.NotFound,response.StatusCode);var text=await response.Content.ReadAsStringAsync();Assert.Contains("identification_not_found",text,StringComparison.Ordinal);Assert.DoesNotContain(code,text,StringComparison.Ordinal);}
    private static async Task<string[]> State(ForgeApp app)
    {
        string[] tables=["inventory.lot","inventory.transaction","inventory.balance","inventory.container","inventory.container_content",
            "inventory.transformation","inventory.transformation_line","inventory.shipment","inventory.shipment_line",
            "execution.production_order","execution.order_operation","execution.order_material","execution.labor",
            "quality.inspection","quality.inspection_result","resources.tool","resources.tool_event","engineering.document","engineering.document_revision","engineering.document_file",
            "kernel.audit_log","kernel.event_log","kernel.outbox","kernel.idempotency"];
        var result=new List<string>();await using var connection=new NpgsqlConnection(app.ConnectionString);await connection.OpenAsync();
        foreach(var table in tables)
        {await using var command=new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(owner_identity_snapshot_row) ORDER BY to_jsonb(owner_identity_snapshot_row)::text)::text,'[]') FROM {table} owner_identity_snapshot_row",connection);result.Add(table+":"+(string)(await command.ExecuteScalarAsync())!);}
        return result.ToArray();
    }

    [Fact]
    public async Task All_nineteen_presentations_resolve_actual_normal_command_sources_and_subtypes_never_manufacture_identity_or_authentication()
    {
        await using var app=new ForgeApp(await postgres.CreateDatabaseAsync(),configureBuilder:b=>b.UseSetting(CanonicalIdentification.NamespaceConfigurationKey,Namespace));
        var(plant,planner)=await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Ok(plant.Admin,"/api/kernel/roles",new{role="MinimalDirectoryIdentification",displayName="Minimal active person directory presentation",permissions=new[]{Permissions.IdentificationRead},reason="Printed person reference is not authentication"});
        await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["eng"]}/roles",new{role="MinimalDirectoryIdentification",reason="Explicit minimal directory reference access"});
        await plant.Person("identity-quality","QualityEngineer");var quality=plant.As("identity-quality");
        await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["eng"]}/roles",new{role="QualityEngineer",reason="Read actual inspection sources"});
        var person=Guid.Parse((await plant.Ok(plant.Admin,"/api/kernel/people",new{idpSubject="private-directory-subject",displayName="Recorded Person",markings=new[]{"ExportControlled"},reason="Actual person with a clearance that is not record classification"})).GetProperty("entityId").GetString()!);
        foreach(var(part,serialized)in new[]{("ID-RAW",false),("ID-SERIAL",true)})
        {
            await plant.Ok(plant.Engineer,"/api/engineering/items",new{partNumber=part,description=part,unitOfMeasure=serialized?"EA":"KG",makeBuy="Buy",lotTracked=!serialized,serialized});
            await plant.Ok(plant.Engineer,$"/api/engineering/items/{part}/revisions",new{revision="A"});
            await plant.Ok(plant.Approver,$"/api/engineering/items/{part}/revisions/A/release",new{reason="Independent real item configuration"});
        }
        var stores=plant.As("stores");
        foreach(var(lot,kind)in new[]{("ROLL/1","Roll"),("BATCH/1","Batch")})
            await plant.Ok(stores,"/api/inventory/receipts",new{partNumber="ID-RAW",quantity=10m,locationCode="STORES",lotNumber=lot,revision="A",identityKind=kind,supplierLot="HIDDEN-SUPPLIER-REF",reason="Actual owner kind"});
        await plant.Ok(stores,"/api/inventory/receipts",new{partNumber="ID-SERIAL",quantity=1m,locationCode="STORES",lotNumber="SERIAL/1",revision="A",identityKind="Serial",reason="Actual individual serialized unit"});
        await plant.Ok(plant.Engineer,"/api/engineering/items",new{partNumber="ID-MAKE",description="Recorded output",unitOfMeasure="EA",makeBuy="Make"});
        await plant.Ok(plant.Engineer,"/api/engineering/items/ID-MAKE/revisions",new{revision="A"});
        await plant.Ok(plant.Engineer,"/api/engineering/items/ID-MAKE/revisions/A/bom",new{lineNumber=1,componentPartNumber="ID-RAW",quantityPer=1m,scrapPercent=0m});
        await plant.Ok(plant.Engineer,"/api/engineering/items/ID-MAKE/revisions/A/routing",new{sequence=10,description="Recorded routing",workCenterCode="SLIT-WC",activityCode="RUN_SLITTER",eligibleMachines=new[]{"SLITTER-A"},setupMinutes=0m,runMinutesPerUnit=1m,crewSize=1});
        await plant.Ok(plant.Approver,"/api/engineering/items/ID-MAKE/revisions/A/release",new{reason="Independent actual configuration"});
        var order=Number(await plant.Ok(plant.Engineer,"/api/orders/",new{partNumber="ID-MAKE",quantity=2m,dueDate=DateTime.UtcNow.AddDays(20).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),priority=3}));
        await plant.Ok(planner,$"/api/orders/{order}/release",new{reason="Freeze real routing and BOM"});
        await plant.Ok(stores,"/api/inventory/issues",new{partNumber="ID-RAW",quantity=2m,locationCode="STORES",lotNumber="ROLL/1",orderNumber=order});
        var transformation=Number(await plant.Ok(stores,"/api/inventory/transformations",new{orderNumber=order,operationSequence=10,
            entries=new object[]{new{role="Consumed",partNumber="ID-RAW",lotNumber="ROLL/1",quantity=1.5m},
                new{role="Remnant",partNumber="ID-RAW",lotNumber="REMNANT/1",sourceLot="ROLL/1",quantity=.5m,locationCode="STORES"},
                new{role="Byproduct",partNumber="COIL-AL",quantity=.1m,locationCode="STORES"}},reason="Recorded material transformation with an explicit untracked byproduct"}));
        foreach(var(code,kind)in new[]{("ID-GAUGE","Gauge"),("ID-CUTTER","Cutting")})
        {
            await plant.Ok(plant.Engineer,"/api/resources/tool-types",new{code=code+"-TYPE",name=code,kind});
            await plant.Ok(plant.Engineer,"/api/resources/tools",new{code,typeCode=code+"-TYPE",description=code,calibratedOn=DateTime.UtcNow.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),calibrationDue=DateTime.UtcNow.AddDays(100).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)});
        }
        await plant.Ok(stores,"/api/inventory/containers",new{code="ID-CARTON",kind="Carton",reason="Existing packing owner"});
        await plant.Ok(stores,"/api/inventory/containers/ID-CARTON/contents",new{partNumber="ID-RAW",quantity=.1m,locationCode="STORES",lotNumber="REMNANT/1",reason="Actual recorded package contents"});
        var shipment=Number(await plant.Ok(stores,"/api/inventory/shipments",new{customer="Recorded customer",lines=new[]{new{partNumber="ID-RAW",quantity=.1m,locationCode="STORES",lotNumber="REMNANT/1"}},reason="Actual open shipment source"}));
        await plant.Person("identity-terminal","ShopFloorTerminal");const string badge="IDENTITY-SARAH";
        await plant.Ok(plant.Admin,$"/api/workforce/people/{plant.People["sarah"]}/badge",new{badge});
        var terminal=plant.As("identity-terminal");await plant.Ok(terminal,"/api/shopfloor/clock-in",new{badge});
        await plant.Ok(terminal,"/api/shopfloor/start",new{badge,orderNumber=order,sequence=10,machineCode="SLITTER-A"});
        await plant.Ok(quality,"/api/quality/plans",new{partNumber="ID-MAKE",sequence=10,title="Actual independent attribute inspection",characteristics=new[]{new{name="Visual conformity",kind="Attribute"}},revision="A"});
        var inspection=Number(await plant.Ok(quality,"/api/quality/inspections",new{orderNumber=order,sequence=10,quantity=1m,findings=new[]{new{number=1,accepted=true}},reason="Independent actual inspection source"}));
        await plant.Ok(terminal,"/api/shopfloor/stop",new{badge});
        var orderKey=order.ToString(CultureInfo.InvariantCulture);var operationKey=orderKey+":10";
        (string Kind,string Key,string Status)[] identities=[("person",person.ToString("D"),"Active"),("order",orderKey,"Released"),("operation",operationKey,"InProgress"),
            ("traveler",orderKey,"Released"),("material","ID-RAW:ROLL%2F1","Recorded"),("batch","ID-RAW:BATCH%2F1","Recorded"),
            ("roll","ID-RAW:ROLL%2F1","Recorded"),("serial","ID-SERIAL:SERIAL%2F1","Recorded"),("wip",operationKey,"InProgress"),
            ("remnant","ID-RAW:REMNANT%2F1","Recorded"),("machine","SLITTER-A","Active"),("workcenter","SLIT-WC","Active"),
            ("tool","ID-CUTTER","Available"),("gauge","ID-GAUGE","Available"),("location","STORES","Active"),("container","ID-CARTON","Open"),
            ("package","ID-CARTON","Open"),("inspection",inspection.ToString(CultureInfo.InvariantCulture),"Pass"),("shipment",shipment.ToString(CultureInfo.InvariantCulture),"Open")];
        Assert.Equal(19,identities.Length);var before=await State(app);
        using(var metadata=await plant.Engineer.GetAsync("/api/identification/config"))
        {
            Assert.Equal(HttpStatusCode.OK,metadata.StatusCode);using var configuration=JsonDocument.Parse(await metadata.Content.ReadAsStringAsync());
            Assert.Equal(new[]{"person","order","operation","traveler","material","batch","roll","serial","wip","remnant","machine","workcenter","tool","gauge","location","container","package","inspection","shipment","output","label","alias","instruction","product-release"},
                configuration.RootElement.GetProperty("supportedKinds").EnumerateArray().Select(value=>value.GetString()));
        }
        foreach(var identity in identities)
        {
            var result=await Resolve(plant.Engineer,Code(identity.Kind,identity.Key));Assert.Equal((identity.Kind,identity.Status),(result.GetProperty("kind").GetString(),result.GetProperty("status").GetString()));
            Assert.False(result.TryGetProperty("quantity",out _));Assert.DoesNotContain("HIDDEN-SUPPLIER-REF",result.GetRawText(),StringComparison.Ordinal);
        }
        var directory=await Resolve(plant.Engineer,Code("person",person.ToString("D")));Assert.Equal("Recorded Person",directory.GetProperty("displayName").GetString());
        foreach(var field in new[]{"markings","programs","permissions","idpSubject","badge","pin"})Assert.False(directory.TryGetProperty(field,out _));
        foreach(var wrong in new[]{Code("gauge","ID-CUTTER"),Code("roll","ID-RAW:BATCH%2F1"),Code("batch","ID-RAW:ROLL%2F1"),Code("serial","ID-RAW:ROLL%2F1"),Code("remnant","ID-RAW:ROLL%2F1")})await Hidden(plant.Engineer,wrong);
        var output=await Resolve(plant.Engineer,Code("output",transformation.ToString(CultureInfo.InvariantCulture)+":3"));
        Assert.Equal("UntrackedOutput",output.GetProperty("identityKind").GetString());Assert.Equal(JsonValueKind.Null,output.GetProperty("lotNumber").ValueKind);
        await Hidden(plant.Engineer,Code("output",transformation.ToString(CultureInfo.InvariantCulture)+":1")); // Input consumption is not output identity.
        Assert.Equal(before,await State(app));
        async Task<string> File(string revision)
        {
            using var upload=new HttpRequestMessage(HttpMethod.Post,"/api/engineering/document-files"){Content=new ByteArrayContent(Encoding.UTF8.GetBytes("Actual controlled instruction "+revision))};
            upload.Headers.Add("Idempotency-Key","instruction-file-"+revision);upload.Headers.Add("X-File-Name","instruction-"+revision+".txt");
            using var response=await plant.Engineer.SendAsync(upload);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            using var file=JsonDocument.Parse(await response.Content.ReadAsStringAsync());return file.RootElement.GetProperty("entityId").GetString()!;
        }
        await plant.Ok(plant.Engineer,"/api/engineering/documents",new{number="SCAN.INSTRUCTION",title="Actual controlled work instruction",type="WorkInstruction",authority="Internal"});
        var shaA=await File("A");
        foreach(var revision in new[]{"A","B"})
        {
            var sha=revision=="A"?shaA:await File("B");
            await plant.Ok(plant.Engineer,"/api/engineering/documents/SCAN.INSTRUCTION/revisions",new{revision,fileSha256=sha,fileName="instruction.txt"});
            await plant.Ok(plant.Engineer,$"/api/engineering/documents/SCAN.INSTRUCTION/revisions/{revision}/submit",new{});
            await plant.Ok(plant.Approver,$"/api/engineering/documents/SCAN.INSTRUCTION/revisions/{revision}/approve",new{});
            await plant.Ok(plant.Approver,$"/api/engineering/documents/SCAN.INSTRUCTION/revisions/{revision}/release",new{reason="Independent exact current instruction publication"});
            before=await State(app);var active=await Resolve(plant.Engineer,Code("instruction","SCAN.INSTRUCTION:"+revision));
            Assert.Equal((revision,"Released"),(active.GetProperty("revision").GetString(),active.GetProperty("status").GetString()));
            Assert.False(string.IsNullOrEmpty(active.GetProperty("sourceFingerprint").GetString()));
            if(revision=="B")await Hidden(plant.Engineer,Code("instruction","SCAN.INSTRUCTION:A"));
            Assert.Equal(before,await State(app));
        }
        // Superseded bytes remain a readable historical document source; active scan authority cannot silently substitute them.
        using(var historical=await plant.Engineer.GetAsync("/api/engineering/documents/SCAN.INSTRUCTION/revisions/A/file"))
        {Assert.Equal(HttpStatusCode.OK,historical.StatusCode);Assert.Equal("Actual controlled instruction A",await historical.Content.ReadAsStringAsync());}
        await plant.Ok(plant.Admin,"/api/kernel/roles",new{role="ScopedMachineIdentity",displayName="Actual work-center resource read",permissions=new[]{Permissions.ResourcesRead},reason="Source-specific scoped identification"});
        await plant.Ok(plant.Admin,"/api/kernel/roles/ScopedMachineIdentity/permission-scopes",new{permission=Permissions.ResourcesRead,scopeType="work_center",scopeId="SLIT-WC",reason="Exact owning work center"});
        await plant.Person("identity-scoped","ScopedMachineIdentity");var scoped=plant.As("identity-scoped");
        before=await State(app);Assert.Equal("SLITTER-A",(await Resolve(scoped,Code("machine","SLITTER-A"))).GetProperty("subjectKey").GetString());
        await Hidden(scoped,Code("machine","PAINT-BOOTH"));await Hidden(scoped,Code("person",person.ToString("D")));Assert.Equal(before,await State(app));
        await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["identity-scoped"]}/roles/ScopedMachineIdentity/revoke",new{reason="Withdraw actual source read"});
        before=await State(app);await Hidden(scoped,Code("machine","SLITTER-A"));Assert.Equal(before,await State(app));
    }
}
