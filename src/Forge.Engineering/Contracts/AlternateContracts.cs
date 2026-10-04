using System.Globalization;

namespace Forge.Engineering.Contracts;

/// <summary>What kind of alternate a relationship is (V2 §13–20).</summary>
public enum AlternateKind
{
    /// <summary>An approved alternate for the primary, in the direction stated.</summary>
    ApprovedAlternate,

    /// <summary>Form, fit and function equivalent. Eligible for automatic substitution where the BOM line allows it.</summary>
    Equivalent,

    /// <summary>The primary is superseded by the alternate: use the alternate once primary stock is gone.</summary>
    SupersededBy,

    /// <summary>Either can be used for the other (both directions).</summary>
    Interchangeable,

    /// <summary>Usable only where its conditions hold.</summary>
    Conditional,

    /// <summary>Usable for a limited period only (an end date is required).</summary>
    Temporary,

    /// <summary>For maintenance use only (assets and machines), never in production BOMs. Approved by maintenance.</summary>
    MaintenanceOnly,

    /// <summary>Only when bought from one supplier.</summary>
    SupplierSpecific,

    /// <summary>Only for one program's work.</summary>
    ProgramSpecific,

    /// <summary>A proposal that engineering must review before anyone may use it.</summary>
    EngineeringApprovalRequired,
}

public enum AlternateStatus
{
    /// <summary>Recorded, not usable anywhere until someone else with authority approves it.</summary>
    Proposed,
    Approved,
    Rejected,

    /// <summary>No longer usable; kept as history.</summary>
    Expired,
}

/// <summary>How a BOM line lets MRP use approved alternates. The default suggests, never substitutes.</summary>
public enum SubstitutionPolicy
{
    Never,
    Suggest,

    /// <summary>MRP substitutes approved equivalents (Equivalent, Interchangeable, Approved alternate, Superseded by) automatically, up to the shortage.</summary>
    Automatic,
}

/// <summary>
/// One alternate relationship: the alternate may be used for the primary at <paramref name="Factor"/> alternate
/// units per primary unit, where every stated condition holds.
/// </summary>
public sealed record AlternateInfo(
    long AlternateId,
    string PrimaryPart,
    string AlternatePart,
    AlternateKind Kind,
    AlternateStatus Status,
    decimal Factor,
    Guid? ProgramId,
    string? Customer,
    string? ParentPart,
    string? AssetCode,
    string? MachineCode,
    int? OperationSequence,
    string? SupplierCode,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    string? QualityNotes,
    string? Evidence,
    string? ChangeNumber,
    string ProposedBy,
    DateTimeOffset ProposedAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? DecisionReason,
    string? ExpiredBy,
    DateTimeOffset? ExpiredAt,
    string? ExpiredReason)
{
    /// <summary>The conditions in words, for screens and explanations.</summary>
    public string Conditions
    {
        get
        {
            var parts = new List<string>();
            if (ParentPart is not null)
            {
                parts.Add($"in {ParentPart}");
            }

            if (ProgramId is not null)
            {
                parts.Add("for one program");
            }

            if (Customer is not null)
            {
                parts.Add($"for customer {Customer}");
            }

            if (AssetCode is not null)
            {
                parts.Add($"on asset {AssetCode}");
            }

            if (MachineCode is not null)
            {
                parts.Add($"on machine {MachineCode}");
            }

            if (OperationSequence is { } op)
            {
                parts.Add($"at op {op.ToString(CultureInfo.InvariantCulture)}");
            }

            if (SupplierCode is not null)
            {
                parts.Add($"bought from {SupplierCode}");
            }

            if (ValidFrom is { } from)
            {
                parts.Add($"from {from:yyyy-MM-dd}");
            }

            if (ValidTo is { } to)
            {
                parts.Add($"until {to:yyyy-MM-dd}");
            }

            return parts.Count == 0 ? "everywhere" : string.Join(", ", parts);
        }
    }
}

/// <summary>
/// Where an alternate would be used. A condition the relationship states but the context does not know
/// (null here) is unknown, and an unknown condition never makes a relationship usable.
/// </summary>
public sealed record AlternateContext(
    DateOnly Date,
    string? ParentPart = null,
    Guid? ProgramId = null,
    string? Customer = null,
    string? AssetCode = null,
    string? MachineCode = null,
    int? OperationSequence = null,
    string? SupplierCode = null,
    bool Maintenance = false);

public enum AlternateUsability
{
    /// <summary>Approved and every condition holds.</summary>
    Usable,

    /// <summary>Proposed, or approved but a condition does not hold or is unknown here: someone with authority must decide.</summary>
    RequiresApproval,

    /// <summary>Rejected, expired, or outside its dates: not offered.</summary>
    NotUsable,
}

/// <summary>One relationship seen from the part it would replace: the part offered and why it is (or is not) usable.</summary>
public sealed record AlternateCandidate(AlternateInfo Relationship, string OfferedPart, decimal Factor, AlternateUsability Usability, IReadOnlyList<string> Reasons);

/// <summary>Candidates for one part in one context, approved and requiring approval kept apart (never mixed).</summary>
public sealed record AlternateCandidates(string PartNumber, IReadOnlyList<AlternateCandidate> Approved, IReadOnlyList<AlternateCandidate> RequiresApproval);

