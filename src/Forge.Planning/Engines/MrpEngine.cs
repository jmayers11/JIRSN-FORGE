using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Engines;

namespace Forge.Planning.Engines;

/// <summary>An item as MRP plans it. Lead time is in calendar days.</summary>
public sealed record MrpItem(
    string PartNumber,
    string Description,
    string UnitOfMeasure,
    MakeBuy MakeBuy,
    int LeadTimeDays,
    decimal SafetyStock,
    LotPolicy LotPolicy,
    decimal OrderQuantity,
    decimal OrderMultiple);

/// <summary>A released revision's BOM and the local dates it is in force: [From, To).</summary>
public sealed record MrpStructure(string PartNumber, string Revision, DateOnly EffectiveFrom, DateOnly? EffectiveTo, IReadOnlyList<BomLineInfo> Bom);

/// <summary>A gross requirement: independent demand, or material for an order that already exists.</summary>
public sealed record MrpRequirement(string Key, string PartNumber, decimal Quantity, DateOnly NeedDate, int Priority, string Kind, string Description);

/// <summary>A scheduled receipt: an approved purchase order line or an open production order.</summary>
public sealed record MrpSupply(string Key, string PartNumber, decimal Quantity, DateOnly DueDate, string Kind, string Description);

/// <summary>Stock by status. Only Available counts; Inspection and Hold are shown so nothing is hidden.</summary>
public sealed record MrpStock(string PartNumber, decimal Available, decimal Inspection, decimal Hold);

public sealed record MrpSnapshot(
    DateOnly Today,
    IReadOnlyList<MrpItem> Items,
    IReadOnlyList<MrpStructure> Structures,
    IReadOnlyList<MrpStock> Stock,
    IReadOnlyList<MrpRequirement> Requirements,
    IReadOnlyList<MrpSupply> Supplies);

public sealed record MrpRequest(string? PartNumber = null);

/// <summary>How much of a requirement a supply covers.</summary>
public sealed record MrpPeg(string RequirementKey, string Kind, string Description, DateOnly NeedDate, decimal Quantity);

public sealed record PlannedOrder(
    string Key,
    string PartNumber,
    MakeBuy MakeBuy,
    decimal Quantity,
    string UnitOfMeasure,
    DateOnly ReleaseDate,
    DateOnly DueDate,
    bool PastDue,
    decimal NetRequirement,
    string? Revision,
    string Explanation,
    IReadOnlyList<MrpPeg> Pegging);

/// <summary>One date in an item's plan: what is needed, what arrives and what is left.</summary>
public sealed record MrpBucket(DateOnly Date, decimal Gross, decimal ScheduledReceipts, decimal PlannedReceipts, decimal ProjectedAvailable);

public sealed record MrpException(string PartNumber, string Kind, string Detail);

public sealed record MrpItemPlan(
    string PartNumber,
    string Description,
    string UnitOfMeasure,
    MakeBuy MakeBuy,
    int Level,
    int LeadTimeDays,
    decimal SafetyStock,
    LotPolicy LotPolicy,
    decimal OrderQuantity,
    decimal OrderMultiple,
    decimal Available,
    decimal Inspection,
    decimal Hold,
    IReadOnlyList<MrpRequirement> Requirements,
    IReadOnlyList<MrpSupply> Receipts,
    IReadOnlyList<MrpBucket> Buckets);

public sealed record MrpResult(DateOnly Today, IReadOnlyList<MrpItemPlan> Items, IReadOnlyList<PlannedOrder> PlannedOrders, IReadOnlyList<MrpException> Exceptions);

/// <summary>
/// Deterministic MRP (spec §13). Items are planned top-down by low-level code so a parent's planned
/// orders become its components' requirements before the components are netted. For each item, by
/// date: projected available = previous + scheduled receipts − gross requirements; when it would fall
/// below safety stock, a planned receipt covers the shortfall, sized by the item's lot policy and
/// released one lead time earlier. Make items explode through the BOM in force on the release date.
/// Every planned order carries its arithmetic and the requirements it is pegged to, so "why 400?"
/// has a written answer. Identical inputs give identical outputs.
/// </summary>
public sealed class MrpEngine : IEngine<MrpSnapshot, MrpRequest, MrpResult>
{
    public string Name => "mrp";

    public string Version => "1";

