using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Resources.Contracts;
using NodaTime;

namespace Forge.Planning.Reporting;

/// <summary>What a report builder produced: rows in catalog column order, headline lines and the records it read.</summary>
internal sealed record ReportBody(IReadOnlyList<ReportRow> Rows, IReadOnlyList<string> Summary, IReadOnlyList<string> Sources);

internal sealed record ReportSpec(ReportCatalogEntry Entry, string Permission, ReportRatio? Ratio, Func<ReportRun, CancellationToken, Task<ReportBody>> Build);

/// <summary>Everything a report builder needs: one consistent snapshot, the reader, and the period as dates and instants.</summary>
internal sealed class ReportRun
{
    public required IKernelUnitOfWork Uow { get; init; }

    public required ForgePrincipal Principal { get; init; }

    public required ReportParameters Parameters { get; init; }

    public required DateOnly FromDate { get; init; }

    public required DateOnly ToDate { get; init; }

    /// <summary>Start of <see cref="FromDate"/> at the site.</summary>
    public required DateTimeOffset From { get; init; }

    /// <summary>Start of the day after <see cref="ToDate"/> at the site (exclusive).</summary>
    public required DateTimeOffset To { get; init; }

    public required DateTimeOffset Now { get; init; }

    public required DateOnly Today { get; init; }

    public required DateTimeZone Zone { get; init; }

    public DateOnly Local(DateTimeOffset at)
    {
        var d = Instant.FromDateTimeOffset(at).InZone(Zone).Date;
        return new DateOnly(d.Year, d.Month, d.Day);
    }

    public bool InPeriod(DateTimeOffset at) => at >= From && at < To;

    public bool InPeriod(DateOnly date) => date >= FromDate && date <= ToDate;

    /// <summary>
    /// Program-segregated records are visible only to people with access to the program. When a program is
    /// asked for, only its records are kept (records with no program are left out).
    /// </summary>
    public bool Visible(Guid? program) =>
        (program is not { } p || Principal.Programs.Contains(new ProgramId(p))) && (Parameters.ProgramId is not { } wanted || program == wanted);

    /// <summary>Case-insensitive equality; an empty filter matches everything.</summary>
    public static bool Is(string? value, string? filter) =>
        string.IsNullOrWhiteSpace(filter) || string.Equals(value?.Trim(), filter.Trim(), StringComparison.OrdinalIgnoreCase);

    public bool Matches(params string?[] fields) =>
        string.IsNullOrWhiteSpace(Parameters.Search) || fields.Any(f => f is not null && f.Contains(Parameters.Search.Trim(), StringComparison.OrdinalIgnoreCase));

    public static decimal Hours(TimeSpan span) => decimal.Round((decimal)span.TotalHours, 2, MidpointRounding.AwayFromZero);

    public static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The reporting engine (spec §54): standard reports over the records every context keeps, with
/// filters, grouping and traceability to source records. Read-only: one consistent snapshot, the
/// reader's permissions and program access applied to every row. Nothing is estimated; a report
/// says what it was computed from.
/// </summary>
public sealed class ReportService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    public const int MaxRows = 5000;

    /// <summary>The standard reports this person may run.</summary>
    public IReadOnlyList<ReportCatalogEntry> Catalog(ForgePrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return StandardReports.All.Where(s => Permitted(principal, s)).Select(s => s.Entry).ToList();
    }

    internal bool Permitted(ForgePrincipal principal, string reportId) =>
        StandardReports.Find(reportId) is { } spec && Permitted(principal, spec);

    internal static string PermissionOf(string reportId) =>
        StandardReports.Find(reportId)?.Permission ?? throw new CommandRejectedException("report_not_found", $"There is no report '{reportId}'.");

    public async Task<ReportResult> RunAsync(ForgePrincipal principal, string reportId, ReportParameters parameters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(parameters);
        var spec = StandardReports.Find(reportId) ?? throw new CommandRejectedException("report_not_found", $"There is no report '{reportId}'.");
        var decision = authorizer.Authorize(principal, spec.Permission, ResourceRef.Of("Report", spec.Entry.Id));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }

