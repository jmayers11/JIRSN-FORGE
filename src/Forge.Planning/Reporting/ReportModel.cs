namespace Forge.Planning.Reporting;

public enum ReportColumnKind
{
    Text,
    Number,
    Hours,
    Date,
    DateTime,
}

public sealed record ReportColumn(string Key, string Title, ReportColumnKind Kind);

/// <summary>
/// One row. <see cref="Trace"/> names the records the row was computed from ("order:1001",
/// "constraint:12", "schedule:7"), so every figure can be followed back to its source.
/// </summary>
public sealed record ReportRow(IReadOnlyList<object?> Cells, IReadOnlyList<string> Trace);

/// <summary>A group (or the whole report): row count, sums of numeric columns and the report's headline ratio.</summary>
public sealed record ReportGroup(string Key, int Rows, IReadOnlyDictionary<string, decimal> Sums, decimal? Ratio);

/// <summary>A percentage shown per group and in total: sum of one column over the sum of another.</summary>
public sealed record ReportRatio(string Title, string Numerator, string Denominator);

public sealed record ReportFilter(string Key, string Title, string Kind);

public sealed record ReportCatalogEntry(
    string Id,
    string Title,
    string Category,
    string Description,
    IReadOnlyList<ReportFilter> Filters,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<string> GroupBy,
    string? DefaultGroupBy,
    int MaxDays,
    string Basis);

/// <summary>
/// What to run. The period is site-local dates, both inclusive. <see cref="LastDays"/> (used by saved
/// definitions) means "the last N days up to today" and overrides From and To.
/// </summary>
public sealed record ReportParameters(
    DateOnly? From = null,
    DateOnly? To = null,
    int? LastDays = null,
    Guid? ProgramId = null,
    string? WorkCenter = null,
    string? Machine = null,
    string? PartNumber = null,
    string? Status = null,
    string? Search = null,
    string? GroupBy = null);

public sealed record ReportResult(
    string ReportId,
    string Title,
    ReportParameters Parameters,
    DateOnly From,
    DateOnly To,
    string TimeZone,
    DateTimeOffset GeneratedAt,
    string GeneratedBy,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<ReportRow> Rows,
    string? GroupBy,
    IReadOnlyList<ReportGroup> Groups,
    ReportGroup Total,
    ReportRatio? Ratio,
    IReadOnlyList<string> Summary,
    string Basis,
    IReadOnlyList<string> Sources);

public sealed record ReportDefinition(
    long DefinitionId,
    string Name,
    string ReportId,
    ReportParameters Parameters,
    bool Shared,
    string Owner,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IReportDefinitionStore
{
    Task<long> InsertAsync(ReportDefinition definition, CancellationToken ct);

    Task UpdateAsync(ReportDefinition definition, CancellationToken ct);

    Task DeleteAsync(long definitionId, DateTimeOffset at, CancellationToken ct);

    Task<ReportDefinition?> FindAsync(long definitionId, CancellationToken ct);

    /// <summary>The owner's definitions and everyone's shared ones.</summary>
    Task<IReadOnlyList<ReportDefinition>> ListAsync(string owner, CancellationToken ct);

    Task<bool> NameTakenAsync(string owner, string name, long exceptId, CancellationToken ct);
}
