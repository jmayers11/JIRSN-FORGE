using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Kernel.Engines;

namespace Forge.Planning.Engines;

/// <summary>A released order's material, as allocation sees it. Lines of the same part are added together.</summary>
public sealed record MaterialOrder(long OrderNumber, int Priority, DateOnly DueDate, IReadOnlyList<OrderMaterialInfo> Materials);

/// <summary>Material already issued to an order (issues less returns).</summary>
public sealed record MaterialIssued(long OrderNumber, string PartNumber, decimal Quantity);

/// <summary>Material on its way: an approved purchase order line or an open production order for a component.</summary>
public sealed record MaterialSupply(string Key, string PartNumber, decimal Quantity, DateOnly DueDate, string Description);

public sealed record MaterialStock(string PartNumber, decimal Available);

public sealed record MaterialSnapshot(
    DateOnly Today,
    IReadOnlyList<MaterialStock> Stock,
    IReadOnlyList<MaterialIssued> Issued,
    IReadOnlyList<MaterialSupply> Supplies,
    IReadOnlyList<MaterialOrder> Orders);

public enum MaterialState
{
    /// <summary>Everything still needed is on hand (or already issued).</summary>
    Ready,

    /// <summary>Covered only once scheduled receipts arrive, on <see cref="OrderMaterialStatus.ReadyOn"/>.</summary>
    Expected,

    /// <summary>Not covered by stock or any scheduled receipt.</summary>
    Shortfall,
}

public sealed record MaterialAllocation(string Source, string Description, decimal Quantity, DateOnly? DueDate);

public sealed record MaterialLineStatus(
    string PartNumber,
    decimal Required,
    decimal Issued,
    decimal Remaining,
    decimal FromStock,
    decimal Shortfall,
    DateOnly? ExpectedOn,
    IReadOnlyList<MaterialAllocation> Allocations);

public sealed record OrderMaterialStatus(long OrderNumber, MaterialState State, DateOnly? ReadyOn, bool Overdue, string Summary, IReadOnlyList<MaterialLineStatus> Lines);

public sealed record MaterialRequest;

/// <summary>
/// Material readiness for released orders (spec §38). Available stock, then scheduled receipts in
/// due-date order, are allocated to orders in the same documented order the scheduler uses
/// (priority, due date, order number), so two orders never count the same coil. Inspection and held
/// stock never count. Nothing is assumed about receipts beyond their recorded promised dates.
/// </summary>
public sealed class MaterialEngine : IEngine<MaterialSnapshot, MaterialRequest, IReadOnlyList<OrderMaterialStatus>>
{
    public string Name => "material-allocation";

    public string Version => "1";

    public EngineEvaluation<IReadOnlyList<OrderMaterialStatus>> Evaluate(MaterialSnapshot snapshot, MaterialRequest parameters, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var stock = snapshot.Stock.GroupBy(s => s.PartNumber, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(s => s.Available), StringComparer.Ordinal);
        var supplies = snapshot.Supplies
            .OrderBy(s => s.DueDate).ThenBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => new Pool(s))
            .ToList();
        var results = new List<OrderMaterialStatus>();
        var nodes = new List<EvidenceNode>();