        if (parameters.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program)))
        {
            throw new CommandRejectedException("program_not_found", $"Program {program} does not exist.");
        }

        if (parameters.GroupBy is { Length: > 0 } g && !spec.Entry.GroupBy.Contains(g, StringComparer.Ordinal))
        {
            throw new CommandRejectedException("invalid_grouping", $"{spec.Entry.Title} cannot be grouped by '{g}'.");
        }

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var sites = await uow.Store<IResourcesLookup>().ListSitesAsync(ct);
        var zoneId = sites.Count > 0 ? sites[0].TimeZone : "America/New_York";
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(zoneId) ?? DateTimeZoneProviders.Tzdb["America/New_York"];
        var now = time.GetUtcNow();
        var todayLocal = Instant.FromDateTimeOffset(now).InZone(zone).Date;
        var today = new DateOnly(todayLocal.Year, todayLocal.Month, todayLocal.Day);

        var (fromDate, toDate) = Period(parameters, today, spec.Entry.MaxDays);
        DateTimeOffset StartOf(DateOnly d) => zone.AtStartOfDay(new LocalDate(d.Year, d.Month, d.Day)).ToDateTimeOffset().ToUniversalTime();
        var run = new ReportRun
        {
            Uow = uow,
            Principal = principal,
            Parameters = parameters,
            FromDate = fromDate,
            ToDate = toDate,
            From = StartOf(fromDate),
            To = StartOf(toDate.AddDays(1)),
            Now = now,
            Today = today,
            Zone = zone,
        };

        var body = await spec.Build(run, ct);
        // Decimals keep no trailing zeros from storage scale (9, not 9.000000), on screen and in exports.
        var rows = body.Rows.Take(MaxRows)
            .Select(r => r.Cells.Any(c => c is decimal) ? r with { Cells = r.Cells.Select(c => c is decimal d ? (object)(d / 1.000000000000000000000000000000000m) : c).ToList() } : r)
            .ToList();
        var summary = body.Summary.ToList();
        if (body.Rows.Count > MaxRows)
        {
            summary.Add($"Showing the first {MaxRows} of {body.Rows.Count} rows; narrow the period or filters to see the rest.");
        }

        var columns = spec.Entry.Columns;
        var groupBy = string.IsNullOrEmpty(parameters.GroupBy) ? null : parameters.GroupBy;
        var groups = groupBy is null ? [] : Group(columns, rows, groupBy, spec.Ratio, zone);
        var total = Aggregate("Total", columns, rows, spec.Ratio);
        return new ReportResult(
            spec.Entry.Id, spec.Entry.Title, parameters, fromDate, toDate, zone.Id, now, principal.DisplayName,
            columns, rows, groupBy, groups, total, spec.Ratio, summary, spec.Entry.Basis, body.Sources);
    }

    internal static (DateOnly From, DateOnly To) Period(ReportParameters p, DateOnly today, int maxDays)
    {
        DateOnly from, to;
        if (p.LastDays is { } days)
        {
            if (days < 1 || days > maxDays)
            {
                throw new CommandRejectedException("invalid_period", $"The last-days window must be 1 to {maxDays} days.");
            }

            (from, to) = (today.AddDays(1 - days), today);
        }
        else
        {
            to = p.To ?? today;
            from = p.From ?? to.AddDays(-29);
        }

        if (to < from)
        {
            throw new CommandRejectedException("invalid_period", "The period must end on or after the day it starts.");
        }

        if (to.DayNumber - from.DayNumber + 1 > maxDays)
        {
            throw new CommandRejectedException("invalid_period", $"This report covers at most {maxDays} days at a time.");
        }

        return (from, to);
    }

    private static List<ReportGroup> Group(IReadOnlyList<ReportColumn> columns, List<ReportRow> rows, string key, ReportRatio? ratio, DateTimeZone zone)
    {
        var index = columns.Select((c, i) => (c, i)).First(x => x.c.Key == key);
        string Label(object? v) => v switch
        {
            null => "(none)",
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTimeOffset t => Instant.FromDateTimeOffset(t).InZone(zone).Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "(none)",
        };
        return rows.GroupBy(r => Label(r.Cells[index.i]), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => Aggregate(g.Key, columns, g.ToList(), ratio))
            .ToList();
    }

    private static ReportGroup Aggregate(string key, IReadOnlyList<ReportColumn> columns, IReadOnlyList<ReportRow> rows, ReportRatio? ratio)
    {
        var sums = new Dictionary<string, decimal>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Kind is ReportColumnKind.Number or ReportColumnKind.Hours)
            {
                var column = i;
                sums[columns[i].Key] = rows.Sum(r => r.Cells[column] is decimal d ? d : r.Cells[column] is int n ? n : 0m) / 1.000000000000000000000000000000000m;
            }
        }

        decimal? percent = null;
        if (ratio is not null && sums.TryGetValue(ratio.Denominator, out var den) && den != 0 && sums.TryGetValue(ratio.Numerator, out var num))
        {
            percent = decimal.Round(num / den * 100m, 1, MidpointRounding.AwayFromZero) / 1.000000000000000000000000000000000m;
        }

        return new ReportGroup(key, rows.Count, sums, percent);
    }

    private bool Permitted(ForgePrincipal principal, ReportSpec spec) =>
        authorizer.Authorize(principal, spec.Permission, ResourceRef.Of("Report", spec.Entry.Id)).IsPermitted;
}
