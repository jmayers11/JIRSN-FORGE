using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;

namespace Forge.Procurement.Contracts;

/// <summary>Every actual requisition program is a required purchase context; unsourced lines remain unscoped.</summary>
public static class PurchaseOrderAuthority
{
    public static Task<IReadOnlyList<ResourceRef>> ResourcesAsync(IKernelUnitOfWork uow, PurchaseOrderInfo po, CancellationToken ct) =>
        ResourcesForRequisitionsAsync(uow, po.PoNumber.ToString(CultureInfo.InvariantCulture), po.Lines.Select(line => line.RequisitionId), ct);

    public static async Task<IReadOnlyList<ResourceRef>> ResourcesForRequisitionsAsync(IKernelUnitOfWork uow, string? subjectId, IEnumerable<long?> requisitionIds, CancellationToken ct)
    {
        var programs = new List<ProgramId?>();
        foreach (var requisitionId in requisitionIds)
        {
            var requisition = requisitionId is { } id ? await uow.Store<IRequisitionLookup>().FindRequisitionAsync(id, ct) : null;
            if (requisitionId is not null && requisition is null)
                throw new CommandRejectedException("requisition_not_found", "The purchase order's recorded sourcing need no longer exists.");
            programs.Add(requisition?.ProgramId is { } program ? new ProgramId(program) : null);
        }
        if (programs.Count == 0) programs.Add(null);
        // Purchase orders record no price/currency. Unknown money stays unknown for monetary policies.
        return programs.Distinct().OrderBy(program => program?.Value)
            .Select(program => new ResourceRef("PurchaseOrder", subjectId, program)).ToArray();
    }
}
