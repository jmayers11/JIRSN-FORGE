using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Host.Identification;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using ZXing;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V41LabelLifecycleTests(PostgresFixture postgres)
{
    private const string Namespace="LABEL.EXAMPLE";
    private static string Subject(long order,string kind="order")=>$"forge:v1:{Namespace}:{kind}:{order.ToString(CultureInfo.InvariantCulture)}";
    private static Guid Id(JsonElement value)=>Guid.Parse(value.GetProperty("entityId").GetString()!);
    private static async Task<JsonElement> Get(HttpClient client,string path)
    {using var response=await client.GetAsync(path);var text=await response.Content.ReadAsStringAsync();Assert.True(response.IsSuccessStatusCode,$"{path}: {text}");using var json=JsonDocument.Parse(text);return json.RootElement.Clone();}
    private static async Task Reject(SlittingPlant.Plant plant,HttpClient client,string path,object body,string code)
    {using var response=await plant.Try(client,path,body);Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);Assert.Contains(code,await response.Content.ReadAsStringAsync(),StringComparison.Ordinal);}
    private static async Task Concealed(HttpClient client,string code)
    {using var response=await client.PostAsJsonAsync("/api/identification/resolve",new{code});Assert.Equal(HttpStatusCode.NotFound,response.StatusCode);var text=await response.Content.ReadAsStringAsync();Assert.Contains("identification_not_found",text,StringComparison.Ordinal);Assert.DoesNotContain(code,text,StringComparison.Ordinal);}
    private static async Task<string[]> Rows(ForgeApp app,bool presentation=false,bool full=false)
    {
        string[] source=["inventory.lot","inventory.balance","inventory.transaction","inventory.instance_link","inventory.transformation","inventory.transformation_line","execution.production_order","execution.order_operation","execution.order_material"];
        string[] labels=["kernel.identification_presentation","kernel.identification_presentation_version","engineering.document_file"];
        string[] delivery=["kernel.audit_log","kernel.event_log","kernel.outbox","kernel.idempotency"];
        var tables=full?source.Concat(labels).Concat(delivery):presentation?labels:source;
        var result=new List<string>();await using var connection=new NpgsqlConnection(app.ConnectionString);await connection.OpenAsync();
        foreach(var table in tables)
        {await using var command=new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(label_snapshot_row) ORDER BY to_jsonb(label_snapshot_row)::text)::text,'[]') FROM {table} label_snapshot_row",connection);result.Add(table+":"+(string)(await command.ExecuteScalarAsync())!);}
        return result.ToArray();
    }

    private static async Task<(SlittingPlant.Plant Plant,long Order,Guid Program)> Setup(ForgeApp app,bool independent=true)
    {
        var(plant,planner)=await SlittingPlant.BuildEngineeredAsync(app);
        string[] permissions=[Permissions.IdentificationRead,Permissions.IdentificationLabelsManage,Permissions.IdentificationLabelsApprove,
            Permissions.IdentificationLabelsPrint,Permissions.IdentificationAliasesManage,Permissions.OrdersRead,Permissions.InventoryRead,
            Permissions.ResourcesRead,Permissions.EngineeringRead,Permissions.QualityRead,Permissions.QmsRead,Permissions.DocumentsManage];
        await plant.Ok(plant.Admin,"/api/kernel/roles",new{role="LabelFixtureAuthority",displayName="Explicit acceptance presentation authority",permissions,reason="Separate current source permissions for the label acceptance"});
        foreach(var person in new[]{plant.People["eng"],plant.People["approver"]})
            await plant.Ok(plant.Admin,$"/api/kernel/people/{person}/roles",new{role="LabelFixtureAuthority",reason="Explicit independent label authority"});
        await plant.Person("label-printer","LabelFixtureAuthority","QmsPolicyConsumer");
        await plant.Person("label-programs","ProgramManager");
        var program=Id(await plant.Ok(plant.As("label-programs"),"/api/programs/",new{code="LABEL-PRIVATE",name="Actual private presentation source"}));
        foreach(var person in new[]{plant.People["eng"],plant.People["approver"],plant.People["planner"],plant.People["label-printer"]})
            await plant.Ok(plant.Admin,$"/api/kernel/people/{person}/programs",new{programId=program,reason="Actual source membership"});
        var order=long.Parse((await plant.Ok(plant.Engineer,"/api/orders/",new{partNumber="ROLL-100",quantity=4m,dueDate=DateTime.UtcNow.AddDays(15).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),priority=3,programId=program})).GetProperty("entityId").GetString()!,CultureInfo.InvariantCulture);
        await plant.Ok(planner,$"/api/orders/{order}/release",new{reason="Real released primitive source"});
        var life=new QmsLabelLifecycleRules(QmsActiveCopyMode.SingleActive,1,true,true,true,true,independent,
            QmsLabelConfirmation.OperatorObservedPrintingAndAttachment,["order","traveler","operation","wip"],[new("ORDER-LABEL",1)],
            [QmsLabelSymbology.Code128,QmsLabelSymbology.QR,QmsLabelSymbology.DataMatrix],true,true,true);
        await QmsPolicyScenario.AdoptAsync(plant,"LABEL-POLICY","LABEL-ADOPTION","LABEL.PROCEDURE",
            new(QmsPolicyKind.LabelCopy,"One active logical label copy, explicit independent approval and observed attachment",plant.People["eng"],"Recorded local briefing","On source change",true,
                new(LabelCopy:new(true,1,true,life))),QmsPolicyPurpose.LabelCopy,new(),program);
        await plant.Ok(plant.Engineer,"/api/identification/templates",new{code="ORDER-LABEL",facilityCode="PLANT1",width=1200,height=320,
            allowedKinds=new[]{"order","traveler","operation","wip"},allowedSymbologies=new[]{"Code128","QR","DataMatrix"},documents=new[]{new{documentNumber="LABEL.PROCEDURE",revision="A"}},reason="Exact governed controlled layout"});
        await Reject(plant,plant.Engineer,"/api/identification/templates/ORDER-LABEL/revisions/1/approve",new{expectedVersion=1,reason="Self approval prohibited"},"separation_of_duties");
        await plant.Ok(plant.Approver,"/api/identification/templates/ORDER-LABEL/revisions/1/approve",new{expectedVersion=1,reason="Independent exact template approval"});
        return(plant,order,program);
    }

    private static async Task<(Guid Id,string Code)> Copy(SlittingPlant.Plant plant,HttpClient client,Guid issue)
    {
        var issuance=await Get(client,$"/api/identification/labels/issuances/{issue}");var copies=issuance.GetProperty("copies").EnumerateArray().ToArray();Assert.Single(copies);
        return(copies[0].GetProperty("labelId").GetGuid(),copies[0].GetProperty("code").GetString()!);
    }

    [Fact]
    public async Task Real_copy_print_reconciliation_replacement_and_alias_history_preserve_physical_source_and_current_replay_authority()
    {
        await using var app=new ForgeApp(await postgres.CreateDatabaseAsync(),configureBuilder:b=>{b.UseSetting(CanonicalIdentification.NamespaceConfigurationKey,Namespace);b.UseSetting(QmsService.ConfigurationKey,"EXAMPLE-ORG");});
        var(plant,order,program)=await Setup(app);var printer=plant.As("label-printer");var physical=await Rows(app);
        var issueBody=new{subjectCode=Subject(order),templateCode="ORDER-LABEL",templateVersion=1,symbology="QR",copies=1,facilityCode="PLANT1",reason="Original logical copy"};
        const string issueKey="private-label-original";using var issued=await printer.CommandAsync("/api/identification/labels",issueBody,issueKey);
        Assert.Equal(HttpStatusCode.OK,issued.StatusCode);var accepted=await issued.Content.ReadAsStringAsync();using var json=JsonDocument.Parse(accepted);var issue=Id(json.RootElement);
        var copy=await Copy(plant,printer,issue);await Concealed(printer,copy.Code);
        await Reject(plant,printer,$"/api/identification/labels/{copy.Id}/approve",new{expectedVersion=1,reason="Issuer still cannot approve independent policy"},"separation_of_duties");
        await plant.Ok(plant.Approver,$"/api/identification/labels/{copy.Id}/approve",new{expectedVersion=1,reason="Independent source-bound copy approval"});
        await Concealed(printer,copy.Code); // Active metadata is not evidence of a printed/attached label.
        var attempt=Id(await plant.Ok(printer,$"/api/identification/labels/{copy.Id}/print-attempts",new{expectedVersion=2,printerCode="DECLARED-PRINTER",reason="Browser-observed request, no hardware acknowledgement"}));
        var detail=await Get(printer,$"/api/identification/labels/{copy.Id}");var request=detail.GetProperty("printAttempts")[0];
        Assert.Equal(("Requested",false,false),(request.GetProperty("state").GetString(),request.GetProperty("observedPrinted").GetBoolean(),request.GetProperty("observedAttached").GetBoolean()));
        using var artifact=await printer.GetAsync($"/api/identification/print-attempts/{attempt}/artifact");Assert.Equal(HttpStatusCode.OK,artifact.StatusCode);
        var bytes=await artifact.Content.ReadAsByteArrayAsync();Assert.Equal(copy.Code,LabelRenderingTests.Decode(bytes,BarcodeFormat.QR_CODE).Text);
        Assert.Equal(request.GetProperty("artifactSha256").GetString(),Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        await plant.Ok(printer,$"/api/identification/print-attempts/{attempt}/outcome",new{expectedVersion=1,outcome="Uncertain",observation="Printer jam; inspect physical output before deciding",observedPrinted=false,observedAttached=false,reason="Record actual uncertainty"});
        await Concealed(printer,copy.Code);
        await Reject(plant,printer,$"/api/identification/labels/{copy.Id}/print-attempts",new{expectedVersion=2,reason="A pending physical attempt cannot be bypassed with another request"},"print_reconciliation_required");
        await Reject(plant,printer,$"/api/identification/print-attempts/{attempt}/outcome",new{expectedVersion=2,outcome="SpoolAccepted",observation="Dialog acknowledgement",observedPrinted=false,observedAttached=false,reason="Untrusted client cannot report provider acknowledgement"},"print_observation");
        await plant.Ok(printer,$"/api/identification/print-attempts/{attempt}/outcome",new{expectedVersion=2,outcome="Confirmed",observation="Observed exactly one correct physical print",observedPrinted=true,observedAttached=false,reason="Printing is separate from attachment"});
        await Concealed(printer,copy.Code);
        await plant.Ok(printer,$"/api/identification/print-attempts/{attempt}/outcome",new{expectedVersion=3,outcome="Confirmed",observation="Observed attachment to the actual recorded order traveler",observedPrinted=true,observedAttached=true,reason="Append physical attachment observation"});
        var beforeRead=await Rows(app,full:true);
        using(var resolved=await printer.PostAsJsonAsync("/api/identification/resolve",new{code=copy.Code}))
        {Assert.Equal(HttpStatusCode.OK,resolved.StatusCode);using var result=JsonDocument.Parse(await resolved.Content.ReadAsStringAsync());Assert.Equal(("order",order,"Released"),(result.RootElement.GetProperty("kind").GetString(),result.RootElement.GetProperty("orderNumber").GetInt64(),result.RootElement.GetProperty("status").GetString()));}
        using(var retained=await printer.GetAsync($"/api/identification/print-attempts/{attempt}/artifact"))Assert.Equal(bytes,await retained.Content.ReadAsByteArrayAsync());
        Assert.Equal(beforeRead,await Rows(app,full:true));Assert.Equal(physical,await Rows(app));
        await Reject(plant,printer,$"/api/identification/labels/{copy.Id}/print-attempts",new{expectedVersion=2,reason="Confirmed printing requires the governed reprint path"},"label_reprint_required");
        var state=await Rows(app,presentation:true);
        await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["label-printer"]}/programs/{program}/revoke",new{reason="Current source withdrawal after saved success"});
        using(var denied=await printer.CommandAsync("/api/identification/labels",issueBody,issueKey))Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        await Concealed(printer,copy.Code);
        foreach(var path in new[]{$"/api/identification/labels/{copy.Id}",$"/api/identification/print-attempts/{attempt}/artifact"})
        {using var denied=await printer.GetAsync(path);Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode);Assert.DoesNotContain("ROLL-100",await denied.Content.ReadAsStringAsync(),StringComparison.Ordinal);}
        Assert.Equal(state,await Rows(app,presentation:true));Assert.Equal(physical,await Rows(app));
        await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["label-printer"]}/programs",new{programId=program,reason="Restore actual current authority"});
        using(var replay=await printer.CommandAsync("/api/identification/labels",issueBody,issueKey)){Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.Equal(accepted,await replay.Content.ReadAsStringAsync());}
        Assert.Equal(state,await Rows(app,presentation:true));
        // A traveler is another presentation of this same physical order, so its approval cannot bypass SingleActive.
        var other=Id(await plant.Ok(printer,"/api/identification/labels",issueBody with { subjectCode=Subject(order,"traveler") }));
        var otherCopy=await Copy(plant,printer,other);
        await Reject(plant,plant.Approver,$"/api/identification/labels/{otherCopy.Id}/approve",new{expectedVersion=1,reason="Same-source second copy"},"label_active_copy_limit");
        var replacement=Id(await plant.Ok(printer,$"/api/identification/labels/{copy.Id}/replace",new{expectedVersion=2,reason="Physical replacement with retained original identity/history"}));
        var replacementCopy=await Copy(plant,printer,replacement);
        await plant.Ok(plant.Approver,$"/api/identification/labels/{replacementCopy.Id}/approve",new{expectedVersion=1,reason="Independent replacement approval"});
        Assert.Equal("Replaced",(await Get(printer,$"/api/identification/labels/{copy.Id}")).GetProperty("label").GetProperty("state").GetString());
        await Concealed(printer,copy.Code);
        var alias=Id(await plant.Ok(printer,"/api/identification/aliases",new{domain="customer.example",alias="CUSTOMER-EXACT/REFERENCE",subjectCode=Subject(order,"traveler"),facilityCode="PLANT1",reason="Explicit direct primitive mapping"}));
        var aliasRecord=(await Get(printer,$"/api/identification/aliases/{alias}")).GetProperty("record");
        Assert.Equal(Subject(order),aliasRecord.GetProperty("subjectCode").GetString());
        var beforeAliasRead=await Rows(app,full:true);
        using(var resolved=await printer.PostAsJsonAsync("/api/identification/aliases/resolve",new{domain="customer.example",alias="CUSTOMER-EXACT/REFERENCE"}))
        {
            Assert.Equal(HttpStatusCode.OK,resolved.StatusCode);
            using var aliasJson=JsonDocument.Parse(await resolved.Content.ReadAsStringAsync());
            Assert.Equal(order,aliasJson.RootElement.GetProperty("orderNumber").GetInt64());
            Assert.Equal(aliasRecord.GetProperty("code").GetString(),aliasJson.RootElement.GetProperty("code").GetString());
        }
        using(var otherDomain=await printer.PostAsJsonAsync("/api/identification/aliases/resolve",new{domain="other.example",alias="CUSTOMER-EXACT/REFERENCE"}))
        {Assert.Equal(HttpStatusCode.NotFound,otherDomain.StatusCode);Assert.DoesNotContain("CUSTOMER-EXACT/REFERENCE",await otherDomain.Content.ReadAsStringAsync(),StringComparison.Ordinal);}
        Assert.Equal(beforeAliasRead,await Rows(app,full:true));
        await Reject(plant,printer,"/api/identification/aliases",new{domain="customer.example",alias="CUSTOMER-EXACT/REFERENCE",subjectCode=Subject(order),facilityCode="PLANT1",reason="Duplicate configured mapping"},"alias_already_assigned");
        await Reject(plant,printer,"/api/identification/aliases",new{domain="customer.example",alias="CHAIN",subjectCode=aliasRecord.GetProperty("code").GetString(),facilityCode="PLANT1",reason="Mappings cannot target presentations"},"primitive_subject_required");
        var replacedAlias=Id(await plant.Ok(printer,$"/api/identification/aliases/{alias}/replace",new{expectedVersion=1,subjectCode=Subject(order),facilityCode="PLANT1",reason="Explicit superseding direct mapping"}));
        Assert.Equal(alias,(await Get(printer,$"/api/identification/aliases/{replacedAlias}")).GetProperty("record").GetProperty("previousAliasId").GetGuid());
        await Concealed(printer,aliasRecord.GetProperty("code").GetString()!);
        await plant.Ok(printer,$"/api/identification/aliases/{replacedAlias}/void",new{expectedVersion=1,reason="Retire only the mapping, not the production order"});
        using(var voided=await printer.PostAsJsonAsync("/api/identification/aliases/resolve",new{domain="customer.example",alias="CUSTOMER-EXACT/REFERENCE"}))Assert.Equal(HttpStatusCode.NotFound,voided.StatusCode);
        Assert.Equal(physical,await Rows(app));
        await using var connection=new NpgsqlConnection(app.ConnectionString);await connection.OpenAsync();
        await using var audit=new NpgsqlCommand("SELECT count(*) FROM kernel.audit_log WHERE outcome='Denied' AND action='IssueLabels' AND program_id=$1",connection);audit.Parameters.AddWithValue(program);
        Assert.Equal(1L,(long)(await audit.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Exact_policy_withdrawal_is_checked_before_saved_success_and_generated_hash_use_remains_source_bound()
    {
        await using var app=new ForgeApp(await postgres.CreateDatabaseAsync(),configureBuilder:b=>{b.UseSetting(CanonicalIdentification.NamespaceConfigurationKey,Namespace);b.UseSetting(QmsService.ConfigurationKey,"EXAMPLE-ORG");});
        var(plant,order,program)=await Setup(app);var printer=plant.As("label-printer");
        var issueBody=new{subjectCode=Subject(order),templateCode="ORDER-LABEL",templateVersion=1,symbology="Code128",copies=1,facilityCode="PLANT1",reason="Exact source/configuration"};
        const string key="withdrawn-current-policy";using var issued=await printer.CommandAsync("/api/identification/labels",issueBody,key);Assert.Equal(HttpStatusCode.OK,issued.StatusCode);
        using var result=JsonDocument.Parse(await issued.Content.ReadAsStringAsync());var copy=await Copy(plant,printer,Id(result.RootElement));
        await plant.Ok(plant.Approver,$"/api/identification/labels/{copy.Id}/approve",new{expectedVersion=1,reason="Independent copy authorization"});
        var attempt=Id(await plant.Ok(printer,$"/api/identification/labels/{copy.Id}/print-attempts",new{expectedVersion=2,reason="Retain exact artifact"}));
        var detail=await Get(printer,$"/api/identification/labels/{copy.Id}");var sha=detail.GetProperty("printAttempts")[0].GetProperty("artifactSha256").GetString();
        await plant.Ok(printer,"/api/engineering/documents",new{number="PUBLIC-LABEL-REFERENCE",title="Reference for guarded shared hash",type="Other",authority="Reference"});
        await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["label-printer"]}/programs/{program}/revoke",new{reason="Creator loses current private source authority"});
        using(var attach=await plant.Try(printer,"/api/engineering/documents/PUBLIC-LABEL-REFERENCE/revisions",new{revision="A",fileSha256=sha,fileName="generated.svg",reason="Known hash cannot bypass source withdrawal"}))
            Assert.Equal(HttpStatusCode.Forbidden,attach.StatusCode);
        await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["label-printer"]}/programs",new{programId=program,reason="Restore actual source membership"});
        var before=await Rows(app,presentation:true);
        await plant.Ok(plant.Approver,"/api/quality/qms/adoptions/LABEL-ADOPTION/revisions/1/withdraw",new{expectedVersion=5,note="Operational copy control withdrawn pending reviewed successor",reason="Explicit current policy withdrawal"});
        using(var replay=await printer.CommandAsync("/api/identification/labels",issueBody,key)){Assert.Equal(HttpStatusCode.UnprocessableEntity,replay.StatusCode);Assert.Contains("governed_policy_unavailable",await replay.Content.ReadAsStringAsync(),StringComparison.Ordinal);}
        Assert.Equal(before,await Rows(app,presentation:true));
        // Historical bytes are still readable to authorized current readers; this does not establish current use authority.
        using var artifact=await printer.GetAsync($"/api/identification/print-attempts/{attempt}/artifact");Assert.Equal(HttpStatusCode.OK,artifact.StatusCode);
        Assert.Equal(copy.Code,LabelRenderingTests.Decode(await artifact.Content.ReadAsByteArrayAsync(),BarcodeFormat.CODE_128).Text);
        await Concealed(printer,copy.Code);
    }

    [Fact]
    public async Task Organization_can_allow_issuer_copy_authorization_while_template_approval_remains_independent_and_withdrawal_blocks_saved_issue()
    {
        await using var app=new ForgeApp(await postgres.CreateDatabaseAsync(),configureBuilder:b=>{b.UseSetting(CanonicalIdentification.NamespaceConfigurationKey,Namespace);b.UseSetting(QmsService.ConfigurationKey,"EXAMPLE-ORG");});
        var(plant,order,program)=await Setup(app,independent:false);var printer=plant.As("label-printer");
        await plant.Ok(plant.Admin,"/api/kernel/roles",new{role="ScopedLabelFacility",displayName="Only recorded PLANT1 presentation actions",permissions=new[]{Permissions.IdentificationLabelsManage,Permissions.IdentificationLabelsApprove,Permissions.IdentificationLabelsPrint},reason="Actual own presentation facility scope"});
        foreach(var permission in new[]{Permissions.IdentificationLabelsManage,Permissions.IdentificationLabelsApprove,Permissions.IdentificationLabelsPrint})
            await plant.Ok(plant.Admin,"/api/kernel/roles/ScopedLabelFacility/permission-scopes",new{permission,scopeType="facility",scopeId="PLANT1",reason="Only actual presentation facility"});
        await plant.Ok(plant.Admin,"/api/kernel/roles",new{role="ScopedLabelSourceRead",displayName="Independent actual source reads",permissions=new[]{Permissions.IdentificationRead,Permissions.OrdersRead,Permissions.EngineeringRead,Permissions.QmsRead},reason="Source permissions remain distinct from label action authority"});
        await plant.Person("label-scoped","ScopedLabelFacility","ScopedLabelSourceRead");
        await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["label-scoped"]}/programs",new{programId=program,reason="Actual source membership for scoped presentation actor"});
        printer=plant.As("label-scoped");
        await plant.Ok(plant.Engineer,"/api/resources/sites",new{code="PLANT2",name="Another real facility"});
        using(var outside=await plant.Try(printer,"/api/identification/labels",new{subjectCode=Subject(order),templateCode="ORDER-LABEL",templateVersion=1,symbology="DataMatrix",copies=1,facilityCode="PLANT2",reason="An actual source cannot override own action facility scope"}))Assert.Equal(HttpStatusCode.Forbidden,outside.StatusCode);
        var body=new{subjectCode=Subject(order),templateCode="ORDER-LABEL",templateVersion=1,symbology="DataMatrix",copies=1,facilityCode="PLANT1",reason="Explicit organization's same-issuer authorization policy"};
        const string key="exact-withdrawn-template";using var issued=await printer.CommandAsync("/api/identification/labels",body,key);Assert.Equal(HttpStatusCode.OK,issued.StatusCode);
        using var result=JsonDocument.Parse(await issued.Content.ReadAsStringAsync());var copy=await Copy(plant,printer,Id(result.RootElement));
        var detail=await Get(printer,$"/api/identification/labels/{copy.Id}");Assert.Contains("Approve",detail.GetProperty("allowedActions").EnumerateArray().Select(value=>value.GetString()));
        Assert.False(detail.GetProperty("policy").GetProperty("requireIndependentCopyAuthorization").GetBoolean());
        await plant.Ok(printer,$"/api/identification/labels/{copy.Id}/approve",new{expectedVersion=1,reason="Same issuer is permitted by the exact current organization policy"});
        await plant.Ok(plant.Engineer,"/api/identification/templates/ORDER-LABEL/revisions/1/withdraw",new{expectedVersion=2,reason="Withdraw this exact controlled presentation layout"});
        var before=await Rows(app,presentation:true);
        using(var replay=await printer.CommandAsync("/api/identification/labels",body,key))
        {Assert.Equal(HttpStatusCode.UnprocessableEntity,replay.StatusCode);Assert.Contains("label_configuration",await replay.Content.ReadAsStringAsync(),StringComparison.Ordinal);}
        detail=await Get(printer,$"/api/identification/labels/{copy.Id}");Assert.DoesNotContain("RequestPrint",detail.GetProperty("allowedActions").EnumerateArray().Select(value=>value.GetString()));
        Assert.Contains("Void",detail.GetProperty("allowedActions").EnumerateArray().Select(value=>value.GetString()));
        Assert.Equal(before,await Rows(app,presentation:true));
    }
}
