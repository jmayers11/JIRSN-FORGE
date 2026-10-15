using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Planning.Analyst;
using Forge.Planning.Reporting;
using Forge.Quality.Audit;
using Forge.Quality.Contracts;
using Forge.Quality.Planning;

namespace Forge.Host.Analyst;

/// <summary>Deterministic Quality Analyst answers over the existing authorized QMS read services.</summary>
public sealed class QmsAnalystService(
    IForgeAuthorizer authorizer,
    QmsAuditService audits,
    QmsCapaService capas,
    QmsPlanningService planning,
    IAnalystNarrator narrator,
    TimeProvider time)
{
    public static readonly IReadOnlyList<string> Suggestions =
    [
        "Which audits have open findings?",
        "Which corrective actions are overdue?",
        "Which quality objectives are below target?",
    ];

    public Interpretation? Interpret(ForgePrincipal principal,string question)
    {
        var intent=IntentOf(question);
        if(intent is null)return null;
        Demand(principal,Permissions.AnalystUse,ResourceRef.Of("Analyst"));
        var understood=intent switch
        {
            AnalystIntent.OpenAuditFindings=>"Open findings in current authorized QMS audit records",
            AnalystIntent.OverdueCorrectiveActions=>"Overdue, unverified actions in current authorized QMS corrective-action records",
            AnalystIntent.QualityObjectivesBelowTarget=>"Completed quality-objective periods below their approved target using complete authorized source cohorts",
            _=>throw new InvalidOperationException("Unsupported QMS Analyst intent."),
        };
        return new(question,intent.Value,understood,AnalystConfidence.High,null,null,null,null,null,null,null,null,
            null,null,null,null,[],"Quality records","QMS");
    }

    public async Task<AnalystAnswer?> AskAsync(ForgePrincipal principal,string question,bool narrate,CancellationToken ct)
    {
        var interpretation=Interpret(principal,question);
        if(interpretation is null)return null;
        if(!authorizer.Authorize(principal,Permissions.QualityRead,ResourceRef.Of("Quality")).IsPermitted)
            return NotAuthorized(interpretation);

        AnalystAnswer answer;
        try
        {
            answer=interpretation.Intent switch
            {
                AnalystIntent.OpenAuditFindings=>await OpenAuditFindingsAsync(principal,interpretation,ct),
                AnalystIntent.OverdueCorrectiveActions=>await OverdueActionsAsync(principal,interpretation,ct),
                AnalystIntent.QualityObjectivesBelowTarget=>await ObjectivesBelowTargetAsync(principal,interpretation,ct),
                _=>throw new InvalidOperationException("Unsupported QMS Analyst intent."),
            };
        }
        catch(CommandRejectedException e) when(e.Code=="qms_not_configured")
        {
            answer=new(interpretation,"The QMS organization is not configured, so Forge cannot query these records.",[],null,null,
                ["Configure an explicit QMS organization before using the governed Quality Analyst queries."],false,[]);
        }

        if(narrate&&narrator.Enabled&&!answer.NotAuthorized)
        {
            var facts=CanonicalJson.Serialize(new
            {
                answer.Headline,
                Facts=answer.Facts.Select(f=>f.Text),
                answer.Notes,
                Columns=answer.Columns?.Select(c=>c.Title),
                Rows=answer.Rows?.Take(25).Select(r=>r.Cells),
            });
            var narrative=await narrator.NarrateAsync(question,facts,ct);
            if(!string.IsNullOrWhiteSpace(narrative))answer=answer with{Narrative=narrative.Trim(),NarrativeProvider=narrator.Name};
        }
        return answer;
    }

    private async Task<AnalystAnswer> OpenAuditFindingsAsync(ForgePrincipal principal,Interpretation i,CancellationToken ct)
    {
        var items=await audits.OpenFindingsForAnalystAsync(principal,100,ct);
        var rows=items.Select(x=>new ReportRow(
            [x.AuditId.ToString("D"),x.CriterionCode,x.Kind.ToString(),x.Statement,x.State.ToString(),x.RecordedAt],
            ["qms-audit:"+x.AuditId.ToString("D"),"qms-audit-finding:"+x.FindingId.ToString("D")])).ToArray();
        var facts=items.Select(x=>new AnalystFact(
            $"Audit {x.AuditId:D}, criterion {x.CriterionCode}: {x.Statement} ({x.Kind}; {x.State}).",
            "qms-audit-finding:"+x.FindingId.ToString("D"),"warn")).ToArray();
        var headline=items.Count==0
            ?"No open audit findings were found in the records available to you."
            :$"{items.Count} open audit finding(s) were found in the records available to you.";
        return new(i,headline,facts,
            [new("audit","Audit",ReportColumnKind.Text),new("criterion","Criterion",ReportColumnKind.Text),
             new("kind","Finding type",ReportColumnKind.Text),new("statement","Finding",ReportColumnKind.Text),
             new("state","State",ReportColumnKind.Text),new("recorded","Recorded",ReportColumnKind.DateTime)],rows,
            ["Only current-generation findings from audits you can currently access are included.",
             "A finding is an attributed audit record; it does not by itself establish product nonconformity."],false,["Governed QMS audit records"]);
    }

    private async Task<AnalystAnswer> OverdueActionsAsync(ForgePrincipal principal,Interpretation i,CancellationToken ct)
    {
        var asOf=time.GetUtcNow();
        var items=await capas.OverdueActionsForAnalystAsync(principal,asOf,100,ct);
        var rows=items.Select(x=>new ReportRow(
            [x.CapaId,x.ActionId.ToString("D"),x.Kind.ToString(),x.Description,x.State.ToString(),x.DueAt],
            ["capa:"+x.CapaId,"capa-action:"+x.ActionId.ToString("D")])).ToArray();
        var facts=items.Select(x=>new AnalystFact(
            $"CAPA {x.CapaId}, {x.Kind} action {x.Description}: {x.State}, due {x.DueAt:yyyy-MM-dd} UTC.",
            "capa-action:"+x.ActionId.ToString("D"),"bad")).ToArray();
        var headline=items.Count==0
            ?"No overdue, unverified corrective actions were found in the records available to you."
            :$"{items.Count} overdue, unverified corrective action(s) were found in the records available to you.";
        return new(i,headline,facts,
            [new("capa","CAPA",ReportColumnKind.Text),new("action","Action",ReportColumnKind.Text),
             new("kind","Action type",ReportColumnKind.Text),new("description","Description",ReportColumnKind.Text),
             new("state","State",ReportColumnKind.Text),new("due","Due",ReportColumnKind.DateTime)],rows,
            [$"Overdue is evaluated at {asOf:O}; verified and withdrawn actions are excluded.",
             "Only action records in current CAPA generations that you can currently access are included."],false,["Governed QMS corrective-action records"]);
    }

    private async Task<AnalystAnswer> ObjectivesBelowTargetAsync(ForgePrincipal principal,Interpretation i,CancellationToken ct)
    {
        var items=await planning.ObjectivesBelowTargetAsync(principal,100,ct);
        var rows=items.Select(x=>new ReportRow(
            [x.ObjectiveCode,x.Title,x.MetricCode,x.MetricRevision,x.Actual,x.Unit,
             x.Comparison.ToString(),x.Target,x.UpperTarget,x.Tolerance,x.From,x.To,x.Measurement.Basis],
            ["qms-objective:"+x.ObjectiveCode+":"+x.ObjectiveRevision,"qms-metric:"+x.MetricCode+":"+x.MetricRevision]))
            .ToArray();
        var facts=items.Select(x=>new AnalystFact(
            $"Objective {x.ObjectiveCode}: {x.Title}; actual {x.Actual} {x.Unit}, target {x.Comparison} {x.Target}"
            +(x.UpperTarget is{} upper?$"–{upper}":string.Empty)+$" ± {x.Tolerance}; complete period {x.From:yyyy-MM-dd} to {x.To:yyyy-MM-dd} UTC.",
            "qms-objective:"+x.ObjectiveCode+":"+x.ObjectiveRevision,"bad")).ToArray();
        var headline=items.Count==0
            ?"No completed objective period with a complete authorized cohort was identified below target. This does not establish that other objectives are on target."
            :$"{items.Count} completed quality objective(s) are below their approved target based on complete authorized source cohorts.";
        return new(i,headline,facts,
            [new("objective","Objective",ReportColumnKind.Text),new("title","Title",ReportColumnKind.Text),
             new("metric","Metric",ReportColumnKind.Text),new("revision","Metric revision",ReportColumnKind.Number),
             new("actual","Actual",ReportColumnKind.Number),new("unit","Unit",ReportColumnKind.Text),
             new("comparison","Comparison",ReportColumnKind.Text),new("target","Target",ReportColumnKind.Number),
             new("upperTarget","Upper target",ReportColumnKind.Number),new("tolerance","Tolerance",ReportColumnKind.Number),
             new("from","From",ReportColumnKind.DateTime),new("to","To",ReportColumnKind.DateTime),
             new("basis","Cohort basis",ReportColumnKind.Text)],rows,
            ["Only completed periods with complete, currently authorized recorded cohorts are compared. Human-assessed, incomplete, out-of-scope, and in-progress periods are not labeled below target.",
             "A below-target result is a calculated comparison to the organization's approved objective; it is not an AI conclusion or conformity assessment."],
            false,["Governed objective and metric definitions","Recorded production evidence"]);
    }

    private static AnalystAnswer NotAuthorized(Interpretation i)=>new(i,
        "Not authorized. You do not have access to the Quality information this question needs.",[],null,null,
        ["Forge Analyst answers with the same access you have on Forge's Quality screens."],true,[]);

    private static AnalystIntent? IntentOf(string question)
    {
        if(string.IsNullOrWhiteSpace(question)||question.Length>500)return null;
        var text=question.Trim().ToLowerInvariant();
        if(text.Contains("audit",StringComparison.Ordinal)&&text.Contains("finding",StringComparison.Ordinal)
            &&(text.Contains("open",StringComparison.Ordinal)||text.Contains("unresolved",StringComparison.Ordinal)))
            return AnalystIntent.OpenAuditFindings;
        if(text.Contains("overdue",StringComparison.Ordinal)
            &&(text.Contains("corrective action",StringComparison.Ordinal)||text.Contains("capa",StringComparison.Ordinal)))
            return AnalystIntent.OverdueCorrectiveActions;
        if(text.Contains("objective",StringComparison.Ordinal)
            &&(text.Contains("below target",StringComparison.Ordinal)||text.Contains("under target",StringComparison.Ordinal)
                ||text.Contains("missed target",StringComparison.Ordinal)))
            return AnalystIntent.QualityObjectivesBelowTarget;
        return null;
    }

    private void Demand(ForgePrincipal principal,string permission,ResourceRef resource)
    {
        var decision=authorizer.Authorize(principal,permission,resource);
        if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision);
    }
}
