using Forge.Kernel.Access;
using Forge.Kernel.Commands;

namespace Forge.Hazcom.Contracts;

// Hazard communication (owner decisions 2026-11-01): an EHS Coordinator keeps the chemical register and approves safety data sheets;
// a hazardous material without a current, approved SDS cannot be bought or received; operators open SDS at the kiosk, anyone signed in
// can read them, and a printable binder backs them up; an SDS is reviewed every three years and when the supplier revises it.

public enum SignalWord { None, Warning, Danger }

public enum SdsStatus
{
    /// <summary>Uploaded, waiting for the EHS Coordinator.</summary>
    Submitted,

    /// <summary>The SDS in force for the material.</summary>
    Approved,

    /// <summary>Not accepted (wrong product, incomplete, not in English …).</summary>
    Rejected,

    /// <summary>Replaced by a newer approved revision; kept for the record.</summary>
    Superseded,
}

/// <summary>The GHS pictograms an SDS and label may carry.</summary>
public static class GhsPictograms
{
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GHS01"] = "Exploding bomb (explosives)",
        ["GHS02"] = "Flame (flammables)",
        ["GHS03"] = "Flame over circle (oxidizers)",
        ["GHS04"] = "Gas cylinder (gases under pressure)",
        ["GHS05"] = "Corrosion",
        ["GHS06"] = "Skull and crossbones (acute toxicity)",
        ["GHS07"] = "Exclamation mark (irritant, harmful)",
        ["GHS08"] = "Health hazard",
        ["GHS09"] = "Environment",
    };
}

/// <summary>A material in the chemical register.</summary>
/// <param name="Hazardous">Whether the material is a hazardous chemical; only hazardous ones need an SDS before buying or receiving.</param>
/// <param name="TrainingReviewDue">The chemical became hazardous and the EHS Coordinator has not yet decided whether HazCom training covers it.</param>
public sealed record ChemicalInfo(string PartNumber, bool Hazardous, SignalWord SignalWord, IReadOnlyList<string> Pictograms, string? HazardStatements, string? Ppe, string? Storage,
    string UpdatedBy, DateTimeOffset UpdatedAt, bool TrainingReviewDue = false, string? TrainingReviewedBy = null, DateTimeOffset? TrainingReviewedAt = null,
    string? TrainingReviewNote = null);

/// <param name="ReviewDue">When the EHS Coordinator should confirm the SDS is still the supplier's latest (three years from its revision date, or from the last review).</param>
public sealed record SdsInfo(
    long SdsId,
    string PartNumber,
    string Manufacturer,
    string ProductName,
    DateOnly RevisionDate,
    string FileSha256,
    string? FileName,
    SdsStatus Status,
    string SubmittedBy,
    DateTimeOffset SubmittedAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? DecisionNote,
    DateOnly? ReviewDue,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt);

public interface IHazcomLookup
{
    Task<IReadOnlyList<ChemicalInfo>> ListChemicalsAsync(CancellationToken ct);

    Task<ChemicalInfo?> FindChemicalAsync(string partNumber, CancellationToken ct);

    /// <summary>Every SDS, or one material's, newest first.</summary>
    Task<IReadOnlyList<SdsInfo>> ListSdsAsync(string? partNumber, CancellationToken ct);

    Task<SdsInfo?> FindSdsAsync(long sdsId, CancellationToken ct);

    /// <summary>The approved SDS in force for a material, if any.</summary>
    Task<SdsInfo?> CurrentSdsAsync(string partNumber, CancellationToken ct);
}

/// <summary>The rule purchasing and receiving apply: a hazardous material needs a current, approved SDS first.</summary>
public static class SdsGate
{
    public static async Task RequireAsync(IKernelUnitOfWork uow, string partNumber, string what, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var lookup = uow.Store<IHazcomLookup>();
        if (await lookup.FindChemicalAsync(partNumber, ct) is not { Hazardous: true }) return;
        if (await lookup.CurrentSdsAsync(partNumber, ct) is null)
            throw new CommandRejectedException("sds_required", $"{what}: {partNumber} is a hazardous chemical with no approved safety data sheet. Ask the EHS Coordinator to approve its SDS first.");
    }
}
