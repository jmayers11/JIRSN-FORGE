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
public sealed record SafetyAreaInfo(string Code, string SiteCode, string Name);

public sealed record WorkCenterInfo(string Code, string SafetyAreaCode, string Name);

public sealed record MachineInfo(string Code, string WorkCenterCode, string SafetyAreaCode, string Name);

/// <summary>Kinds of work: "RUN_SLITTER", "STAGE_ROLL", "KITTING". Safety rules and requirements refer to them.</summary>
public sealed record ActivityTypeInfo(string Code, string Name, string? Description);

/// <summary>
/// Part of a machine's requirement profile: this activity can be performed on the machine by a person
/// holding any one of the operator qualifications.
/// </summary>
public sealed record MachineActivityInfo(string MachineCode, string ActivityCode, IReadOnlyList<string> OperatorQualifications);

/// <summary>An activity that may be performed at a work center without a machine (staging, kitting, cleaning).</summary>
public sealed record WorkCenterActivityInfo(string WorkCenterCode, string ActivityCode);

/// <summary>What other contexts may ask Resources. Available through the unit of work.</summary>
public interface IResourcesLookup
{
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