/// <summary>
/// Whether an alternate may be used, decided from data only (V2 §13–20): proposed relationships are never
/// usable; rejected and expired ones are never offered; each stated condition must match the context, and a
/// condition the context cannot answer is unknown — not usable. Interchangeable relationships work both ways
/// (the reverse at the inverse factor).
/// </summary>
public static class Alternates
{
    /// <summary>Kinds MRP may substitute automatically on a line whose policy is Automatic.</summary>
    public static bool IsEquivalent(AlternateKind kind) =>
        kind is AlternateKind.Equivalent or AlternateKind.Interchangeable or AlternateKind.ApprovedAlternate or AlternateKind.SupersededBy;

    /// <summary>Every relationship that offers a part for <paramref name="part"/>, with the offered part and the factor in that direction.</summary>
    public static IEnumerable<(AlternateInfo Relationship, string OfferedPart, decimal Factor)> For(IEnumerable<AlternateInfo> relationships, string part)
    {
        ArgumentNullException.ThrowIfNull(relationships);
        foreach (var r in relationships)
        {
            if (r.PrimaryPart == part)
            {
                yield return (r, r.AlternatePart, r.Factor);
            }
            else if (r.Kind == AlternateKind.Interchangeable && r.AlternatePart == part)
            {
                yield return (r, r.PrimaryPart, decimal.Round(1m / r.Factor, 6, MidpointRounding.AwayFromZero));
            }
        }
    }

    public static (AlternateUsability Usability, IReadOnlyList<string> Reasons) Evaluate(AlternateInfo relationship, AlternateContext context)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        ArgumentNullException.ThrowIfNull(context);
        var r = relationship;
        if (r.Status is AlternateStatus.Rejected or AlternateStatus.Expired)
        {
            return (AlternateUsability.NotUsable, [$"Relationship {r.AlternateId} is {r.Status}."]);
        }

        if (r.ValidTo is { } to && context.Date > to)
        {
            return (AlternateUsability.NotUsable, [$"Relationship {r.AlternateId} ended {to:yyyy-MM-dd}."]);
        }

        if (r.ValidFrom is { } from && context.Date < from)
        {
            return (AlternateUsability.NotUsable, [$"Relationship {r.AlternateId} starts {from:yyyy-MM-dd}."]);
        }

        var reasons = new List<string>();
        if (r.Status == AlternateStatus.Proposed)
        {
            reasons.Add("Proposed: engineering approval required before anyone may use it.");
        }

        if (r.Kind == AlternateKind.MaintenanceOnly && !context.Maintenance)
        {
            return (AlternateUsability.NotUsable, ["Maintenance-only alternate: never used in production."]);
        }

        void Check(string what, string? stated, string? known)
        {
            if (stated is null)
            {
                return;
            }

            if (known is null)
            {
                reasons.Add($"Applies only {what} {stated}; not known here.");
            }
            else if (!string.Equals(stated.Trim(), known.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add($"Applies only {what} {stated}, not {known}.");
            }
        }

        Check("in", r.ParentPart, context.ParentPart);
        if (r.ProgramId is { } program)
        {
            if (context.ProgramId is not { } known)
            {
                reasons.Add("Applies only to one program; the program is not known here.");
            }
            else if (known != program)
            {
                reasons.Add("Applies only to another program: engineering may extend it to this one.");
            }
        }

        Check("for customer", r.Customer, context.Customer);
        Check("on asset", r.AssetCode, context.AssetCode);
        Check("on machine", r.MachineCode, context.MachineCode);
        Check("at op", r.OperationSequence?.ToString(CultureInfo.InvariantCulture), context.OperationSequence?.ToString(CultureInfo.InvariantCulture));
        Check("from supplier", r.SupplierCode, context.SupplierCode);
        if (r.Kind == AlternateKind.ProgramSpecific && r.ProgramId is null)
        {
            reasons.Add("Program-specific, but names no program.");
        }

        if (r.Kind == AlternateKind.SupplierSpecific && r.SupplierCode is null)
        {
            reasons.Add("Supplier-specific, but names no supplier.");
        }

        return reasons.Count == 0 ? (AlternateUsability.Usable, [$"Approved {Label(r.Kind)} ({r.Conditions})."]) : (AlternateUsability.RequiresApproval, reasons);
    }

    public static AlternateCandidates Candidates(IEnumerable<AlternateInfo> relationships, string part, AlternateContext context)
    {
        var all = For(relationships, part)
            .Select(x =>
            {
                var (usability, reasons) = Evaluate(x.Relationship, context);
                return new AlternateCandidate(x.Relationship, x.OfferedPart, x.Factor, usability, reasons);
            })
            .Where(c => c.Usability != AlternateUsability.NotUsable)
            .OrderBy(c => c.OfferedPart, StringComparer.Ordinal).ThenBy(c => c.Relationship.AlternateId)
            .ToList();
        return new AlternateCandidates(part, all.Where(c => c.Usability == AlternateUsability.Usable).ToList(), all.Where(c => c.Usability == AlternateUsability.RequiresApproval).ToList());
    }

    public static string Label(AlternateKind kind) => kind switch
    {
        AlternateKind.ApprovedAlternate => "alternate",
        AlternateKind.SupersededBy => "replacement (superseded by)",
        AlternateKind.MaintenanceOnly => "maintenance-only alternate",
        AlternateKind.SupplierSpecific => "supplier-specific alternate",
        AlternateKind.ProgramSpecific => "program-specific alternate",
        AlternateKind.EngineeringApprovalRequired => "alternate pending engineering",
        _ => kind.ToString().ToLowerInvariant() + " alternate",
    };
}

public interface IAlternateLookup
{
    /// <summary>Every relationship (all statuses), oldest first.</summary>
    Task<IReadOnlyList<AlternateInfo>> ListAlternatesAsync(CancellationToken ct);

    Task<AlternateInfo?> FindAlternateAsync(long alternateId, CancellationToken ct);
}
