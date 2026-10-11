using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Engines;
using Forge.Workforce.Contracts;

namespace Forge.Planning.Engines;

/// <summary>A controlled document revision an operation is worked to, which operators must be trained on (V3.9).</summary>
/// <param name="OperationSequence">Null: the document applies to every operation of the order.</param>
public sealed record TrainingRequirement(string DocumentNumber, string Revision, int? OperationSequence)
{
    public string Describe() => $"{DocumentNumber} rev {Revision}";
}

/// <summary>Training on controlled documents for an order: the plant's policy and the revisions that apply (V3.9).</summary>
public sealed record OrderTraining(TrainingPolicy Policy, IReadOnlyList<TrainingRequirement> Requirements)
{
    public IReadOnlyList<TrainingRequirement> For(int sequence) =>
        Requirements.Where(r => r.OperationSequence is null || r.OperationSequence == sequence).ToList();
}

/// <summary>Released, usable stock at the point of use of each work center, and which work centers have such a location (V3.9).</summary>
public sealed record OrderPointOfUse(IReadOnlyList<StockPosition> AtPointOfUse, IReadOnlySet<string> WorkCentersWithPointOfUse);

/// <summary>How far presence evidence goes for one person (V3.9, V3 brief AC). A schedule is never presence.</summary>
public enum PresenceLevel
{
    /// <summary>No presence evidence and not on shift, or unavailable, or inactive.</summary>
    NotPresent,

    /// <summary>On a shift right now by the schedule, with no presence evidence. Never counted as present.</summary>
    Scheduled,

    /// <summary>Clocked in, or confirmed on site by a supervisor.</summary>
    ClockedIn,

    /// <summary>Present and assigned to a work center.</summary>
    AssignedToWorkCenter,

    /// <summary>Present with open direct labor on an order operation.</summary>
    ActiveOnOperation,
}

/// <param name="Source">How presence was established: ClockedIn or SupervisorConfirmed; null when not present.</param>
public sealed record PersonPresence(
    Guid PersonId,
    string Name,
    PresenceLevel Level,
    bool CountsAsPresent,
    string? Source,
    string? HomeWorkCenter,
    string? WorkCenter,
    string? Machine,
    string? Activity,
    string? Operation,
    bool OnShift,
    string? Unavailable,
    string Detail);

/// <summary>
/// The V3.9 readiness dimensions (V3 brief B): tooling, holds, material at the point of use and training on controlled
/// documents; and the presence level of each person (AC). Pure functions of the snapshot and their inputs.
/// </summary>
public static class ExecutableChecks
{
    public static ReadinessDimension Tooling(SiteSnapshot snapshot, string? machine, string activity, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var check = snapshot.Tooling(machine, activity, at);
        var where = machine is null ? activity : $"{activity} on {machine}";
        return !check.Required
            ? new ReadinessDimension("Tooling", EvidenceOutcome.Pass, $"No tools are required for {where}.")
            : new ReadinessDimension("Tooling", check.Ready ? EvidenceOutcome.Pass : EvidenceOutcome.Fail, check.Ready ? $"Tools for {where} are valid now: {string.Join("; ", check.Met)}." : check.Detail);
    }

