using System.Text.Json;
using Forge.Quality.Contracts;
using static Forge.Integration.Tests.QmsPlanningScenario;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class QmsPlanningCaseTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Risk_case_requires_approved_assessment_actual_owned_verified_tasks_and_independent_effectiveness_before_closure()
    {
        await using var f=await CreateAsync(postgres);await DocumentAsync(f,"ACTUAL.SOURCE.BASIS");
        var process=new QmsContent(Process:new("Controlled source selection",["Actual source owner IDs"],["Recorded exact references"],f.Plant.People["eng"],[],["Assigned controller time"],["Wrong revision selection"],[new("Source completeness","Compare exact revision and hash","Engineering immutable source records","On change")]),Documents:[new("ACTUAL.SOURCE.BASIS","A")]);
        await f.Plant.Ok(f.Author,"/api/quality/qms/records",new{code="PROCESS",kind="Process",facilityCode="PLANT1",markings="None",content=process,reason="Actual source-control process definition"});
        var processPath="/api/quality/qms/records/PROCESS/revisions/1";await f.Plant.Ok(f.Author,processPath+"/submit",Decision(1));await f.Plant.Ok(f.Reviewer,processPath+"/review",Decision(2));await f.Plant.Ok(f.Reviewer,processPath+"/approve",Decision(3));await f.Plant.Ok(f.Reviewer,processPath+"/effective",Decision(4));
        await CreateRecordAsync(f,"METHOD",QmsPlanningKind.EvaluationMethod,Method("ACTUAL.SOURCE.BASIS"));await EffectiveAsync(f,"METHOD");
        var risk=new QmsPlanningContent(RiskOpportunity:new(QmsRiskKind.Risk,"Exact controlled-source selection",new("PROCESS",1),"Wrong controlled source revision selected","A decision could cite the wrong authoritative record",new("METHOD",1),f.Plant.People["eng"],"Review and retain each exact source owner ID/revision/hash",f.Time.Now.AddDays(2),"Assigned controller review time","On source change","Every required reference agrees with its actual immutable owner"),Documents:[new("ACTUAL.SOURCE.BASIS","A")]);
        await CreateRecordAsync(f,"RISK",QmsPlanningKind.RiskOpportunity,risk);await EffectiveAsync(f,"RISK");
        var created=await f.Plant.Ok(f.Author,Api+"/cases",new{code="RISK",definitionRevision=1,reason="Actual source-control risk case"});var caseId=created.GetProperty("entityId").GetString();
        await f.Plant.Ok(f.Author,$"{Api}/cases/{caseId}/assess",Decision(1));
        var riskAssessment=await AssessmentAsync(f,caseId!,2,QmsAssessmentKind.RiskEvaluation,new(RiskEvaluation:new("METHOD",1,"Reviewed actual reference identity",null,null,"Action required","Current exact source record and owner evidence","Assigned exact-reference review",f.Time.Now.AddDays(7),"All exact source references match")));
        await f.Plant.Ok(f.Author,$"{Api}/cases/{caseId}/plan",new{expectedVersion=2,assessmentId=riskAssessment,note="Actual approved assessment supports this exact action plan",reason="Governed action planning"});
        var taskResult=await f.Plant.Ok(f.Author,Api+"/tasks",new{code="RISK",definitionRevision=1,title="Record and review the exact original source hash",ownerPersonId=f.Plant.People["eng"],dueAt=f.Time.Now.AddDays(1),resources="Assigned controller review time",completionCriteria="Exact source number/revision/hash is retained and agrees with immutable owner",reason="Actual required source-control task"});var task=Guid.Parse(taskResult.GetProperty("entityId").GetString()!);
        await f.Plant.Ok(f.Author,$"{Api}/cases/{caseId}/implement",Decision(3));
        await RejectedAsync(await f.Plant.Try(f.Author,Api+"/assessments",new{code="RISK",definitionRevision=1,kind="ImplementationVerification",content=new QmsPlanningAssessmentContent(ImplementationVerification:new([task],"Exact references present","Attempt verification before task completion",true,"Not implemented")),evidence=Array.Empty<QmsPlanningEvidenceReference>(),reason="Must not invent implementation"}),"planning_tasks");
        await f.Plant.Ok(f.Author,$"{Api}/tasks/{task}/complete",new{expectedVersion=1,result="Assigned controller reviewed the original exact owner reference",evidence=new[]{new QmsPlanningEvidenceReference("Engineering","DocumentRevision","ACTUAL.SOURCE.BASIS","A")},reason="Actual attributed reference-review completion"});
        await f.Plant.Ok(f.Reviewer,$"{Api}/tasks/{task}/verify",new{expectedVersion=2,result="Independent reviewer compared the retained exact source with its immutable owner",evidence=Array.Empty<QmsPlanningEvidenceReference>(),reason="Independent verification of actual source evidence"});
        await f.Plant.Ok(f.Author,$"{Api}/cases/{caseId}/requestverification",Decision(4));
        await RejectedAsync(await f.Plant.Try(f.Reviewer,$"{Api}/cases/{caseId}/close",new{expectedVersion=5,assessmentId=(Guid?)null,note="Attempt closure without actual effectiveness review",reason="Must not bypass verification/effectiveness"}),"planning_state");
        var implementation=await AssessmentAsync(f,caseId!,5,QmsAssessmentKind.ImplementationVerification,new(ImplementationVerification:new([task],"All declared exact references present","Compared actual retained owner key/revision/hash",true,"No limits identified in this reference check")));
        await f.Plant.Ok(f.Author,$"{Api}/cases/{caseId}/verify",new{expectedVersion=5,assessmentId=implementation,note="Approved implementation assessment covers every independently verified actual task",reason="Actual implementation verified"});
        var effectiveness=await AssessmentAsync(f,caseId!,6,QmsAssessmentKind.EffectivenessReview,new(EffectivenessReview:new(f.Time.Now.AddMinutes(-1),f.Time.Now,"Compare exact recorded owner references","Every required source number/revision/hash agrees","The actual retained references agree with the immutable Engineering source",true,"This decision evaluates recorded reference control only","Continue on-change review")));
        var acceptance=new{expectedVersion=6,assessmentId=effectiveness,note="Independently reviewed actual criteria and recorded effectiveness result",reason="Actual effectiveness acceptance"};
        await RejectedAsync(await f.Plant.Try(f.Author,$"{Api}/cases/{caseId}/accept",acceptance),"separation_of_duties");await f.Plant.Ok(f.Reviewer,$"{Api}/cases/{caseId}/accept",acceptance);
        await f.Plant.Ok(f.Reviewer,$"{Api}/cases/{caseId}/close",acceptance with{expectedVersion=7});
        var work=await ReadAsync(f.Reviewer,Api+"/records/RISK/revisions/1/work");Assert.Equal("Closed",work.GetProperty("cases")[0].GetProperty("state").GetString());Assert.Equal(8,work.GetProperty("cases")[0].GetProperty("version").GetInt32());
        await f.Plant.Ok(f.Author,$"{Api}/cases/{caseId}/reopen",Decision(8));var reopened=await ReadAsync(f.Reviewer,Api+"/records/RISK/revisions/1/work");Assert.Equal("Reopened",reopened.GetProperty("cases")[0].GetProperty("state").GetString());Assert.Equal(9,reopened.GetProperty("cases")[0].GetProperty("history").GetArrayLength());
        Assert.Equal(2,reopened.GetProperty("cases")[0].GetProperty("generation").GetInt32());
        await f.Plant.Ok(f.Author,$"{Api}/cases/{caseId}/assess",Decision(9));
        await RejectedAsync(await f.Plant.Try(f.Author,$"{Api}/cases/{caseId}/plan",new{expectedVersion=10,assessmentId=riskAssessment,note="An earlier approved assessment cannot close the reopened context",reason="Require a fresh current-generation decision"}),"planning_case_context");
    }

    [Fact]
    public async Task Controlled_QMS_change_links_actual_engineering_change_and_requires_independent_impact_implementation_and_effectiveness()
    {
        await using var f = await CreateAsync(postgres, engineered: true);
        var process = new QmsContent(Process: new("Controlled change review", ["Approved change request"], ["Reviewed and verified implementation"],
            f.Plant.People["eng"], [], ["Assigned implementation owner"], ["Unreviewed process change"],
            [new("Current process revision", "Compare actual approved revision and linked change", "Engineering change and controlled process owner records", "On change")]));
        await f.Plant.Ok(f.Author, "/api/quality/qms/records", new { code = "CHANGE-PROCESS", kind = "Process", facilityCode = "PLANT1", markings = "None", content = process, reason = "Actual process definition for controlled QMS change" });
        var processPath = "/api/quality/qms/records/CHANGE-PROCESS/revisions/1";
        await f.Plant.Ok(f.Author, processPath + "/submit", Decision(1));
        await f.Plant.Ok(f.Reviewer, processPath + "/review", Decision(2));
        await f.Plant.Ok(f.Reviewer, processPath + "/approve", Decision(3));
        await f.Plant.Ok(f.Reviewer, processPath + "/effective", Decision(4));

        var engineeringChange = await EngineeringChangeAsync(f, "Controlled source selection improvement",
            "Recorded process review identified an outdated selection step");
        var origin = new QmsPlanningEvidenceReference("Engineering", "EngineeringChange", engineeringChange);
        var definition = new QmsPlanningContent(Change: new(
            "Revise the controlled source selection workflow", "An actual engineering change requires coordinated QMS process controls",
            "Implementation is verified against the released source and independently reviewed effectiveness criteria",
            [new("CHANGE-PROCESS", 1)], [], "One assigned controller and reviewer", "Current process owner remains accountable",
            "QMS review and approval plus Engineering's existing change authority", "Update controlled instructions and train the assigned roles",
            "The approved procedure and actual change source are linked and current", "A later independent review confirms the exact procedure and change remain aligned",
            f.Plant.People["eng"], f.Time.Now.AddDays(2), [origin]));
        await CreateRecordAsync(f, "CONTROLLED-CHANGE", QmsPlanningKind.Change, definition);
        await EffectiveAsync(f, "CONTROLLED-CHANGE");
        var planned = await ReadAsync(f.Reviewer, Api + "/records/CONTROLLED-CHANGE/revisions/1");
        Assert.Contains(planned.GetProperty("sources").EnumerateArray(), source =>
            source.GetProperty("ownerContext").GetString() == "Engineering" && source.GetProperty("ownerType").GetString() == "EngineeringChange"
            && source.GetProperty("ownerId").GetString() == engineeringChange);

        var created = await f.Plant.Ok(f.Author, Api + "/cases", new { code = "CONTROLLED-CHANGE", definitionRevision = 1, reason = "Actual QMS change implementation case" });
        var caseId = created.GetProperty("entityId").GetString()!;
        var evidence = new[] { origin };
        await f.Plant.Ok(f.Author, $"{Api}/cases/{caseId}/assess", Decision(1));
        var impact = await AssessmentForAsync(f, "CONTROLLED-CHANGE", 1, caseId, 2, QmsAssessmentKind.ChangeImpact,
            new(ChangeImpact: new("The released process definition applies to item revision selection", "The linked actual Engineering change remains independently governed",
                "Assigned owner and review time are recorded", "The process owner retains responsibility", "Original Engineering history and QMS revisions remain immutable",
                "The organization-selected QMS reviewer independently approved this impact")), evidence);
        await f.Plant.Ok(f.Author, $"{Api}/cases/{caseId}/plan", new { expectedVersion = 2, assessmentId = impact, note = "Approved impact supports the recorded implementation task", reason = "Plan actual controlled implementation" });
        var taskResult = await f.Plant.Ok(f.Author, Api + "/tasks", new
        {
            code = "CONTROLLED-CHANGE", definitionRevision = 1, title = "Verify the released process and linked engineering change",
            ownerPersonId = f.Plant.People["eng"], dueAt = f.Time.Now.AddDays(1), resources = "Assigned controller review time",
            completionCriteria = "Exact effective process revision and current Engineering change evidence are recorded", reason = "Actual controlled change implementation task"
        });
        var task = Guid.Parse(taskResult.GetProperty("entityId").GetString()!);
        await f.Plant.Ok(f.Author, $"{Api}/cases/{caseId}/implement", Decision(3));
        await f.Plant.Ok(f.Author, $"{Api}/tasks/{task}/complete", new { expectedVersion = 1, result = "Actual process and change references were compared and recorded", evidence, reason = "Attribute actual implementation work" });
        await f.Plant.Ok(f.Reviewer, $"{Api}/tasks/{task}/verify", new { expectedVersion = 2, result = "Independent reviewer verified the linked current process and Engineering change", evidence, reason = "Independent implementation task verification" });
        await f.Plant.Ok(f.Author, $"{Api}/cases/{caseId}/requestverification", Decision(4));
        var implementation = await AssessmentForAsync(f, "CONTROLLED-CHANGE", 1, caseId, 5, QmsAssessmentKind.ImplementationVerification,
            new(ImplementationVerification: new([task], "The effective process and exact Engineering source remain linked", "The verified task records the required evidence", true, "This verifies only the recorded procedure/change link")), evidence);
        await f.Plant.Ok(f.Author, $"{Api}/cases/{caseId}/verify", new { expectedVersion = 5, assessmentId = implementation, note = "Independent implementation verification is approved", reason = "Verify actual QMS change implementation" });
        var effectiveness = await AssessmentForAsync(f, "CONTROLLED-CHANGE", 1, caseId, 6, QmsAssessmentKind.EffectivenessReview,
            new(EffectivenessReview: new(f.Time.Now.AddMinutes(-1), f.Time.Now, "Compare the effective process to the exact Engineering change source",
                "The two current controlled records state the approved selection process", "Independent review found the recorded source and process aligned", true,
                "This result covers the recorded source relationship only", "Continue the owner-defined on-change review")), evidence);
        await RejectedAsync(await f.Plant.Try(f.Author, $"{Api}/cases/{caseId}/accept", new { expectedVersion = 6, assessmentId = effectiveness, note = "Author cannot independently accept", reason = "Preserve independent approval" }), "separation_of_duties");
        await f.Plant.Ok(f.Reviewer, $"{Api}/cases/{caseId}/accept", new { expectedVersion = 6, assessmentId = effectiveness, note = "Independent reviewer accepts the actual effective result", reason = "Approve recorded effectiveness" });
        await f.Plant.Ok(f.Reviewer, $"{Api}/cases/{caseId}/close", new { expectedVersion = 7, assessmentId = effectiveness, note = "All actual tasks and effectiveness evidence are reviewed", reason = "Close verified QMS change case" });
        var work = await ReadAsync(f.Reviewer, Api + "/records/CONTROLLED-CHANGE/revisions/1/work");
        Assert.Equal("Closed", work.GetProperty("cases")[0].GetProperty("state").GetString());
        Assert.Contains(work.GetProperty("assessments").EnumerateArray(), row => row.GetProperty("kind").GetString() == "ChangeImpact");
        Assert.Contains(work.GetProperty("assessments").EnumerateArray(), row => row.GetProperty("kind").GetString() == "ImplementationVerification");
        Assert.Contains(work.GetProperty("assessments").EnumerateArray(), row => row.GetProperty("kind").GetString() == "EffectivenessReview");
    }

    [Fact]
    public async Task Improvement_case_links_actual_origin_and_preserves_unmeasured_before_after_without_inventing_benefit()
    {
        await using var f = await CreateAsync(postgres, engineered: true);
        var engineeringChange = await EngineeringChangeAsync(f, "Process-selection improvement proposal",
            "A recorded process review proposed clarifying the material-selection step");
        var origin = new QmsPlanningEvidenceReference("Engineering", "EngineeringChange", engineeringChange);
        var improvement = new QmsPlanningContent(Improvement: new(
            "Clarify the material-selection step", "The recorded Engineering change identifies a process-selection opportunity",
            "Reduce ambiguity for the next process owner", f.Plant.People["eng"],
            "Compare the approved process and actual Engineering change", "Confirm the controlled records agree and identify comparable measures",
            "Update and verify the exact process source", "Assigned process owner and independent reviewer", f.Time.Now.AddDays(2),
            "Record a before/after measure only when an actual comparable source is available", [origin]));
        await CreateRecordAsync(f, "PROCESS-IMPROVEMENT", QmsPlanningKind.Improvement, improvement);
        await EffectiveAsync(f, "PROCESS-IMPROVEMENT");
        var created = await f.Plant.Ok(f.Author, Api + "/cases", new { code = "PROCESS-IMPROVEMENT", definitionRevision = 1, reason = "Actual improvement register case" });
        var caseId = created.GetProperty("entityId").GetString()!;
        await f.Plant.Ok(f.Author, $"{Api}/cases/{caseId}/assess", Decision(1));
        await RejectedAsync(await f.Plant.Try(f.Author, Api + "/assessments", new
        {
            code = "PROCESS-IMPROVEMENT", definitionRevision = 1, kind = "ImprovementEvaluation", caseId = Guid.Parse(caseId), expectedCaseVersion = 2,
            content = new QmsPlanningAssessmentContent(ImprovementEvaluation: new("Review", "Attempt to claim an unsupported result", 12m, null,
                "count", "Claim a reduction", "No comparable actual source or valid baseline exists; do not infer a numeric result")),
            evidence = new[] { origin }, reason = "An unsupported before/after pair must be rejected"
        }), "planning_measurement");
        var improvementAssessment = await AssessmentForAsync(f, "PROCESS-IMPROVEMENT", 1, caseId, 2, QmsAssessmentKind.ImprovementEvaluation,
            new(ImprovementEvaluation: new("Qualitative improvement candidate", "The exact controlled change and owner source were reviewed",
                null, null, "not measured", "No comparable actual performance source was captured; no numerical benefit is claimed",
                "Before/after measurement remains unavailable; record a numeric result only after a comparable source exists")), [origin]);
        await f.Plant.Ok(f.Author, $"{Api}/cases/{caseId}/plan", new { expectedVersion = 2, assessmentId = improvementAssessment, note = "Approved evaluation supports a bounded owner task", reason = "Plan the recorded process improvement" });
        var taskResult = await f.Plant.Ok(f.Author, Api + "/tasks", new
        {
            code = "PROCESS-IMPROVEMENT", definitionRevision = 1, title = "Compare the actual process and Engineering change",
            ownerPersonId = f.Plant.People["eng"], dueAt = f.Time.Now.AddDays(1), resources = "Assigned process owner time",
            completionCriteria = "Exact owner records compared and result recorded", reason = "Actual improvement implementation task"
        });
        var task = Guid.Parse(taskResult.GetProperty("entityId").GetString()!);
        await f.Plant.Ok(f.Author, $"{Api}/cases/{caseId}/implement", Decision(3));
        await f.Plant.Ok(f.Author, $"{Api}/tasks/{task}/complete", new { expectedVersion = 1, result = "Exact process source compared to the linked Engineering change; no numeric measure was available", evidence = new[] { origin }, reason = "Attribute actual improvement work" });
        await f.Plant.Ok(f.Reviewer, $"{Api}/tasks/{task}/verify", new { expectedVersion = 2, result = "Independent reviewer verified the actual owner comparison and its limitation", evidence = new[] { origin }, reason = "Independently verify the actual improvement task" });
        await f.Plant.Ok(f.Author, $"{Api}/cases/{caseId}/requestverification", Decision(4));
        var implementation = await AssessmentForAsync(f, "PROCESS-IMPROVEMENT", 1, caseId, 5, QmsAssessmentKind.ImplementationVerification,
            new(ImplementationVerification: new([task], "The exact owner comparison is recorded", "The assigned task and independent verification are complete", true,
                "This verifies recorded process comparison only; it is not a numeric performance result")), [origin]);
        await f.Plant.Ok(f.Author, $"{Api}/cases/{caseId}/verify", new { expectedVersion = 5, assessmentId = implementation, note = "The actual improvement task is verified", reason = "Verify bounded improvement implementation" });
        var effectiveness = await AssessmentForAsync(f, "PROCESS-IMPROVEMENT", 1, caseId, 6, QmsAssessmentKind.EffectivenessReview,
            new(EffectivenessReview: new(f.Time.Now.AddMinutes(-1), f.Time.Now, "Compare the current process with the actual change source",
                "The approved controlled process and source agree", "Independent qualitative review found the recorded process step clarified",
                true, "No quantitative before/after claim is supported", "Measure comparable performance when an approved source is available")), [origin]);
        await f.Plant.Ok(f.Reviewer, $"{Api}/cases/{caseId}/accept", new { expectedVersion = 6, assessmentId = effectiveness, note = "Accept the qualitative effectiveness result with its explicit limitation", reason = "Independently accept actual improvement review" });
        await f.Plant.Ok(f.Reviewer, $"{Api}/cases/{caseId}/close", new { expectedVersion = 7, assessmentId = effectiveness, note = "All actual tasks and the bounded effectiveness review are complete", reason = "Close the actual improvement case" });
        var work = await ReadAsync(f.Reviewer, Api + "/records/PROCESS-IMPROVEMENT/revisions/1/work");
        var saved = Assert.Single(work.GetProperty("assessments").EnumerateArray(), row => row.GetProperty("kind").GetString() == "ImprovementEvaluation");
        var evaluation = saved.GetProperty("content").GetProperty("improvementEvaluation");
        Assert.Equal(JsonValueKind.Null, evaluation.GetProperty("before").ValueKind);
        Assert.Equal(JsonValueKind.Null, evaluation.GetProperty("after").ValueKind);
        Assert.Equal("Closed", work.GetProperty("cases")[0].GetProperty("state").GetString());
    }

    private static async Task<string> EngineeringChangeAsync(Flow f, string title, string changeReason)
    {
        await f.Plant.Ok(f.Author, "/api/engineering/items/ROLL-100/revisions", new { revision = "B" });
        var number = (await f.Plant.Ok(f.Author, "/api/engineering/changes", new
        {
            title, @class = "Minor", type = "Process", changeReason, reason = "Actual Engineering source for the QMS workflow"
        })).GetProperty("entityId").GetString()!;
        await f.Plant.Ok(f.Author, $"/api/engineering/changes/{number}/lines", new
        {
            kind = "ItemRevision", subject = "ROLL-100", toRevision = "B", note = "Actual controlled revision attached to the change", reason = "Record the exact Engineering source"
        });
        return number;
    }

    private static async Task<Guid> AssessmentAsync(Flow f,string caseId,int caseVersion,QmsAssessmentKind kind,QmsPlanningAssessmentContent content)
        => await AssessmentForAsync(f, "RISK", 1, caseId, caseVersion, kind, content,
            [new QmsPlanningEvidenceReference("Engineering", "DocumentRevision", "ACTUAL.SOURCE.BASIS", "A")]);

    private static async Task<Guid> AssessmentForAsync(Flow f, string code, int revision, string caseId, int caseVersion,
        QmsAssessmentKind kind, QmsPlanningAssessmentContent content, IReadOnlyList<QmsPlanningEvidenceReference> evidence)
    {
        var created=await f.Plant.Ok(f.Author,Api+"/assessments",new{code,definitionRevision=revision,kind=kind.ToString(),caseId=Guid.Parse(caseId),expectedCaseVersion=caseVersion,content,evidence,reason="Actual attributed human assessment with exact owner evidence"});var id=Guid.Parse(created.GetProperty("entityId").GetString()!);
        await f.Plant.Ok(f.Author,$"{Api}/assessments/{id}/submit",Decision(1));await f.Plant.Ok(f.Reviewer,$"{Api}/assessments/{id}/review",Decision(2));await f.Plant.Ok(f.Reviewer,$"{Api}/assessments/{id}/approve",Decision(3));return id;
    }
}

