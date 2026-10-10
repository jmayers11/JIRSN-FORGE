using System.Globalization;
using Forge.Inventory.Contracts;
using Forge.Maintenance.Contracts;

namespace Forge.Planning;

/// <summary>The thirteen ways one thing in Forge becomes, feeds or carries another (V3.3). A move is never one of them.</summary>
public enum GenealogyRelation
{
    /// <summary>A receipt (supplier, supplier lot, purchase order) became an instance.</summary>
    Received,

    /// <summary>Part of an instance was divided off into a new instance.</summary>
    Split,

    /// <summary>An instance was combined into another where policy allowed.</summary>
    Merged,

    /// <summary>An instance was issued to a production order (the as-built record).</summary>
    Issued,

    /// <summary>Material came back from a production order, keeping its identity.</summary>
    Returned,

    /// <summary>Issued material went into the order's output.</summary>
    Consumed,

    /// <summary>Issued material was scrapped at the work center.</summary>
    Scrapped,

    /// <summary>Issued material was lost in the process.</summary>
    Lost,

    /// <summary>A new instance was cut from issued material and put back into stock.</summary>
    Remnant,

    /// <summary>A production order made an instance of its part.</summary>
    Produced,

    /// <summary>A production order made an instance of another part alongside its output.</summary>
    Byproduct,

    /// <summary>An instance left on a shipment.</summary>
    Shipped,

    /// <summary>An instance was installed in an asset.</summary>
    Installed,
}

public enum GenealogyNodeKind
{
    Instance,
    Receipt,
    Order,
    Shipment,
    Asset,
}

public sealed record GenealogyNode(string Id, GenealogyNodeKind Kind, string Label, string? PartNumber = null, string? LotNumber = null, long? OrderNumber = null, long? ShipmentId = null, string? AssetCode = null);

public sealed record GenealogyEdge(string From, string To, GenealogyRelation Relation, decimal Quantity, DateTimeOffset At, string? Note = null);

public enum TraceDirection
{
    /// <summary>Where it came from.</summary>
    Back,

    /// <summary>What it became and where it went.</summary>
    Forward,

    Both,
}

/// <summary>A trace from one node: the nodes reached and the links followed, back, forward or both.</summary>
public sealed record GenealogyTrace(GenealogyNode Start, TraceDirection Direction, IReadOnlyList<GenealogyNode> Nodes, IReadOnlyList<GenealogyEdge> Edges, bool Truncated, IReadOnlyList<string> Notes);

/// <summary>The records genealogy is built from, exactly as written.</summary>
public sealed record GenealogySources(
    IReadOnlyList<LotInfo> Lots,
    IReadOnlyList<InstanceLink> Links,
    IReadOnlyList<InventoryTransaction> Transactions,
    IReadOnlyList<TransformationInfo> Transformations,
    IReadOnlyList<AssetComponentInfo> AssetComponents,
    IReadOnlyDictionary<long, string> SupplierByPo,
    IReadOnlySet<long> HiddenOrders);

/// <summary>
/// Genealogy as a graph (V3.3, V3 brief G). Nodes are instances (a lot, roll, batch or serial of a part),
/// receipts, production orders, shipments and assets; edges are the thirteen relations, each read from a record
/// that was written when it happened: the ledger (receipts, issues, returns, production receipts, shipments,
/// installs), instance links (splits, merges) and transformation records (consumed, scrapped, lost, remnants,
/// byproducts). Moving stock creates no edge. Orders in programs the reader cannot see are left out, with
/// everything that only connects through them.
/// </summary>
public sealed class GenealogyGraph
{
    private readonly Dictionary<string, GenealogyNode> nodes = new(StringComparer.Ordinal);
    private readonly List<GenealogyEdge> edges = [];
    private readonly Dictionary<string, List<GenealogyEdge>> outgoing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<GenealogyEdge>> incoming = new(StringComparer.Ordinal);

    private GenealogyGraph()
    {
    }

    public int HiddenOrderCount { get; private set; }

    public IReadOnlyCollection<GenealogyNode> Nodes => nodes.Values;

    public IReadOnlyList<GenealogyEdge> Edges => edges;

    public static string InstanceId(string part, string lot) => $"lot:{part}/{lot}";

    public static string OrderId(long order) => "order:" + order.ToString(CultureInfo.InvariantCulture);

    public static string ShipmentId(long shipment) => "shipment:" + shipment.ToString(CultureInfo.InvariantCulture);

