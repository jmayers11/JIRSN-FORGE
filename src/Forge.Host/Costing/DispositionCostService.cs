using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;

namespace Forge.Host.Costing;

/// <summary>One NCR of a split family, valued at the standard unit cost in force when the nonconformance was raised.</summary>
public sealed record DispositionCostPortion(long NcrId, long? ParentNcrId, decimal Quantity, string Status, string? Disposition, string Treatment, decimal? Value);

/// <summary>Work an approved rework plan added for a reworked portion, valued at the policy's labor and overhead rates and standard material costs.</summary>
public sealed record ReworkAddedCost(long ReworkId, decimal Quantity, decimal LaborHours, decimal? LaborAndOverhead, decimal? Material, IReadOnlyList<string> Notes);

/// <summary>
/// How the cost of nonconforming units splits across their dispositions (Sprint 10 WW-S10-24). Every unit of the original NCR is in
/// exactly one portion, and the portions' standard value adds up to the original value: scrapped units are a loss, reworked, repaired or
/// accepted units stay in inventory value, returned units go back to the supplier. Rework adds its own labor, overhead and material.
/// Nothing is posted to a ledger; amounts are standard-cost valuations of quantities Forge records.
/// </summary>
public sealed record DispositionCostReconciliation(
    long RootNcrId,
    string PartNumber,
    long? OrderNumber,
    string Method,
    string Currency,
    DateTimeOffset ValuedAt,
    decimal? UnitStandardCost,
    decimal OriginalQuantity,
    decimal? OriginalValue,
    IReadOnlyList<DispositionCostPortion> Portions,
    decimal? ScrapLoss,
    decimal ScrapQuantityRecorded,
    IReadOnlyList<ReworkAddedCost> Rework,
    bool QuantitiesReconcile,
    bool? ValuesReconcile,
    IReadOnlyList<string> Notes);

public sealed class DispositionCostService(IKernelUnitOfWorkFactory unitOfWork)
{
    public async Task<DispositionCostReconciliation> ReconcileAsync(ForgePrincipal principal, long ncrId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var quality = uow.Store<IQualityLookup>();
        var ncr = await quality.FindNcrAsync(ncrId, ct) ?? throw new CommandRejectedException("ncr_not_found", $"NCR {ncrId} does not exist.");
        while (ncr.ParentNcrId is { } parent)
            ncr = await quality.FindNcrAsync(parent, ct) ?? throw new CommandRejectedException("ncr_not_found", $"NCR {parent} does not exist.");
        var root = ncr;
        if (root.OrderNumber is { } orderNumber && await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct) is { ProgramId: { } program }
            && !principal.Programs.Contains(new ProgramId(program)))
            throw new CommandRejectedException("ncr_not_found", $"NCR {ncrId} does not exist.");

        // The family: the original NCR and every NCR split from it, however deep.
        var all = await quality.ListNcrsAsync(true, ct);
        var family = new List<NcrInfo> { root };
        for (var i = 0; i < family.Count; i++)
            family.AddRange(all.Where(n => n.ParentNcrId == family[i].NcrId && family.All(f => f.NcrId != n.NcrId)));

        var costing = uow.Store<ICostingLookup>();
        var policy = Forge.Inventory.Contracts.Costing.PolicyAt(await costing.ListPoliciesAsync(ct), root.RaisedAt);
        var costs = await costing.ListStandardCostsAsync(null, ct);
        var unit = Forge.Inventory.Contracts.Costing.StandardAt(costs, root.PartNumber, root.RaisedAt);
        var notes = new List<string>();
        if (unit is null) notes.Add($"{root.PartNumber} had no standard cost when NCR {root.NcrId} was raised; quantities reconcile, values cannot.");
        var currency = unit?.Currency ?? policy?.Currency ?? "USD";
        decimal? Value(decimal quantity) => unit is null ? null : decimal.Round(quantity * unit.Total, 4);

