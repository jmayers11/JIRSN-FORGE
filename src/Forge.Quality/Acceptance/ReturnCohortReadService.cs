using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;

namespace Forge.Quality.Acceptance;

public sealed class ReturnCohortReadService(IKernelUnitOfWorkFactory factory,IInspectionSubjectResolver subjects,IForgeAuthorizer authorizer)
{
 public async Task<ReturnInspectionCohort> ReadAsync(ForgePrincipal principal,long cohortId,CancellationToken ct)
 {
  await using var uow=await factory.BeginSnapshotAsync(ct);var cohort=await uow.Store<IReturnAcceptanceLookup>().FindCohortAsync(cohortId,ct)??throw Missing();
  if(!await CanReadAsync(uow,principal,cohortId,ct))throw Missing();return cohort;
 }
 public async Task<bool> CanReadAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long cohortId,CancellationToken ct)
 {
  try
  {
   var cohort=await uow.Store<IReturnAcceptanceLookup>().FindCohortAsync(cohortId,ct);if(cohort is null)return false;
   principal=await uow.People.LoadPrincipalAsync(principal.PersonId,ct)??principal with{IsActive=false};
   var subject=await subjects.ResolveAsync(uow,principal,cohort.Subject,false,ct);
   foreach(var resource in AcceptanceSubjectResources.All(subject,"ReturnInspectionCohort",cohortId))Permit(principal,Permissions.QualityRead,resource);
   foreach(var source in cohort.Sources)Permit(principal,source.Permission,source.Resource);
   return true;
  }
  catch(ForgeAuthorizationException){return false;}
  catch(CommandRejectedException){return false;}
 }
 private void Permit(ForgePrincipal principal,string permission,ResourceRef resource){var decision=authorizer.Authorize(principal,permission,resource);if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision,resource);}
 private static CommandRejectedException Missing()=>new("return_cohort_unavailable","The exact return reinspection cohort is unavailable.");
}