    public GenealogyNode? Find(string id) => nodes.GetValueOrDefault(id);

    public static GenealogyGraph Build(GenealogySources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var g = new GenealogyGraph();
        var lots = sources.Lots.ToDictionary(l => (l.PartNumber, l.LotNumber));
        string Instance(string part, string lot)
        {
            var id = InstanceId(part, lot);
            if (!g.nodes.ContainsKey(id))
            {
                var info = lots.GetValueOrDefault((part, lot));
                var kind = info?.IdentityKind ?? IdentityKind.Lot;
                g.nodes[id] = new GenealogyNode(id, GenealogyNodeKind.Instance, $"{part} {kind.ToString().ToLowerInvariant()} {lot}{(info?.PartRevision is { } rev ? " rev " + rev : string.Empty)}", part, lot);
            }

            return id;
        }

        var hidden = new HashSet<long>();
        string? Order(long order)
        {
            if (sources.HiddenOrders.Contains(order))
            {
                hidden.Add(order);
                return null;
            }

            var id = OrderId(order);
            g.nodes.TryAdd(id, new GenealogyNode(id, GenealogyNodeKind.Order, $"Order {order.ToString(CultureInfo.InvariantCulture)}", OrderNumber: order));
            return id;
        }

        var byId = sources.Transactions.ToDictionary(t => t.TransactionId);
        foreach (var t in sources.Transactions.OrderBy(t => t.TransactionId))
        {
            if (t.LotNumber is not { } lot)
            {
                continue; // without an instance there is nothing to trace
            }

            switch (t.Kind)
            {
                case InventoryTransactionKind.Receipt:
                {
                    var id = $"receipt:{t.TransactionId.ToString(CultureInfo.InvariantCulture)}";
                    var supplierLot = lots.GetValueOrDefault((t.PartNumber, lot))?.SupplierLot;
                    var supplier = t.PoNumber is { } po ? sources.SupplierByPo.GetValueOrDefault(po) : null;
                    var label = string.Join(" · ", new[]
                    {
                        "Received",
                        supplier,
                        supplierLot is null ? null : "supplier lot " + supplierLot,
                        t.PoNumber is { } n ? $"PO {n.ToString(CultureInfo.InvariantCulture)}/{t.PoLine?.ToString(CultureInfo.InvariantCulture)}" : null,
                    }.Where(x => x is not null));
                    g.nodes[id] = new GenealogyNode(id, GenealogyNodeKind.Receipt, label, t.PartNumber, lot);
                    g.Add(id, Instance(t.PartNumber, lot), GenealogyRelation.Received, t.Quantity, t.At, t.Reason);
                    break;
                }

                case InventoryTransactionKind.Issue or InventoryTransactionKind.Return or InventoryTransactionKind.ProductionReceipt when t.OrderNumber is { } number:
                {
                    if (Order(number) is not { } order)
                    {
                        break;
                    }

                    var instance = Instance(t.PartNumber, lot);
                    if (t.Kind == InventoryTransactionKind.Issue)
                    {
                        g.Add(instance, order, GenealogyRelation.Issued, t.Quantity, t.At, t.SubstitutesFor is { } p ? "in place of " + p : null);
                    }
                    else
                    {
                        g.Add(order, instance, t.Kind == InventoryTransactionKind.Return ? GenealogyRelation.Returned : GenealogyRelation.Produced, t.Quantity, t.At);
                    }

                    break;
                }

                case InventoryTransactionKind.Shipment when t.ShipmentId is { } shipment:
                {
                    var id = ShipmentId(shipment);
                    g.nodes.TryAdd(id, new GenealogyNode(id, GenealogyNodeKind.Shipment, $"Shipment {shipment.ToString(CultureInfo.InvariantCulture)}", ShipmentId: shipment));
                    g.Add(Instance(t.PartNumber, lot), id, GenealogyRelation.Shipped, t.Quantity, t.At);
                    break;
                }

                default:
                    break;
            }
        }

        foreach (var l in sources.Links)
        {
            g.Add(Instance(l.SourcePart, l.SourceLot), Instance(l.ResultPart, l.ResultLot), l.Relation == InstanceRelation.Split ? GenealogyRelation.Split : GenealogyRelation.Merged, l.Quantity, l.At, l.Reason);
        }

        foreach (var x in sources.Transformations)
        {
            if (Order(x.OrderNumber) is not { } order)
            {
                continue;
            }

            foreach (var line in x.Lines)
            {
                switch (line.Role)
                {
                    case TransformationRole.Consumed or TransformationRole.Scrap or TransformationRole.Loss when line.LotNumber is { } lot:
                        var relation = line.Role switch
                        {
                            TransformationRole.Consumed => GenealogyRelation.Consumed,
                            TransformationRole.Scrap => GenealogyRelation.Scrapped,
                            _ => GenealogyRelation.Lost,
                        };
                        g.Add(Instance(line.PartNumber, lot), order, relation, line.Quantity, x.RecordedAt, x.Reason);
                        break;
                    case TransformationRole.Remnant when line.LotNumber is { } lot && line.SourceLot is { } source:
                        g.Add(Instance(line.SourcePart ?? line.PartNumber, source), Instance(line.PartNumber, lot), GenealogyRelation.Remnant, line.Quantity, x.RecordedAt, $"cut on order {x.OrderNumber.ToString(CultureInfo.InvariantCulture)}");
                        break;
                    case TransformationRole.Byproduct when line.LotNumber is { } lot:
                        g.Add(order, Instance(line.PartNumber, lot), GenealogyRelation.Byproduct, line.Quantity, x.RecordedAt);
                        break;
                    default:
                        break;
                }
            }
        }

        foreach (var c in sources.AssetComponents)
        {
            if (c.SourceTransactionId is { } source && byId.TryGetValue(source, out var issue) && issue.LotNumber is { } lot)
            {
                var id = "asset:" + c.AssetCode;
                g.nodes.TryAdd(id, new GenealogyNode(id, GenealogyNodeKind.Asset, $"Asset {c.AssetCode}", AssetCode: c.AssetCode));
                g.Add(Instance(c.PartNumber, lot), id, GenealogyRelation.Installed, c.Quantity, c.InstalledAt, c.Position);
            }
        }

        g.HiddenOrderCount = hidden.Count;
        return g;
    }

