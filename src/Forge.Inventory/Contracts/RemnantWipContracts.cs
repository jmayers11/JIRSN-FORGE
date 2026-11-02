using System.Globalization;
using Forge.Kernel.Access;

namespace Forge.Inventory.Contracts;

/// <summary>The unit a piece's size is recorded in.</summary>
public enum SizeUnit
{
    Millimetre,
    Centimetre,
    Metre,
    Inch,
    Foot,
}

/// <summary>A piece's size, or the smallest piece a job can use. A size not given is not recorded (or, for a minimum, not checked).</summary>
public sealed record PieceSize(decimal? Length, decimal? Width, decimal? Thickness, SizeUnit Unit)
{
    public static decimal ToMillimetres(decimal value, SizeUnit unit) => unit switch
    {
        SizeUnit.Centimetre => value * 10m,
        SizeUnit.Metre => value * 1000m,
        SizeUnit.Inch => value * 25.4m,
        SizeUnit.Foot => value * 304.8m,
        _ => value,
    };

    public static string Symbol(SizeUnit unit) => unit switch
    {
        SizeUnit.Centimetre => "cm",
        SizeUnit.Metre => "m",
        SizeUnit.Inch => "in",
        SizeUnit.Foot => "ft",
        _ => "mm",
    };

    public string Describe()
    {
        var parts = new[] { Length, Width, Thickness }.Where(v => v is not null).Select(v => v!.Value.ToString("0.###", CultureInfo.InvariantCulture)).ToList();
        return parts.Count == 0 ? "no size" : string.Join(" × ", parts) + " " + Symbol(Unit);
    }

    public bool HasAny => Length is not null || Width is not null || Thickness is not null;
}

/// <summary>The size recorded on a lot: usually a leftover piece (remnant) put back into stock.</summary>
public sealed record LotDimension(string PartNumber, string LotNumber, PieceSize Size, string RecordedBy, DateTimeOffset RecordedAt);

/// <summary>The smallest piece of a component a parent part's job can use: the BOM line's minimum piece size.</summary>
public sealed record PieceSizeRule(string ParentPart, string ComponentPart, PieceSize Minimum, string SetBy, DateTimeOffset SetAt);

/// <summary>Whether a piece is big enough. Length and width may be swapped (a piece can be turned); thickness may not.</summary>
public static class PieceFit
{
    public static bool Fits(PieceSize piece, PieceSize minimum, out string why)
    {
        ArgumentNullException.ThrowIfNull(piece);
        ArgumentNullException.ThrowIfNull(minimum);
        decimal? Mm(decimal? v, SizeUnit u) => v is { } x ? PieceSize.ToMillimetres(x, u) : null;
        var (l, w, t) = (Mm(piece.Length, piece.Unit), Mm(piece.Width, piece.Unit), Mm(piece.Thickness, piece.Unit));
        var (ml, mw, mt) = (Mm(minimum.Length, minimum.Unit), Mm(minimum.Width, minimum.Unit), Mm(minimum.Thickness, minimum.Unit));
        var missing = new List<string>();
        if (ml is not null && l is null) missing.Add("length");
        if (mw is not null && w is null) missing.Add("width");
        if (mt is not null && t is null) missing.Add("thickness");
        if (missing.Count > 0)
        {
            why = $"its {string.Join(" and ", missing)} is not recorded";
            return false;
        }

        static bool AtLeast(decimal? have, decimal? need) => need is null || have >= need;
        var straight = AtLeast(l, ml) && AtLeast(w, mw);
        var turned = l is not null && w is not null && AtLeast(w, ml) && AtLeast(l, mw);
        if (!straight && !turned)
        {
            why = $"{piece.Describe()} is smaller than the {minimum.Describe()} needed";
            return false;
        }

        if (!AtLeast(t, mt))
        {
            why = $"{piece.Describe()} is thinner than the {minimum.Describe()} needed";
            return false;
        }

        why = turned && !straight ? $"{piece.Describe()} fits {minimum.Describe()} turned" : $"{piece.Describe()} fits {minimum.Describe()}";
        return true;
    }

    /// <summary>How much material a piece is, for choosing the smallest that fits: area (or length) times thickness, in mm.</summary>
    public static decimal Measure(PieceSize piece)
    {
        ArgumentNullException.ThrowIfNull(piece);
        decimal V(decimal? v) => v is { } x ? PieceSize.ToMillimetres(x, piece.Unit) : 1m;
        return V(piece.Length) * V(piece.Width) * V(piece.Thickness);
    }
}

/// <summary>A specific lot set aside for an order when an allocation picked it (the smallest piece that fits). Others may not issue it.</summary>
/// <param name="Drawn">What the order has since issued from the lot.</param>
public sealed record LotEarmark(long EarmarkId, long OrderNumber, string PartNumber, string LotNumber, decimal Quantity, decimal Drawn, string CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset? ReleasedAt)
{
    public decimal Outstanding => ReleasedAt is null ? Math.Max(0m, Quantity - Drawn) : 0m;
}

public enum WipAllocationStatus
{
    /// <summary>The work-in-progress order's output is promised to the consuming order.</summary>
    Active,

    /// <summary>The output was received and reserved for the consuming order.</summary>
    Fulfilled,

    /// <summary>A person released the promise, or it was no longer needed.</summary>
    Released,
}

/// <summary>
/// Output of an open production order promised to another order that is short of the part (owner decision 2026-11-02: only output no
/// customer or forecast demand is counting on). When the output is received it is reserved for the consuming order.
/// </summary>
public sealed record WipAllocation(long AllocationId, long WipOrderNumber, long ConsumerOrderNumber, string PartNumber, decimal Quantity, decimal Fulfilled, WipAllocationStatus Status,
    string Why, string ApprovedBy, DateTimeOffset ApprovedAt, string? ClosedBy = null, DateTimeOffset? ClosedAt = null, string? CloseReason = null)
{
    public decimal Outstanding => Status == WipAllocationStatus.Active ? Math.Max(0m, Quantity - Fulfilled) : 0m;
}

/// <summary>Open customer, forecast or other independent demand for a part that open production orders are counting on.</summary>
public sealed record IndependentDemand(long DemandId, string Reference, DateOnly NeedDate, decimal Quantity);

/// <summary>Demand belongs to Planning; the host supplies what is still open for a part, net of what is already reserved for it.</summary>
public interface IWipDemandSource
{
    Task<IReadOnlyList<IndependentDemand>> OpenDemandAsync(IKernelUnitOfWork uow, string partNumber, CancellationToken ct);
}

/// <summary>What one open production order's output is counting on, for the WIP page and the optimizer.</summary>
/// <param name="Projected">Order quantity less scrap and what has been received.</param>
/// <param name="Pegged">Output open demand for the part counts on (after free stock).</param>
/// <param name="Promised">Output already promised to other orders.</param>
public sealed record WipSupply(long OrderNumber, string PartNumber, string Revision, string Status, DateOnly DueDate, Guid? ProgramId, decimal Projected, decimal Pegged, decimal Promised,
    decimal Unpegged, IReadOnlyList<string> RemainingOperations, IReadOnlyList<string> PeggedTo);
