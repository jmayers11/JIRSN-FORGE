using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;

namespace Forge.Kernel.Holds;

/// <summary>Why something is held (V3.10, V3 brief AS). Each kind has its own release authority and blocked activities.</summary>
public enum HoldKind
{
    Quality,
    Material,
    Production,
    Engineering,
    Customer,
    Shipping,
    Procurement,
    Configuration,
    Safety,
    Administrative,
}

/// <summary>What a hold is on.</summary>
public enum HoldTargetType
{
    Order,

    /// <summary>One operation of an order: the target is "ORDER/SEQUENCE".</summary>
    Operation,
    Part,

    /// <summary>A lot, batch, roll or serial: the target is the lot number, with its part.</summary>
    Lot,
    WorkCenter,
    Machine,
    Supplier,
    PurchaseOrder,
    Shipment,
    Program,
}

/// <summary>The downstream activities a hold can stop.</summary>
public enum HoldActivity
{
    ReleaseOrder,
    StartWork,
    ReportOutput,
    IssueMaterial,
    Ship,
    Purchase,
    Receive,
}

/// <summary>What a kind of hold stops, and the permission needed to release one (its release authority).</summary>
public sealed record HoldKindPolicy(HoldKind Kind, IReadOnlyList<HoldActivity> Blocks, string ReleasePermission, DateTimeOffset? ChangedAt = null, string? ChangedBy = null)
{
    /// <summary>The policies a new plant starts with. A security administrator can change what each kind blocks.</summary>
    public static IReadOnlyList<HoldKindPolicy> Defaults { get; } =
    [
        new(HoldKind.Quality, [HoldActivity.StartWork, HoldActivity.ReportOutput, HoldActivity.IssueMaterial, HoldActivity.Ship], Permissions.QualityDisposition),
        new(HoldKind.Material, [HoldActivity.IssueMaterial, HoldActivity.StartWork, HoldActivity.Ship], Permissions.InventoryApprove),
        new(HoldKind.Production, [HoldActivity.StartWork, HoldActivity.ReportOutput], Permissions.HoldsRelease),
        new(HoldKind.Engineering, [HoldActivity.ReleaseOrder, HoldActivity.StartWork], Permissions.EngineeringRelease),
        new(HoldKind.Customer, [HoldActivity.ReleaseOrder, HoldActivity.Ship], Permissions.ProgramsManage),
        new(HoldKind.Shipping, [HoldActivity.Ship], Permissions.ShippingRelease),
        new(HoldKind.Procurement, [HoldActivity.Purchase, HoldActivity.Receive], Permissions.PurchasingApprove),
        new(HoldKind.Configuration, [HoldActivity.ReleaseOrder, HoldActivity.StartWork, HoldActivity.Ship], Permissions.ChangesApprove),
        new(HoldKind.Safety, [HoldActivity.StartWork], Permissions.SafetyRulesApprove),
        new(HoldKind.Administrative, Enum.GetValues<HoldActivity>(), Permissions.AccessManage),
    ];
}

