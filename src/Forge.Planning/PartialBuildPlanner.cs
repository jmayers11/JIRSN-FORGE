using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Engines;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Engines;

namespace Forge.Planning;

/// <summary>Planner-supplied lead times (Forge holds no receiving or inspection lead-time master data) and site policy for one preview.</summary>
public sealed record PartialBuildOptions(
    int? Sequence = null,
    decimal? Quantity = null,
    decimal? ReceivingHours = null,
    decimal? InspectionHours = null,
    decimal? PutAwayHours = null,
    decimal StagingHours = 0,
    decimal SetupHours = 0,
    decimal MinimumBatch = 1,
    decimal PackMultiple = 1,
    bool PartialAllowed = true);

/// <summary>One operation's reach: how many good units can get through it with the material the order can use now.</summary>
public sealed record OperationReach(int Sequence, string Description, decimal Done, decimal Reachable, decimal Additional, string? LimitedBy);

public sealed record PartialBuildMaterialNeed(string PartNumber, int ConsumedAtSequence, decimal Quantity);

/// <summary>
/// The partial-build preview for one order (Sprint 10 §17–19): how far its material lets it go now, the WIP that would wait and where, when the
/// missing material is expected to be usable, and partial-versus-wait. Nothing is reserved until the planner approves it.
/// </summary>
public sealed record PartialBuildPlan(
    long OrderNumber,
    string PartNumber,
    DateTimeOffset AsOf,
    string RuleVersion,
    IReadOnlyList<OperationReach> Operations,
    int? StopSequence,
    int? BlockedSequence,
    PartialBuildFeasibility? Feasibility,
    decimal Quantity,
    IReadOnlyList<PartialBuildMaterialNeed> Materials,
    decimal StorageQuantity,
    string? StorageLocation,
    IReadOnlyList<StorageAvailability> Storage,
    IReadOnlyList<MaterialUsableDateForecast> MaterialDates,
    DateTimeOffset? ExpectedRestart,
    decimal? ExpectedDwellHours,
    int? MaxDwellHours,
    decimal WorkBroughtForwardMinutes,
    bool CanApprove,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Notes,
    string Comparison,
    PartialBuild? ActiveBuild);

/// <summary>A calendar with no non-working time: lead times are elapsed hours as the planner gave them.</summary>
internal sealed class ElapsedCalendar : IWorkTimeCalendar
{
    public DateTimeOffset AddWorkingTime(DateTimeOffset start, TimeSpan workingDuration) => start + workingDuration;
}

public static class PartialBuildPlanner
{
    public const string RuleVersion = "partial-build-v1";

    private static readonly string[] PrerequisiteDimensions = ["Order", "Engineering", "Holds", "Quality", "Documentation", "Safety", "Procedure"];

    /// <param name="answers">Readiness of the order's open operations (now), keyed by sequence.</param>
    /// <param name="usableBy">Planning-visible material the order holds or can draw now, per part: issued plus allocated stock (MaterialEngine).</param>
    public static PartialBuildPlan Plan(OrderInfo order, OrderMaterialStatus? material, IReadOnlyDictionary<int, OperationAnswer> answers,
        IReadOnlyList<StorageAvailability> storage, PartialBuildOptions options, DateTimeOffset asOf, PartialBuild? active)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(options);
        var ops = order.Operations.OrderBy(o => o.Sequence).ToList();
        var notes = new List<string>();
        var blockers = new List<string>();
        if (ops.Count == 0 || order.Quantity <= 0)
            return Empty(order, asOf, storage, active, "The order has no operations to build.");

        var first = ops[0].Sequence;
        int Point(OrderMaterialInfo m) => m.ConsumedAtSequence is { } at && ops.Any(o => o.Sequence == at) ? at : first;
        if (order.Materials.Any(m => m.ConsumedAtSequence is { } at && ops.All(o => o.Sequence != at)))
            notes.Add("A BOM consumption point names an operation the order does not have; that material is treated as needed from the first operation.");
        if (order.Materials.All(m => m.ConsumedAtSequence is null) && order.Materials.Count > 0)
            notes.Add("No BOM line names its consumption point, so all material is needed from the first operation; set consumption points on the BOM to plan partial builds.");

