using System.Globalization;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Forge.Planning.Reporting;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ReportDefinitionRequest(long? DefinitionId, string Name, string ReportId, ReportParameters? Parameters, bool Shared, string? Reason);

/// <summary>A saved definition as listed: whether it is yours, and whether you may run it.</summary>
public sealed record ReportDefinitionView(long DefinitionId, string Name, string ReportId, string ReportTitle, ReportParameters Parameters, bool Shared, string Owner, bool Mine, DateTimeOffset UpdatedAt);

public static class ReportsEndpoints
{
    private static readonly string[] HistoryAuditTypes = ["ProductionOrder", "OrderOperation", "Machine"];

    public static void MapReportsEndpoints(this IEndpointRouteBuilder app)
    {
        var reports = app.MapGroup("/api/reports").RequireAuthorization();

        reports.MapGet("/catalog", (HttpContext http, ReportService s) =>
            Read(http, Permissions.ReportsSave, principal => Task.FromResult<object?>(s.Catalog(principal))));

        reports.MapGet("/definitions", (HttpContext http, ReportService s) =>
            Read(http, Permissions.ReportsSave, async principal =>
            {
                var catalog = s.Catalog(principal).ToDictionary(e => e.Id, StringComparer.Ordinal);
                await using var uow = await http.RequestServices.GetRequiredService<Forge.Kernel.Access.IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var me = "person:" + principal.PersonId;
                return (await uow.Store<IReportDefinitionStore>().ListAsync(me, http.RequestAborted))
                    .Where(d => catalog.ContainsKey(d.ReportId))
                    .Select(d => new ReportDefinitionView(d.DefinitionId, d.Name, d.ReportId, catalog[d.ReportId].Title, d.Parameters, d.Shared, d.Owner, d.Owner == me, d.UpdatedAt))
                    .ToList();
            }));

        reports.MapPost("/definitions", (HttpContext http, ReportDefinitionRequest b, ReportDefinitionService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SaveAsync(new SaveReportDefinition(b.DefinitionId, b.Name, b.ReportId, b.Parameters ?? new ReportParameters(), b.Shared), c, ct)));
        reports.MapPost("/definitions/{id:long}/delete", (HttpContext http, long id, ReasonRequest b, ReportDefinitionService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DeleteAsync(new DeleteReportDefinition(id), c, ct)));

        reports.MapGet("/{reportId}", async (HttpContext http, string reportId, ReportService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.RunAsync(principal, reportId, Parameters(http.Request.Query), ct));
        });

        reports.MapGet("/{reportId}/export", async (HttpContext http, string reportId, string? format, ReportService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null)
            {
                return NotRegistered();
            }

            var result = await s.RunAsync(principal, reportId, Parameters(http.Request.Query), ct);
            var name = $"{result.ReportId}_{result.From:yyyyMMdd}-{result.To:yyyyMMdd}";
            return string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase)
                ? Results.File(ReportExport.Xlsx(result), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx")
                : Results.File(ReportExport.Csv(result), "text/csv; charset=utf-8", name + ".csv");
        });

        app.MapGet("/api/planning/history/compare", async (HttpContext http, DateTimeOffset at1, DateTimeOffset at2, bool? includeUnchanged, HorizonHistoryService s, KernelReadStore reads, IForgeAuthorizer authorizer, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null)
            {
                return NotRegistered();
            }

            var comparison = await s.CompareAsync(principal, at1, at2, includeUnchanged ?? false, ct);
            if (authorizer.Authorize(principal, Permissions.AuditRead, ResourceRef.Of("Audit")).IsPermitted)
            {
                var entries = await reads.QueryAuditBetweenAsync(at1, at2, HistoryAuditTypes, principal.Programs, 5000, ct);
                comparison = HorizonHistoryService.WithAudit(comparison, entries);
            }

            return Results.Ok(comparison);
        }).RequireAuthorization();
    }

    private static ReportParameters Parameters(IQueryCollection q)
    {
        string? Text(string key) => q.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v.ToString()) ? v.ToString().Trim() : null;
        DateOnly? Date(string key) => Text(key) is { } t
            ? DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw new Forge.Kernel.Commands.CommandRejectedException("invalid_period", $"'{t}' is not a date (yyyy-MM-dd).")
            : null;
        return new ReportParameters(
            Date("from"),
            Date("to"),
            Text("lastDays") is { } days && int.TryParse(days, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null,
            Text("programId") is { } p && Guid.TryParse(p, out var g) ? g : null,
            Text("workCenter"),
            Text("machine"),
            Text("partNumber"),
            Text("status"),
            Text("search"),
            Text("groupBy"));
    }
}