    /// <summary>Production holds that stop this operation (on this machine, when one is named).</summary>
    public static ReadinessDimension Holds(SiteSnapshot snapshot, long orderNumber, string partNumber, int sequence, string workCenter, string? machine)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var holds = snapshot.ProductionHolds.Where(h => h.Covers(orderNumber, partNumber, sequence, workCenter, machine)).OrderBy(h => h.HoldId).ToList();
        return holds.Count == 0
            ? new ReadinessDimension("Holds", EvidenceOutcome.Pass, "No production hold applies.")
            : new ReadinessDimension("Holds", EvidenceOutcome.Fail, string.Join(" ", holds.Select(h => h.Describe() + ".")));
    }

    /// <summary>Production holds on whatever is running now (machine or work center), for current-work readiness.</summary>
    public static ReadinessDimension? EquipmentHolds(SiteSnapshot snapshot, string? workCenter, string? machine)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var holds = snapshot.ProductionHolds
            .Where(h => h.Active && ((h.Scope == HoldScope.WorkCenter && h.WorkCenterCode == workCenter) || (h.Scope == HoldScope.Machine && machine is not null && h.MachineCode == machine)))
            .OrderBy(h => h.HoldId)
            .ToList();
        return holds.Count == 0 ? null : new ReadinessDimension("Holds", EvidenceOutcome.Fail, string.Join(" ", holds.Select(h => h.Describe() + ".")));
    }

    /// <summary>
    /// Material at the point of use, for the first operation of an order that has not started: what is still to be
    /// issued must be released, usable stock at a point-of-use location of the operation's work center. Where the work
    /// center has no point-of-use location, material is issued from stores and this is a fact, not a check.
    /// </summary>
    public static ReadinessDimension? PointOfUse(OrderPointOfUse? pointOfUse, OrderMaterialStatus? material, string workCenter)
    {
        if (pointOfUse is null || material is null)
        {
            return null;
        }

        if (!pointOfUse.WorkCentersWithPointOfUse.Contains(workCenter))
        {
            return new ReadinessDimension("PointOfUse", EvidenceOutcome.Info, $"No point-of-use location serves {workCenter}; material is issued from stores.");
        }

        var needed = material.Lines.Where(l => l.Remaining > 0).OrderBy(l => l.PartNumber, StringComparer.Ordinal).ToList();
        if (needed.Count == 0)
        {
            return new ReadinessDimension("PointOfUse", EvidenceOutcome.Pass, "All material is issued to the order.");
        }

        var lines = needed.Select(l =>
        {
            var at = pointOfUse.AtPointOfUse.Where(p => p.PartNumber == l.PartNumber && p.PointOfUseWorkCenter == workCenter && p.QualityStatus == StockStatus.Released && p.Usable).Sum(p => p.Quantity);
            return (l.PartNumber, l.Remaining, At: at);
        }).ToList();
        var missing = lines.Where(l => l.At < l.Remaining).ToList();
        return missing.Count == 0
            ? new ReadinessDimension("PointOfUse", EvidenceOutcome.Pass, $"Material is at the point of use of {workCenter}: {string.Join(", ", lines.Select(l => $"{l.PartNumber} {Fmt(l.At)} for {Fmt(l.Remaining)}"))}.")
            : new ReadinessDimension("PointOfUse", EvidenceOutcome.Fail, $"Not at the point of use of {workCenter}: {string.Join(", ", missing.Select(l => $"{l.PartNumber} needs {Fmt(l.Remaining)}, {Fmt(l.At)} there"))}. Stage it.");
    }

    /// <summary>
    /// Training on the controlled documents an operation is worked to: of the qualified, present candidates, how many
    /// are trained on every applicable revision. Off: no dimension. Warn: a fact. Enforce: the crew must be trained.
    /// </summary>
    public static ReadinessDimension? Training(OrderTraining? training, int sequence, IReadOnlyList<PersonSnapshot> candidates, int crew)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (training is null || training.Policy == TrainingPolicy.Off)
        {
            return null;
        }

        var documents = training.For(sequence);
        if (documents.Count == 0)
        {
            return new ReadinessDimension("Training", EvidenceOutcome.Pass, "No controlled document needing training applies to this operation.");
        }

        var trained = candidates.Where(p => documents.All(d => p.TrainedOn(d.DocumentNumber, d.Revision))).ToList();
        var untrained = candidates.Except(trained).Select(p => $"{p.Name} ({string.Join(", ", documents.Where(d => !p.TrainedOn(d.DocumentNumber, d.Revision)).Select(d => d.Describe()))})").ToList();
        var docs = string.Join(", ", documents.Select(d => d.Describe()));
        if (trained.Count >= Math.Max(crew, 1))
        {
            return new ReadinessDimension("Training", EvidenceOutcome.Pass, $"Trained on {docs}: {string.Join(", ", trained.Select(p => p.Name))}.");
        }

        var detail = $"Needs {Math.Max(crew, 1)} trained on {docs}; {trained.Count} of the qualified people present are" + (untrained.Count == 0 ? "." : $". Not trained: {string.Join("; ", untrained)}.");
        return training.Policy == TrainingPolicy.Enforce
            ? new ReadinessDimension("Training", EvidenceOutcome.Fail, detail)
            : new ReadinessDimension("Training", EvidenceOutcome.Info, "Warning (training is not enforced): " + detail);
    }

    /// <summary>Each worker's presence level now. A scheduled person is never counted as present.</summary>
    public static IReadOnlyList<PersonPresence> Presence(SiteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var schedules = Projection.Schedules(snapshot, snapshot.AsOf, snapshot.AsOf.AddMinutes(1));
        return snapshot.People.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.PersonId).Select(p =>
        {
            var onShift = schedules.TryGetValue(p.PersonId, out var spans) && spans.Any(s => s.Contains(snapshot.AsOf));
            var present = p.CountsAsPresent && p.Presence != PresenceKind.Projected;
            var level = !present
                ? (p.Active && p.Unavailability is null && onShift ? PresenceLevel.Scheduled : PresenceLevel.NotPresent)
                : p.ActiveOperation is not null ? PresenceLevel.ActiveOnOperation
                : p.Assignment?.WorkCenterCode is not null ? PresenceLevel.AssignedToWorkCenter
                : PresenceLevel.ClockedIn;
            var source = present ? p.Presence?.ToString() : null;
            var detail = level switch
            {
                PresenceLevel.ActiveOnOperation => $"Working on operation {p.ActiveOperation}" + (p.Assignment?.MachineCode is { } m ? $" at {m}." : "."),
                PresenceLevel.AssignedToWorkCenter => $"Assigned to {p.Assignment!.WorkCenterCode}{(p.Assignment.MachineCode is { } m2 ? $" ({m2})" : string.Empty)} for {p.Assignment.ActivityCode}{(p.Assignment.AsTrainee ? " as a trainee" : string.Empty)}.",
                PresenceLevel.ClockedIn => p.Presence == PresenceKind.SupervisorConfirmed ? "Confirmed on site by a supervisor; not assigned." : "Clocked in; not assigned.",
                PresenceLevel.Scheduled => "On shift by the schedule, but not clocked in or confirmed: not counted as present.",
                _ => !p.Active ? "Inactive." : p.Unavailability is { } u ? $"Unavailable ({u})." : "Not on shift and not clocked in.",
            };
            return new PersonPresence(p.PersonId, p.Name, level, present, source, p.HomeWorkCenterCode, present ? p.Assignment?.WorkCenterCode : null, present ? p.Assignment?.MachineCode : null,
                present ? p.Assignment?.ActivityCode : null, p.ActiveOperation, onShift, p.Unavailability?.ToString(), detail);
        }).ToList();
    }

    private static string Fmt(decimal value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
