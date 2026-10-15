using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Investigations;

namespace Forge.Host.Quality;

/// <summary>Retains only the exact authorized projected owner values used for the evidence fingerprint; partial/gaps are explicit.</summary>
public sealed class InvestigationPlanningEvidenceResolver(InvestigationReadService reads,ITraceInvestigationResolver sources,TimeProvider time):IQmsPlanningOwnerEvidenceResolver
{
 public bool Owns(string context,string type)=>context=="Quality"&&type is "MeasurementFinding" or "TraceInvestigation" or "TraceInvestigationSnapshot" or "TraceAssessment";
 public async Task<QmsPlanningSourceSnapshot> ResolveAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsPlanningEvidenceReference reference,bool lockSource,
  Func<QmsPlanningEvidenceReference,bool,Task<QmsPlanningSourceSnapshot>> resolveSource,CancellationToken ct)
 {
  object value;DateTimeOffset? created=null,approved=null,closed=null;var authors=new List<string>();var bindings=new List<TraceSourceReference>();var lookup=uow.Store<IQualityInvestigationLookup>();
  if(reference.Type=="MeasurementFinding")
  {
   var id=Number(reference.Key);if(lockSource)await uow.Store<IQualityInvestigationSourceLock>().LockFindingAsync(id,ct);
   if(!await reads.CanReadFindingAsync(uow,principal,id,ct))throw Missing();var finding=await lookup.FindFindingAsync(id,ct)??throw Missing();
   value=finding;created=finding.History[0].At;approved=finding.History.FirstOrDefault(change=>change.Action=="APPROVE")?.At;closed=finding.State==MeasurementFindingState.Withdrawn?finding.History[^1].At:null;authors.Add(finding.CreatedBy);authors.AddRange(finding.History.Select(change=>change.Actor));
   var contexts=await reads.ResourcesForFindingAsync(uow,id,ct)??throw Missing();bindings.Add(new(reference.Type,reference.Key,Permissions.QualityRead,contexts));bindings.AddRange(finding.Sources.Select(source=>new TraceSourceReference(source.Resource.Type,source.Resource.Id??throw Missing(),source.Permission,[source.Resource],false)));
  }
  else
  {
   long id;int? number=null;TraceCandidateAssessment? assessment=null;
   if(reference.Type=="TraceAssessment"){assessment=await lookup.FindAssessmentAsync(Number(reference.Key),ct)??throw Missing();id=assessment.InvestigationId;number=assessment.SnapshotNumber;}
   else if(reference.Type=="TraceInvestigationSnapshot"){var key=reference.Key.Split('/');if(key.Length!=2||!int.TryParse(key[1],NumberStyles.None,CultureInfo.InvariantCulture,out var parsed)||parsed<=0)throw Missing();id=Number(key[0]);number=parsed;}
   else id=Number(reference.Key);
   if(lockSource)await uow.Store<IQualityInvestigationSourceLock>().LockInvestigationAsync(id,ct);
   var view=await reads.ProjectCaseAsync(uow,principal,id,number,ct);
   if(assessment is not null){if(!await reads.CanReadAssessmentAsync(uow,principal,assessment.AssessmentId,ct))throw Missing();value=assessment;created=assessment.ProposedAt;approved=assessment.ApprovedAt;closed=assessment.State==TraceAssessmentState.Withdrawn?assessment.History[^1].At:null;authors.Add(assessment.ProposedBy);if(assessment.ApprovedBy is{} signer)authors.Add(signer);}
   else{value=view;created=view.Header.CreatedAt;closed=view.Header.State==TraceInvestigationState.Closed?view.Header.History[^1].At:null;authors.AddRange(view.Header.History.Select(change=>change.Actor));authors.AddRange(view.Assessments.SelectMany(assessment=>assessment.History.Select(change=>change.Actor)));}
   bindings.AddRange(view.Header.RootSources);bindings.AddRange(view.Snapshot.Capture.Candidates.SelectMany(candidate=>candidate.Sources));bindings.AddRange(view.Snapshot.Capture.Links.SelectMany(link=>link.Evidence.Sources));
   var scopes=InvestigationService.Resources(bindings,reference.Type,Number(reference.Type=="TraceInvestigationSnapshot"?reference.Key.Split('/')[0]:reference.Key));bindings.Add(new(reference.Type,reference.Key,Permissions.QualityRead,scopes));
  }
  // Current and captured owner permissions remain distinct from action approval; a projected case is never silently a complete KPI population.
  var current=await sources.CurrentContextsAsync(uow,bindings.Where(binding=>binding.Type is not("TraceInvestigation" or "TraceInvestigationSnapshot" or "TraceAssessment")).ToArray(),ct);
  var readSources=current.SelectMany(binding=>binding.Resources.Select(resource=>new QmsPlanningReadSource(binding.ReadPermission,resource))).Concat(bindings.SelectMany(binding=>binding.Resources.Select(resource=>new QmsPlanningReadSource(binding.ReadPermission,resource)))).Distinct().ToArray();
  return new(reference,"Quality",reference.Type,reference.Key,reference.Revision,CanonicalJson.Fingerprint(value),time.GetUtcNow(),created,approved,closed,null,authors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),readSources,false);
 }
 private static long Number(string key)=>long.TryParse(key,NumberStyles.None,CultureInfo.InvariantCulture,out var id)&&id>0?id:throw Missing();
 private static CommandRejectedException Missing()=>new("planning_evidence_unavailable","The exact authorized investigation owner evidence is unavailable.");
}
