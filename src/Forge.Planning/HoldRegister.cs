using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Holds;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;

namespace Forge.Planning;

/// <summary>
/// One entry of the plant's hold register (V3.10): a hold placed in the kernel, or a hold another context keeps by its
/// own rules (stock on quality hold, an undecided NCR, an inspection not passed) shown as a kind of hold.
/// </summary>
/// <param name="HoldId">The kernel hold id; null for a hold another context keeps.</param>
/// <param name="Reference">"501", "NCR 9001", "Inspection 10001/10", "Stock COIL-AL L-7 at QUAR".</param>
/// <param name="ReleasedThrough">How it is released: "Release (hold)", "NCR disposition", "Passing inspection", "Quality release of stock".</param>
public sealed record HoldEntry(
    long? HoldId,
    string Reference,
    HoldKind Kind,
    HoldTargetType TargetType,
    string Target,
    string Source,
    string Reason,
    string? Evidence,
    IReadOnlyList<string> AffectedRecords,
    string RequiredResolution,
    IReadOnlyList<HoldActivity> Blocks,
    string ReleaseAuthority,
    string ReleasedThrough,
    bool Active,
    DateTimeOffset? PlacedAt,
    string? PlacedBy,
    DateTimeOffset? ReleasedAt,
    string? ReleasedBy,
    string? ReleaseReason,
    Guid? ProgramId);

/// <summary>The hold register: kernel holds and the holds Quality and Inventory keep, in one list (V3.10).</summary>
public static class HoldRegister
{
    public static async Task<IReadOnlyList<HoldEntry>> ListAsync(IKernelUnitOfWork uow, ForgePrincipal principal, bool includeReleased, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(principal);
        var lookup = uow.Store<IHoldLookup>();
        var policies = await lookup.ListKindPoliciesAsync(ct);
        var orders = (await uow.Store<IExecutionLookup>().ListOrdersAsync(ct)).ToDictionary(o => o.OrderNumber);
        bool Visible(Guid? program) => program is not { } p || principal.Programs.Contains(new ProgramId(p));
        Guid? ProgramOf(long? order) => order is { } n && orders.TryGetValue(n, out var o) ? o.ProgramId : null;

        var entries = new List<HoldEntry>();
        foreach (var h in await lookup.ListHoldsAsync(includeReleased, ct))
        {
            var program = h.TargetType switch
            {
                HoldTargetType.Order => ProgramOf(long.Parse(h.TargetId, CultureInfo.InvariantCulture)),
                HoldTargetType.Operation => ProgramOf(long.Parse(h.TargetId.Split('/')[0], CultureInfo.InvariantCulture)),
                HoldTargetType.Program => Guid.Parse(h.TargetId),
                _ => null,
            };
            var policy = HoldService.PolicyOf(policies, h.Kind);
            entries.Add(new HoldEntry(h.HoldId, h.HoldId.ToString(CultureInfo.InvariantCulture), h.Kind, h.TargetType, h.Target, h.Source, h.Reason, h.Evidence, h.AffectedRecords,
                h.RequiredResolution, policy.Blocks, policy.ReleasePermission, "Release (hold)", h.Active, h.PlacedAt, h.PlacedBy, h.ReleasedAt, h.ReleasedBy, h.ReleaseReason, program));
        }

        // Holds other contexts keep by their own rules: shown as kinds of hold, released through their own process.
        var quality = HoldService.PolicyOf(policies, HoldKind.Quality);
        foreach (var ncr in await uow.Store<IQualityLookup>().ListNcrsAsync(false, ct))
        {
            if (ncr.Status is not (NcrStatus.Open or NcrStatus.AwaitingApproval))
            {
                continue;
            }

            var (type, target) = ncr.OrderNumber is { } n ? (HoldTargetType.Order, $"order {n}") : ncr.LotNumber is { } lot ? (HoldTargetType.Lot, $"lot {ncr.PartNumber} {lot}") : (HoldTargetType.Part, $"part {ncr.PartNumber}");
            var affected = new List<string> { $"part:{ncr.PartNumber}" };
            if (ncr.OrderNumber is { } order)
            {
                affected.Add($"order:{order}");
            }

            if (ncr.LotNumber is { } l)
            {
                affected.Add($"lot:{l}");
            }

            entries.Add(new HoldEntry(null, $"NCR {ncr.NcrId}", HoldKind.Quality, type, target, $"NCR {ncr.NcrId}", $"{ncr.Defect}: {ncr.Description}", null, affected,
                ncr.Status == NcrStatus.AwaitingApproval ? "A second person approves the disposition." : "Quality dispositions the nonconformance.",
                quality.Blocks, Permissions.QualityDisposition, "NCR disposition", true, ncr.RaisedAt, ncr.RaisedBy, null, null, null, ProgramOf(ncr.OrderNumber)));
        }

        foreach (var summary in orders.Values.Where(o => o.Status is OrderStatus.Released or OrderStatus.Held).Where(o => Visible(o.ProgramId)).OrderBy(o => o.OrderNumber))
        {
            var order = await uow.Store<IExecutionLookup>().FindOrderAsync(summary.OrderNumber, ct)
                ?? throw new InvalidOperationException($"Order {summary.OrderNumber} disappeared from the snapshot.");
            var quality2 = await uow.Store<IQualityLookup>().GetOrderQualityAsync(order.OrderNumber, order.PartNumber, ct);
            foreach (var sequence in quality2.PlannedSequences.Where(s => !quality2.PassedSequences.Contains(s)).Order())
            {
                // Only an inspection the work has reached holds anything: the operation after it waits.
                var reached = order.Operations.FirstOrDefault(o => o.Sequence == sequence)?.Status == OperationStatus.Complete;
                if (!reached)
                {
                    continue;
                }

                entries.Add(new HoldEntry(null, $"Inspection {order.OrderNumber}/{sequence}", HoldKind.Quality, HoldTargetType.Order, $"order {order.OrderNumber} after operation {sequence}",
                    "Inspection plan", $"The inspection after operation {sequence} has not passed.", null, [$"order:{order.OrderNumber}"], "The inspection passes.",
                    [HoldActivity.StartWork], Permissions.QualityInspect, "Passing inspection", true, null, null, null, null, null, order.ProgramId));
            }
        }

        foreach (var stock in (await uow.Store<IInventoryLookup>().ListBalancesAsync(null, ct)).Where(b => b.Status == StockStatus.Hold && b.Quantity > 0)
                     .OrderBy(b => b.PartNumber, StringComparer.Ordinal).ThenBy(b => b.LotNumber, StringComparer.Ordinal).ThenBy(b => b.LocationCode, StringComparer.Ordinal))
        {
            var lot = stock.LotNumber;
            entries.Add(new HoldEntry(null, $"Stock {stock.PartNumber} {lot ?? "(no lot)"} at {stock.LocationCode}", HoldKind.Quality, lot is null ? HoldTargetType.Part : HoldTargetType.Lot,
                lot is null ? $"part {stock.PartNumber} at {stock.LocationCode}" : $"lot {stock.PartNumber} {lot}", "Stock status",
                $"{stock.Quantity.ToString("0.###", CultureInfo.InvariantCulture)} on quality hold at {stock.LocationCode}.", null,
                lot is null ? new List<string> { $"part:{stock.PartNumber}" } : new List<string> { $"part:{stock.PartNumber}", $"lot:{lot}" }, "Quality releases the stock.",
                [HoldActivity.IssueMaterial, HoldActivity.Ship], Permissions.InventoryApprove, "Quality release of stock", true, null, null, null, null, null, null));
        }

        return entries.Where(e => Visible(e.ProgramId)).ToList();
    }
}