/// <summary>
/// A hold (V3.10): a kind, what it is on, where it came from, the evidence, the records it affects and what must happen
/// before it is released. Released once, with a reason, by someone holding the kind's release authority.
/// </summary>
/// <param name="TargetPart">For a lot, the part it is a lot of (null: that lot number of any part).</param>
/// <param name="Source">What raised it: a person's observation, "NCR 9001", "Customer call 4471".</param>
public sealed record Hold(
    long HoldId,
    HoldKind Kind,
    HoldTargetType TargetType,
    string TargetId,
    string? TargetPart,
    string Source,
    string Reason,
    string? Evidence,
    IReadOnlyList<string> AffectedRecords,
    string RequiredResolution,
    DateTimeOffset PlacedAt,
    string PlacedBy,
    DateTimeOffset? ReleasedAt = null,
    string? ReleasedBy = null,
    string? ReleaseReason = null)
{
    public bool Active => ReleasedAt is null;

    public string Target => TargetType switch
    {
        HoldTargetType.Order => $"order {TargetId}",
        HoldTargetType.Operation => $"operation {TargetId}",
        HoldTargetType.Part => $"part {TargetId}",
        HoldTargetType.Lot => TargetPart is null ? $"lot {TargetId}" : $"lot {TargetPart} {TargetId}",
        HoldTargetType.WorkCenter => $"work center {TargetId}",
        HoldTargetType.Machine => $"machine {TargetId}",
        HoldTargetType.Supplier => $"supplier {TargetId}",
        HoldTargetType.PurchaseOrder => $"PO {TargetId}",
        HoldTargetType.Shipment => $"shipment {TargetId}",
        _ => $"program {TargetId}",
    };

    public string Describe() => $"{Kind} hold {HoldId} on {Target}: {Reason}";

    /// <summary>Whether this hold's target is among the records an activity touches (not whether its kind blocks the activity).</summary>
    public bool Touches(HoldCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return TargetType switch
        {
            HoldTargetType.Order => check.OrderNumber is { } o && TargetId == Text(o),
            HoldTargetType.Operation => check.OrderNumber is { } n && check.Sequence is { } s && TargetId == $"{Text(n)}/{Text(s)}",
            HoldTargetType.Part => check.PartNumbers.Contains(TargetId, StringComparer.Ordinal),
            HoldTargetType.Lot => check.Lots.Any(l => l.LotNumber == TargetId && (TargetPart is null || l.PartNumber == TargetPart)),
            HoldTargetType.WorkCenter => check.WorkCenterCode == TargetId,
            HoldTargetType.Machine => check.MachineCode is not null && check.MachineCode == TargetId,
            HoldTargetType.Supplier => check.SupplierCode == TargetId,
            HoldTargetType.PurchaseOrder => check.PoNumber is { } po && TargetId == Text(po),
            HoldTargetType.Shipment => check.ShipmentId is { } sh && TargetId == Text(sh),
            _ => check.ProgramId is { } p && Guid.TryParse(TargetId, out var g) && g == p,
        };
    }

    /// <summary>Whether this hold stops the activity: active, its kind blocks the activity, and it is on one of the records touched.</summary>
    public bool Stops(HoldCheck check, IReadOnlyCollection<HoldKindPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(policies);
        var policy = policies.FirstOrDefault(p => p.Kind == Kind) ?? HoldKindPolicy.Defaults.First(p => p.Kind == Kind);
        return Active && policy.Blocks.Contains(check.Activity) && Touches(check);
    }

    private static string Text(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record HoldLot(string PartNumber, string LotNumber);

/// <summary>An activity about to happen and the records it touches, checked against active holds.</summary>
public sealed record HoldCheck(
    HoldActivity Activity,
    long? OrderNumber = null,
    int? Sequence = null,
    IReadOnlyList<string>? Parts = null,
    IReadOnlyList<HoldLot>? LotList = null,
    string? WorkCenterCode = null,
    string? MachineCode = null,
    string? SupplierCode = null,
    long? PoNumber = null,
    long? ShipmentId = null,
    Guid? ProgramId = null)
{
    public IReadOnlyList<string> PartNumbers => Parts ?? [];

    public IReadOnlyList<HoldLot> Lots => LotList ?? [];
}

public interface IHoldLookup
{
    /// <summary>Holds, newest first; only active ones unless <paramref name="includeReleased"/>.</summary>
    Task<IReadOnlyList<Hold>> ListHoldsAsync(bool includeReleased, CancellationToken ct);

    Task<IReadOnlyList<HoldKindPolicy>> ListKindPoliciesAsync(CancellationToken ct);
}

public interface IHoldStore
{
    Task<long> InsertHoldAsync(Hold hold, CancellationToken ct);

    Task ReleaseHoldAsync(long holdId, DateTimeOffset at, string actor, string reason, CancellationToken ct);

    Task SetKindPolicyAsync(HoldKindPolicy policy, CancellationToken ct);
}

/// <summary>Checks used by every context before an activity a hold can stop.</summary>
public static class HoldGate
{
    /// <summary>The active holds that stop this activity on these records.</summary>
    public static async Task<IReadOnlyList<Hold>> StoppingAsync(IKernelUnitOfWork uow, HoldCheck check, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var lookup = uow.Store<IHoldLookup>();
        var policies = await lookup.ListKindPoliciesAsync(ct);
        return (await lookup.ListHoldsAsync(false, ct)).Where(h => h.Stops(check, policies)).OrderBy(h => h.HoldId).ToList();
    }

    /// <summary>Refuses the activity with "on_hold" when an active hold stops it, naming each hold and what releases it.</summary>
    public static async Task RequireNoneAsync(IKernelUnitOfWork uow, HoldCheck check, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        var holds = await StoppingAsync(uow, check, ct);
        if (holds.Count > 0)
        {
            throw new CommandRejectedException("on_hold", string.Join(" ", holds.Select(h => $"{h.Describe()}. To release: {h.RequiredResolution}")));
        }
    }
}
