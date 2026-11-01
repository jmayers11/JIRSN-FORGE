using Forge.Execution.Contracts;
using Forge.Host.Costing;
using Forge.Host.Security;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Resources.Contracts;
using Forge.Workforce;
using Forge.Workforce.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ChargeCodeRequest(string Code, string Name, ChargeCodeKind Kind, string? ContractNumber, string? Clin, string? Reason);

public sealed record ChargeCodeActiveRequest(bool Active, string? Reason);

public sealed record TimeEntryRequest(DateOnly WorkDate, decimal Hours, long? OrderNumber, int? Sequence, string? ChargeCode, string? Note, string? LateReason, string? Reason);

public sealed record TimeCorrectionRequest(decimal Hours, long? OrderNumber, int? Sequence, string? ChargeCode, string? Note, string? Reason);

public sealed record TimesheetSignRequest(DateOnly WeekStart, string? Reason);

/// <summary>One day's hours on one order: what the employee's timesheet charges and what shop-floor labor records for them.</summary>
public sealed record TimeReconciliationLine(DateOnly WorkDate, long OrderNumber, int Sequence, decimal TimesheetHours, decimal ShopFloorHours, decimal Difference);

public sealed record TimesheetView(TimesheetInfo Timesheet, string Person, decimal Hours, IReadOnlyList<TimeReconciliationLine> Reconciliation);

/// <summary>DCAA-style timekeeping (owner decision 2026-10-31) and actual job cost.</summary>
internal static class TimekeepingEndpoints
{
    public static void MapTimekeepingEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/timekeeping").RequireAuthorization();
        api.MapGet("/charge-codes", async (HttpContext http, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            if (!new[] { Permissions.TimekeepingRecord, Permissions.TimekeepingRead, Permissions.TimekeepingCodesManage, Permissions.TimekeepingApprove }.Any(p => principal.HasPermission(p)))
                BusinessSourceReadAccess.Demand(http, principal, Permissions.TimekeepingRecord);
            await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(ct);
            return Results.Ok(await uow.Store<ITimesheetLookup>().ListChargeCodesAsync(ct));
        });
        api.MapPost("/charge-codes", (HttpContext http, ChargeCodeRequest b, TimesheetService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateChargeCodeAsync(new CreateChargeCode(b.Code, b.Name, b.Kind, b.ContractNumber, b.Clin), c, ct)));
        api.MapPost("/charge-codes/{code}/active", (HttpContext http, string code, ChargeCodeActiveRequest b, TimesheetService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetChargeCodeActiveAsync(new SetChargeCodeActive(code, b.Active), c, ct)));

