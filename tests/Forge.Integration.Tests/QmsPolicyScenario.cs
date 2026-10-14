using System.Text;
using System.Text.Json;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;

namespace Forge.Integration.Tests;

/// <summary>Shared real-command governance setup. Callers choose every organization control explicitly.</summary>
internal static class QmsPolicyScenario
{
    private static int key;
    public static async Task AdoptAsync(SlittingPlant.Plant plant,string policyCode,string adoptionCode,string documentNumber,
        QmsPolicyContent policy,QmsPolicyPurpose purpose,QmsOperationSelector selector,Guid? programId=null,
        QmsRevisionReference? process=null,string? riskClassCode=null,string? riskBasis=null,string facility="PLANT1")
    {
        foreach(var role in new[]{Roles.QmsAuthor}) await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["eng"]}/roles",new{role,reason="Explicit example organization governance assignment"});
        foreach(var role in new[]{Roles.QmsReviewer,Roles.QmsApprover,Roles.DocumentController}) await plant.Ok(plant.Admin,$"/api/kernel/people/{plant.People["approver"]}/roles",new{role,reason="Independent example organization governance authority"});
        if(programId is{} program)
            foreach(var person in new[]{plant.People["eng"],plant.People["approver"]}) await plant.Ok(plant.Admin,$"/api/kernel/people/{person}/programs",new{programId=program,reason="Independent actual policy source membership"});
        using var upload=new HttpRequestMessage(HttpMethod.Post,"/api/engineering/document-files")
        {Content=new ByteArrayContent(Encoding.UTF8.GetBytes("Explicit example organization policy chosen for this acceptance case; no normative conformity or physical occurrence inferred."))};
        upload.Headers.Add("Idempotency-Key","policy-file-"+Interlocked.Increment(ref key));upload.Headers.Add("X-File-Name","policy.txt");
        using var response=await plant.Engineer.SendAsync(upload);var text=await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode,text);using var file=JsonDocument.Parse(text);var sha=file.RootElement.GetProperty("entityId").GetString();
        await plant.Ok(plant.Engineer,"/api/engineering/documents",new{number=documentNumber,title="Explicit organization operational controls",type="Procedure",authority="Internal",programId});
        await plant.Ok(plant.Engineer,$"/api/engineering/documents/{documentNumber}/revisions",new{revision="A",fileSha256=sha,fileName="policy.txt"});
        await plant.Ok(plant.Engineer,$"/api/engineering/documents/{documentNumber}/revisions/A/submit",new{});
        await plant.Ok(plant.Approver,$"/api/engineering/documents/{documentNumber}/revisions/A/approve",new{});
        await plant.Ok(plant.Approver,$"/api/engineering/documents/{documentNumber}/revisions/A/release",new{reason="Independent controlled procedure release"});
        await plant.Ok(plant.Engineer,"/api/quality/qms/records",new{code=policyCode,kind="Policy",facilityCode=facility,programId,markings="None",
            content=new QmsContent(Policy:policy,Documents:[new(documentNumber,"A")]),reason="Human-defined controls for this explicit example organization"});
        await EffectiveAsync(plant,"/api/quality/qms/records/"+policyCode+"/revisions/1");
        await plant.Ok(plant.Engineer,"/api/quality/qms/adoptions",new{code=adoptionCode,facilityCode=facility,programId,markings="None",
            content=new QmsPolicyAdoptionContent(purpose,policyCode,1,selector,process,riskClassCode,riskBasis,"Leadership explicitly adopted this exact policy for the actual source selector"),reason="Explicit governed adoption"});
        await EffectiveAsync(plant,"/api/quality/qms/adoptions/"+adoptionCode+"/revisions/1");
    }
    public static async Task EffectiveAsync(SlittingPlant.Plant plant,string path,int firstVersion=1)
    {
        object Decision(int version)=>new{expectedVersion=version,note="Reviewed actual policy, source scope, resource needs and human applicability basis",reason="Independent explicit organization approval"};
        await plant.Ok(plant.Engineer,path+"/submit",Decision(firstVersion));
        await plant.Ok(plant.Approver,path+"/review",Decision(firstVersion+1));
        await plant.Ok(plant.Approver,path+"/approve",Decision(firstVersion+2));
        await plant.Ok(plant.Approver,path+"/effective",Decision(firstVersion+3));
    }
}
