using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;

namespace Forge.Quality.Investigations;

public sealed class InvestigationReadService(IKernelUnitOfWorkFactory factory,ITraceInvestigationResolver resolver,IForgeAuthorizer authorizer,QmsOptions options)
{
 public async Task<IReadOnlyList<MeasurementResourceFinding>> FindingsAsync(ForgePrincipal principal,CancellationToken ct)
 {await using var uow=await factory.BeginSnapshotAsync(ct);var visible=new List<MeasurementResourceFinding>();foreach(var finding in await uow.Store<IQualityInvestigationLookup>().ListFindingsAsync(ct))if(await CanReadFindingAsync(uow,principal,finding.FindingId,ct))visible.Add(finding);return visible;}
 public async Task<MeasurementResourceFinding> FindingAsync(ForgePrincipal principal,long id,CancellationToken ct)
 {await using var uow=await factory.BeginSnapshotAsync(ct);if(!await CanReadFindingAsync(uow,principal,id,ct))throw Missing();return await uow.Store<IQualityInvestigationLookup>().FindFindingAsync(id,ct)??throw Missing();}
 public async Task<bool> CanReadFindingAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long id,CancellationToken ct)
 {
  try{principal=await CurrentAsync(uow,principal,ct);var finding=await uow.Store<IQualityInvestigationLookup>().FindFindingAsync(id,ct);if(finding is null||!options.Configured||finding.OrganizationCode!=options.OrganizationCode)return false;foreach(var source in finding.Sources)Permit(principal,source.Permission,source.Resource);var current=await resolver.FindingSourcesAsync(uow,principal,finding.Content,ct);foreach(var resource in InvestigationService.Resources(current,"MeasurementFinding",id))Permit(principal,Permissions.QualityRead,resource);foreach(var source in finding.Sources)Permit(principal,Permissions.QualityRead,source.Resource with{Type="MeasurementFinding",Id=id.ToString(System.Globalization.CultureInfo.InvariantCulture)});return true;}catch(ForgeAuthorizationException){return false;}catch(CommandRejectedException){return false;}
 }
 public async Task<IReadOnlyList<TraceabilityInvestigation>> CasesAsync(ForgePrincipal principal,CancellationToken ct)
 {await using var uow=await factory.BeginSnapshotAsync(ct);var visible=new List<TraceabilityInvestigation>();foreach(var header in await uow.Store<IQualityInvestigationLookup>().ListInvestigationsAsync(ct))if(await CanReadCaseAsync(uow,principal,header.InvestigationId,ct)){var view=await ReadAsync(uow,principal,header,header.LatestSnapshot,ct);visible.Add(view.Header);}return visible;}
 public async Task<TraceInvestigationView> ReadAsync(ForgePrincipal principal,long id,int? snapshot,CancellationToken ct)
 {await using var uow=await factory.BeginSnapshotAsync(ct);if(!await CanReadCaseAsync(uow,principal,id,ct))throw Missing();var header=await uow.Store<IQualityInvestigationLookup>().FindInvestigationAsync(id,ct)??throw Missing();return await ReadAsync(uow,principal,header,snapshot??header.LatestSnapshot,ct);}
 public async Task<TraceInvestigationView> ProjectCaseAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long id,int? snapshot,CancellationToken ct)
 {if(!await CanReadCaseAsync(uow,principal,id,ct))throw Missing();var header=await uow.Store<IQualityInvestigationLookup>().FindInvestigationAsync(id,ct)??throw Missing();return await ReadAsync(uow,principal,header,snapshot??header.LatestSnapshot,ct);}
 public async Task<bool> CanReadCaseAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long id,CancellationToken ct)
 {try{principal=await CurrentAsync(uow,principal,ct);var header=await uow.Store<IQualityInvestigationLookup>().FindInvestigationAsync(id,ct);if(header is null||!options.Configured||header.OrganizationCode!=options.OrganizationCode)return false;await resolver.RequireCurrentReadsAsync(uow,principal,header.RootSources,ct);var current=await resolver.CurrentContextsAsync(uow,header.RootSources,ct);foreach(var resource in InvestigationService.Resources(current,"TraceInvestigation",id))Permit(principal,Permissions.QualityRead,resource);return true;}catch(ForgeAuthorizationException){return false;}catch(CommandRejectedException){return false;}}
 /// <summary>Raw retained human narratives/audit/evidence require every recorded candidate; a partial API projection does not authorize old raw bytes.</summary>
 public async Task<bool> CanReadWholeCaseAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long id,int? number,CancellationToken ct)
 {
  if(!await CanReadCaseAsync(uow,principal,id,ct))return false;var header=await uow.Store<IQualityInvestigationLookup>().FindInvestigationAsync(id,ct);if(header is null)return false;var snapshot=await uow.Store<IQualityInvestigationLookup>().FindSnapshotAsync(id,number??header.LatestSnapshot,ct);if(snapshot is null)return false;
  foreach(var candidate in snapshot.Capture.Candidates)if(!await CanReadCandidateAsync(uow,principal,candidate,ct))return false;
  foreach(var link in snapshot.Capture.Links)if(!await resolver.CanReadAsync(uow,principal,link.Evidence.Sources,ct))return false;
  foreach(var link in (await uow.Store<IQualityInvestigationLookup>().HoldLinksAsync(id,ct)).Where(link=>link.SnapshotNumber==snapshot.Number)){var candidate=snapshot.Capture.Candidates.SingleOrDefault(candidate=>candidate.Key==link.CandidateKey);if(candidate is null)return false;var hold=new TraceSourceReference("Hold",link.HoldId.ToString(System.Globalization.CultureInfo.InvariantCulture),Permissions.HoldsRead,candidate.ActionResources.Select(resource=>resource with{Type="Hold",Id=link.HoldId.ToString(System.Globalization.CultureInfo.InvariantCulture)}).ToArray());if(!await resolver.CanReadAsync(uow,principal,[hold],ct))return false;}
  return true;
 }
 public async Task<IReadOnlyList<ResourceRef>?> ResourcesForFindingAsync(IKernelUnitOfWork uow,long id,CancellationToken ct)
 {var finding=await uow.Store<IQualityInvestigationLookup>().FindFindingAsync(id,ct);if(finding is null||!options.Configured||finding.OrganizationCode!=options.OrganizationCode)return null;var refs=finding.Sources.Select(source=>new TraceSourceReference(source.Resource.Type,source.Resource.Id??throw Missing(),source.Permission,[source.Resource])).ToArray();return InvestigationService.Resources(await resolver.CurrentContextsAsync(uow,refs,ct),"MeasurementFinding",id);}
 public async Task<IReadOnlyList<ResourceRef>?> ResourcesForCaseAsync(IKernelUnitOfWork uow,long id,int? number,CancellationToken ct)
 {
  var header=await uow.Store<IQualityInvestigationLookup>().FindInvestigationAsync(id,ct);if(header is null||!options.Configured||header.OrganizationCode!=options.OrganizationCode)return null;var snapshot=await uow.Store<IQualityInvestigationLookup>().FindSnapshotAsync(id,number??header.LatestSnapshot,ct);if(snapshot is null)return null;
  var refs=header.RootSources.Concat(snapshot.Capture.Candidates.SelectMany(candidate=>candidate.Sources)).Distinct().ToArray();return InvestigationService.Resources(await resolver.CurrentContextsAsync(uow,refs,ct),"TraceInvestigation",id);
 }
 public async Task<bool> CanReadAssessmentAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long id,CancellationToken ct)
 {var assessment=await uow.Store<IQualityInvestigationLookup>().FindAssessmentAsync(id,ct);return assessment is not null&&await CanReadWholeCaseAsync(uow,principal,assessment.InvestigationId,assessment.SnapshotNumber,ct);}
 private async Task<TraceInvestigationView> ReadAsync(IKernelUnitOfWork uow,ForgePrincipal principal,TraceabilityInvestigation header,int number,CancellationToken ct)
 {
  principal=await CurrentAsync(uow,principal,ct);var snapshot=await uow.Store<IQualityInvestigationLookup>().FindSnapshotAsync(header.InvestigationId,number,ct)??throw Missing();
  var candidates=new List<TraceCandidate>();foreach(var candidate in snapshot.Capture.Candidates)if(await CanReadCandidateAsync(uow,principal,candidate,ct))candidates.Add(candidate);
  var keys=candidates.Select(candidate=>candidate.Key).ToHashSet(StringComparer.Ordinal);var links=new List<TraceGraphLink>();foreach(var link in snapshot.Capture.Links)if((keys.Contains(link.To)||snapshot.Capture.RootSources.Any(source=>source.Type+":"+source.Id==link.To))&&await resolver.CanReadAsync(uow,principal,link.Evidence.Sources,ct))links.Add(link);
  var redacted=candidates.Count!=snapshot.Capture.Candidates.Count||links.Count!=snapshot.Capture.Links.Count;
  var assessments=(await uow.Store<IQualityInvestigationLookup>().AssessmentsAsync(header.InvestigationId,number,ct)).Where(assessment=>keys.Contains(assessment.CandidateKey)).ToArray();var savedHolds=(await uow.Store<IQualityInvestigationLookup>().HoldLinksAsync(header.InvestigationId,ct)).Where(link=>link.SnapshotNumber==number).ToArray();var visibleHolds=new List<InvestigationHoldLink>();
  foreach(var link in savedHolds)if(keys.Contains(link.CandidateKey)){var candidate=candidates.Single(candidate=>candidate.Key==link.CandidateKey);var holdSource=new TraceSourceReference("Hold",link.HoldId.ToString(System.Globalization.CultureInfo.InvariantCulture),Permissions.HoldsRead,candidate.ActionResources.Select(resource=>resource with{Type="Hold",Id=link.HoldId.ToString(System.Globalization.CultureInfo.InvariantCulture)}).ToArray());if(await resolver.CanReadAsync(uow,principal,[holdSource],ct))visibleHolds.Add(link);}
  redacted|=visibleHolds.Count!=savedHolds.Length;var holds=visibleHolds.ToArray();
  var capture=snapshot.Capture with{Candidates=candidates,Links=links};
  if(redacted)
  {
   // Do not replace identifiers inside stored free text: hide the whole human narrative when its recorded context is no longer readable.
   header=header with{Title="Investigation with restricted current evidence",TriggerEvidence="Restricted current evidence",History=header.History.Select(change=>change with{Reason="Restricted current evidence"}).ToArray()};
   assessments=assessments.Select(assessment=>assessment with{Basis="Restricted current evidence",Evidence="Restricted current evidence",PlannedDisposition="Restricted current evidence",History=assessment.History.Select(change=>change with{Reason="Restricted current evidence"}).ToArray()}).ToArray();
   holds=holds.Select(link=>link with{Basis="Restricted current evidence"}).ToArray();
   capture=capture with{Completeness=capture.Completeness with{RecordedTraversalComplete=false,RestrictedSources=true,Gaps=capture.Completeness.Gaps.Append(new("restricted_current_source","Some recorded evidence is currently inaccessible; no hidden identities or counts are disclosed.")).Distinct().ToArray()}};
  }
  return new(header,snapshot with{Capture=capture},assessments,holds,redacted);
 }
 private void Permit(ForgePrincipal principal,string permission,ResourceRef resource){var decision=authorizer.Authorize(principal,permission,resource);if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision,resource);}
 private async Task<bool> CanReadCandidateAsync(IKernelUnitOfWork uow,ForgePrincipal principal,TraceCandidate candidate,CancellationToken ct)
 {
  try{principal=await CurrentAsync(uow,principal,ct);await resolver.RequireCurrentReadsAsync(uow,principal,candidate.Sources,ct);foreach(var resource in candidate.ActionResources)Permit(principal,Permissions.QualityRead,resource);var current=await resolver.CurrentContextsAsync(uow,candidate.Sources,ct);foreach(var resource in current.Where(source=>source.ActionScope).SelectMany(source=>source.Resources))Permit(principal,Permissions.QualityRead,resource with{Type=candidate.RecordType,Id=candidate.RecordId});return true;}catch(ForgeAuthorizationException){return false;}catch(CommandRejectedException){return false;}
 }
 private static async Task<ForgePrincipal> CurrentAsync(IKernelUnitOfWork uow,ForgePrincipal principal,CancellationToken ct)=>await uow.People.LoadPrincipalAsync(principal.PersonId,ct)??principal with{IsActive=false};
 private static CommandRejectedException Missing()=>new("investigation_not_found","The governed investigation source is unavailable.");
}