    public EngineEvaluation<MrpResult> Evaluate(MrpSnapshot snapshot, MrpRequest parameters, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(parameters);
        var today = snapshot.Today;
        var items = snapshot.Items.ToDictionary(i => i.PartNumber, StringComparer.Ordinal);
        var exceptions = new List<MrpException>();

        // Low-level codes from every released structure; parts on a loop cannot be exploded.
        var (order, levels, looped) = LowLevelCodes(snapshot, items.Keys);
        foreach (var part in looped)
        {
            exceptions.Add(new MrpException(part, "BomLoop", $"{part} is part of a loop in released bills of materials; its components are not planned."));
        }

        var gross = new Dictionary<string, List<MrpRequirement>>(StringComparer.Ordinal);
        foreach (var r in snapshot.Requirements)
        {
            if (!items.ContainsKey(r.PartNumber))
            {
                exceptions.Add(new MrpException(r.PartNumber, "UnknownItem", $"{r.Description} needs {r.PartNumber}, which is not in the item master."));
                continue;
            }

            Add(gross, r.PartNumber, r);
        }

        var plans = new List<MrpItemPlan>();
        var planned = new List<PlannedOrder>();
        var evidence = new List<EvidenceNode>();
        foreach (var part in order)
        {
            var item = items[part];
            var requirements = (gross.TryGetValue(part, out var list) ? list : new List<MrpRequirement>())
                .OrderBy(r => Max(r.NeedDate, today)).ThenBy(r => r.Priority).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
            var receipts = snapshot.Supplies.Where(s => s.PartNumber == part)
                .OrderBy(s => s.DueDate).ThenBy(s => s.Key, StringComparer.Ordinal).ToList();
            var stock = snapshot.Stock.FirstOrDefault(s => s.PartNumber == part) ?? new MrpStock(part, 0, 0, 0);

            foreach (var r in requirements.Where(r => r.NeedDate < today))
            {
                exceptions.Add(new MrpException(part, "PastDueRequirement", $"{r.Description}: {Q(r.Quantity)} was needed {Day(r.NeedDate)}; planned as needed today."));
            }

            foreach (var s in receipts.Where(s => s.DueDate < today))
            {
                exceptions.Add(new MrpException(part, "OverdueReceipt", $"{s.Description}: {Q(s.Quantity)} was due {Day(s.DueDate)} and has not arrived; counted as arriving today. Confirm the date with the supplier."));
            }

            var (buckets, itemPlanned, nodes) = Net(item, stock, requirements, receipts, today, exceptions);
            planned.AddRange(itemPlanned);

            // Explode planned orders of made items into their components' requirements.
            if (item.MakeBuy == MakeBuy.Make && !looped.Contains(part))
            {
                foreach (var p in itemPlanned)
                {
                    var structure = StructureAt(snapshot, part, p.ReleaseDate);
                    if (structure is null)
                    {
                        exceptions.Add(new MrpException(part, "NoRevision", $"{p.Key}: no released revision of {part} is in force on {Day(p.ReleaseDate)}, so its components are not planned."));
                        continue;
                    }

                    foreach (var line in structure.Bom.OrderBy(l => l.LineNumber))
                    {
                        if (!items.ContainsKey(line.ComponentPartNumber))
                        {
                            exceptions.Add(new MrpException(line.ComponentPartNumber, "UnknownItem", $"{part} rev {structure.Revision} line {line.LineNumber} uses {line.ComponentPartNumber}, which is not in the item master."));
                            continue;
                        }

                        var quantity = Round(p.Quantity * line.QuantityPer * (1 + (line.ScrapPercent / 100m)));
                        Add(gross, line.ComponentPartNumber, new MrpRequirement(
                            $"{p.Key}/{line.LineNumber}",
                            line.ComponentPartNumber,
                            quantity,
                            Max(p.ReleaseDate, today),
                            5,
                            "PlannedOrder",
                            $"Planned order {p.Key} ({p.Quantity} {part}, rev {structure.Revision} line {line.LineNumber})"));
                    }

                    var index = planned.IndexOf(p);
                    planned[index] = p with { Revision = structure.Revision };
                }
            }

            if (requirements.Count > 0 || receipts.Count > 0 || stock.Available + stock.Inspection + stock.Hold != 0 || item.SafetyStock > 0 || parameters.PartNumber == part)
            {
                plans.Add(new MrpItemPlan(
                    part, item.Description, item.UnitOfMeasure, item.MakeBuy, levels.GetValueOrDefault(part), item.LeadTimeDays, item.SafetyStock, item.LotPolicy, item.OrderQuantity, item.OrderMultiple,
                    stock.Available, stock.Inspection, stock.Hold, requirements, receipts, buckets));
                evidence.Add(new EvidenceNode("Item", itemPlanned.Count == 0 ? EvidenceOutcome.Pass : EvidenceOutcome.Info, itemPlanned.Count == 0
                    ? $"{part}: requirements covered by stock and scheduled receipts."
                    : $"{part}: {itemPlanned.Count} planned order(s) for {Q(itemPlanned.Sum(o => o.Quantity))} {item.UnitOfMeasure}.")
                {
                    Children = nodes,
                });
            }
        }

        var orderedPlanned = planned.OrderBy(p => p.ReleaseDate).ThenBy(p => p.PartNumber, StringComparer.Ordinal).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
        var orderedExceptions = exceptions.Distinct().OrderBy(e => e.PartNumber, StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Detail, StringComparer.Ordinal).ToList();
        if (parameters.PartNumber is { } only)
        {
            plans = plans.Where(p => p.PartNumber == only).ToList();
            orderedPlanned = orderedPlanned.Where(p => p.PartNumber == only).ToList();
            orderedExceptions = orderedExceptions.Where(e => e.PartNumber == only).ToList();
        }

        var root = new EvidenceNode(
            "Mrp",
            orderedPlanned.Count == 0 ? EvidenceOutcome.Pass : EvidenceOutcome.Info,
            $"MRP as of {Day(today)}: {plans.Count} item(s) planned, {orderedPlanned.Count} planned order(s), {orderedPlanned.Count(p => p.PastDue)} past due, {orderedExceptions.Count} exception(s).")
        {
            Children = evidence,
        };
        return new EngineEvaluation<MrpResult>(new MrpResult(today, plans, orderedPlanned, orderedExceptions), root);
    }