        foreach (var order in snapshot.Orders.OrderBy(o => o.Priority).ThenBy(o => o.DueDate).ThenBy(o => o.OrderNumber))
        {
            var lines = new List<MaterialLineStatus>();
            foreach (var group in order.Materials.GroupBy(m => m.ComponentPartNumber, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var part = group.Key;
                var required = group.Sum(m => m.RequiredQuantity);
                var issued = snapshot.Issued.Where(i => i.OrderNumber == order.OrderNumber && i.PartNumber == part).Sum(i => i.Quantity);
                var remaining = Math.Max(0, required - issued);
                var allocations = new List<MaterialAllocation>();

                var onHand = stock.GetValueOrDefault(part);
                var fromStock = Math.Min(remaining, onHand);
                stock[part] = onHand - fromStock;
                if (fromStock > 0)
                {
                    allocations.Add(new MaterialAllocation("Stock", "Available stock", fromStock, null));
                }

                var need = remaining - fromStock;
                foreach (var pool in supplies.Where(p => p.Supply.PartNumber == part))
                {
                    if (need <= 0)
                    {
                        break;
                    }

                    var take = Math.Min(need, pool.Left);
                    if (take <= 0)
                    {
                        continue;
                    }

                    pool.Left -= take;
                    need -= take;
                    allocations.Add(new MaterialAllocation(pool.Supply.Key, pool.Supply.Description, take, pool.Supply.DueDate));
                }

                var expectedOn = allocations.Where(a => a.DueDate is not null).Select(a => a.DueDate).Max();
                lines.Add(new MaterialLineStatus(part, required, issued, remaining, fromStock, need, expectedOn, allocations));
            }

            results.Add(Status(order, lines, snapshot.Today));
            var status = results[^1];
            nodes.Add(new EvidenceNode("OrderMaterial", status.State == MaterialState.Ready ? EvidenceOutcome.Pass : EvidenceOutcome.Fail, $"Order {order.OrderNumber}: {status.Summary}"));
        }

        var root = new EvidenceNode(
            "MaterialAllocation",
            results.All(r => r.State == MaterialState.Ready) ? EvidenceOutcome.Pass : EvidenceOutcome.Fail,
            $"{results.Count} released order(s): {results.Count(r => r.State == MaterialState.Ready)} ready, {results.Count(r => r.State == MaterialState.Expected)} waiting for receipts, {results.Count(r => r.State == MaterialState.Shortfall)} short.")
        {
            Children = nodes,
        };
        return new EngineEvaluation<IReadOnlyList<OrderMaterialStatus>>(results.OrderBy(r => r.OrderNumber).ToList(), root);
    }

    private static OrderMaterialStatus Status(MaterialOrder order, IReadOnlyList<MaterialLineStatus> lines, DateOnly today)
    {
        if (lines.Count == 0)
        {
            return new OrderMaterialStatus(order.OrderNumber, MaterialState.Ready, null, false, "No material required.", lines);
        }

        var shorts = lines.Where(l => l.Shortfall > 0).ToList();
        if (shorts.Count > 0)
        {
            return new OrderMaterialStatus(
                order.OrderNumber,
                MaterialState.Shortfall,
                null,
                false,
                "Short: " + string.Join("; ", shorts.Select(l => $"{l.PartNumber} needs {Q(l.Remaining)}, {Q(l.Remaining - l.Shortfall)} covered, {Q(l.Shortfall)} with no stock or supply")) + ".",
                lines);
        }

        var waiting = lines.Where(l => l.ExpectedOn is not null).ToList();
        if (waiting.Count == 0)
        {
            var issuedAll = lines.All(l => l.Remaining == 0);
            return new OrderMaterialStatus(
                order.OrderNumber,
                MaterialState.Ready,
                null,
                false,
                issuedAll ? "All material issued." : "On hand: " + string.Join("; ", lines.Where(l => l.Remaining > 0).Select(l => $"{Q(l.Remaining)} {l.PartNumber}")) + ".",
                lines);
        }

        var readyOn = waiting.Max(l => l.ExpectedOn!.Value);
        var overdue = waiting.Any(l => l.Allocations.Any(a => a.DueDate < today));
        var detail = string.Join("; ", waiting.SelectMany(l => l.Allocations.Where(a => a.DueDate is not null).Select(a =>
            $"{Q(a.Quantity)} {l.PartNumber} from {a.Description}, due {a.DueDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" + (a.DueDate < today ? " (overdue, not received)" : string.Empty))));
        return new OrderMaterialStatus(order.OrderNumber, MaterialState.Expected, readyOn, overdue, $"Waiting for {detail}.", lines);
    }

    private static string Q(decimal value) => Quantities.Text(value);

    private sealed class Pool(MaterialSupply supply)
    {
        public MaterialSupply Supply { get; } = supply;

        public decimal Left { get; set; } = supply.Quantity;
    }
}
