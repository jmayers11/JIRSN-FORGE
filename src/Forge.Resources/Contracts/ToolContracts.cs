
namespace Forge.Resources.Contracts;

public enum ToolKind
{
    Gauge,
    Cutting,
    Fixture,
    Die,
    Mold,
    Other,
}

public enum ToolCustody
{
    InHouse,
    AtVendor,
    WithPerson,
}

public enum ToolCondition
{
    Good,
    Worn,
    Damaged,
}

public enum ToolStatus
{
    Available,

    /// <summary>Away for a service cycle (a blade out for sharpening, a gauge at the calibration lab).</summary>
    OutForService,

    Retired,
}

public enum ToolEventKind
{
    Registered,
    Moved,
    Calibrated,
    CalibrationFailed,
    Used,
    SentForService,
    Returned,
    ConditionChanged,
    Retired,
}

/// <param name="CalibrationDays">Calibration interval; tools of this type need a calibration in force to be used.</param>
/// <param name="UsageLimit">Uses over a tool's life, after which it is not used again.</param>
/// <param name="ServiceUses">Uses between service cycles (sharpening, regrinding), after which it goes for service.</param>
public sealed record ToolTypeInfo(string Code, string Name, ToolKind Kind, int? CalibrationDays, int? UsageLimit, int? ServiceUses, bool Active = true);

/// <param name="EligibleMachines">Machines it may be used on; empty means any.</param>
/// <param name="EligibleActivities">Activities it may be used for; empty means any.</param>
public sealed record ToolInfo(
    string Code,
    string TypeCode,
    string Description,
    string? Serial,
    string? Location,
    ToolCustody Custody,
    string? Custodian,
    ToolCondition Condition,
    IReadOnlyList<string> EligibleMachines,
    IReadOnlyList<string> EligibleActivities,
    int UsageCount,
    int UsesSinceService,
    DateOnly? CalibrationDue,
    string? Certificate,
    ToolStatus Status,
    DateTimeOffset ChangedAt,
    string ChangedBy);

/// <summary>Something that happened to a tool, kept for good.</summary>
public sealed record ToolEvent(
    long EventId,
    string ToolCode,
    ToolEventKind Kind,
    DateTimeOffset At,
    string Actor,
    string? Location = null,
    string? Custodian = null,
    ToolCondition? Condition = null,
    DateOnly? CalibratedOn = null,
    DateOnly? DueOn = null,
    string? Certificate = null,
    int? Uses = null,
    string? Reference = null,
    string? Note = null);