        var portions = family.OrderBy(n => n.NcrId).Select(n => new DispositionCostPortion(n.NcrId, n.ParentNcrId, n.Quantity, n.Status.ToString(), n.Disposition?.ToString(),
            Treatment(n), Value(n.Quantity))).ToList();
        var original = family.Sum(n => n.Quantity);
        var scrapPortions = family.Where(n => n.Disposition == Disposition.Scrap && n.Status is NcrStatus.Dispositioned or NcrStatus.Closed).ToList();
        if (family.Any(n => n.Disposition is null || n.Status is NcrStatus.Open or NcrStatus.AwaitingApproval))
            notes.Add("Some units are still awaiting a decided disposition; their value is shown but not yet treated.");

        var ids = family.Select(n => n.NcrId).ToHashSet();
        var rework = (await uow.Store<IReworkLookup>().ListReworkAsync(root.OrderNumber, false, ct)).Where(r => r.NcrId is { } id && ids.Contains(id)).ToList();
        var scrapRecorded = rework.Where(r => r.Kind == ReworkKind.Scrap).Sum(r => r.ScrapQuantity ?? 0);
        var plans = (await uow.Store<IReworkPlanStore>().ListPlansAsync(root.OrderNumber, ct)).Where(p => p.Status == ReworkPlanStatus.Approved).ToList();
        var added = new List<ReworkAddedCost>();
        foreach (var r in rework.Where(r => r.Kind is ReworkKind.Rework or ReworkKind.Repair))
        {
            var lineNotes = new List<string>();
            decimal hours = 0m, material = 0m;
            var materialKnown = true;
            DateTimeOffset? approvedAt = null;
            foreach (var plan in plans.Where(p => p.ReworkId == r.ReworkId))
            {
                approvedAt ??= plan.DecidedAt;
                hours += plan.Operations.Sum(o => (o.SetupMinutes + (o.RunMinutesPerUnit * r.Quantity)) * o.CrewSize) / 60m;
                foreach (var m in plan.Materials)
                {
                    if (Forge.Inventory.Contracts.Costing.StandardAt(costs, m.PartNumber, plan.DecidedAt ?? plan.ProposedAt) is { } std) material += m.Quantity * std.Total;
                    else
                    {
                        materialKnown = false;
                        lineNotes.Add($"{m.PartNumber} has no standard cost; its material is not valued.");
                    }
                }
            }

            var ratePolicy = approvedAt is { } at ? Forge.Inventory.Contracts.Costing.PolicyAt(await costing.ListPoliciesAsync(ct), at) : policy;
            decimal? labor = ratePolicy is { LaborRatePerHour: { } lr, OverheadRatePerHour: { } ohr } ? decimal.Round(hours * (lr + ohr), 4) : null;
            if (hours > 0 && labor is null) lineNotes.Add("Labor and overhead rates are not set in the costing policy; added labor is not valued.");
            if (approvedAt is null) lineNotes.Add("No approved rework plan added work for this rework.");
            added.Add(new ReworkAddedCost(r.ReworkId, r.Quantity, decimal.Round(hours, 4), labor, materialKnown ? decimal.Round(material, 4) : null, lineNotes));
        }

        var quantitiesReconcile = portions.Sum(p => p.Quantity) == original;
        bool? valuesReconcile = unit is null ? null : portions.Sum(p => p.Value ?? 0) == Value(original);
        var scrapQuantity = scrapPortions.Sum(n => n.Quantity);
        if (scrapQuantity != scrapRecorded)
            notes.Add($"{scrapQuantity:0.###} unit(s) are dispositioned scrap and {scrapRecorded:0.###} are recorded scrapped against the order.");
        return new DispositionCostReconciliation(root.NcrId, root.PartNumber, root.OrderNumber, policy?.Method.ToString() ?? "Standard", currency, root.RaisedAt,
            unit?.Total, original, Value(original), portions, Value(scrapQuantity), scrapRecorded, added, quantitiesReconcile, valuesReconcile, notes);
    }

    private static string Treatment(NcrInfo ncr) => ncr.Status is NcrStatus.Open or NcrStatus.AwaitingApproval || ncr.Disposition is null
        ? "Undecided"
        : ncr.Disposition switch
        {
            Disposition.Scrap => "Loss",
            Disposition.ReturnToSupplier => "Returned to supplier",
            Disposition.Rework => "Retained (rework)",
            Disposition.Repair => "Retained (repair)",
            _ => "Retained (use as is)",
        };
}
