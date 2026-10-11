using Forge.Kernel.Holds;
using Forge.Maintenance.Contracts;
using Forge.Resources.Contracts;
using Forge.SafetyRules.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Planning.Engines;

/// <summary>
/// A person as the engines see them at one instant. <see cref="ValidQualifications"/> already
/// excludes expired, suspended, revoked and in-training records.
/// </summary>
public sealed record PersonSnapshot(
    Guid PersonId,
    string Name,
    bool Active,
    PresenceKind? Presence,
    DateTimeOffset? PresentUntil,
    UnavailabilityKind? Unavailability,
    WorkAssignment? Assignment,
    IReadOnlyList<string> ValidQualifications)
{
    /// <summary>Counts toward staffing only with current presence evidence and no unavailability. A schedule never counts.</summary>
    public bool CountsAsPresent => Active && Presence is not null && Unavailability is null;

    public bool Holds(string qualification) => ValidQualifications.Contains(qualification, StringComparer.Ordinal);

    public string? HomeWorkCenterCode { get; init; }

    public string? ShiftCode { get; init; }

    /// <summary>Expiry of each currently valid qualification (null = never expires).</summary>
    public IReadOnlyDictionary<string, DateTimeOffset?> QualificationExpiry { get; init; } = new Dictionary<string, DateTimeOffset?>();

    /// <summary>Controlled-document revisions this person is trained on, as "NUMBER@REV" (V3.9).</summary>
    public IReadOnlySet<string> TrainedDocuments { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The order operation this person has open direct labor on, as "order/sequence" (V3.9): active on an operation.</summary>
    public string? ActiveOperation { get; init; }

    public bool TrainedOn(string documentNumber, string revision) => TrainedDocuments.Contains($"{documentNumber}@{revision}");

    /// <summary>Whether a currently valid qualification is still valid at a later instant.</summary>
    public bool HoldsAt(string qualification, DateTimeOffset at) =>
        Holds(qualification) && (!QualificationExpiry.TryGetValue(qualification, out var expiry) || expiry is null || at < expiry);
}

/// <summary>
/// Everything the Phase 2 engines read, captured in one consistent read. Lists are sorted by key so
/// iteration order can never change a result.
/// </summary>
public sealed record SiteSnapshot(
    DateTimeOffset AsOf,
    IReadOnlyList<SafetyAreaInfo> SafetyAreas,
    IReadOnlyList<WorkCenterInfo> WorkCenters,
    IReadOnlyList<MachineInfo> Machines,
    IReadOnlyList<MachineActivityInfo> MachineActivities,
    IReadOnlyList<WorkCenterActivityInfo> WorkCenterActivities,
    IReadOnlyList<ActivityTypeInfo> Activities,
    IReadOnlyList<PersonSnapshot> People,
    IReadOnlyList<SafetyRuleDefinition> Rules)
{
    public IReadOnlyList<SiteInfo> Sites { get; init; } = [];

    public IReadOnlyList<ShiftInfo> Shifts { get; init; } = [];

    public IReadOnlyList<WorkCenterShiftInfo> WorkCenterShifts { get; init; } = [];

    public IReadOnlyList<SiteClosureInfo> Closures { get; init; } = [];

    /// <summary>Recorded machine downtime overlapping the planning horizon.</summary>
    public IReadOnlyList<DowntimeWindow> Downtime { get; init; } = [];

    /// <summary>Recorded unavailability overlapping the planning horizon (no reasons).</summary>
    public IReadOnlyList<UnavailabilityWindow> Unavailability { get; init; } = [];

    /// <summary>Tool types, tools, their history and what activities need (V3.9).</summary>
    public IReadOnlyList<ToolTypeInfo> ToolTypes { get; init; } = [];

    public IReadOnlyList<ToolInfo> Tools { get; init; } = [];

    public IReadOnlyList<ToolEvent> ToolEvents { get; init; } = [];

    public IReadOnlyList<ToolRequirement> ToolRequirements { get; init; } = [];

    /// <summary>Active holds (V3.9; generalized in V3.10) and what each kind blocks.</summary>
    public IReadOnlyList<Hold> Holds { get; init; } = [];

    public IReadOnlyList<HoldKindPolicy> HoldPolicies { get; init; } = HoldKindPolicy.Defaults;

    /// <summary>The active holds that stop an activity on these records.</summary>
    public IReadOnlyList<Hold> Stopping(HoldCheck check) => Holds.Where(h => h.Stops(check, HoldPolicies)).OrderBy(h => h.HoldId).ToList();

    /// <summary>Whether the tools <paramref name="activity"/> needs on <paramref name="machine"/> are valid at <paramref name="at"/> (V3.9).</summary>
    public ToolingCheck Tooling(string? machine, string activity, DateTimeOffset at) =>
        ToolingCheck.For(ToolRequirements, ToolTypes, Tools, ToolEvents.ToLookup(e => e.ToolCode, StringComparer.Ordinal), machine, activity, at);

    public PersonSnapshot? Person(Guid id) => People.FirstOrDefault(p => p.PersonId == id);

    public DowntimeWindow? DowntimeAt(string machineCode, DateTimeOffset at) =>
        Downtime.Where(d => d.MachineCode == machineCode && d.Covers(at)).OrderBy(d => d.StartsAt).ThenBy(d => d.Id).FirstOrDefault();

    /// <summary>A copy with one person's assignment replaced. Used by scenarios; production data is untouched.</summary>
    public SiteSnapshot WithAssignment(Guid personId, WorkAssignment? assignment) => this with
    {
        People = People.Select(p => p.PersonId == personId ? p with { Assignment = assignment } : p).ToList(),
    };

    public IEnumerable<PersonSnapshot> PresentInArea(string safetyAreaCode) =>
        People.Where(p => p.CountsAsPresent && p.Assignment?.SafetyAreaCode == safetyAreaCode).OrderBy(p => p.PersonId);

    /// <summary>Distinct pieces of work people are currently doing, in a stable order.</summary>
    public IReadOnlyList<WorkItem> ActiveWork() => People
        .Where(p => p.CountsAsPresent && p.Assignment is not null)
        .Select(p => new WorkItem(p.Assignment!.SafetyAreaCode, p.Assignment.WorkCenterCode, p.Assignment.MachineCode, p.Assignment.ActivityCode))
        .Distinct()
        .OrderBy(w => w.SafetyAreaCode, StringComparer.Ordinal)
        .ThenBy(w => w.MachineCode ?? string.Empty, StringComparer.Ordinal)
        .ThenBy(w => w.WorkCenterCode ?? string.Empty, StringComparer.Ordinal)
        .ThenBy(w => w.ActivityCode, StringComparer.Ordinal)
        .ToList();
}

/// <summary>A piece of work: an activity at a scope. The safety area is always known.</summary>
public sealed record WorkItem(string SafetyAreaCode, string? WorkCenterCode, string? MachineCode, string ActivityCode)
{
    public string Describe() => $"{ActivityCode} on {MachineCode ?? WorkCenterCode ?? SafetyAreaCode}";
}