/// <summary>Whether a tool could be used at one moment, for one machine and activity (V3.8), and why not.</summary>
public sealed record ToolValidity(string ToolCode, DateTimeOffset AsOf, bool Valid, DateOnly? CalibrationDue, int? UsesLeft, IReadOnlyList<string> Problems)
{
    /// <summary>
    /// Judged as of <paramref name="at"/> from the tool's history: the calibration in force then (the latest passing
    /// calibration on or before that day, unless a failure came after it), its status, custody and condition then,
    /// its uses then against the type's limits, and its eligibility for the machine and activity.
    /// </summary>
    public static ToolValidity At(ToolInfo tool, ToolTypeInfo type, IEnumerable<ToolEvent> history, DateTimeOffset at, string? machine = null, string? activity = null, string? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(type);
        var events = history.Where(e => e.ToolCode == tool.Code && e.At <= at).OrderBy(e => e.At).ThenBy(e => e.EventId).ToList();
        var problems = new List<string>();
        var day = SiteTime.DateAtOrUtc(at, timeZone); // the site's date (ADR 0057 §1); tools belong to the home site

        if (events.Count == 0)
        {
            problems.Add($"{tool.Code} was not registered yet.");
        }

        // Status, custody and condition as they stood then.
        var status = ToolStatus.Available;
        var condition = ToolCondition.Good;
        var uses = 0;
        var sinceService = 0;
        DateOnly? due = null;
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case ToolEventKind.Registered:
                    condition = e.Condition ?? condition;
                    due = e.DueOn ?? due;
                    break;
                case ToolEventKind.Calibrated:
                    due = e.DueOn;
                    break;
                case ToolEventKind.CalibrationFailed:
                    due = null;
                    break;
                case ToolEventKind.Used:
                    uses += e.Uses ?? 1;
                    sinceService += e.Uses ?? 1;
                    break;
                case ToolEventKind.SentForService:
                    status = ToolStatus.OutForService;
                    break;
                case ToolEventKind.Returned:
                    status = ToolStatus.Available;
                    sinceService = 0;
                    condition = e.Condition ?? condition;
                    due = e.DueOn ?? due;
                    break;
                case ToolEventKind.ConditionChanged:
                    condition = e.Condition ?? condition;
                    break;
                case ToolEventKind.Retired:
                    status = ToolStatus.Retired;
                    break;
                default:
                    break;
            }
        }

        if (status == ToolStatus.Retired)
        {
            problems.Add($"{tool.Code} is retired.");
        }
        else if (status == ToolStatus.OutForService)
        {
            problems.Add($"{tool.Code} is out for service.");
        }

        if (condition == ToolCondition.Damaged)
        {
            problems.Add($"{tool.Code} is damaged.");
        }

        var needsCalibration = type.CalibrationDays is not null || type.Kind == ToolKind.Gauge;
        if (needsCalibration && (due is not { } d || d < day))
        {
            problems.Add(due is { } was ? $"{tool.Code} was due for calibration {was:yyyy-MM-dd}." : $"{tool.Code} has no calibration in force.");
        }

        int? left = type.UsageLimit is { } limit ? limit - uses : null;
        if (left is <= 0)
        {
            problems.Add($"{tool.Code} has reached its usage limit of {type.UsageLimit}.");
        }

        if (type.ServiceUses is { } every && sinceService >= every)
        {
            problems.Add($"{tool.Code} is due for service: {sinceService} uses since the last one, every {every}.");
        }

        if (machine is not null && tool.EligibleMachines.Count > 0 && !tool.EligibleMachines.Contains(machine, StringComparer.Ordinal))
        {
            problems.Add($"{tool.Code} is not for {machine} (only {string.Join(", ", tool.EligibleMachines)}).");
        }

        if (activity is not null && tool.EligibleActivities.Count > 0 && !tool.EligibleActivities.Contains(activity, StringComparer.Ordinal))
        {
            problems.Add($"{tool.Code} is not for {activity} (only {string.Join(", ", tool.EligibleActivities)}).");
        }

        return new ToolValidity(tool.Code, at, problems.Count == 0, due, left, problems);
    }
}

/// <summary>
/// An activity (on one machine, or on any machine when <see cref="MachineCode"/> is null) needs this many valid tools
/// of a type (V3.9). A machine-specific requirement for the same tool type replaces the general one.
/// </summary>
public sealed record ToolRequirement(long RequirementId, string ActivityCode, string? MachineCode, string ToolTypeCode, int Quantity, DateTimeOffset SetAt, string SetBy)
{
    public string Describe() => $"{Quantity} x {ToolTypeCode} for {ActivityCode}{(MachineCode is null ? string.Empty : $" on {MachineCode}")}";
}

/// <summary>Whether the tools an activity needs on a machine are there and valid at one moment (V3.9).</summary>
/// <param name="Required">False when nothing is required.</param>
/// <param name="Problems">One line per requirement that is not met, naming what is wrong with each tool of that type.</param>
public sealed record ToolingCheck(bool Required, bool Ready, IReadOnlyList<string> Met, IReadOnlyList<string> Problems)
{
    public string Detail => !Required
        ? "No tools are required."
        : Ready ? $"Tools ready: {string.Join("; ", Met)}." : string.Join(" ", Problems);