    /// <summary>Sizes a planned order for a net requirement under the item's lot policy and multiple.</summary>
    public static decimal LotSize(MrpItem item, decimal net)
    {
        ArgumentNullException.ThrowIfNull(item);
        var quantity = item.LotPolicy switch
        {
            LotPolicy.FixedQuantity when item.OrderQuantity > 0 => Math.Ceiling(net / item.OrderQuantity) * item.OrderQuantity,
            LotPolicy.MinimumQuantity => Math.Max(net, item.OrderQuantity),
            _ => net,
        };
        if (item.OrderMultiple > 0)
        {
            quantity = Math.Ceiling(quantity / item.OrderMultiple) * item.OrderMultiple;
        }

        return Round(quantity);
    }

    private static (List<MrpBucket> Buckets, List<PlannedOrder> Planned, List<EvidenceNode> Nodes) Net(
        MrpItem item,
        MrpStock stock,
        IReadOnlyList<MrpRequirement> requirements,
        IReadOnlyList<MrpSupply> receipts,
        DateOnly today,
        List<MrpException> exceptions)
    {
        var dates = requirements.Select(r => Max(r.NeedDate, today))
            .Concat(receipts.Select(s => Max(s.DueDate, today)))
            .Append(today)
            .Distinct()
            .Order()
            .ToList();
        var buckets = new List<MrpBucket>();
        var planned = new List<PlannedOrder>();
        var nodes = new List<EvidenceNode>();
        var available = stock.Available;
        var withoutPlanned = stock.Available;
        var firstShort = (DateOnly?)null;

        foreach (var date in dates)
        {
            var dayRequirements = requirements.Where(r => Max(r.NeedDate, today) == date).ToList();
            var dayReceipts = receipts.Where(s => Max(s.DueDate, today) == date).ToList();
            var grossQty = dayRequirements.Sum(r => r.Quantity);
            var receiptQty = dayReceipts.Sum(s => s.Quantity);
            var before = available;
            available += receiptQty - grossQty;
            withoutPlanned += receiptQty - grossQty;
            if (withoutPlanned < item.SafetyStock && firstShort is null)
            {
                firstShort = date;
            }

            var plannedQty = 0m;
            if (available < item.SafetyStock)
            {
                var net = Round(item.SafetyStock - available);
                var quantity = LotSize(item, net);
                var release = date.AddDays(-item.LeadTimeDays);
                var pastDue = release < today;
                var key = $"PLN-{item.PartNumber}-{(planned.Count + 1).ToString("000", CultureInfo.InvariantCulture)}";
                var explanation = Explain(item, date, before, receiptQty, dayRequirements, available, net, quantity, release, pastDue);
                planned.Add(new PlannedOrder(key, item.PartNumber, item.MakeBuy, quantity, item.UnitOfMeasure, Max(release, today), date, pastDue, net, null, explanation, []));
                nodes.Add(new EvidenceNode("PlannedOrder", pastDue ? EvidenceOutcome.Fail : EvidenceOutcome.Info, $"{key}: {explanation}"));
                if (pastDue)
                {
                    exceptions.Add(new MrpException(item.PartNumber, "PastDueRelease", $"{key}: {Q(quantity)} {item.UnitOfMeasure} needed {Day(date)} should have been released {Day(release)} ({item.LeadTimeDays}-day lead time). Release now and expedite."));
                }

                plannedQty = quantity;
                available += quantity;
            }

            buckets.Add(new MrpBucket(date, grossQty, receiptQty, plannedQty, available));
        }

        // A scheduled receipt due after stock first runs short should come in earlier.
        if (firstShort is { } shortOn)
        {
            foreach (var late in receipts.Where(s => Max(s.DueDate, today) > shortOn))
            {
                exceptions.Add(new MrpException(item.PartNumber, "RescheduleIn", $"{late.Description} is due {Day(late.DueDate)}, but {item.PartNumber} runs below {(item.SafetyStock > 0 ? "safety stock" : "zero")} on {Day(shortOn)}. Ask for it earlier."));
            }
        }

        return (buckets, Peg(item, stock, requirements, receipts, planned, today), nodes);
    }

