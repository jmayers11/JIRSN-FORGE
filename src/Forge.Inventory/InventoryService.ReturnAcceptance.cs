using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Inventory;

public sealed record PrepareCustomerReturnInspection(long RmaId,decimal Quantity,string? LocationCode);
public sealed partial class InventoryService
{
    public Task<CommandResult> PrepareReturnInspectionAsync(PrepareCustomerReturnInspection c,CommandContext context,CancellationToken ct)
    {
        var spec=new CommandSpec(SourceContext,nameof(PrepareCustomerReturnInspection),Permissions.InventoryTransact,ResourceRef.Of("CustomerReturn",Key(c.RmaId)),true);
        async Task<CommandAuthority> Authority(IKernelUnitOfWork uow,DateTimeOffset now)
        {
            if(returnAcceptance is null)throw new CommandRejectedException("return_policy_required","Configure governed return acceptance.");
            var actual=await returnAcceptance.PreparationAuthorityAsync(uow,context.Principal,c.RmaId,c.LocationCode,now,ct);
            return new(actual.Resources[0],actual.SubjectIdentity,actual.Resources.Skip(1).ToArray());
        }
        return pipeline.ExecuteAsync(spec,c,context,async(uow,now)=>
        {
            var authority=await Authority(uow,now);
            var prepared=await returnAcceptance!.PrepareAsync(uow,context,c.RmaId,c.Quantity,c.LocationCode,now,ct);
            return new(CommandResult.Ok(Key(prepared.CohortId),$"Return {c.RmaId}: exact cohort {prepared.CohortId} prepared for reinspection."),
                [pipeline.Event(new ReceivingRecorded("ReturnInspectionCohort",prepared.CohortId,"Prepared"),context,SourceContext,"ReturnInspectionCohort",Key(prepared.CohortId),authority.Resource.Program,authority.Resource.Markings)],
                new("ReturnInspectionCohort",Key(prepared.CohortId),null,CanonicalJson.Serialize(new{prepared,authority.Resource,authority.AdditionalResources}),authority.Resource.Program,
                    (authority.AdditionalResources??[]).Aggregate(authority.Resource.Markings,(marks,resource)=>marks|resource.Markings)));
        },ct,Authority);
    }
    private async Task<CommandAuthority> ReturnDispositionAuthorityAsync(IKernelUnitOfWork uow,CommandSpec spec,DispositionCustomerReturn c,CommandContext context,DateTimeOffset now,CancellationToken ct)
    {
        if(returnAcceptance is not null)
        {
            var actual=await returnAcceptance.AuthorityAsync(uow,context.Principal,c.RmaId,c.DecisionId,c.ExpectedDecisionVersion,now,ct);
            if(actual is not null)
            {
                if(c.Disposition!=CustomerReturnDisposition.Restock&&await uow.Store<IReturnAcceptanceInventoryLookup>().PreparedForRmaAsync(c.RmaId,ct)>0)
                    throw new CommandRejectedException("return_cohort_disposition_required","Use exact prepared-cohort disposition evidence.");
                if(c.Disposition==CustomerReturnDisposition.Restock&&(c.DecisionId is null||c.ExpectedDecisionVersion is null))
                    throw new CommandRejectedException("return_release_required","Use an independent release decision for this exact returned cohort.");
                return new(actual.Resources[0],actual.SubjectIdentity,actual.Resources.Skip(1).ToArray());
            }
        }
        return await ReceivingAcceptanceAuthorityAsync(uow,spec,c,context,now,ct);
    }
}

