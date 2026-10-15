using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Npgsql;
using Microsoft.AspNetCore.Hosting;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ProductAcceptanceTests(PostgresFixture postgres)
{
 private const string Api="/api/quality/acceptance";
 private static long Id(JsonElement result)=>long.Parse(result.GetProperty("entityId").GetString()!,CultureInfo.InvariantCulture);
 private static async Task<JsonElement> Json(HttpResponseMessage response)
 {var text=await response.Content.ReadAsStringAsync();Assert.True(response.IsSuccessStatusCode,$"{response.StatusCode}: {text}");using var document=JsonDocument.Parse(text);return document.RootElement.Clone();}
 private static async Task Rejected(SlittingPlant.Plant plant,HttpClient actor,string path,object body,string code,HttpStatusCode status=HttpStatusCode.UnprocessableEntity)
 {using var response=await plant.Try(actor,path,body);Assert.Equal(status,response.StatusCode);Assert.Contains(code,await response.Content.ReadAsStringAsync(),StringComparison.Ordinal);}
 private static async Task<string> Rows(ForgeApp app,string table)
 {await using var connection=new NpgsqlConnection(app.ConnectionString);await connection.OpenAsync();await using var command=new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(acceptance_snapshot_row) ORDER BY to_jsonb(acceptance_snapshot_row)::text)::text,'[]') FROM {table} acceptance_snapshot_row",connection);return(string)(await command.ExecuteScalarAsync())!;}
 private static async Task<string[]> WaitForToolWritersAsync(NpgsqlConnection connection,int expected,params Task<HttpResponseMessage>[] writers)
 {
  var deadline=DateTimeOffset.UtcNow.AddSeconds(5);
  var waiting=Array.Empty<string>();
  await using var backendCommand=new NpgsqlCommand("SELECT pg_backend_pid()",connection);
  var blocker=(int)(await backendCommand.ExecuteScalarAsync())!;
  while(DateTimeOffset.UtcNow<deadline)
  {
   if(writers.Any(task=>task.IsCompleted))break;
   await using(var query=new NpgsqlCommand("SELECT format('pid=%s app=%s state=%s blocker=%s %s/%s query=%s',pid,application_name,state,pg_blocking_pids(pid),wait_event_type,wait_event,left(query,240)) FROM pg_stat_activity WHERE datname=current_database() AND application_name='ForgeAcceptanceGaugeRace' AND pid<>pg_backend_pid() AND $1=ANY(pg_blocking_pids(pid)) ORDER BY pid",connection))
   {query.Parameters.AddWithValue(blocker);await using var reader=await query.ExecuteReaderAsync();var current=new List<string>();while(await reader.ReadAsync())current.Add(reader.GetString(0));waiting=current.ToArray();}
   if(waiting.Count(value=>value.Contains("resources.tool",StringComparison.OrdinalIgnoreCase))>=expected)return waiting;
   await Task.Delay(25);
  }
  return waiting;
 }
 internal static async Task<(SlittingPlant.Plant Plant,long Order,Guid? Program)> Setup(ForgeApp app,AcceptanceCoverageKind coverage=AcceptanceCoverageKind.FullQuantity,bool measured=false,bool privateProgram=false,bool serialized=false,int outputQuantity=1000,bool designApplicability=false)
 {
  var(plant,planner)=await SlittingPlant.BuildEngineeredAsync(app);
  await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["eng"]}/roles",new{role=Roles.QualityEngineer,reason="Explicit acceptance arrangement author"});
  await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["approver"]}/roles",new{role=Roles.QualityManager,reason="Independent criteria and sampling authority"});
  await plant.Person("acceptance-inspector",Roles.Inspector,"QmsPolicyConsumer");
  await plant.Ok(plant.Admin,"/api/kernel/roles",new{role="ExampleProductRelease",displayName="Explicit example organization product release",permissions=new[]{Permissions.QualityProductRelease,Permissions.QualityRead,Permissions.InventoryRead,Permissions.OrdersRead,Permissions.EngineeringRead,Permissions.ResourcesRead,Permissions.HoldsRead},reason="Human assignment of distinct Quality product-release authority; shipping authority is separate"});
  await plant.Person("release-signer","ExampleProductRelease","QmsPolicyConsumer");await plant.Person("shipper",Roles.Shipper,"QmsPolicyConsumer");
  await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["stores"]}/roles",new{role="QmsPolicyConsumer",reason="Explicit human adopted-policy/source read access"});
  Guid? program=null;
  if(privateProgram)
  {
   await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["eng"]}/roles",new{role=Roles.ProgramManager,reason="Create the actual private manufacturing source program"});
   program=Guid.Parse((await plant.Ok(plant.Engineer,"/api/programs/",new{code="ACCEPT-PRIVATE",name="Actual private acceptance source"})).GetProperty("entityId").GetString()!);
   foreach(var actor in new[]{"eng","approver","planner","stores","dana","acceptance-inspector","release-signer","shipper"})await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People[actor]}/programs",new{programId=program,reason="Explicit membership for the actual private source"});
  }
  await plant.Ok(plant.Engineer,"/api/engineering/items",new{partNumber="ACCEPT-PRODUCT",description="Identified acceptance output",unitOfMeasure="EA",makeBuy="Make",leadTimeDays=1,lotTracked=true,serialized=serialized});
  await plant.Ok(plant.Engineer,"/api/engineering/items/ACCEPT-PRODUCT/revisions",new{revision="A"});
  await plant.Ok(plant.Engineer,"/api/engineering/items/ACCEPT-PRODUCT/revisions/A/routing",new{sequence=10,description="Assemble actual output",workCenterCode="ASSY-WC",activityCode="ASSEMBLE",setupMinutes=0,runMinutesPerUnit=1,crewSize=1});
  await plant.Ok(plant.Approver,"/api/engineering/items/ACCEPT-PRODUCT/revisions/A/release",new{reason="Actual approved build configuration"});
  var policy=new QmsPolicyContent(QmsPolicyKind.ProductRelease,"Produced, accepted and independently released are separate recorded states",plant.People["eng"],"Recorded training briefing","On criteria/source change",true,new(ProductRelease:new(true,true,true,Permissions.QualityProductRelease)));
  await QmsPolicyScenario.AdoptAsync(plant,"PRODUCT-RELEASE","RELEASE-PRODUCT","RELEASE.CONTROLS",policy,QmsPolicyPurpose.ProductRelease,new(PartNumber:"ACCEPT-PRODUCT",PartRevision:"A"),programId:program);
  await plant.Ok(plant.As("stores"),"/api/inventory/locations",new{code="QUARANTINE",siteCode="PLANT1",name="Identified rejected child quarantine",kind="Quarantine",reason="Actual segregated rejected scope"});
  var criteria=new AcceptanceCriteriaContent("ACCEPT-PRODUCT","A",10,"Actual frozen acceptance arrangement",new(coverage,1,1000,coverage==AcceptanceCoverageKind.FullQuantity?"Each identified unit":"Human-approved represented cohort sample","Independent Quality determination of method and represented quantity"),
   [measured?new(1,"Width",CharacteristicKind.Variable,10,9,11,"mm"):new(1,"Visual condition",CharacteristicKind.Attribute,null,null,null,null)],[new("RELEASE.CONTROLS","A")]);
  await plant.Ok(plant.Engineer,Api+"/criteria",new{code="OUTPUT-ACCEPTANCE",facilityCode="PLANT1",programId=program,markings="None",content=criteria,reason="Human acceptance arrangement proposal"});
  var path=Api+"/criteria/OUTPUT-ACCEPTANCE/revisions/1";
  object Decision(int version)=>new{expectedVersion=version,note="Reviewed actual exact configuration, criteria, source and coverage basis",reason="Independent criteria decision"};
  await plant.Ok(plant.Engineer,path+"/submit",Decision(1));
  await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["eng"]}/roles",new{role=Roles.QualityManager,reason="Role possession cannot override criterion author exclusion"});
  await Rejected(plant,plant.Engineer,path+"/approve",Decision(2),"separation_of_duties");
  await plant.Ok(plant.Approver,path+"/approve",Decision(2));await plant.Ok(plant.Approver,path+"/effective",Decision(3));
  await Rejected(plant,plant.Engineer,path+"/edit",new{expectedVersion=4,content=criteria with{Title="Altered approved arrangement"},reason="Submitted content is immutable"},"immutable_revision");
  if(designApplicability)
  {
   var designEvidence=new[]{new CommercialReference(CommercialReferenceKind.DocumentRevision,"RELEASE.CONTROLS",Revision:"A")};
   var controls=Enum.GetValues<DesignControlStage>().Select(stage=>new DesignControlEvidence(stage,"Actual controlled design record for the released configuration",designEvidence)).ToArray();
   var content=new DesignApplicabilityContent("ACCEPT-PRODUCT","A",true,"This organization performs design for the exact released product revision.",controls,designEvidence);
   var designPath="/api/quality/commercial/records/DesignApplicability/ACCEPT-PRODUCT-DESIGN/revisions/1";
   await plant.Ok(plant.Engineer,"/api/quality/commercial/records",new{kind="DesignApplicability",code="ACCEPT-PRODUCT-DESIGN",facilityCode="PLANT1",content=new{designApplicability=content},reason="Record actual organization design applicability and all controlled stages"});
   await plant.Ok(plant.Engineer,designPath+"/submit",new{expectedVersion=1,basis="Submit exact design applicability evidence",reason="Submit design applicability"});
   await plant.Ok(plant.Approver,designPath+"/review",new{expectedVersion=2,basis="Review actual seven-stage design evidence",reason="Independently review design controls"});
   await plant.Ok(plant.Approver,designPath+"/approve",new{expectedVersion=3,basis="Approve actual design applicability and controlled evidence",reason="Approve design applicability"});
   await plant.Ok(plant.Approver,designPath+"/effective",new{expectedVersion=4,basis="Make the reviewed design determination effective",reason="Publish design applicability"});
  }
  var order=Id(await plant.Ok(plant.Engineer,"/api/orders/",new{partNumber="ACCEPT-PRODUCT",programId=program,quantity=outputQuantity,dueDate=DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),priority=1,reason="Actual production source for exact acceptance"}));
  await plant.Ok(planner,$"/api/orders/{order}/release",new{reason="Produce exact identified quantity"});
  await plant.Ok(plant.Admin,$"/api/workforce/people/{plant.People["dana"]}/badge",new{badge="ACCEPT-DANA"});
  await plant.Person("acceptance-terminal",Roles.ShopFloorTerminal);
  if(program is{} actualProgram)await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["acceptance-terminal"]}/programs",new{programId=actualProgram,reason="Current terminal source access; worker eligibility remains separate"});
  await plant.Ok(plant.As("acceptance-terminal"),"/api/shopfloor/clock-in",new{badge="ACCEPT-DANA"});
  await plant.Ok(plant.As("acceptance-terminal"),"/api/shopfloor/start",new{badge="ACCEPT-DANA",orderNumber=order,sequence=10});
  await plant.Ok(plant.As("acceptance-terminal"),"/api/shopfloor/report",new{badge="ACCEPT-DANA",orderNumber=order,sequence=10,good=outputQuantity,scrap=0,complete=true});
  await plant.Ok(plant.As("stores"),"/api/inventory/production-receipts",new{orderNumber=order,quantity=outputQuantity,locationCode="STORES",lotNumber=serialized?null:"OUTPUT-1000",serials=serialized?new[]{"SERIAL-OUTPUT-1"}:null,reason="Produced quantity arrives in Inspection, before acceptance or release"});
  return(plant,order,program);
 }
 internal static object Inspect(AcceptanceSubjectKey subject,int quantity,int samples,bool pass=true,string? gauge=null)=>new{subject,criteriaCode="OUTPUT-ACCEPTANCE",criteriaRevision=1,expectedCriteriaVersion=4,quantity,sampleQuantity=samples,
  findings=Enumerable.Range(1,samples).Select(n=>new AcceptanceFinding(1,n,gauge is null?null:pass?10m:12m,gauge is null?pass:null,gauge)).ToArray(),evidence="Actual observed identified cohort and required criteria recorded",quarantineLocation=pass?null:"QUARANTINE",reason="Actual independent inspection"};
 internal static object Release(AcceptanceSubjectKey subject,long inspection,decimal quantity)=>new{subject,quantity,inspectionIds=new[]{inspection},expectedInspectionVersion=1,basis="Independent actual acceptance evidence and exact scope reviewed",reason="Independent Quality product release"};

 [Fact]
 public async Task Current_design_applicability_is_captured_by_inspection_and_release_and_withdrawal_blocks_consumption()
 {
  await using var app=new ForgeApp(await postgres.CreateDatabaseAsync(),configureBuilder:b=>b.UseSetting(QmsService.ConfigurationKey,"EXAMPLE-ORG"));
  var(plant,_,_)=await Setup(app,designApplicability:true);var subject=new AcceptanceSubjectKey("ACCEPT-PRODUCT","OUTPUT-1000","STORES");
  var inspector=plant.As("acceptance-inspector");var signer=plant.As("release-signer");var stores=plant.As("stores");
  var inspection=Id(await plant.Ok(inspector,Api+"/inspections",Inspect(subject,1000,1000)));
  var accepted=await Json(await inspector.GetAsync(Api+"/inspections/"+inspection));
  Assert.Contains(accepted.GetProperty("subject").GetProperty("source").GetProperty("sources").EnumerateArray(),source=>
   source.GetProperty("resource").GetProperty("type").GetString()=="CommercialQuality"
   &&source.GetProperty("resource").GetProperty("id").GetString()!.Contains("ACCEPT-PRODUCT-DESIGN",StringComparison.Ordinal));
  var decision=Id(await plant.Ok(signer,Api+"/product-releases",Release(subject,inspection,1000)));
  await plant.Ok(plant.Approver,"/api/quality/commercial/records/DesignApplicability/ACCEPT-PRODUCT-DESIGN/revisions/1/withdraw",
   new{expectedVersion=5,basis="Actual effective design applicability withdrawn for human reassessment",reason="Withdraw current design control determination"});
  await Rejected(plant,stores,$"/api/inventory/product-releases/{decision}/consume",new{expectedVersion=1,quantity=1000,reason="Current design applicability must remain effective at actual inventory release"},"commercial_binding_unavailable");
 }

 [Fact]
 public async Task Real_thousand_unit_output_has_exact_920_release_and_80_segregated_hold_with_no_double_decision_consume_or_shipment()
 {
  await using var app=new ForgeApp(await postgres.CreateDatabaseAsync(),configureBuilder:b=>b.UseSetting(QmsService.ConfigurationKey,"EXAMPLE-ORG"));var(plant,order,_)=await Setup(app);
  var stores=plant.As("stores");var inspector=plant.As("acceptance-inspector");var signer=plant.As("release-signer");var shipper=plant.As("shipper");
  var stock=await stores.GetFromJsonAsync<JsonElement>("/api/inventory/stock?part=ACCEPT-PRODUCT");
  Assert.Contains(stock.EnumerateArray(),r=>r.GetProperty("lotNumber").GetString()=="OUTPUT-1000"&&r.GetProperty("status").GetString()=="Inspection"&&r.GetProperty("quantity").GetDecimal()==1000);
  Assert.DoesNotContain(stock.EnumerateArray(),r=>r.GetProperty("status").GetString()=="Released");
  await plant.Ok(stores,"/api/inventory/splits",new{partNumber="ACCEPT-PRODUCT",lotNumber="OUTPUT-1000",locationCode="STORES",status="Inspection",results=new[]{new{lotNumber="ACCEPTED-920",quantity=920},new{lotNumber="REJECTED-80",quantity=80}},reason="Actual identified physical separation of accepted candidate and rejected candidate"});
  var accepted=new AcceptanceSubjectKey("ACCEPT-PRODUCT","ACCEPTED-920","STORES");var rejected=new AcceptanceSubjectKey("ACCEPT-PRODUCT","REJECTED-80","STORES");
  var goodInspection=Id(await plant.Ok(inspector,Api+"/inspections",Inspect(accepted,920,920)));
  var badInspection=Id(await plant.Ok(inspector,Api+"/inspections",Inspect(rejected,80,80,false)));
  var failed=await Json(await inspector.GetAsync(Api+"/inspections/"+badInspection));Assert.False(failed.GetProperty("passed").GetBoolean());Assert.True(failed.GetProperty("holdTransactionId").GetInt64()>0);Assert.True(failed.GetProperty("ncrId").GetInt64()>0);
  Assert.Contains(failed.GetProperty("subject").GetProperty("originOrderNumbers").EnumerateArray(),r=>r.GetInt64()==order);
  await Rejected(plant,stores,"/api/inventory/releases",new{partNumber="ACCEPT-PRODUCT",lotNumber="ACCEPTED-920",locationCode="STORES",quantity=920,status="Inspection",reason="Legacy inventory approval cannot stand in for independent product release"},"independent_product_release_required");
  await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["acceptance-inspector"]}/roles",new{role="ExampleProductRelease",reason="Even holding release authority cannot permit self-release of inspected output"});
  await Rejected(plant,inspector,Api+"/product-releases",Release(accepted,goodInspection,920),"separation_of_duties");
  var releaseBody=Release(accepted,goodInspection,920);
  var releaseRace=await Task.WhenAll(signer.CommandAsync(Api+"/product-releases",releaseBody,"release-race-a"),signer.CommandAsync(Api+"/product-releases",releaseBody,"release-race-b"));
  var approved=Assert.Single(releaseRace,r=>r.IsSuccessStatusCode);Assert.Single(releaseRace,r=>r.StatusCode==HttpStatusCode.UnprocessableEntity);var decision=Id(await Json(approved));
  var consumeBody=new{expectedVersion=1,quantity=920,reason="Consume exact independently approved quantity in existing ledger"};var consumePath=$"/api/inventory/product-releases/{decision}/consume";
  var consumeRace=await Task.WhenAll(stores.CommandAsync(consumePath,consumeBody,"consume-race-a"),stores.CommandAsync(consumePath,consumeBody,"consume-race-b"));
  var consumed=Assert.Single(consumeRace,r=>r.IsSuccessStatusCode);Assert.Single(consumeRace,r=>r.StatusCode==HttpStatusCode.UnprocessableEntity);var consumedResult=await Json(consumed);var consumedKey=ReferenceEquals(consumed,consumeRace[0])?"consume-race-a":"consume-race-b";
  var canonical=await Rows(app,"quality.acceptance_inspection");var decisions=await Rows(app,"quality.product_release_decision");var consumedRows=await Rows(app,"inventory.product_release_consumption");
  Assert.Equal(consumedResult.GetRawText(),(await Json(await stores.CommandAsync(consumePath,consumeBody,consumedKey))).GetRawText());
  Assert.Equal(canonical,await Rows(app,"quality.acceptance_inspection"));Assert.Equal(decisions,await Rows(app,"quality.product_release_decision"));Assert.Equal(consumedRows,await Rows(app,"inventory.product_release_consumption"));
  stock=await stores.GetFromJsonAsync<JsonElement>("/api/inventory/stock?part=ACCEPT-PRODUCT");
  Assert.Contains(stock.EnumerateArray(),r=>r.GetProperty("lotNumber").GetString()=="ACCEPTED-920"&&r.GetProperty("status").GetString()=="Released"&&r.GetProperty("quantity").GetDecimal()==920);
  Assert.Contains(stock.EnumerateArray(),r=>r.GetProperty("lotNumber").GetString()=="REJECTED-80"&&r.GetProperty("locationCode").GetString()=="QUARANTINE"&&r.GetProperty("status").GetString()=="Hold"&&r.GetProperty("quantity").GetDecimal()==80);
  await plant.Ok(stores,"/api/inventory/containers",new{code="HELD-PACK",kind="Carton",reason="Attempted held physical scope"});
  await Rejected(plant,stores,"/api/inventory/containers/HELD-PACK/contents",new{partNumber="ACCEPT-PRODUCT",lotNumber="REJECTED-80",locationCode="QUARANTINE",quantity=80,status="Hold",reason="Held units cannot be packed as shipping-ready"},"not_released");
  var heldShipment=Id(await plant.Ok(stores,"/api/inventory/shipments",new{customer="Example customer",lines=new[]{new{partNumber="ACCEPT-PRODUCT",lotNumber="REJECTED-80",locationCode="QUARANTINE",quantity=80}},reason="Expose exact held scope blocking"}));
  await Rejected(plant,stores,$"/api/inventory/shipments/{heldShipment}/pack",new{reason="Attempt held shipment"},"not_released");
  await plant.Ok(stores,"/api/inventory/containers",new{code="RELEASED-PACK",kind="Carton",reason="Actual container for the independently released physical scope"});
  var packBody=new{partNumber="ACCEPT-PRODUCT",lotNumber="ACCEPTED-920",locationCode="STORES",quantity=920,status="Released",reason="Physically pack exactly the independently released quantity"};
  var packedContents=await Json(await stores.CommandAsync("/api/inventory/containers/RELEASED-PACK/contents",packBody,"released-container-content"));
  var sealBody=new{reason="Observed exact identified contents sealed"};var sealedContents=await Json(await stores.CommandAsync("/api/inventory/containers/RELEASED-PACK/seal",sealBody,"released-container-seal"));
  var shipment=Id(await plant.Ok(stores,"/api/inventory/shipments",new{customer="Example customer",containerCode="RELEASED-PACK",lines=new[]{new{partNumber="ACCEPT-PRODUCT",lotNumber="ACCEPTED-920",locationCode="STORES",quantity=920}},reason="Ship only independently released identified quantity"}));
  await plant.Ok(stores,$"/api/inventory/shipments/{shipment}/pack",new{reason="Pack actual released scope"});
  var shipBody=new{carrier="Recorded carrier",tracking="OBSERVED-TRACKING",reason="Physical shipment after independent product release"};var shipPath=$"/api/inventory/shipments/{shipment}/ship";
  var shipRace=await Task.WhenAll(shipper.CommandAsync(shipPath,shipBody,"ship-race-a"),shipper.CommandAsync(shipPath,shipBody,"ship-race-b"));
  var shipped=Assert.Single(shipRace,r=>r.IsSuccessStatusCode);Assert.Single(shipRace,r=>r.StatusCode==HttpStatusCode.UnprocessableEntity);var shippedResult=await Json(shipped);var shippedKey=ReferenceEquals(shipped,shipRace[0])?"ship-race-a":"ship-race-b";
  Assert.Equal(shippedResult.GetRawText(),(await Json(await shipper.CommandAsync(shipPath,shipBody,shippedKey))).GetRawText());
  Assert.Equal(packedContents.GetRawText(),(await Json(await stores.CommandAsync("/api/inventory/containers/RELEASED-PACK/contents",packBody,"released-container-content"))).GetRawText());
  Assert.Equal(sealedContents.GetRawText(),(await Json(await stores.CommandAsync("/api/inventory/containers/RELEASED-PACK/seal",sealBody,"released-container-seal"))).GetRawText());
  using var meta=JsonDocument.Parse(await Rows(app,"inventory.product_release_consumption"));Assert.Equal(920,Assert.Single(meta.RootElement.EnumerateArray()).GetProperty("quantity").GetDecimal());
  using var transactions=JsonDocument.Parse(await Rows(app,"inventory.transaction"));var outgoing=transactions.RootElement.EnumerateArray().Where(r=>r.GetProperty("kind").GetString()=="Shipment"&&r.GetProperty("shipment_id").GetInt64()==shipment).ToArray();Assert.Equal(920,Assert.Single(outgoing).GetProperty("quantity").GetDecimal());
  Assert.Equal(canonical,await Rows(app,"quality.acceptance_inspection"));Assert.Equal(decisions,await Rows(app,"quality.product_release_decision"));
 }

 [Fact]
 public async Task Approved_sampling_retains_exact_criteria_measurements_and_actual_gauge_use_then_current_source_withdrawal_blocks_replay()
 {
  var connection=new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync()){ApplicationName="ForgeAcceptanceGaugeRace",CommandTimeout=120}.ConnectionString;
  await using var app=new ForgeApp(connection,configureBuilder:b=>b.UseSetting(QmsService.ConfigurationKey,"EXAMPLE-ORG"));var(plant,_,_)=await Setup(app,AcceptanceCoverageKind.ApprovedSampling,true);
  var stores=plant.As("stores");var inspector=plant.As("acceptance-inspector");var signer=plant.As("release-signer");
  await plant.Ok(plant.Engineer,"/api/quality/gauges",new{code="ACCEPT-GAUGE",description="Actual calibrated acceptance gauge",calibrationDue=DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),certificate="Recorded current gauge certificate"});
  var subject=new AcceptanceSubjectKey("ACCEPT-PRODUCT","OUTPUT-1000","STORES");
  // This SQL lock coordinates normal API commands; it creates/changes no business source or positive evidence.
  // Inspection queues first. Both existing calibration writers must reload its counted use after acquiring the same owner row.
  await using var coordination=new NpgsqlConnection(app.ConnectionString);await coordination.OpenAsync();await using var rowLock=await coordination.BeginTransactionAsync();
  await using(var lockCommand=new NpgsqlCommand("SELECT code FROM resources.tool WHERE code='ACCEPT-GAUGE' FOR UPDATE",coordination,rowLock))Assert.Equal("ACCEPT-GAUGE",await lockCommand.ExecuteScalarAsync());
  var inspecting=inspector.CommandAsync(Api+"/inspections",Inspect(subject,1000,2,true,"ACCEPT-GAUGE"),"counted-inspection");
  var lockObservations=new List<string[]> { await WaitForToolWritersAsync(coordination,1,inspecting) };
  var day=DateTime.UtcNow.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
  var qualityCalibration=plant.Engineer.CommandAsync("/api/quality/gauges/ACCEPT-GAUGE/calibrations",new{calibratedOn=day,dueDate=DateTime.UtcNow.AddDays(15).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),certificate="Observed Quality calibration",reason="Normal Quality writer during measurement"},"quality-calibration-race");
  lockObservations.Add(await WaitForToolWritersAsync(coordination,2,inspecting,qualityCalibration));
  var resourceCalibration=plant.Engineer.CommandAsync("/api/resources/tools/ACCEPT-GAUGE/calibrations",new{calibratedOn=day,passed=true,dueOn=DateTime.UtcNow.AddDays(20).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),certificate="Observed Resources calibration",reason="Normal Resources writer during measurement"},"resource-calibration-race");
  lockObservations.Add(await WaitForToolWritersAsync(coordination,3,inspecting,qualityCalibration,resourceCalibration));await rowLock.CommitAsync();
  JsonElement inspectionResult;using(var result=await inspecting){Assert.True(result.IsSuccessStatusCode,$"Inspection writer failed after coordinated lock release: {(int)result.StatusCode} {await result.Content.ReadAsStringAsync()}");inspectionResult=await Json(result);}
  using(var result=await qualityCalibration)Assert.True(result.IsSuccessStatusCode,$"Quality calibration writer failed after coordinated lock release: {(int)result.StatusCode} {await result.Content.ReadAsStringAsync()}");
  using(var result=await resourceCalibration)Assert.True(result.IsSuccessStatusCode,$"Resources calibration writer failed after coordinated lock release: {(int)result.StatusCode} {await result.Content.ReadAsStringAsync()}");
  Assert.All(lockObservations,(observed)=>Assert.Contains(observed,entry=>entry.Contains("Lock/transactionid",StringComparison.Ordinal)));
  var inspection=Id(inspectionResult);
  var view=await Json(await inspector.GetAsync(Api+"/inspections/"+inspection));Assert.Equal(1000,view.GetProperty("quantity").GetDecimal());Assert.Equal(2,view.GetProperty("sampleQuantity").GetDecimal());
  var measured=view.GetProperty("results").EnumerateArray().ToArray();Assert.Equal(2,measured.Length);
  Assert.All(measured,r=>{Assert.Equal("mm",r.GetProperty("criterion").GetProperty("unit").GetString());Assert.Equal(10,r.GetProperty("criterion").GetProperty("nominal").GetDecimal());Assert.Equal(9,r.GetProperty("criterion").GetProperty("lower").GetDecimal());Assert.Equal(11,r.GetProperty("criterion").GetProperty("upper").GetDecimal());Assert.True(r.GetProperty("historicValidity").GetProperty("valid").GetBoolean());Assert.True(r.GetProperty("toolUseEventId").GetInt64()>0);});
  Assert.Equal(measured[0].GetProperty("toolUseEventId").GetInt64(),measured[1].GetProperty("toolUseEventId").GetInt64());
  using var equipment=JsonDocument.Parse(await Rows(app,"resources.tool_event"));
  var use=Assert.Single(equipment.RootElement.EnumerateArray(),r=>r.GetProperty("event_id").GetInt64()==measured[0].GetProperty("toolUseEventId").GetInt64());
  Assert.Equal("Used",use.GetProperty("kind").GetString());Assert.Equal("ACCEPT-GAUGE",use.GetProperty("tool_code").GetString());
  Assert.Equal("person:"+plant.People["acceptance-inspector"],use.GetProperty("actor").GetString());Assert.Equal(1,use.GetProperty("uses").GetInt32());
  var calibrations=equipment.RootElement.EnumerateArray().Where(r=>r.GetProperty("tool_code").GetString()=="ACCEPT-GAUGE"&&r.GetProperty("kind").GetString()=="Calibrated").ToArray();Assert.Equal(2,calibrations.Length);
  Assert.All(calibrations,r=>Assert.Equal("person:"+plant.People["eng"],r.GetProperty("actor").GetString()));
  using(var tools=JsonDocument.Parse(await Rows(app,"resources.tool")))Assert.Equal(1,Assert.Single(tools.RootElement.EnumerateArray(),r=>r.GetProperty("code").GetString()=="ACCEPT-GAUGE").GetProperty("usage_count").GetInt32());
  // PostgreSQL event timestamps have microsecond precision; immutable JSON retains the original clock ticks.
  Assert.InRange((view.GetProperty("at").GetDateTimeOffset()-use.GetProperty("at").GetDateTimeOffset()).Duration(),TimeSpan.Zero,TimeSpan.FromMicroseconds(1));
  Assert.All(measured,r=>Assert.Equal(view.GetProperty("at").GetDateTimeOffset(),r.GetProperty("historicValidity").GetProperty("asOf").GetDateTimeOffset()));
  var releaseBody=Release(subject,inspection,1000);var decision=Id(await Json(await signer.CommandAsync(Api+"/product-releases",releaseBody,"sampling-release")));
  var original=await Rows(app,"quality.acceptance_inspection");var releaseRows=await Rows(app,"quality.product_release_decision");
  await plant.Ok(plant.Admin,"/api/kernel/roles/ExampleProductRelease/permissions/"+Permissions.QualityProductRelease+"/revoke",new{reason="Revoke only actual product release authority before an identical retry"});
  using(var denied=await signer.CommandAsync(Api+"/product-releases",releaseBody,"sampling-release"))Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
  Assert.Equal(original,await Rows(app,"quality.acceptance_inspection"));Assert.Equal(releaseRows,await Rows(app,"quality.product_release_decision"));
  await plant.Ok(plant.Admin,"/api/kernel/roles/ExampleProductRelease/permissions",new{permission=Permissions.QualityProductRelease,reason="Restore exact independently assigned canonical release authority"});
  Assert.Equal(decision,Id(await Json(await signer.CommandAsync(Api+"/product-releases",releaseBody,"sampling-release"))));
  await plant.Ok(plant.Approver,"/api/quality/qms/adoptions/RELEASE-PRODUCT/revisions/1/withdraw",new{expectedVersion=5,note="Leadership withdrew actual operational applicability",reason="Current policy withdrawal"});
  await Rejected(plant,stores,$"/api/inventory/product-releases/{decision}/consume",new{expectedVersion=1,quantity=1000,reason="Stale release cannot consume after governing withdrawal"},"governed_policy_unavailable");
  using var repeated=await signer.CommandAsync(Api+"/product-releases",releaseBody,"sampling-release");Assert.Equal(HttpStatusCode.UnprocessableEntity,repeated.StatusCode);Assert.Contains("governed_policy_unavailable",await repeated.Content.ReadAsStringAsync(),StringComparison.Ordinal);
  Assert.Equal(original,await Rows(app,"quality.acceptance_inspection"));Assert.Equal(releaseRows,await Rows(app,"quality.product_release_decision"));
  var historic=await Json(await inspector.GetAsync(Api+"/inspections/"+inspection));Assert.Equal(view.GetRawText(),historic.GetRawText());
 }

 [Fact]
 public async Task Exact_versions_and_unconsumed_withdrawal_preserve_history_and_current_criteria_are_checked_before_inspection_replay()
 {
  await using var app=new ForgeApp(await postgres.CreateDatabaseAsync(),configureBuilder:b=>b.UseSetting(QmsService.ConfigurationKey,"EXAMPLE-ORG"));var(plant,_,_)=await Setup(app);
  var inspector=plant.As("acceptance-inspector");var signer=plant.As("release-signer");var stores=plant.As("stores");var subject=new AcceptanceSubjectKey("ACCEPT-PRODUCT","OUTPUT-1000","STORES");
  var inspectionBody=Inspect(subject,1000,1000);
  var inspection=Id(await Json(await inspector.CommandAsync(Api+"/inspections",inspectionBody,"exact-inspection")));
  await Rejected(plant,signer,Api+"/product-releases",new{subject,quantity=1000,inspectionIds=new[]{inspection},expectedInspectionVersion=2,basis="An incorrect evidence version cannot authorize output",reason="Read exact immutable evidence version"},"acceptance_not_complete");
  var releaseBody=Release(subject,inspection,1000);var decision=Id(await Json(await signer.CommandAsync(Api+"/product-releases",releaseBody,"exact-release")));
  await Rejected(plant,signer,$"{Api}/product-releases/{decision}/withdraw",new{expectedVersion=2,basis="Wrong expected decision version",reason="Version race"},"stale_version");
  await plant.Ok(signer,$"{Api}/product-releases/{decision}/withdraw",new{expectedVersion=1,basis="Independent release withdrawn before any ledger consumption",reason="Actual human withdrawal"});
  var canonical=await Rows(app,"quality.acceptance_inspection");var decisions=await Rows(app,"quality.product_release_decision");var withdrawals=await Rows(app,"quality.product_release_withdrawal");
  await Rejected(plant,stores,$"/api/inventory/product-releases/{decision}/consume",new{expectedVersion=1,quantity=1000,reason="A withdrawn decision cannot release inventory"},"release_not_current");
  using(var stored=JsonDocument.Parse(withdrawals))
  {
   var actual=Assert.Single(stored.RootElement.EnumerateArray()).GetProperty("payload");Assert.Equal(decision,actual.GetProperty("decisionId").GetInt64());Assert.Equal(2,actual.GetProperty("version").GetInt32());Assert.Equal("person:"+plant.People["release-signer"],actual.GetProperty("actor").GetString());Assert.Equal("Independent release withdrawn before any ledger consumption",actual.GetProperty("basis").GetString());
  }
  var successor=new AcceptanceCriteriaContent("ACCEPT-PRODUCT","A",10,"Actual independently approved successor",new(AcceptanceCoverageKind.FullQuantity,1,1000,"Each identified unit under revised arrangement","Human approval of revised exact arrangement"),[new(1,"Revised visual condition",CharacteristicKind.Attribute,null,null,null,null)],[new("RELEASE.CONTROLS","A")]);
  await plant.Ok(plant.Engineer,Api+"/criteria",new{code="OUTPUT-ACCEPTANCE",facilityCode="PLANT1",markings="None",expectedVersion=4,content=successor,reason="Actual approved arrangement successor"});
  foreach(var (action,version) in new[]{("submit",1),("approve",2),("effective",3)})await plant.Ok(action=="submit"?plant.Engineer:plant.Approver,Api+"/criteria/OUTPUT-ACCEPTANCE/revisions/2/"+action,new{expectedVersion=version,note="Reviewed exact revised source/coverage",reason="Independent successor approval"});
  using(var staleInspection=await inspector.CommandAsync(Api+"/inspections",inspectionBody,"exact-inspection")){Assert.Equal(HttpStatusCode.UnprocessableEntity,staleInspection.StatusCode);Assert.Contains("criteria_not_current",await staleInspection.Content.ReadAsStringAsync(),StringComparison.Ordinal);}
  using(var staleRelease=await signer.CommandAsync(Api+"/product-releases",releaseBody,"exact-release")){Assert.Equal(HttpStatusCode.UnprocessableEntity,staleRelease.StatusCode);Assert.Contains("criteria_not_current",await staleRelease.Content.ReadAsStringAsync(),StringComparison.Ordinal);}
  var oldCriteria=await Json(await inspector.GetAsync(Api+"/criteria/OUTPUT-ACCEPTANCE/revisions/1"));var currentCriteria=await Json(await inspector.GetAsync(Api+"/criteria/OUTPUT-ACCEPTANCE/revisions/2"));
  Assert.Equal("Superseded",oldCriteria.GetProperty("state").GetString());Assert.Equal(5,oldCriteria.GetProperty("version").GetInt32());Assert.Equal(oldCriteria.GetProperty("effectiveTo").GetDateTimeOffset(),currentCriteria.GetProperty("effectiveFrom").GetDateTimeOffset());Assert.Equal("Actual frozen acceptance arrangement",oldCriteria.GetProperty("content").GetProperty("title").GetString());
  await plant.Ok(plant.Approver,Api+"/criteria/OUTPUT-ACCEPTANCE/revisions/2/withdraw",new{expectedVersion=4,note="Exact acceptance arrangement withdrawn for reconsideration",reason="Current criteria withdrawal"});
  using var retried=await inspector.CommandAsync(Api+"/inspections",inspectionBody,"exact-inspection");Assert.Equal(HttpStatusCode.UnprocessableEntity,retried.StatusCode);Assert.Contains("acceptance_arrangement_unavailable",await retried.Content.ReadAsStringAsync(),StringComparison.Ordinal);
  Assert.Equal(canonical,await Rows(app,"quality.acceptance_inspection"));Assert.Equal(decisions,await Rows(app,"quality.product_release_decision"));Assert.Equal(withdrawals,await Rows(app,"quality.product_release_withdrawal"));Assert.Equal("[]",await Rows(app,"inventory.product_release_consumption"));
  var historical=await Json(await signer.GetAsync(Api+"/product-releases/"+decision));Assert.Equal(decision,historical.GetProperty("decisionId").GetInt64());Assert.Equal(1000,historical.GetProperty("quantity").GetDecimal());
 }

 [Fact]
 public async Task Private_failed_child_NCR_and_linked_CAPA_retain_owner_scope_and_current_membership_before_direct_action_replay()
 {
  await using var app=new ForgeApp(await postgres.CreateDatabaseAsync(),configureBuilder:b=>b.UseSetting(QmsService.ConfigurationKey,"EXAMPLE-ORG"));var(plant,_,program)=await Setup(app,privateProgram:true);Assert.NotNull(program);
  var stores=plant.As("stores");var inspector=plant.As("acceptance-inspector");
  await plant.Ok(stores,"/api/inventory/splits",new{partNumber="ACCEPT-PRODUCT",lotNumber="OUTPUT-1000",locationCode="STORES",status="Inspection",results=new[]{new{lotNumber="PRIVATE-920",quantity=920},new{lotNumber="PRIVATE-80",quantity=80}},reason="Identify the private rejected child without claiming a whole-order defect"});
  var inspection=Id(await plant.Ok(inspector,Api+"/inspections",Inspect(new("ACCEPT-PRODUCT","PRIVATE-80","STORES"),80,80,false)));
  var actual=await Json(await inspector.GetAsync(Api+"/inspections/"+inspection));var ncrId=actual.GetProperty("ncrId").GetInt64();
  var ncrs=await Json(await plant.Engineer.GetAsync("/api/quality/ncrs"));var child=Assert.Single(ncrs.EnumerateArray(),n=>n.GetProperty("ncrId").GetInt64()==ncrId);
  Assert.Equal("PRIVATE-80",child.GetProperty("lotNumber").GetString());Assert.Equal(80,child.GetProperty("quantity").GetDecimal());Assert.Equal(JsonValueKind.Null,child.GetProperty("orderNumber").ValueKind);Assert.Equal(JsonValueKind.Null,child.GetProperty("sequence").ValueKind);
  var capaId=Id(await plant.Ok(plant.Engineer,"/api/quality/capas",new{ncrId,title="Private rejected child follow-up",rootCause="Recorded observation requires an actual investigation",action="Review actual child evidence",dueDate=DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),reason="Authorized source-linked corrective action"}));
  await plant.Person("quality-outsider",Roles.QualityEngineer,"QmsPolicyConsumer");var outsider=plant.As("quality-outsider");
  Assert.DoesNotContain((await Json(await outsider.GetAsync("/api/quality/ncrs"))).EnumerateArray(),n=>n.GetProperty("ncrId").GetInt64()==ncrId);
  Assert.DoesNotContain((await Json(await outsider.GetAsync("/api/quality/capas"))).EnumerateArray(),c=>c.GetProperty("capaId").GetInt64()==capaId);
  var evidence=await Rows(app,"quality.acceptance_inspection");var body=new{disposition="Rework",note="Review the actual private rejected child",reason="Private child disposition request"};var path=$"/api/quality/ncrs/{ncrId}/disposition";
  using(var denied=await outsider.CommandAsync(path,body,"private-child-action"))Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
  await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["quality-outsider"]}/programs",new{programId=program,reason="Independent explicit current source membership"});
  Assert.Contains((await Json(await outsider.GetAsync("/api/quality/capas"))).EnumerateArray(),c=>c.GetProperty("capaId").GetInt64()==capaId);
  await Json(await outsider.CommandAsync(path,body,"private-child-action"));var disposed=await Rows(app,"quality.ncr");
  await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["quality-outsider"]}/programs/{program}/revoke",new{reason="Withdraw current private source membership"});
  using(var deniedRetry=await outsider.CommandAsync(path,body,"private-child-action"))Assert.Equal(HttpStatusCode.Forbidden,deniedRetry.StatusCode);
  Assert.Equal(evidence,await Rows(app,"quality.acceptance_inspection"));Assert.Equal(disposed,await Rows(app,"quality.ncr"));
  Assert.DoesNotContain((await Json(await outsider.GetAsync("/api/quality/ncrs"))).EnumerateArray(),n=>n.GetProperty("ncrId").GetInt64()==ncrId);
  await using var connection=new NpgsqlConnection(app.ConnectionString);await connection.OpenAsync();await using var audit=new NpgsqlCommand("SELECT program_id,markings,actor,action,reason,new_value FROM kernel.audit_log WHERE outcome='Denied' AND action='DispositionNcr' AND actor=$1 ORDER BY recorded_at,audit_id",connection);audit.Parameters.AddWithValue("person:"+plant.People["quality-outsider"]);
  await using var reader=await audit.ExecuteReaderAsync();var denialCount=0;while(await reader.ReadAsync()){denialCount++;Assert.Equal(program!.Value,reader.GetGuid(0));Assert.Equal(0,reader.GetInt32(1));Assert.Equal("person:"+plant.People["quality-outsider"],reader.GetString(2));Assert.Equal("DispositionNcr",reader.GetString(3));Assert.Equal("Private child disposition request",reader.GetString(4));Assert.Contains("program",reader.GetString(5),StringComparison.OrdinalIgnoreCase);}Assert.Equal(2,denialCount);
 }
}