    /// <summary>
    /// Pegging: stock, scheduled receipts and planned orders are consumed first-in-first-out by the
    /// requirements in need-date order. What a planned order holds beyond its pegs is safety stock or lot-size rounding.
    /// </summary>
    private static List<PlannedOrder> Peg(MrpItem item, MrpStock stock, IReadOnlyList<MrpRequirement> requirements, IReadOnlyList<MrpSupply> receipts, List<PlannedOrder> planned, DateOnly today)
    {
        if (planned.Count == 0)
        {
            return planned;
        }

        var pools = new List<(string Key, DateOnly Date, int Rank, decimal Left)> { ("stock", today, 0, stock.Available) };
        pools.AddRange(receipts.Select(s => (s.Key, Max(s.DueDate, today), 1, s.Quantity)));
        pools.AddRange(planned.Select(p => (p.Key, p.DueDate, 2, p.Quantity)));
        pools = pools.OrderBy(p => p.Date).ThenBy(p => p.Rank).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
        var pegs = planned.ToDictionary(p => p.Key, _ => new List<MrpPeg>(), StringComparer.Ordinal);

        foreach (var r in requirements)
        {
            var need = r.Quantity;
            for (var i = 0; i < pools.Count && need > 0; i++)
            {
                if (pools[i].Left <= 0)
                {
                    continue;
                }

                var take = Math.Min(need, pools[i].Left);
                pools[i] = pools[i] with { Left = pools[i].Left - take };
                need -= take;
                if (pegs.TryGetValue(pools[i].Key, out var list))
                {
                    list.Add(new MrpPeg(r.Key, r.Kind, r.Description, Max(r.NeedDate, today), Round(take)));
                }
            }
        }

        return planned.Select(p =>
        {
            var list = pegs[p.Key];
            var left = Round(p.Quantity - list.Sum(x => x.Quantity));
            if (left > 0)
            {
                list.Add(new MrpPeg("stock", item.SafetyStock > 0 ? "SafetyStock" : "LotSize", item.SafetyStock > 0 ? $"Kept on hand: safety stock {Q(item.SafetyStock)} and lot-size rounding" : "Lot-size rounding, kept on hand", p.DueDate, left));
            }

            return p with { Pegging = list };
        }).ToList();
    }