    /// <summary>
    /// For each requirement that applies (machine-specific ones win over general ones for the same tool type), counts
    /// the tools of that type valid for the machine and activity at <paramref name="at"/>, judged from their history.
    /// </summary>
    public static ToolingCheck For(
        IEnumerable<ToolRequirement> requirements,
        IReadOnlyCollection<ToolTypeInfo> types,
        IReadOnlyCollection<ToolInfo> tools,
        ILookup<string, ToolEvent> history,
        string? machine,
        string activity,
        DateTimeOffset at,
        string? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(history);
        var applicable = requirements
            .Where(r => r.ActivityCode == activity && (r.MachineCode is null || r.MachineCode == machine))
            .GroupBy(r => r.ToolTypeCode, StringComparer.Ordinal)
            .Select(g => g.OrderBy(r => r.MachineCode is null ? 1 : 0).First())
            .OrderBy(r => r.ToolTypeCode, StringComparer.Ordinal)
            .ToList();
        var met = new List<string>();
        var problems = new List<string>();
        foreach (var requirement in applicable)
        {
            var type = types.FirstOrDefault(t => t.Code == requirement.ToolTypeCode);
            var candidates = tools.Where(t => t.TypeCode == requirement.ToolTypeCode).OrderBy(t => t.Code, StringComparer.Ordinal).ToList();
            if (type is null || candidates.Count == 0)
            {
                problems.Add($"{requirement.Describe()} is required; no tool of that type is registered.");
                continue;
            }

            var judged = candidates.Select(t => ToolValidity.At(t, type, history[t.Code], at, machine, activity, timeZone)).ToList();
            var valid = judged.Where(v => v.Valid).Select(v => v.ToolCode).ToList();
            if (valid.Count >= requirement.Quantity)
            {
                met.Add($"{requirement.ToolTypeCode} {string.Join(", ", valid)}");
            }
            else
            {
                var why = judged.Where(v => !v.Valid).Select(v => string.Join(" ", v.Problems));
                problems.Add($"{requirement.Describe()} is required; {valid.Count} valid{(valid.Count == 0 ? string.Empty : $" ({string.Join(", ", valid)})")}. {string.Join(" ", why)}".TrimEnd());
            }
        }

        return new ToolingCheck(applicable.Count > 0, problems.Count == 0, met, problems);
    }
}

public interface IToolLookup
{
    Task<ToolTypeInfo?> FindToolTypeAsync(string code, CancellationToken ct);
    Task<IReadOnlyList<ToolTypeInfo>> ListToolTypesAsync(CancellationToken ct);

    Task<IReadOnlyList<ToolInfo>> ListToolsAsync(CancellationToken ct);

    Task<ToolInfo?> FindToolAsync(string code, CancellationToken ct);

    /// <summary>A tool's history (all tools when null), oldest first.</summary>
    Task<IReadOnlyList<ToolEvent>> ListToolEventsAsync(string? toolCode, CancellationToken ct);

    /// <summary>What tools activities need (V3.9), by activity, machine and tool type.</summary>
    Task<IReadOnlyList<ToolRequirement>> ListToolRequirementsAsync(CancellationToken ct);
}

public interface IToolStore
{
    Task LockToolAsync(string code, CancellationToken ct);
    Task<bool> InsertToolTypeAsync(ToolTypeInfo type, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> InsertToolAsync(ToolInfo tool, DateTimeOffset at, string actor, CancellationToken ct);

    Task UpdateToolAsync(ToolInfo tool, CancellationToken ct);

    Task<long> InsertToolEventAsync(ToolEvent e, CancellationToken ct);

    /// <summary>Adds the requirement, or changes the quantity of the same activity, machine and tool type. Returns its id.</summary>
    Task<long> SetToolRequirementAsync(ToolRequirement requirement, CancellationToken ct);

    Task<bool> DeleteToolRequirementAsync(long requirementId, CancellationToken ct);
}
