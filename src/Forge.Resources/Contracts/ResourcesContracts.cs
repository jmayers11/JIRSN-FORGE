namespace Forge.Resources.Contracts;

/// <summary>A plant. Shift times are local to its IANA time zone.</summary>
public sealed record SiteInfo(string Code, string Name, string TimeZone = "America/New_York");

/// <summary>
/// A recurring shift: starts at <see cref="Start"/> local time on each listed ISO weekday
/// (1 = Monday ... 7 = Sunday) and lasts <see cref="DurationMinutes"/>, possibly past midnight.
/// </summary>
public sealed record ShiftInfo(string Code, string SiteCode, string Name, TimeOnly Start, int DurationMinutes, IReadOnlyList<int> Days);

public sealed record WorkCenterShiftInfo(string WorkCenterCode, string ShiftCode);

/// <summary>A local date on which the site does not run any shift (holiday, shutdown).</summary>
public sealed record SiteClosureInfo(string SiteCode, DateOnly Date, string? Note);

/// <summary>A defined area where safety staffing is evaluated. People anywhere in the area count toward its rules.</summary>
/// <remarks>A retired (inactive) record is kept for history but nothing new can be planned, assigned or run there.</remarks>
public sealed record SafetyAreaInfo(string Code, string SiteCode, string Name, bool Active = true);

/// <summary>
/// A place where a kind of work is done: slitting, bonding, winding, kitting. <see cref="Picture"/> is the
/// photo shown for it (one of <see cref="WorkCenterPictures.All"/>); null means "choose from the name".
/// </summary>
public sealed record WorkCenterInfo(string Code, string SafetyAreaCode, string Name, bool Active = true, string? Picture = null);

public sealed record MachineInfo(string Code, string WorkCenterCode, string SafetyAreaCode, string Name, bool Active = true);

/// <summary>Kinds of work: "RUN_SLITTER", "STAGE_ROLL", "KITTING". Safety rules and requirements refer to them.</summary>
public sealed record ActivityTypeInfo(string Code, string Name, string? Description, bool Active = true);

/// <summary>The photos a work center can show. "plain" is a neutral texture for work that matches none of them.</summary>
public static class WorkCenterPictures
{
    public static readonly IReadOnlyList<string> All = ["plain", "slitting", "fabrication", "assembly", "paint", "inspection", "shipping", "raw-material"];
}

/// <summary>
/// Part of a machine's requirement profile: this activity can be performed on the machine by a person
/// holding any one of the operator qualifications.
/// </summary>
public sealed record MachineActivityInfo(string MachineCode, string ActivityCode, IReadOnlyList<string> OperatorQualifications);

/// <summary>An activity that may be performed at a work center without a machine (staging, kitting, cleaning).</summary>
public sealed record WorkCenterActivityInfo(string WorkCenterCode, string ActivityCode);

/// <summary>
/// Answers "is anything still using this?" before a work center, machine, safety area or activity is
/// retired, moved or removed. Implemented outside Resources (it reads orders, routings and workers);
/// each entry is a short description such as "order 1002 op 20".
/// </summary>
public interface IResourceUsage
{
    /// <param name="kind">SafetyArea, WorkCenter or Machine.</param>
    /// <param name="activityCode">When set, only uses for that activity count.</param>
    Task<IReadOnlyList<string>> FindUsesAsync(string kind, string code, string? activityCode, DateTimeOffset at, CancellationToken ct);
}

/// <summary>
/// What other contexts may ask Resources. Available through the unit of work. Lists include retired
/// records (see the Active flags); Find and Exists methods see only active ones, so nothing new can
/// refer to a retired record.
/// </summary>
public interface IResourcesLookup
{
    Task<MachineInfo?> FindRecordedMachineAsync(string code, CancellationToken ct);
    Task<WorkCenterInfo?> FindRecordedWorkCenterAsync(string code, CancellationToken ct);
    Task<SafetyAreaInfo?> FindRecordedSafetyAreaAsync(string code, CancellationToken ct);
    Task<IReadOnlyList<SiteInfo>> ListSitesAsync(CancellationToken ct);

    Task<IReadOnlyList<SafetyAreaInfo>> ListSafetyAreasAsync(CancellationToken ct);

    Task<IReadOnlyList<WorkCenterInfo>> ListWorkCentersAsync(CancellationToken ct);

    Task<IReadOnlyList<MachineInfo>> ListMachinesAsync(CancellationToken ct);

    Task<IReadOnlyList<ActivityTypeInfo>> ListActivityTypesAsync(CancellationToken ct);

    Task<IReadOnlyList<MachineActivityInfo>> ListMachineActivitiesAsync(CancellationToken ct);

    Task<SafetyAreaInfo?> FindSafetyAreaAsync(string code, CancellationToken ct);

    Task<WorkCenterInfo?> FindWorkCenterAsync(string code, CancellationToken ct);

    Task<MachineInfo?> FindMachineAsync(string code, CancellationToken ct);

    Task<bool> ActivityTypeExistsAsync(string code, CancellationToken ct);

    Task<MachineActivityInfo?> FindMachineActivityAsync(string machineCode, string activityCode, CancellationToken ct);

    Task<IReadOnlyList<WorkCenterActivityInfo>> ListWorkCenterActivitiesAsync(CancellationToken ct);

    Task<IReadOnlyList<ShiftInfo>> ListShiftsAsync(CancellationToken ct);

    Task<IReadOnlyList<WorkCenterShiftInfo>> ListWorkCenterShiftsAsync(CancellationToken ct);

    Task<IReadOnlyList<SiteClosureInfo>> ListSiteClosuresAsync(CancellationToken ct);
}
