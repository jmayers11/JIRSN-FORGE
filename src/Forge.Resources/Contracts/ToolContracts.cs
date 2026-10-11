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
    public static ToolValidity At(ToolInfo tool, ToolTypeInfo type, IEnumerable<ToolEvent> history, DateTimeOffset at, string? machine = null, string? activity = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(type);
        var events = history.Where(e => e.ToolCode == tool.Code && e.At <= at).OrderBy(e => e.At).ThenBy(e => e.EventId).ToList();
        var problems = new List<string>();
        var day = DateOnly.FromDateTime(at.UtcDateTime);

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

public interface IToolLookup
{
    Task<IReadOnlyList<ToolTypeInfo>> ListToolTypesAsync(CancellationToken ct);

    Task<IReadOnlyList<ToolInfo>> ListToolsAsync(CancellationToken ct);

    Task<ToolInfo?> FindToolAsync(string code, CancellationToken ct);

    /// <summary>A tool's history (all tools when null), oldest first.</summary>
    Task<IReadOnlyList<ToolEvent>> ListToolEventsAsync(string? toolCode, CancellationToken ct);
}

public interface IToolStore
{
    Task<bool> InsertToolTypeAsync(ToolTypeInfo type, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> InsertToolAsync(ToolInfo tool, DateTimeOffset at, string actor, CancellationToken ct);

    Task UpdateToolAsync(ToolInfo tool, CancellationToken ct);

    Task<long> InsertToolEventAsync(ToolEvent e, CancellationToken ct);
}
