using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;

namespace Forge.Inventory;

public sealed partial class InventoryService
{
 private async Task<CommandAuthority> InventoryAcceptanceAuthorityAsync<T>(IKernelUnitOfWork uow,CommandSpec spec,T command,CommandContext context,DateTimeOffset now,CancellationToken ct)
 {
  var actual=await sourceAuthority.ResolveAsync(uow,spec,context,now,ct);
  switch(command)
  {
   case ReceiveStock receipt:
    var item=await ItemAsync(uow,BusinessPart(receipt.PartNumber),ct);var revision=await ReceivedRevisionAsync(uow,item.PartNumber,receipt.Revision,ct);
    await productRelease.OutputPolicyAsync(uow,context.Principal,item.PartNumber,revision,receipt.LocationCode,null,receipt.PoNumber,now,ct);break;
   case ReleaseStock release:
    if(await productRelease.IsGovernedStockAsync(uow,context.Principal,BusinessPart(release.PartNumber),release.LotNumber,release.LocationCode,now,ct))throw new CommandRejectedException("independent_product_release_required","Consume an exact independent Quality product-release decision; this legacy release action cannot substitute for it.");break;
   case IssueToOrder issue:
    await productRelease.RequireReleasedScopeAsync(uow,context.Principal,BusinessPart(issue.PartNumber),issue.LotNumber,issue.LocationCode,issue.Quantity,now,ct);break;
   case TransferStock transfer when transfer.Status==StockStatus.Released:
    await productRelease.RequireReleasedScopeAsync(uow,context.Principal,BusinessPart(transfer.PartNumber),transfer.LotNumber,transfer.FromLocation,transfer.Quantity,now,ct);break;
  }
  return actual;
 }
 private async Task<CommandAuthority> ReceivingAcceptanceAuthorityAsync<T>(IKernelUnitOfWork uow,CommandSpec spec,T command,CommandContext context,DateTimeOffset now,CancellationToken ct)
 {
  var actual=await sourceAuthority.ResolveAsync(uow,spec,context,now,ct);
  switch(command)
  {
   case InspectReceipt inspection when inspection.Accepted>0:
    var receipt=await uow.Store<IReceivingLookup>().FindTransactionAsync(inspection.ReceiptTransactionId,ct)??throw new CommandRejectedException("receipt_not_found","The actual receiving transaction is unavailable.");
    await RequireLegacyReleaseUnadoptedAsync(uow,context,receipt.PartNumber,receipt.LotNumber,receipt.ToLocation!,now,ct);break;
   case ApproveUseAsIs approval:
    var rejection=(await uow.Store<IReceivingLookup>().ListRejectionsAsync(false,ct)).SingleOrDefault(r=>r.RejectionId==approval.RejectionId)??throw new CommandRejectedException("rejection_not_found","The actual supplier rejection is unavailable.");
    await RequireLegacyReleaseUnadoptedAsync(uow,context,rejection.PartNumber,rejection.LotNumber,rejection.LocationCode,now,ct);break;
   case DispositionCustomerReturn disposition when disposition.Disposition==CustomerReturnDisposition.Restock:
    var returned=(await uow.Store<IReceivingLookup>().ListCustomerReturnsAsync(null,ct)).SingleOrDefault(r=>r.RmaId==disposition.RmaId)??throw new CommandRejectedException("return_not_found","The actual customer return is unavailable.");
    await RequireLegacyReleaseUnadoptedAsync(uow,context,returned.PartNumber,returned.LotNumber,returned.LocationCode,now,ct);break;
   case ReceiveReplacement replacement:
    var rtv=(await uow.Store<IReceivingLookup>().ListRtvsAsync(false,ct)).SingleOrDefault(r=>r.RtvId==replacement.RtvId)??throw new CommandRejectedException("rtv_not_found","The actual return to vendor is unavailable.");
    var source=(await uow.Store<IReceivingLookup>().ListRejectionsAsync(false,ct)).Single(r=>r.RejectionId==rtv.RejectionId);
    var revision=rtv.LotNumber is{} lot?(await uow.Store<IInventoryLookup>().FindLotAsync(rtv.PartNumber,lot,ct))?.PartRevision:null;
    await productRelease.OutputPolicyAsync(uow,context.Principal,rtv.PartNumber,revision,replacement.LocationCode,null,source.PoNumber,now,ct);break;
  }
  return actual;
 }
 private async Task RequireLegacyReleaseUnadoptedAsync(IKernelUnitOfWork uow,CommandContext context,string part,string? lot,string location,DateTimeOffset now,CancellationToken ct)
 {
  if(await productRelease.IsGovernedStockAsync(uow,context.Principal,part,lot,location,now,ct))throw new CommandRejectedException("independent_product_release_required","A legacy acceptance/concession/restock action cannot substitute for exact evidence and an independent governed Quality product release.");
 }
 private static string BusinessPart(string part)=>Forge.Kernel.Identity.BusinessCode.Require(part,"Part");
}
