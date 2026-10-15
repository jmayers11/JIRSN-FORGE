using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Npgsql;
using Microsoft.AspNetCore.Hosting;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ReturnAcceptanceTests(PostgresFixture postgres)
{
 private const string Acceptance="/api/quality/acceptance";
 private static long Id(JsonElement result)=>long.Parse(result.GetProperty("entityId").GetString()!,CultureInfo.InvariantCulture);
 private static async Task<JsonElement> Json(HttpResponseMessage response)
 {var text=await response.Content.ReadAsStringAsync();Assert.True(response.IsSuccessStatusCode,$"{response.StatusCode}: {text}");using var parsed=JsonDocument.Parse(text);return parsed.RootElement.Clone();}
 private static async Task Rejected(SlittingPlant.Plant plant,HttpClient actor,string path,object body,string code,HttpStatusCode status=HttpStatusCode.UnprocessableEntity)
 {using var response=await plant.Try(actor,path,body);Assert.Equal(status,response.StatusCode);Assert.Contains(code,await response.Content.ReadAsStringAsync(),StringComparison.Ordinal);}
 private static async Task<string> Row(ForgeApp app,string table,string key,long id)
 {await using var connection=new NpgsqlConnection(app.ConnectionString);await connection.OpenAsync();await using var query=new NpgsqlCommand($"SELECT to_jsonb(return_snapshot_row)::text FROM {table} return_snapshot_row WHERE {key}=$1",connection);query.Parameters.AddWithValue(id);return(string)(await query.ExecuteScalarAsync())!;}
 private static async Task<decimal> Quantity(ForgeApp app,string sql,long id)
 {await using var connection=new NpgsqlConnection(app.ConnectionString);await connection.OpenAsync();await using var query=new NpgsqlCommand(sql,connection);query.Parameters.AddWithValue(id);return(decimal)(await query.ExecuteScalarAsync())!;}
 internal static async Task<(SlittingPlant.Plant Plant,long Shipment,long OriginalInspection,long OriginalDecision,Guid? Program)> Shipped(ForgeApp app,bool privateProgram=false,bool serialized=false)
 {
  var amount=serialized?1:1000;var lot=serialized?"SERIAL-OUTPUT-1":"OUTPUT-1000";
  var(plant,_,program)=await ProductAcceptanceTests.Setup(app,privateProgram:privateProgram,serialized:serialized,outputQuantity:amount);
  await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["stores"]}/roles",new{role=Roles.QualityEngineer,reason="Explicit actual return disposition authority, distinct from independent product release"});
  var subject=new AcceptanceSubjectKey("ACCEPT-PRODUCT",lot,"STORES");
  var inspection=Id(await plant.Ok(plant.As("acceptance-inspector"),Acceptance+"/inspections",ProductAcceptanceTests.Inspect(subject,amount,amount)));
  var decision=Id(await plant.Ok(plant.As("release-signer"),Acceptance+"/product-releases",ProductAcceptanceTests.Release(subject,inspection,amount)));
  await plant.Ok(plant.As("stores"),$"/api/inventory/product-releases/{decision}/consume",new{expectedVersion=1,quantity=amount,reason="Actual initial independent release before customer shipment"});
  var shipment=Id(await plant.Ok(plant.As("stores"),"/api/inventory/shipments",new{customer="Actual return customer",lines=new[]{new{partNumber="ACCEPT-PRODUCT",lotNumber=lot,locationCode="STORES",quantity=amount}},reason="Actual original customer shipment"}));
  await plant.Ok(plant.As("stores"),$"/api/inventory/shipments/{shipment}/pack",new{reason="Actual initial released product"});
  await plant.Ok(plant.As("shipper"),$"/api/inventory/shipments/{shipment}/ship",new{carrier="Observed carrier",tracking="ACTUAL-INITIAL",reason="Original independent release consumed exactly once"});
  return(plant,shipment,inspection,decision,program);
 }
 private static async Task<long> Returned(SlittingPlant.Plant plant,long shipment,int quantity,string lot="OUTPUT-1000")=>Id(await plant.Ok(plant.As("stores"),"/api/inventory/customer-returns",new{shipmentId=shipment,partNumber="ACCEPT-PRODUCT",lotNumber=lot,quantity,locationCode="QUARANTINE",reason="Actual received customer return retains its physical lot and separate evidence"}));
 private static async Task<ReturnInspectionCohort> Cohort(HttpClient actor,long id)=>await actor.GetFromJsonAsync<ReturnInspectionCohort>(Acceptance+"/return-cohorts/"+id,CanonicalJson.Options)??throw new InvalidOperationException("Cohort response missing.");
 private static async Task<(int Status,string? Title,string? Detail,string? Code)> Problem(HttpResponseMessage response)
 {using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var root=json.RootElement;return(root.GetProperty("status").GetInt32(),root.GetProperty("title").GetString(),root.GetProperty("detail").GetString(),root.GetProperty("code").GetString());}
 [Fact]
 public async Task Actual_return_reinspection_preserves_old_release_and_serializes_partial_duplicate_and_exhausted_restock()
 {
  await using var app=new ForgeApp(await postgres.CreateDatabaseAsync(),configureBuilder:builder=>builder.UseSetting(QmsService.ConfigurationKey,"EXAMPLE-ORG"));
  var(plant,shipment,oldInspection,oldDecision,_)=await Shipped(app);var stores=plant.As("stores");
  var originalInspection=await Row(app,"quality.acceptance_inspection","inspection_id",oldInspection);var originalDecision=await Row(app,"quality.product_release_decision","decision_id",oldDecision);
  var rma=await Returned(plant,shipment,100);
  await Rejected(plant,stores,$"/api/inventory/customer-returns/{rma}/disposition",new{disposition="Restock",decisionId=oldDecision,expectedDecisionVersion=1,quantity=100,reason="Original exhausted product release is not return inspection evidence"},"return_release_required");
  var preparePath=$"/api/inventory/customer-returns/{rma}/prepare-inspection";var prepare=new{quantity=100,locationCode="STORES",reason="Actual same-lot returned cohort enters Inspection; it is not released"};
  var preparations=await Task.WhenAll(stores.CommandAsync(preparePath,prepare,"prepare-return-a"),stores.CommandAsync(preparePath,prepare,"prepare-return-b"));
  var successful=Assert.Single(preparations,response=>response.IsSuccessStatusCode);Assert.Single(preparations,response=>response.StatusCode==HttpStatusCode.UnprocessableEntity);
  var prepared=await Json(successful);var cohort=await Cohort(stores,Id(prepared));Assert.Equal(rma,cohort.RmaId);Assert.Equal(100,cohort.Quantity);Assert.Equal("OUTPUT-1000",cohort.Subject.LotNumber);Assert.Equal(cohort.CohortId,cohort.Subject.ReturnCohortId);
  var prepareKey=ReferenceEquals(successful,preparations[0])?"prepare-return-a":"prepare-return-b";Assert.Equal(prepared.GetRawText(),(await Json(await stores.CommandAsync(preparePath,prepare,prepareKey))).GetRawText());
  await Rejected(plant,stores,$"/api/inventory/customer-returns/{rma}/disposition",new{disposition="Scrap",reason="Legacy whole-return writer must not consume an inspected cohort"},"return_cohort_disposition_required");
  var inspection=Id(await plant.Ok(plant.As("acceptance-inspector"),Acceptance+"/inspections",ProductAcceptanceTests.Inspect(cohort.Subject,100,100)));
  var decision=Id(await plant.Ok(plant.As("release-signer"),Acceptance+"/product-releases",ProductAcceptanceTests.Release(cohort.Subject,inspection,100)));
  await Rejected(plant,stores,$"/api/inventory/product-releases/{decision}/consume",new{expectedVersion=1,quantity=100,reason="Generic consumption must not bypass controlled return ownership disposition"},"return_restock_required");
  var path=$"/api/inventory/customer-returns/{rma}/disposition";var first=new{disposition="Restock",decisionId=decision,expectedDecisionVersion=1,quantity=40,reason="Actual partial consumed return release"};
  await plant.Ok(stores,path,first);
  using(var pending=JsonDocument.Parse(await Row(app,"inventory.customer_return","rma_id",rma)))Assert.Equal(JsonValueKind.Null,pending.RootElement.GetProperty("disposition").ValueKind);
  Assert.Equal(40,await Quantity(app,"SELECT COALESCE(sum(quantity),0) FROM inventory.return_release_consumption WHERE rma_id=$1",rma));
  var remaining=new{disposition="Restock",decisionId=decision,expectedDecisionVersion=1,quantity=60,reason="Only exact remaining returned quantity may be consumed"};
  var race=await Task.WhenAll(stores.CommandAsync(path,remaining,"restock-return-a"),stores.CommandAsync(path,remaining,"restock-return-b"));var finished=Assert.Single(race,response=>response.IsSuccessStatusCode);Assert.Single(race,response=>response.StatusCode==HttpStatusCode.UnprocessableEntity);
  var finalResult=await Json(finished);var successfulKey=ReferenceEquals(finished,race[0])?"restock-return-a":"restock-return-b";var recorded=await Row(app,"inventory.customer_return","rma_id",rma);
  Assert.Equal(finalResult.GetRawText(),(await Json(await stores.CommandAsync(path,remaining,successfulKey))).GetRawText());Assert.Equal(recorded,await Row(app,"inventory.customer_return","rma_id",rma));
  using(var complete=JsonDocument.Parse(recorded))Assert.Equal("Restock",complete.RootElement.GetProperty("disposition").GetString());
  Assert.Equal(100,await Quantity(app,"SELECT COALESCE(sum(quantity),0) FROM inventory.return_release_consumption WHERE rma_id=$1",rma));
  Assert.Equal(100,await Quantity(app,"SELECT COALESCE(sum(quantity),0) FROM inventory.product_release_consumption WHERE decision_id=$1",decision));
  Assert.Equal(1000,await Quantity(app,"SELECT COALESCE(sum(quantity),0) FROM inventory.product_release_consumption WHERE decision_id=$1",oldDecision));
  Assert.Equal(originalInspection,await Row(app,"quality.acceptance_inspection","inspection_id",oldInspection));Assert.Equal(originalDecision,await Row(app,"quality.product_release_decision","decision_id",oldDecision));
 }
 [Fact]
 public async Task Same_lot_return_children_keep_failed_scope_segregated_and_current_private_source_revocation_blocks_successful_retry()
 {
  await using var app=new ForgeApp(await postgres.CreateDatabaseAsync(),configureBuilder:builder=>builder.UseSetting(QmsService.ConfigurationKey,"EXAMPLE-ORG"));
  var(plant,shipment,_,_,program)=await Shipped(app,true);Assert.NotNull(program);var stores=plant.As("stores");var rma=await Returned(plant,shipment,100);
  var goodId=Id(await plant.Ok(stores,$"/api/inventory/customer-returns/{rma}/prepare-inspection",new{quantity=80,locationCode="STORES",reason="Identified returned good candidate cohort"}));
  var badId=Id(await plant.Ok(stores,$"/api/inventory/customer-returns/{rma}/prepare-inspection",new{quantity=20,locationCode="STORES",reason="Separately identified returned suspect candidate cohort"}));
  var good=await Cohort(stores,goodId);var bad=await Cohort(stores,badId);Assert.Equal(good.Subject.LotNumber,bad.Subject.LotNumber);Assert.NotEqual(good.Subject.ReturnCohortId,bad.Subject.ReturnCohortId);
  var failed=Id(await plant.Ok(plant.As("acceptance-inspector"),Acceptance+"/inspections",ProductAcceptanceTests.Inspect(bad.Subject,20,20,false)));
  var rejected=await plant.As("acceptance-inspector").GetFromJsonAsync<AcceptanceInspection>(Acceptance+"/inspections/"+failed,CanonicalJson.Options);Assert.NotNull(rejected);Assert.False(rejected.Passed);Assert.NotNull(rejected.NcrId);Assert.NotNull(rejected.HoldTransactionId);
  var passed=Id(await plant.Ok(plant.As("acceptance-inspector"),Acceptance+"/inspections",ProductAcceptanceTests.Inspect(good.Subject,80,80)));
  var decision=Id(await plant.Ok(plant.As("release-signer"),Acceptance+"/product-releases",ProductAcceptanceTests.Release(good.Subject,passed,80)));
  await plant.Person("return-outsider",Roles.QualityEngineer,Roles.MaterialHandler,"QmsPolicyConsumer");var outsider=plant.As("return-outsider");
  using(var hidden=await outsider.GetAsync(Acceptance+"/return-cohorts/"+goodId))using(var absent=await outsider.GetAsync(Acceptance+"/return-cohorts/9223372036854775807")){Assert.Equal(absent.StatusCode,hidden.StatusCode);Assert.Equal(await Problem(absent),await Problem(hidden));Assert.DoesNotContain("ACCEPT-PRODUCT",await hidden.Content.ReadAsStringAsync(),StringComparison.Ordinal);}
  await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["return-outsider"]}/programs",new{programId=program,reason="Independent current membership for actual returned source"});
  Assert.Equal(goodId,(await Cohort(outsider,goodId)).CohortId);
  var path=$"/api/inventory/customer-returns/{rma}/disposition";var body=new{disposition="Restock",decisionId=decision,expectedDecisionVersion=1,quantity=80,reason="Release only independently inspected returned good cohort; failed twenty remain held"};
  var result=await Json(await outsider.CommandAsync(path,body,"current-return-restock"));var original=await Row(app,"quality.acceptance_inspection","inspection_id",failed);var consumed=await Quantity(app,"SELECT COALESCE(sum(quantity),0) FROM inventory.return_release_consumption WHERE rma_id=$1",rma);
  Assert.Equal(80,consumed);using(var pending=JsonDocument.Parse(await Row(app,"inventory.customer_return","rma_id",rma)))Assert.Equal(JsonValueKind.Null,pending.RootElement.GetProperty("disposition").ValueKind);
  Assert.Equal(result.GetRawText(),(await Json(await outsider.CommandAsync(path,body,"current-return-restock"))).GetRawText());
  await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["return-outsider"]}/programs/{program}/revoke",new{reason="Current source membership withdrawn before identical successful retry"});
  using(var denied=await outsider.CommandAsync(path,body,"current-return-restock")){Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);Assert.Contains("program",await denied.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);}
  Assert.Equal(consumed,await Quantity(app,"SELECT COALESCE(sum(quantity),0) FROM inventory.return_release_consumption WHERE rma_id=$1",rma));Assert.Equal(original,await Row(app,"quality.acceptance_inspection","inspection_id",failed));
  Assert.Equal(20,await Quantity(app,"SELECT COALESCE(sum(quantity),0) FROM inventory.balance WHERE part_number='ACCEPT-PRODUCT' AND lot_number='OUTPUT-1000' AND location_code='QUARANTINE' AND status='Hold' AND $1>0",rma));
  Assert.Equal(80,await Quantity(app,"SELECT COALESCE(sum(quantity),0) FROM inventory.balance WHERE part_number='ACCEPT-PRODUCT' AND lot_number='OUTPUT-1000' AND location_code='STORES' AND status='Released' AND $1>0",rma));
 }
 [Fact]
 public async Task Actual_serial_return_keeps_the_original_serial_and_requires_one_new_independent_return_release()
 {
  await using var app=new ForgeApp(await postgres.CreateDatabaseAsync(),configureBuilder:builder=>builder.UseSetting(QmsService.ConfigurationKey,"EXAMPLE-ORG"));
  var(plant,shipment,_,oldDecision,_)=await Shipped(app,serialized:true);var stores=plant.As("stores");var rma=await Returned(plant,shipment,1,"SERIAL-OUTPUT-1");
  var path=$"/api/inventory/customer-returns/{rma}/prepare-inspection";
  await Rejected(plant,stores,path,new{quantity=0.5m,locationCode="STORES",reason="A serial cannot be divided into a fractional return cohort"},"return_quantity_exhausted");
  var id=Id(await plant.Ok(stores,path,new{quantity=1,locationCode="STORES",reason="Actual whole returned serial enters new Inspection cohort"}));var cohort=await Cohort(stores,id);
  Assert.Equal("SERIAL-OUTPUT-1",cohort.Subject.LotNumber);Assert.Equal(1,cohort.Quantity);
  await Rejected(plant,stores,path,new{quantity=1,locationCode="STORES",reason="A different key cannot prepare the same returned serial twice"},"return_quantity_exhausted");
  var inspection=Id(await plant.Ok(plant.As("acceptance-inspector"),Acceptance+"/inspections",ProductAcceptanceTests.Inspect(cohort.Subject,1,1)));
  var decision=Id(await plant.Ok(plant.As("release-signer"),Acceptance+"/product-releases",ProductAcceptanceTests.Release(cohort.Subject,inspection,1)));Assert.NotEqual(oldDecision,decision);
  var restockPath=$"/api/inventory/customer-returns/{rma}/disposition";var restock=new{disposition="Restock",decisionId=decision,expectedDecisionVersion=1,quantity=1,reason="Independently accepted returned serial restocked through actual return owner"};
  var result=await Json(await stores.CommandAsync(restockPath,restock,"serial-return-restock"));Assert.Equal(result.GetRawText(),(await Json(await stores.CommandAsync(restockPath,restock,"serial-return-restock"))).GetRawText());
  Assert.Equal(1,await Quantity(app,"SELECT COALESCE(sum(quantity),0) FROM inventory.return_release_consumption WHERE rma_id=$1",rma));Assert.Equal(1,await Quantity(app,"SELECT COALESCE(sum(quantity),0) FROM inventory.product_release_consumption WHERE decision_id=$1",oldDecision));
  Assert.Equal(1,await Quantity(app,"SELECT COALESCE(sum(quantity),0) FROM inventory.balance WHERE part_number='ACCEPT-PRODUCT' AND lot_number='SERIAL-OUTPUT-1' AND status='Released' AND $1>0",rma));
 }
}