        var lines = material?.Lines.ToDictionary(l => l.PartNumber, StringComparer.Ordinal) ?? new Dictionary<string, MaterialLineStatus>(StringComparer.Ordinal);
        var done = ops.ToDictionary(o => o.Sequence, o => Math.Min(order.Quantity, o.GoodQuantity));
        var groups = order.Materials.GroupBy(m => (Part: m.ComponentPartNumber, At: Point(m))).Select(g =>
        {
            var required = g.Sum(m => m.RequiredQuantity);
            var perUnit = required / order.Quantity;
            var line = lines.GetValueOrDefault(g.Key.Part);
            // Material of this part across the order, shared out by consumption point in proportion to what each point requires.
            var partRequired = order.Materials.Where(m => m.ComponentPartNumber == g.Key.Part).Sum(m => m.RequiredQuantity);
            var share = partRequired == 0 ? 0 : required / partRequired;
            var holdable = line is null ? 0m : (line.Issued + line.FromStock) * share;
            var unconsumed = Math.Max(0m, holdable - done[g.Key.At] * perUnit);
            var buildable = perUnit == 0 ? order.Quantity : decimal.Floor(unconsumed / perUnit);
            return (g.Key.Part, g.Key.At, PerUnit: perUnit, Unconsumed: unconsumed, IssuedUnconsumed: Math.Max(0m, (line?.Issued ?? 0m) * share - done[g.Key.At] * perUnit),
                Buildable: buildable, Line: line);
        }).ToList();

        var reach = new List<OperationReach>();
        var cap = order.Quantity;
        foreach (var op in ops)
        {
            var at = groups.Where(g => g.At == op.Sequence).ToList();
            var byMaterial = at.Count == 0 ? decimal.MaxValue : done[op.Sequence] + at.Min(g => g.Buildable);
            var reachable = Math.Max(done[op.Sequence], Math.Min(cap, byMaterial));
            string? limitedBy = null;
            if (byMaterial < cap)
                limitedBy = string.Join(", ", at.Where(g => done[op.Sequence] + g.Buildable == byMaterial).Select(g => g.Part));
            else if (cap < order.Quantity)
                limitedBy = "upstream operations";
            reach.Add(new OperationReach(op.Sequence, op.Description, done[op.Sequence], reachable, reachable - done[op.Sequence], limitedBy));
            cap = reachable;
        }

        // The blocked operation: the first whose own material stops units the operation before it could deliver. A shortage at the first
        // operation only limits the quantity (nothing waits before it) unless no unit can start at all.
        OperationReach? blocked = null;
        for (var i = 0; i < reach.Count; i++)
        {
            var step = reach[i];
            var before = i == 0 ? order.Quantity : reach[i - 1].Reachable;
            if (step.LimitedBy is not null && step.LimitedBy != "upstream operations" && step.Reachable < before && (i > 0 || step.Additional == 0))
            {
                blocked = step;
                break;
            }
        }
        var open = reach.Where(r => r.Done < order.Quantity).ToList();
        if (open.Count == 0)
            return Empty(order, asOf, storage, active, "Every operation is complete.");

        // The stopping point: the requested operation, or the last open operation the material allows any progress through.
        var stop = options.Sequence is { } wanted
            ? reach.FirstOrDefault(r => r.Sequence == wanted) ?? throw new CommandRejectedException("operation_not_found", $"Order {order.OrderNumber} has no operation {wanted}.")
            : (blocked is null ? reach[^1] : reach.LastOrDefault(r => r.Sequence < blocked.Sequence && r.Additional > 0) ?? (blocked.Additional > 0 ? blocked : null));
        if (stop is null)
        {
            var at = blocked!;
            return Empty(order, asOf, storage, active, $"Operation {at.Sequence} cannot start any more units: {at.LimitedBy} is short and nothing upstream is left to build.") with
            {
                Operations = reach,
                BlockedSequence = at.Sequence,
            };
        }

        if (blocked is null && reach.All(r => r.Reachable == order.Quantity))
            notes.Add("Material covers the whole order now; this is a full build, not a partial one.");

