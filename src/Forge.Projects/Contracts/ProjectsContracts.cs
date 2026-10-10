using System.Globalization;

namespace Forge.Projects.Contracts;

/// <summary>What a project builds or changes: the plant's facilities, or capital equipment and infrastructure.</summary>
public enum ProjectType
{
    Facility,
    Capital,
}

/// <summary>How the project is paid for. Forge keeps the financial reference; accounting stays where it is.</summary>
public enum ProjectFunding
{
    Operating,
    Capital,
}

public enum ProjectStatus
{
    /// <summary>Being prepared; edited directly by whoever manages it.</summary>
    Draft,

    /// <summary>Approved by a second person; changes from here on are requested with a reason and approved by someone else.</summary>
    Approved,

    Completed,
    Cancelled,
}

public enum ProjectLineStatus
{
    Active,
    Cancelled,
}

public enum ProjectChangeKind
{
    /// <summary>Title, description, budget, dates or financial reference.</summary>
    Header,
    AddLine,
    ChangeLine,
    CancelLine,
}

public enum ProjectChangeStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <param name="FinancialReference">The capital request, AFE or cost-center reference the spending is booked against.</param>
public sealed record ProjectInfo(
    string Code,
    string Title,
    ProjectType Type,
    ProjectFunding Funding,
    string? Description,
    string? Owner,
    decimal? Budget,
    DateOnly StartDate,
    DateOnly EndDate,
    string? FinancialReference,
    ProjectStatus Status,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? ClosedBy,
    DateTimeOffset? ClosedAt,
    string? CloseReason)
{
    /// <summary>Material can be bought for, issued to and returned from approved projects only.</summary>
    public bool IsOpen => Status == ProjectStatus.Approved;
}

/// <summary>A material line: a part the project needs, how much and by when.</summary>
public sealed record ProjectLine(string ProjectCode, int LineNumber, string PartNumber, decimal Quantity, DateOnly NeedDate, string? Description, ProjectLineStatus Status)
{
    /// <summary>The source key that demand, requisitions and purchase lines for this line carry (V2.7).</summary>
    public string SourceKey => ProjectSource.Key(ProjectCode, LineNumber);
}

public sealed record ProjectTask(long TaskId, string ProjectCode, string Title, DateOnly DueDate, bool Milestone, DateOnly? CompletedOn, string? CompletedBy, string CreatedBy, DateTimeOffset CreatedAt);

/// <summary>
/// A change to an approved project, requested with a reason and applied only when a different person
/// with project approval authority approves it. Fields not used by the kind are null.
/// </summary>
public sealed record ProjectChange(
    long ChangeId,
    string ProjectCode,
    ProjectChangeKind Kind,
    int? LineNumber,
    string? PartNumber,
    decimal? Quantity,
    DateOnly? NeedDate,
    string? LineDescription,
    string? Title,
    string? Description,
    decimal? Budget,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string? FinancialReference,
    string Reason,
    ProjectChangeStatus Status,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? DecisionReason);

/// <summary>What other contexts may ask about projects.</summary>
public interface IProjectLookup
{
    Task<ProjectInfo?> FindProjectAsync(string code, CancellationToken ct);

    Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken ct);

    /// <summary>Lines of one project, or of every project when <paramref name="projectCode"/> is null.</summary>
    Task<IReadOnlyList<ProjectLine>> ListLinesAsync(string? projectCode, CancellationToken ct);

    Task<IReadOnlyList<ProjectTask>> ListTasksAsync(string projectCode, CancellationToken ct);

    Task<IReadOnlyList<ProjectChange>> ListChangesAsync(string? projectCode, bool pendingOnly, CancellationToken ct);
}

/// <summary>The kind:key form a project material line takes as a demand or purchase source: project:FAC-2027-014/3.</summary>
public static class ProjectSource
{
    public const string Prefix = "project:";

    public static string Key(string projectCode, int lineNumber) => $"{Prefix}{projectCode}/{lineNumber.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParse(string? key, out string projectCode, out int lineNumber)
    {
        projectCode = string.Empty;
        lineNumber = 0;
        if (key is null || !key.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = key[Prefix.Length..];
        var slash = rest.LastIndexOf('/');
        if (slash <= 0 || !int.TryParse(rest.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out lineNumber) || lineNumber <= 0)
        {
            return false;
        }

        projectCode = rest[..slash];
        return true;
    }
}