    /// <summary>Follows links from a node: back to where it came from, forward to what it became, or both (each one way, so siblings are not pulled in).</summary>
    public GenealogyTrace Trace(string startId, TraceDirection direction, int maxDepth = 12, int maxNodes = 400)
    {
        var start = nodes.GetValueOrDefault(startId) ?? throw new KeyNotFoundException(startId);
        var reached = new Dictionary<string, GenealogyNode>(StringComparer.Ordinal) { [start.Id] = start };
        var followed = new HashSet<GenealogyEdge>();
        var truncated = false;
        void Walk(bool forward)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { start.Id };
            var frontier = new List<string> { start.Id };
            for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var id in frontier)
                {
                    foreach (var e in (forward ? outgoing : incoming).GetValueOrDefault(id) ?? [])
                    {
                        var other = forward ? e.To : e.From;
                        if (!seen.Contains(other) && reached.Count >= maxNodes)
                        {
                            truncated = true;
                            continue;
                        }

                        followed.Add(e);
                        reached[other] = nodes[other];
                        if (seen.Add(other))
                        {
                            next.Add(other);
                        }
                    }
                }

                frontier = next;
            }

            truncated |= frontier.Count > 0 && frontier.Any(id => ((forward ? outgoing : incoming).GetValueOrDefault(id)?.Count ?? 0) > 0);
        }

        if (direction is TraceDirection.Back or TraceDirection.Both)
        {
            Walk(false);
        }

        if (direction is TraceDirection.Forward or TraceDirection.Both)
        {
            Walk(true);
        }

        var notes = new List<string>();
        if (HiddenOrderCount > 0)
        {
            notes.Add($"{HiddenOrderCount} order(s) in programs you cannot see are left out, with what connects only through them.");
        }

        if (truncated)
        {
            notes.Add($"The trace stops at {maxDepth} steps or {maxNodes} records; open a record further along to continue.");
        }

        return new GenealogyTrace(start, direction, reached.Values.ToList(), edges.Where(followed.Contains).ToList(), truncated, notes);
    }

    private void Add(string from, string to, GenealogyRelation relation, decimal quantity, DateTimeOffset at, string? note = null)
    {
        var edge = new GenealogyEdge(from, to, relation, quantity, at, note);
        edges.Add(edge);
        if (!outgoing.TryGetValue(from, out var o))
        {
            outgoing[from] = o = [];
        }

        if (!incoming.TryGetValue(to, out var i))
        {
            incoming[to] = i = [];
        }

        o.Add(edge);
        i.Add(edge);
    }
}
