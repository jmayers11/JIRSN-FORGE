using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Engines;
using Forge.Programs.Contracts;

namespace Forge.Planning;

/// <summary>One scheduled delivery as the program sees it: what the contract asks, what is planned and what has shipped.</summary>
public sealed record DeliveryStatusView(
    long DeliveryId,
    string ContractNumber,
    string Clin,
    string? PartNumber,
    decimal Quantity,
    DateOnly DueDate,
    bool Cancelled,
    long? DemandId,
    bool DemandInStep,
    decimal Shipped,
    string State);

public sealed record ContractSummary(
    long ContractId,
    string Number,
    string Title,
    string Customer,
    ContractStatus Status,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal? Ceiling,
    decimal? Value,
    int Clins,
    int Requirements,
    decimal Funded,
    decimal DeliveredValue,
    decimal ScheduledValue);

/// <summary>
/// Schedule-based earned value, from contract prices and recorded deliveries. Budget at completion is the
/// value of part CLINs; planned value is the value of deliveries due by today; earned value is the value
/// of what shipped against them. Cost performance needs actual cost, which Forge does not record.
/// </summary>
public sealed record EarnedValueView(
    decimal BudgetAtCompletion,
    decimal PlannedValue,
    decimal EarnedValue,
    decimal ScheduleVariance,
    decimal? SchedulePerformanceIndex,
    decimal Funded,
    decimal FundingRemaining,
    decimal ScheduledValue,
    decimal ScheduledBeyondFunding,
    int UnpricedDeliveries,
    string Note);

/// <summary>One WBS element with the orders charged to it and everything below it.</summary>
public sealed record WbsRollup(string Code, string? ParentCode, string Title, int Orders, decimal Quantity, decimal Completed, IReadOnlyList<long> OrderNumbers);

public sealed record ProgramOrderView(long OrderNumber, string PartNumber, decimal Quantity, DateOnly DueDate, string OrderStatus, string Delivery, DateTimeOffset? ProjectedCompletion, string? Explanation, string? WbsCode = null);

public sealed record ConstraintCount(string Constraint, int Operations, string Example);

public sealed record ProgramHorizonView(
    ProgramInfo Program,
    DateOnly Today,
    decimal UnitsRequired,
    decimal UnitsDelivered,
    decimal UnitsPlanned,
    int OnSchedule,
    int AtRisk,
    int ProjectedLate,
    int NotScheduled,
    ConstraintCount? PrimaryConstraint,
    IReadOnlyList<ConstraintCount> Constraints,
    long? ScheduleVersion,
    DateTimeOffset? ScheduleApprovedAt,
    IReadOnlyList<ContractSummary> Contracts,
    IReadOnlyList<DeliveryStatusView> Deliveries,
    IReadOnlyList<ProgramOrderView> Orders,
    IReadOnlyList<MilestoneInfo> Milestones,
    EarnedValueView EarnedValue,
    IReadOnlyList<WbsRollup> Wbs);