        var stopIndex = ops.FindIndex(o => o.Sequence == stop.Sequence);
        var span = ops.Take(stopIndex + 1).Where(o => done[o.Sequence] < order.Quantity).ToList();
        var spanAnswers = span.Select(o => answers.GetValueOrDefault(o.Sequence)).OfType<OperationAnswer>().ToList();
        bool Failed(string name) => spanAnswers.Any(a => a.Dimensions.Any(d => d.Name == name && d.Outcome is EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate));
        string Why(string name) => string.Join(" ", spanAnswers.SelectMany(a => a.Dimensions.Where(d => d.Name == name && d.Outcome is EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate)
            .Select(d => $"Op {a.Sequence}: {d.Detail}")));
        foreach (var name in PrerequisiteDimensions.Where(Failed))
            blockers.Add($"{name}: {Why(name)}");

        // WIP that gets through the stopping point but not through the blocked operation has to wait somewhere approved.
        var continues = blocked is null || blocked.Sequence <= stop.Sequence ? decimal.MaxValue : reach.First(r => r.Sequence == blocked.Sequence).Reachable;
        var needsStorage = blocked is not null && blocked.Sequence > stop.Sequence;
        var known = storage.Where(s => s.Free is not null).OrderByDescending(s => s.Free).ThenBy(s => s.Rule.LocationCode, StringComparer.Ordinal).ToList();
        var best = known.Count == 0 ? null : known[0];
        decimal? storageLimit = needsStorage
            ? best is null ? null : Math.Max(0m, continues - stop.Done) + best.Free!.Value
            : order.Quantity - stop.Done;
        if (needsStorage && best is null)
        {
            blockers.Add(storage.Count == 0
                ? $"No approved WIP storage rule exists for {order.PartNumber}; work cannot wait before operation {blocked!.Sequence} without one."
                : "Every approved storage location for this part has unknown free space: " + string.Join(" ", storage.Select(s => s.Status)));
        }

        var materials = span.SelectMany(o => groups.Where(g => g.At == o.Sequence)).ToList();
        var input = new PartialBuildInput(
            order.Quantity - reach[^1].Done,
            order.Quantity - stop.Done,
            [.. materials.GroupBy(g => g.Part, StringComparer.Ordinal).Select(g =>
            {
                // Expressed in units through the stopping point: units already past the consumption point carry their material with them.
                var tightest = g.OrderBy(m => done[m.At] - stop.Done + m.Buildable).First();
                return new OperationComponentAvailability(g.Key, Math.Max(0m, (done[tightest.At] - stop.Done) * tightest.PerUnit + tightest.Unconsumed),
                    tightest.PerUnit == 0 ? 1 : tightest.PerUnit, 0m, 1m);
            })],
            [
                new PartialBuildLimit(PartialBuildCapacityKind.Operation, $"Operation {stop.Sequence} open quantity", order.Quantity - stop.Done),
                new PartialBuildLimit(PartialBuildCapacityKind.QualifiedLabor, "Qualified operators present now", Failed("Workforce") ? 0m : order.Quantity - stop.Done),
                new PartialBuildLimit(PartialBuildCapacityKind.Machine, "Eligible machine available now", Failed("Equipment") ? 0m : order.Quantity - stop.Done),
                new PartialBuildLimit(PartialBuildCapacityKind.SafeStorage, needsStorage ? $"Approved WIP storage{(best is null ? string.Empty : " at " + best.Rule.LocationCode)}" : "No WIP storage needed", storageLimit),
            ],
            options.MinimumBatch,
            options.PackMultiple,
            blockers.Count == 0,
            !Failed("Tooling"),
            true,
            options.PartialAllowed);
        var feasibility = PartialBuildFeasibilityCalculator.Calculate(input);
        if (Failed("Workforce")) blockers.Add("Workforce: " + Why("Workforce"));
        if (Failed("Equipment")) blockers.Add("Equipment: " + Why("Equipment"));
        if (Failed("Tooling")) blockers.Add("Tooling: " + Why("Tooling"));

        var quantity = feasibility.RecommendedQuantity;
        if (options.Quantity is { } requested)
        {
            if (requested <= 0 || requested > quantity)
                blockers.Add($"{Q(requested)} is more than the {Q(quantity)} that is feasible now.");
            else
                quantity = requested;
        }