        // The signed-in employee's own week (the current week when none is given).
        api.MapGet("/my", (HttpContext http, DateOnly? week, CancellationToken _) =>
            Read(http, Permissions.TimekeepingRecord, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var day = week ?? await uow.SiteDateAsync(http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow(), http.RequestAborted);
                var start = TimesheetInfo.WeekOf(day);
                var sheet = await uow.Store<ITimesheetLookup>().FindTimesheetAsync(principal.PersonId.Value, start, http.RequestAborted);
                return new { weekStart = start, timesheet = sheet is null ? null : await ViewAsync(uow, sheet, principal.DisplayName, http.RequestAborted) };
            }));
        api.MapPost("/entries", (HttpContext http, TimeEntryRequest b, TimesheetService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordAsync(new RecordTime(b.WorkDate, b.Hours, b.OrderNumber, b.Sequence, b.ChargeCode, b.Note, b.LateReason), c, ct)));
        api.MapPost("/entries/{entry:guid}/correct", (HttpContext http, Guid entry, TimeCorrectionRequest b, TimesheetService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CorrectAsync(new CorrectTime(entry, b.Hours, b.OrderNumber, b.Sequence, b.ChargeCode, b.Note), c, ct)));
        api.MapPost("/sign", (HttpContext http, TimesheetSignRequest b, TimesheetService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SignAsync(new SignTimesheet(b.WeekStart), c, ct)))
            .WithMetadata(new StepUpRequiredAttribute());

        // Approvers and readers: a week's timesheets, with each day's order hours against shop-floor labor.
        api.MapGet("/timesheets", async (HttpContext http, DateOnly? week, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            if (!principal.HasPermission(Permissions.TimekeepingApprove)) BusinessSourceReadAccess.Demand(http, principal, Permissions.TimekeepingRead);
            await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(ct);
            var start = TimesheetInfo.WeekOf(week ?? await uow.SiteDateAsync(http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow(), ct));
            var views = new List<TimesheetView>();
            foreach (var sheet in await uow.Store<ITimesheetLookup>().ListTimesheetsAsync(start, null, ct))
            {
                var person = await uow.People.FindAsync(new Forge.Kernel.Identity.PersonId(sheet.PersonId), ct);
                views.Add(await ViewAsync(uow, sheet, person?.DisplayName ?? sheet.PersonId.ToString("D"), ct));
            }

            return Results.Ok(new { weekStart = start, timesheets = views.OrderBy(v => v.Person, StringComparer.OrdinalIgnoreCase) });
        });
        api.MapPost("/timesheets/{id:guid}/approve", (HttpContext http, Guid id, ReasonRequest b, TimesheetService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveAsync(new ApproveTimesheet(id), c, ct)))
            .WithMetadata(new StepUpRequiredAttribute());
        api.MapPost("/timesheets/{id:guid}/return", (HttpContext http, Guid id, ReasonRequest b, TimesheetService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReturnAsync(new ReturnTimesheet(id), c, ct)));

        app.MapGroup("/api/costing").RequireAuthorization()
            .MapGet("/orders/{order:long}/job-cost", (HttpContext http, long order, JobCostService s, CancellationToken _) =>
                Read(http, Permissions.CostingRead, async principal => await s.CostAsync(principal, order, http.RequestAborted)));
    }

    /// <summary>A timesheet with each day's order hours compared to the employee's shop-floor labor on that order and day (site dates).</summary>
    private static async Task<TimesheetView> ViewAsync(IKernelUnitOfWork uow, TimesheetInfo sheet, string person, CancellationToken ct)
    {
        var zone = await uow.SiteTimeZoneAsync(ct);
        var from = SiteTime.StartOfOrUtc(sheet.WeekStart, zone);
        var to = SiteTime.StartOfOrUtc(sheet.WeekStart.AddDays(7), zone);
        var labor = (await uow.Store<IExecutionLookup>().ListLaborAsync(from, to, null, ct))
            .Where(l => l.PersonId == sheet.PersonId && l.Role != LaborRole.Trainee && l.EndedAt is not null)
            .GroupBy(l => (Day: SiteTime.DateAtOrUtc(l.StartedAt, zone), l.OrderNumber, l.Sequence))
            .ToDictionary(g => g.Key, g => Math.Round(g.Sum(l => (decimal)(l.EndedAt!.Value - l.StartedAt).TotalHours), 2));
        var charged = sheet.Entries.Where(e => e.State == TimeEntryState.Active && e.OrderNumber is not null)
            .GroupBy(e => (Day: e.WorkDate, Order: e.OrderNumber!.Value, Sequence: e.Sequence!.Value))
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Hours));
        var lines = charged.Keys.Concat(labor.Keys).Distinct().OrderBy(k => k.Day).ThenBy(k => k.Item2).ThenBy(k => k.Item3)
            .Select(k =>
            {
                var t = charged.GetValueOrDefault(k);
                var s = labor.GetValueOrDefault(k);
                return new TimeReconciliationLine(k.Day, k.Item2, k.Item3, t, s, t - s);
            }).ToList();
        return new TimesheetView(sheet, person, sheet.Hours, lines);
    }
}
