using System.Globalization;
using Forge.Inventory.Contracts;
using Forge.Procurement.Contracts;
using Forge.Projects.Contracts;

namespace Forge.Planning.Engines;

/// <summary>Everything a project's material check is decided from, read at one instant.</summary>
/// <param name="Available">Available stock on hand by part (not reserved: other work may take it first).</param>
/// <param name="Demands">Open demand, of which the lines' own (by source key) is shown.</param>
public sealed record ProjectMaterialRequest(
    ProjectInfo Project,
    IReadOnlyList<ProjectLine> Lines,
    IReadOnlyList<ProjectMaterial> Moved,
    IReadOnlyDictionary<string, decimal> Available,
    IReadOnlyList<DemandInfo> Demands,
    IReadOnlyList<RequisitionInfo> Requisitions,
    IReadOnlyList<PurchaseOrderInfo> PurchaseOrders);

/// <param name="Status">Issued, In stock, On order, Requested, Short, or Cancelled.</param>
/// <param name="OnOrder">Still to arrive on purchase lines bought for this line.</param>
/// <param name="Requested">On open requisitions for this line, not yet ordered.</param>
public sealed record ProjectLineMaterial(
    int LineNumber,
    string PartNumber,
    string? Description,
    DateOnly NeedDate,
    string SourceKey,
    decimal Required,
    decimal Issued,
    decimal StillNeeded,
    decimal Received,
    decimal FromStock,
    decimal OnOrder,
    decimal Requested,
    decimal Shortage,
    long? DemandId,
    IReadOnlyList<long> RequisitionIds,
    IReadOnlyList<string> PurchaseLines,
    string Status,
    string Detail);

public sealed record ProjectMaterialCheck(string ProjectCode, IReadOnlyList<ProjectLineMaterial> Lines, int ShortLines, string Summary);

/// <summary>
/// Checks a project's material lines against what has been issued to them, stock on hand (shared between lines
/// in line order, and with nothing reserved), purchases bought for each line and requisitions not yet ordered.
/// What is left is the shortage, which becomes project demand and requisitions sourced to the line. Pure.
/// </summary>
public static class ProjectMaterialEngine
{
    public static ProjectMaterialCheck Check(ProjectMaterialRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var free = r.Available.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var results = new List<ProjectLineMaterial>();
        foreach (var line in r.Lines.OrderBy(l => l.NeedDate).ThenBy(l => l.LineNumber))
        {
            var key = line.SourceKey;
            var moved = r.Moved.Where(m => m.ProjectCode == line.ProjectCode && m.LineNumber == line.LineNumber).ToList();
            var issued = moved.Sum(m => m.NetIssued);
            var received = moved.Sum(m => m.Received);
            var demand = r.Demands.Where(d => d.SourceKey == key && d.Status == DemandStatus.Open).OrderBy(d => d.DemandId).FirstOrDefault();
            var requisitions = r.Requisitions.Where(q => q.SourceKey == key && q.Status != RequisitionStatus.Cancelled).ToList();
            var bought = r.PurchaseOrders
                .Where(o => o.Status is PurchaseOrderStatus.Draft or PurchaseOrderStatus.Approved)
                .SelectMany(o => o.Lines.Where(l => l.SourceKey == key && l.Status == PurchaseLineStatus.Open).Select(l => (Po: o, Line: l)))
                .ToList();
            var purchaseLines = bought.Select(b => $"PO {b.Po.PoNumber} line {b.Line.LineNumber}{(b.Po.Status == PurchaseOrderStatus.Draft ? " (draft)" : string.Empty)}").ToList();
            var requisitionIds = requisitions.Select(q => q.RequisitionId).ToList();
            if (line.Status == ProjectLineStatus.Cancelled)
            {
                results.Add(new ProjectLineMaterial(line.LineNumber, line.PartNumber, line.Description, line.NeedDate, key, line.Quantity, issued, 0, received, 0, 0, 0, 0,
                    demand?.DemandId, requisitionIds, purchaseLines, "Cancelled",
                    issued > 0 ? $"Line cancelled with {Q(issued)} issued; return what is unused." : "Line cancelled."));
                continue;
            }

            var still = Math.Max(0, line.Quantity - issued);
            var onOrder = Math.Max(0, bought.Sum(b => b.Line.Quantity) - received);
            var requested = requisitions.Where(q => q.Status == RequisitionStatus.Open).Sum(q => q.Quantity);
            var stock = free.TryGetValue(line.PartNumber, out var q0) ? q0 : 0;
            var fromStock = Math.Min(stock, still);
            free[line.PartNumber] = stock - fromStock;
            var shortage = Math.Max(0, still - fromStock - onOrder - requested);
            var status = still == 0 ? "Issued"
                : fromStock >= still ? "In stock"
                : shortage > 0 ? "Short"
                : requested > 0 && fromStock + onOrder < still ? "Requested"
                : "On order";
            var parts = new List<string> { still == 0 ? $"{Q(issued)} of {Q(line.Quantity)} issued" : $"{Q(still)} still needed of {Q(line.Quantity)}{(issued > 0 ? $" ({Q(issued)} issued)" : string.Empty)}" };
            if (still > 0)
            {
                parts.Add(fromStock > 0 ? $"{Q(fromStock)} from stock on hand" : "none in stock");
                if (received > 0)
                {
                    parts.Add($"{Q(received)} received for this line");
                }

                if (onOrder > 0)
                {
                    parts.Add($"{Q(onOrder)} on order ({string.Join(", ", purchaseLines)})");
                }

                if (requested > 0)
                {
                    parts.Add($"{Q(requested)} requested (requisition {string.Join(", ", requisitions.Where(q => q.Status == RequisitionStatus.Open).Select(q => q.RequisitionId))})");
                }

                if (shortage > 0)
                {
                    parts.Add($"short {Q(shortage)}");
                }
            }

            results.Add(new ProjectLineMaterial(line.LineNumber, line.PartNumber, line.Description, line.NeedDate, key, line.Quantity, issued, still, received, fromStock, onOrder, requested,
                shortage, demand?.DemandId, requisitionIds, purchaseLines, status, $"Line {line.LineNumber} ({line.PartNumber}): {string.Join("; ", parts)}."));
        }

        var ordered = results.OrderBy(l => l.LineNumber).ToList();
        var shortLines = ordered.Count(l => l.Status == "Short");
        var active = ordered.Count(l => l.Status != "Cancelled");
        var summary = active == 0 ? $"Project {r.Project.Code} has no material lines."
            : shortLines == 0 ? $"Project {r.Project.Code}: every one of {active} material line(s) is issued, in stock, on order or requested."
            : $"Project {r.Project.Code}: {shortLines} of {active} material line(s) short — " + string.Join(", ", ordered.Where(l => l.Status == "Short").Select(l => $"line {l.LineNumber} {Q(l.Shortage)} {l.PartNumber}")) + ".";
        return new ProjectMaterialCheck(r.Project.Code, ordered, shortLines, summary);
    }

    private static string Q(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