        // Material to reserve: what the units still to be built at each consumption point need, less what is already issued and unconsumed.
        var needs = materials
            .Select(m =>
            {
                var units = Math.Max(0m, stop.Done + quantity - done[m.At]);
                var need = Math.Max(0m, decimal.Round(units * m.PerUnit, 6, MidpointRounding.ToPositiveInfinity) - m.IssuedUnconsumed);
                return new PartialBuildMaterialNeed(m.Part, m.At, Math.Min(need, m.Line?.FromStock ?? 0m));
            })
            .Where(n => n.Quantity > 0)
            .GroupBy(n => n.PartNumber, StringComparer.Ordinal)
            .Select(g => new PartialBuildMaterialNeed(g.Key, g.Min(n => n.ConsumedAtSequence), g.Sum(n => n.Quantity)))
            .ToList();
        var storageQuantity = needsStorage ? Math.Max(0m, Math.Min(quantity, stop.Done + quantity - Math.Max(stop.Done, continues))) : 0m;

        // §19: when the missing material becomes usable, and whether the WIP can wait that long.
        var calendar = new ElapsedCalendar();
        var dates = new List<MaterialUsableDateForecast>();
        if (blocked is not null)
        {
            foreach (var g in groups.Where(g => g.At >= blocked.Sequence && g.Line is { Shortfall: > 0 } or { ExpectedOn: not null }).GroupBy(g => g.Part, StringComparer.Ordinal))
            {
                var line = g.First().Line!;
                var pending = line.Allocations.Where(a => a.DueDate is not null).ToList();
                var arrival = pending.Count == 0 ? (DateTimeOffset?)null : new DateTimeOffset(pending.Max(a => a.DueDate!.Value).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                var neededLater = Math.Max(0m, line.Remaining - line.FromStock);
                dates.Add(MaterialUsableDateCalculator.Calculate(new MaterialUsableDateInput(
                    g.Key, Math.Max(neededLater, 0.000001m), null, pending.Sum(a => a.Quantity), null, arrival, null, arrival, null,
                    Hours(options.ReceivingHours), Hours(options.InspectionHours), Hours(options.PutAwayHours),
                    line.Shortfall > 0 ? "None: part of the need has no supply" : "Promised date only",
                    pending.Count == 0 ? "No open supply" : string.Join(", ", pending.Select(a => a.Description))), calendar));
            }
        }

        var restart = blocked is null ? null
            : MaterialUsableDateCalculator.EarliestOperationRestart(dates, MaterialDateScenario.Expected, asOf, TimeSpan.FromHours((double)options.StagingHours),
                TimeSpan.FromHours((double)options.SetupHours), calendar);
        if (blocked is not null && restart is null)
        {
            notes.Add(options.ReceivingHours is null || options.InspectionHours is null || options.PutAwayHours is null
                ? "The restart date is unknown: give receiving, inspection and put-away hours (Forge has no lead-time master data for them)."
                : "The restart date is unknown: part of the missing material has no open supply with enough quantity.");
        }

        decimal? dwell = restart is null ? null : decimal.Round((decimal)(restart.Value - asOf).TotalHours, 1);
        var maxDwell = best?.Rule.MaxDwellHours;
        if (storageQuantity > 0 && dwell is { } waited && maxDwell is { } limit && waited > limit)
            blockers.Add($"The WIP would wait about {Q(waited)} hours at {best!.Rule.LocationCode}; its approved maximum dwell is {limit} hours.");
        if (storageQuantity > 0 && dwell is null && maxDwell is not null)
            notes.Add($"{best!.Rule.LocationCode} allows {maxDwell} hours of dwell; with no restart date the wait cannot be checked.");
        if (active is not null)
            blockers.Add($"Partial build {active.BuildId} is already approved for this order; complete or cancel it first.");

        var minutes = span.Sum(o => o.PlannedMinutes / order.Quantity * Math.Max(0m, Math.Min(quantity, stop.Done + quantity - done[o.Sequence])));
        var canApprove = quantity > 0 && feasibility.IsRecommendationFeasible && blockers.Count == 0;
        var comparison = quantity <= 0
            ? "Wait: no units can be advanced now. " + string.Join(" ", feasibility.Blockers)
            : blocked is null
                ? quantity < order.Quantity - stop.Done
                    ? $"Partial quantity: {Q(quantity)} of {Q(order.Quantity - stop.Done)} open units can go through the whole routing now; the rest waits for material."
                    : $"Build now: material covers all {Q(order.Quantity - stop.Done)} open units."
                : $"Partial: {Q(quantity)} units go through operation {stop.Sequence} now (about {Q(decimal.Round(minutes, 0))} planned minutes brought forward)" +
                  (storageQuantity > 0 ? $", {Q(storageQuantity)} of them wait at {best?.Rule.LocationCode ?? "approved storage"}" : string.Empty) +
                  $". Operation {blocked.Sequence} restarts {(restart is { } restartAt ? "about " + restartAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "when the missing material is usable (date unknown)")} either way. " +
                  "Waiting keeps the material free for other orders and needs no WIP space, but leaves that work for after the restart.";
        if (canApprove)
            notes.Add("Approving reserves the listed material and storage space; it does not issue material, move stock or change the approved schedule.");

        return new PartialBuildPlan(order.OrderNumber, order.PartNumber, asOf, RuleVersion, reach, stop.Sequence, blocked?.Sequence, feasibility, quantity, needs,
            storageQuantity, storageQuantity > 0 ? best?.Rule.LocationCode : null, storage, dates, restart, dwell, maxDwell, decimal.Round(minutes, 1),
            canApprove, [.. blockers.Concat(feasibility.Blockers).Distinct(StringComparer.Ordinal)], notes, comparison, active);
    }

