using System.Globalization;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;

namespace Forge.Host.Quality;

public sealed class ReturnAcceptanceSourceResolver(ICommercialQualitySourceResolver commercial):IReturnAcceptanceSourceResolver
{
 public async Task<ReturnSourceContext> ResolveAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long rmaId,string facility,CancellationToken ct)
 {
  var actual=await commercial.ResolveReferencesAsync(uow,principal,facility,[new(CommercialReferenceKind.CustomerReturn,rmaId.ToString(CultureInfo.InvariantCulture))],ct);
  if(actual.Ownership.Count==0)throw new CommandRejectedException("return_source_unavailable","The actual returned product source is unavailable.");
  return new(actual.Ownership,actual.Sources.Select(source=>new QmsPolicyReadSource(source.Permission,source.Resource)).ToArray(),actual.Fingerprint);
 }
 public Task<IReadOnlyList<ResourceRef>> ResourceContextsAsync(IKernelUnitOfWork uow,long rmaId,string facility,CancellationToken ct)=>commercial.ResourceContextsAsync(uow,facility,[new(CommercialReferenceKind.CustomerReturn,rmaId.ToString(CultureInfo.InvariantCulture))],ct);
}