    private static string Explain(MrpItem item, DateOnly date, decimal before, decimal receipts, IReadOnlyList<MrpRequirement> due, decimal after, decimal net, decimal quantity, DateOnly release, bool pastDue)
    {
        var needs = due.Count == 0 ? "no new requirements" : $"requirements of {Q(due.Sum(r => r.Quantity))} ({string.Join("; ", due.Select(r => $"{r.Description} {Q(r.Quantity)}"))})";
        var policy = item.LotPolicy switch
        {
            LotPolicy.FixedQuantity => $"fixed lots of {Q(item.OrderQuantity)}",
            LotPolicy.MinimumQuantity => $"a minimum order of {Q(item.OrderQuantity)}",
            _ => "lot-for-lot",
        };
        var multiple = item.OrderMultiple > 0 ? $", in multiples of {Q(item.OrderMultiple)}" : string.Empty;
        var timing = pastDue
            ? $"With a {item.LeadTimeDays}-day lead time it should have been released {Day(release)}: release now and expedite."
            : $"With a {item.LeadTimeDays}-day lead time, release {Day(release)}.";
        return $"On {Day(date)}: {Q(before)} available, plus receipts of {Q(receipts)}, less {needs}, leaves {Q(after)}. "
            + $"Safety stock is {Q(item.SafetyStock)}, so {Q(net)} more is needed. {char.ToUpperInvariant(policy[0])}{policy[1..]}{multiple} makes the order {Q(quantity)}. {timing}";
    }

    private static MrpStructure? StructureAt(MrpSnapshot snapshot, string part, DateOnly date) =>
        snapshot.Structures
            .Where(s => s.PartNumber == part && s.EffectiveFrom <= date && (s.EffectiveTo is null || date < s.EffectiveTo))
            .OrderByDescending(s => s.EffectiveFrom)
            .ThenBy(s => s.Revision, StringComparer.Ordinal)
            .FirstOrDefault()
        // A date before the first release (a past-due order) uses the earliest released revision still in force.
        ?? snapshot.Structures
            .Where(s => s.PartNumber == part && (s.EffectiveTo is null || date < s.EffectiveTo))
            .OrderBy(s => s.EffectiveFrom)
            .ThenBy(s => s.Revision, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>Topological order (parents first), each part's low-level code, and parts caught in a loop.</summary>
    private static (List<string> Order, Dictionary<string, int> Levels, HashSet<string> Looped) LowLevelCodes(MrpSnapshot snapshot, IEnumerable<string> parts)
    {
        var known = parts.ToHashSet(StringComparer.Ordinal);
        var edges = snapshot.Structures
            .SelectMany(s => s.Bom.Select(l => (Parent: s.PartNumber, Child: l.ComponentPartNumber)))
            .Where(e => known.Contains(e.Parent) && known.Contains(e.Child) && e.Parent != e.Child)
            .Distinct()
            .ToList();
        var indegree = known.ToDictionary(p => p, _ => 0, StringComparer.Ordinal);
        foreach (var e in edges)
        {
            indegree[e.Child]++;
        }

        var levels = known.ToDictionary(p => p, _ => 0, StringComparer.Ordinal);
        var ready = new SortedSet<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key), StringComparer.Ordinal);
        var order = new List<string>();
        while (ready.Count > 0)
        {
            var part = ready.Min!;
            ready.Remove(part);
            order.Add(part);
            foreach (var e in edges.Where(e => e.Parent == part))
            {
                levels[e.Child] = Math.Max(levels[e.Child], levels[part] + 1);
                if (--indegree[e.Child] == 0)
                {
                    ready.Add(e.Child);
                }
            }
        }

        var looped = known.Where(p => !order.Contains(p)).ToHashSet(StringComparer.Ordinal);
        order.AddRange(looped.Order(StringComparer.Ordinal));

        // Plan by level so every parent of a part is netted before it.
        var byLevel = order.Select((p, i) => (p, i)).OrderBy(x => levels[x.p]).ThenBy(x => x.i).Select(x => x.p).ToList();
        return (byLevel, levels, looped);
    }

    private static void Add(Dictionary<string, List<MrpRequirement>> gross, string part, MrpRequirement requirement)
    {
        if (!gross.TryGetValue(part, out var list))
        {
            list = [];
            gross[part] = list;
        }

        list.Add(requirement);
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static string Q(decimal value) => Quantities.Text(value);

    private static decimal Round(decimal value) => Quantities.Normal(decimal.Round(value, 6, MidpointRounding.AwayFromZero));

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