/// <summary>
/// Program Horizon (spec §7): one program through contracts, deliveries, demand, orders and the approved
/// schedule. Every number is counted from records: units required are scheduled contract deliveries,
/// delivered is what shipped against them, order status is the approved schedule's projection, and the
/// primary constraint is the binding constraint that delayed the most of the program's operations.
/// Programs the reader has no access to are reported as not existing.
/// </summary>
public sealed class ProgramHorizonService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    public async Task<IReadOnlyList<ProgramHorizonView>> ListAsync(ForgePrincipal principal, CancellationToken ct)
    {
        Demand(principal);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var programs = (await uow.Store<IProgramsLookup>().ListProgramsAsync(ct)).Where(p => principal.Programs.Contains(new ProgramId(p.ProgramId))).ToList();
        var views = new List<ProgramHorizonView>();
        foreach (var program in programs)
        {
            views.Add(await BuildAsync(uow, program, ct));
        }

        return views;
    }

    public async Task<ProgramHorizonView> GetAsync(ForgePrincipal principal, Guid programId, CancellationToken ct)
    {
        Demand(principal);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var program = await uow.Store<IProgramsLookup>().FindProgramAsync(programId, ct);
        if (program is null || !principal.Programs.Contains(new ProgramId(programId)))
        {
            throw new CommandRejectedException("program_not_found", $"Program {programId} does not exist.");
        }

        return await BuildAsync(uow, program, ct);
    }

    private async Task<ProgramHorizonView> BuildAsync(IKernelUnitOfWork uow, ProgramInfo program, CancellationToken ct)
    {
        var today = await MaterialData.TodayAsync(uow, time, ct);
        var programs = uow.Store<IProgramsLookup>();
        var contracts = await programs.ListContractsAsync(program.ProgramId, ct);
        var milestones = await programs.ListMilestonesAsync(program.ProgramId, ct);

        var demand = (await uow.Store<IDemandStore>().ListAsync(true, ct)).Where(d => d.ProgramId == program.ProgramId).ToList();
        var bySource = demand.Where(d => d.SourceKey is not null).ToDictionary(d => d.SourceKey!, StringComparer.Ordinal);
        var shipped = (await uow.Store<IInventoryLookup>().ListShippedByDemandAsync(ct)).GroupBy(s => s.DemandId).ToDictionary(g => g.Key, g => g.Sum(s => s.Shipped));

        var deliveries = new List<DeliveryStatusView>();
        foreach (var contract in contracts)
        {
            foreach (var d in contract.Deliveries)
            {
                var part = contract.Clins.FirstOrDefault(c => c.Clin == d.Clin)?.PartNumber;
                bySource.TryGetValue(DeliveryReference.SourceKeyFor(d.DeliveryId), out var planned);
                var sent = planned is null ? 0 : shipped.GetValueOrDefault(planned.DemandId);
                var inStep = planned is not null && (d.Cancelled ? planned.Status != DemandStatus.Open : planned.Status != DemandStatus.Open || (planned.Quantity == d.Quantity && planned.NeedDate == d.DueDate));
                var state = d.Cancelled ? "Cancelled"
                    : sent >= d.Quantity ? "Delivered"
                    : planned is null ? "Not planned"
                    : !inStep ? "Contract changed"
                    : d.DueDate < today ? "Overdue"
                    : sent > 0 ? "Partly delivered"
                    : "Planned";
                deliveries.Add(new DeliveryStatusView(d.DeliveryId, contract.Number, d.Clin, part, d.Quantity, d.DueDate, d.Cancelled, planned?.DemandId, inStep, sent, state));
            }
        }

        var live = deliveries.Where(d => !d.Cancelled).ToList();
        var required = live.Sum(d => d.Quantity);
        var delivered = live.Sum(d => Math.Min(d.Shipped, d.Quantity));
        var plannedUnits = live.Where(d => d.DemandId is not null).Sum(d => d.Quantity);

        // Orders of the program, with the approved schedule's projection.
        var approved = await uow.Store<IPlanningStore>().LatestAsync(ScheduleStatus.Approved, true, ct);
        var projected = (approved?.Result?.Orders ?? []).ToDictionary(o => o.OrderNumber);
        var orders = (await uow.Store<IExecutionLookup>().ListOrdersAsync(ct))
            .Where(o => o.ProgramId == program.ProgramId && o.Status is not (OrderStatus.Cancelled or OrderStatus.Completed))
            .OrderBy(o => o.DueDate).ThenBy(o => o.OrderNumber)
            .Select(o => projected.TryGetValue(o.OrderNumber, out var p)
                ? new ProgramOrderView(o.OrderNumber, o.PartNumber, o.Quantity, o.DueDate, o.Status.ToString(), p.Status.ToString(), p.ProjectedCompletion, p.Explanation, o.WbsCode)
                : new ProgramOrderView(o.OrderNumber, o.PartNumber, o.Quantity, o.DueDate, o.Status.ToString(), "NotScheduled", null, o.Status == OrderStatus.Released ? "Not in the approved schedule; propose and approve a new schedule." : "Not released, so not scheduled.", o.WbsCode))
            .ToList();

        var constraints = (approved?.Result?.Operations ?? [])
            .Where(op => op.ProgramId == program.ProgramId && op.BindingConstraint is not ("Now" or "Predecessor"))
            .GroupBy(op => op.BindingConstraint, StringComparer.Ordinal)
            .Select(g => new ConstraintCount(g.Key, g.Count(), $"Order {g.First().OrderNumber} op {g.First().Sequence}: {g.First().BindingDetail}"))
            .OrderByDescending(c => c.Operations).ThenBy(c => c.Constraint, StringComparer.Ordinal)
            .ToList();

        // Earned value: every delivery priced at its CLIN's unit price.
        decimal? Price(long contractId, string clin) => contracts.First(c => c.ContractId == contractId).Clins.FirstOrDefault(l => l.Clin == clin)?.UnitPrice;
        var priced = live.Select(d => (d, Price: Price(contracts.First(c => c.Number == d.ContractNumber).ContractId, d.Clin))).ToList();
        var bac = contracts.Where(c => c.Status != ContractStatus.Cancelled)
            .SelectMany(c => c.Clins.Where(l => l.ParentClin is null && l.PartNumber is not null))
            .Sum(l => (l.Quantity ?? 0) * (l.UnitPrice ?? 0));
        var pv = priced.Where(x => x.d.DueDate <= today).Sum(x => x.d.Quantity * (x.Price ?? 0));
        var ev = priced.Sum(x => Math.Min(x.d.Shipped, x.d.Quantity) * (x.Price ?? 0));
        var scheduledValue = priced.Sum(x => x.d.Quantity * (x.Price ?? 0));
        var funded = contracts.Where(c => c.Status != ContractStatus.Cancelled).Sum(c => c.Funded);
        var earned = new EarnedValueView(
            bac, pv, ev, ev - pv, pv == 0 ? null : decimal.Round(ev / pv, 3, MidpointRounding.AwayFromZero),
            funded, funded - ev, scheduledValue, Math.Max(0, scheduledValue - funded), priced.Count(x => x.Price is null),
            "Schedule-based: planned value is deliveries due by today, earned value is what shipped against them, at CLIN prices. Cost performance (CPI) needs actual cost, which Forge does not record.");

        var summaries = contracts.Select(c =>
        {
            var mine = priced.Where(x => x.d.ContractNumber == c.Number).ToList();
            return new ContractSummary(
                c.ContractId, c.Number, c.Title, c.Customer, c.Status, c.PeriodStart, c.PeriodEnd, c.Ceiling,
                c.Clins.All(l => l.Quantity is null || l.UnitPrice is null) ? null : c.Clins.Where(l => l.ParentClin is null).Sum(l => (l.Quantity ?? 0) * (l.UnitPrice ?? 0)),
                c.Clins.Count, c.Requirements.Count, c.Funded,
                mine.Sum(x => Math.Min(x.d.Shipped, x.d.Quantity) * (x.Price ?? 0)),
                mine.Sum(x => x.d.Quantity * (x.Price ?? 0)));
        }).ToList();

        // WBS: orders charged to an element count toward it and every element above it.
        var wbs = await programs.ListWbsAsync(program.ProgramId, ct);
        var programOrders = (await uow.Store<IExecutionLookup>().ListOrdersAsync(ct)).Where(o => o.ProgramId == program.ProgramId && o.Status != OrderStatus.Cancelled && o.WbsCode is not null).ToList();
        var completed = new Dictionary<long, decimal>();
        foreach (var o in programOrders)
        {
            var full = await uow.Store<IExecutionLookup>().FindOrderAsync(o.OrderNumber, ct);
            completed[o.OrderNumber] = full is { Operations.Count: > 0 } ? full.Operations.OrderBy(op => op.Sequence).Last().GoodQuantity : 0;
        }

        var rollup = wbs.Select(w =>
        {
            var under = programOrders.Where(o => o.WbsCode == w.Code || o.WbsCode!.StartsWith(w.Code + ".", StringComparison.Ordinal)).ToList();
            return new WbsRollup(w.Code, w.ParentCode, w.Title, under.Count, under.Sum(o => o.Quantity), under.Sum(o => completed[o.OrderNumber]), under.Select(o => o.OrderNumber).Order().ToList());
        }).ToList();

        return new ProgramHorizonView(
            program,
            today,
            required,
            delivered,
            plannedUnits,
            orders.Count(o => o.Delivery == nameof(DeliveryStatus.OnTime)),
            orders.Count(o => o.Delivery == nameof(DeliveryStatus.AtRisk)),
            orders.Count(o => o.Delivery is nameof(DeliveryStatus.Late) or nameof(DeliveryStatus.Unschedulable)),
            orders.Count(o => o.Delivery == "NotScheduled"),
            constraints.Count > 0 ? constraints[0] : null,
            constraints,
            approved?.VersionId,
            approved?.ApprovedAt,
            summaries,
            deliveries.OrderBy(d => d.DueDate).ThenBy(d => d.DeliveryId).ToList(),
            orders,
            milestones,
            earned,
            rollup);
    }

    private void Demand(ForgePrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decision = authorizer.Authorize(principal, Permissions.ProgramsRead, ResourceRef.Of("Program"));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }
    }
}