    private static PartialBuildPlan Empty(OrderInfo order, DateTimeOffset asOf, IReadOnlyList<StorageAvailability> storage, PartialBuild? active, string why) =>
        new(order.OrderNumber, order.PartNumber, asOf, RuleVersion, [], null, null, null, 0m, [], 0m, null, storage, [], null, null, null, 0m, false, [why], [], "Wait: " + why, active);

    private static TimeSpan? Hours(decimal? hours) => hours is { } h ? TimeSpan.FromHours((double)h) : null;

    private static string Q(decimal value) => Quantities.Text(value);
}

/// <summary>Reads canonical order, material, readiness and storage facts for <see cref="PartialBuildPlanner"/> in one snapshot.</summary>
public sealed class PartialBuildService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    public async Task<PartialBuildPlan> PreviewAsync(ForgePrincipal principal, long orderNumber, PartialBuildOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(options);
        var decision = authorizer.Authorize(principal, Permissions.ScheduleRead, ResourceRef.Of("ProductionOrder", orderNumber.ToString(CultureInfo.InvariantCulture)));
        if (!decision.IsPermitted)
            throw new ForgeAuthorizationException(decision);
        if (options.ReceivingHours is < 0 || options.InspectionHours is < 0 || options.PutAwayHours is < 0 || options.StagingHours < 0 || options.SetupHours < 0
            || options.MinimumBatch < 0 || options.PackMultiple <= 0)
            throw new CommandRejectedException("invalid_option", "Lead times and the minimum batch cannot be negative; the pack multiple must be above zero.");

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
        if (order is null || (order.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program))))
            throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
        if (order.Status != OrderStatus.Released)
            throw new CommandRejectedException("order_not_released", $"Order {orderNumber} is {order.Status}; partial builds are planned for released orders.");

        var asOf = time.GetUtcNow();
        var material = (await MaterialPlanningService.RunMaterialAsync(uow, time, ct, principal)).Output.FirstOrDefault(m => m.OrderNumber == orderNumber);
        var answers = (await ExecutableOperations.EvaluateAsync(uow, time, principal, ct)).Where(a => a.OrderNumber == orderNumber).ToDictionary(a => a.Sequence);
        var storage = await WipStorageQueries.AvailabilityAsync(uow, order.PartNumber, asOf, ct);
        var builds = await uow.Store<IWipStorageLookup>().ListPartialBuildsAsync(true, orderNumber, ct);
        var active = builds.Count == 0 ? null : builds[0];
        return PartialBuildPlanner.Plan(order, material, answers, storage, options, asOf, active);
    }
}
