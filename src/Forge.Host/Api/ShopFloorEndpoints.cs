using Forge.Execution;
using Forge.Execution.Contracts;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning;
using Forge.Planning.Engines;
using Forge.Workforce;
using Forge.Workforce.Contracts;
using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Quality;
using Forge.Quality.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record BadgeRequest(string Badge, string? Reason);

public sealed record PinRequest(string Badge, string? CurrentPin, string NewPin);

public sealed record StartRequest(string Badge, long OrderNumber, int Sequence, string? MachineCode, string? TrainerBadge, string? ToolCode = null, LaborPhase Phase = LaborPhase.Run);

public sealed record ReportRequest(string Badge, long OrderNumber, int Sequence, decimal Good, decimal Scrap, string? ScrapReason, bool Complete, string? ScrapReasonCode = null);

public sealed record OperatorMaterialRequest(string Badge, long OrderNumber, int Sequence, string PartNumber, decimal Quantity, string ToLocation, string? Reason, bool AllowNonBomMaterial = false);

public sealed record SuspectScrapRequest(string Badge, SuspectReportKind Kind, long OrderNumber, int Sequence, string PartNumber, decimal Quantity,
    string ReasonCode, string Description, string? LotNumber, string? SerialNumber, string? Reason);

public sealed record EvaluateRequest(string TrainerBadge, string Pin, TrainingEvaluation Evaluation, string? Notes);

public sealed record IssueBadgeRequest(string Badge, string? Reason);

/// <summary>
/// Shop-floor terminals. The caller is the terminal (or a supervisor); the badge in each request
/// identifies the person. Badge numbers and PINs are wrapped as secrets so they never reach the
/// audit trail, events or logs.
/// </summary>
internal static class ShopFloorEndpoints
{
    public static void MapShopFloorEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/shopfloor").RequireAuthorization();

        api.MapPost("/identify", (HttpContext http, BadgeRequest b, TimeProvider time, CancellationToken _) =>
            Read(http, Permissions.ShopFloorRead, principal => IdentifyAsync(http, principal, b, time)));

        // Kiosk portrait lookup is badge-scoped so ShopFloorTerminal does not need broad workforce.read access.
        api.MapPost("/worker-photo", async (HttpContext http, BadgeRequest b, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var authorizer = http.RequestServices.GetRequiredService<IForgeAuthorizer>();
            var decision = authorizer.Authorize(principal, Permissions.ShopFloorRead, ResourceRef.Of(Permissions.ShopFloorRead));
            if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision);
            if (!TrySecret(b.Badge, out var badge)) return BadBadge();

            await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(ct);
            var holder = await uow.Store<IWorkforceLookup>().FindBadgeHolderAsync(badge.Reveal(), ct);
            if (holder is null || !holder.IsWorker) return Results.NotFound();
            var photo = await uow.Store<IWorkforceLookup>().FindWorkerPhotoAsync(holder.PersonId, ct);
            if (photo is null) return Results.NotFound();
            var bytes = await uow.Store<IDocumentLookup>().ReadFileAsync(photo.Sha256, ct);
            if (bytes is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(bytes, photo.ContentType);
        });

        api.MapPost("/clock-in", (HttpContext http, BadgeRequest b, AttendanceService s, CancellationToken ct) =>
            TrySecret(b.Badge, out var badge) ? Command(http, b.Reason, null, CommandSource.Terminal, c => s.ClockInAsync(new ClockIn(badge), c, ct)) : Task.FromResult(BadBadge()));

        // Clocking out ends any open direct labor first: two commands, each audited on its own.
        api.MapPost("/clock-out", async (HttpContext http, BadgeRequest b, AttendanceService attendance, ShopFloorService floor, CancellationToken ct) =>
        {
            if (!TrySecret(b.Badge, out var badge))
            {
                return BadBadge();
            }

            var (stop, error) = await ContextAsync(http, b.Reason, "stop", CommandSource.Terminal);
            if (stop is null)
            {
                return error!;
            }

            var stopped = await floor.StopAsync(new StopWork(badge), stop, ct);
            var (outContext, _) = await ContextAsync(http, b.Reason, "out", CommandSource.Terminal);
            var clockedOut = await attendance.ClockOutAsync(new ClockOut(badge), outContext!, ct);
            return Results.Ok(new { stopped, clockedOut });
        });

        api.MapPost("/pin", (HttpContext http, PinRequest b, AttendanceService s, CancellationToken ct) =>
            TrySecret(b.Badge, out var badge) && TrySecret(b.NewPin, out var pin)
                ? Command(http, null, null, CommandSource.Terminal, c => s.SetBadgePinAsync(new SetBadgePin(badge, TrySecret(b.CurrentPin, out var current) ? current : null, pin), c, ct))
                : Task.FromResult(BadBadge()));

        api.MapPost("/start", async (HttpContext http, StartRequest b, ShopFloorService s, CancellationToken ct) =>
        {
            if (!TrySecret(b.Badge, out var badge))
            {
                return BadBadge();
            }

            Secret? trainer = TrySecret(b.TrainerBadge, out var t) ? t : null;
            return await Command(http, null, null, CommandSource.Terminal, c => s.StartAsync(new StartOperation(badge, b.OrderNumber, b.Sequence, b.MachineCode, trainer, ToolCode: b.ToolCode, Phase: b.Phase), c, ct));
        });

        api.MapPost("/stop", (HttpContext http, BadgeRequest b, ShopFloorService s, CancellationToken ct) =>
            TrySecret(b.Badge, out var badge) ? Command(http, b.Reason, null, CommandSource.Terminal, c => s.StopAsync(new StopWork(badge), c, ct)) : Task.FromResult(BadBadge()));

        api.MapPost("/report", (HttpContext http, ReportRequest b, ShopFloorService s, CancellationToken ct) =>
            TrySecret(b.Badge, out var badge)
                ? Command(http, null, null, CommandSource.Terminal, c => s.ReportAsync(new ReportProduction(badge, b.OrderNumber, b.Sequence, b.Good, b.Scrap, b.ScrapReason, b.Complete, b.ScrapReasonCode), c, ct))
                : Task.FromResult(BadBadge()));

        api.MapPost("/suspect-scrap", async (HttpContext http, SuspectScrapRequest b, QualityService quality, IForgeAuthorizer authorizer,
            IKernelUnitOfWorkFactory uows, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var decision = authorizer.Authorize(principal, Permissions.QualityReportSuspect, ResourceRef.Of(Permissions.QualityReportSuspect));
            if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision);
            if (!TrySecret(b.Badge, out var badge)) return BadBadge();
            Guid reporterPersonId;
            await using (var uow = await uows.BeginSnapshotAsync(ct))
            {
                var holder = await uow.Store<IWorkforceLookup>().FindBadgeHolderAsync(badge.Reveal(), ct);
                if (holder is not { IsWorker: true })
                    return Results.Problem(title: "Worker badge required", detail: "Scan the active employee badge identified at this terminal.", statusCode: StatusCodes.Status403Forbidden);
                reporterPersonId = holder.PersonId;
            }
            return await Command(http, b.Reason, null, CommandSource.Terminal,
                c => quality.ReportSuspectScrapAsync(new ReportSuspectScrap(b.Kind, reporterPersonId, b.OrderNumber, b.Sequence, b.PartNumber,
                    b.Quantity, b.ReasonCode, b.Description, b.LotNumber, b.SerialNumber), c, ct));
        });

        api.MapGet("/material-request-locations/{orderNumber:long}/{sequence:int}", (HttpContext http, long orderNumber, int sequence, CancellationToken _) =>
            Read(http, Permissions.ShopFloorRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, http.RequestAborted);
                if (order is null || order.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program)))
                    throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
                var operation = order.Operations.SingleOrDefault(candidate => candidate.Sequence == sequence)
                    ?? throw new CommandRejectedException("operation_not_found", $"Order {orderNumber} has no operation {sequence}.");
                return (await uow.Store<IInventoryLookup>().ListLocationsAsync(http.RequestAborted))
                    .Where(location => location.Active && location.InCompanyCustody && location.WorkCenterCode == operation.WorkCenterCode
                        && (location.Kind is LocationKind.Staging or LocationKind.Production
                            || location.Kind == LocationKind.MachineSide && location.PointOfUse))
                    .OrderBy(location => location.Kind == LocationKind.Staging ? 0 : 1).ThenBy(location => location.Code, StringComparer.Ordinal)
                    .Select(location => new { location.Code, location.Name, location.Kind, location.WorkCenterCode }).ToArray();
            }));

        api.MapGet("/material-requests/{orderNumber:long}", (HttpContext http, long orderNumber, CancellationToken _) =>
            Read(http, Permissions.ShopFloorRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, http.RequestAborted);
                if (order is null || order.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program)))
                    throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
                return (await uow.Store<IInventoryLookup>().ListMaterialTasksAsync(false, orderNumber, http.RequestAborted))
                    .Select(task => new { task.TaskId, task.PartNumber, task.Quantity, task.PickedQuantity, task.Status, task.OperationSequence, task.ToLocation, task.RequestedAt, task.RequesterPersonId, task.SourceSuspectReportId, task.IsNonBom, task.ReviewNote })
                    .ToArray();
            }));

        api.MapPost("/material-request", async (HttpContext http, OperatorMaterialRequest b, InventoryService inventory, IForgeAuthorizer authorizer,
            IKernelUnitOfWorkFactory uows, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var decision = authorizer.Authorize(principal, Permissions.InventoryMaterialRequest, ResourceRef.Of(Permissions.InventoryMaterialRequest));
            if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision);
            if (!TrySecret(b.Badge, out var badge)) return BadBadge();
            Guid requesterPersonId;
            await using (var uow = await uows.BeginSnapshotAsync(ct))
            {
                var holder = await uow.Store<IWorkforceLookup>().FindBadgeHolderAsync(badge.Reveal(), ct);
                if (holder is not { IsWorker: true })
                    return Results.Problem(title: "Worker badge required", detail: "Use the active employee badge already identified at this terminal.", statusCode: StatusCodes.Status403Forbidden);
                requesterPersonId = holder.PersonId;
            }
            return await Command(http, b.Reason, null, CommandSource.Terminal, c => inventory.RequestMaterialAsync(
                new RequestMaterial(MaterialTaskKind.Stage, b.OrderNumber, b.PartNumber, b.Quantity, b.ToLocation,
                    OperationSequence: b.Sequence, OperatorInitiated: true, RequesterPersonId: requesterPersonId, AllowNonBomMaterial: b.AllowNonBomMaterial), c, ct));
        });

        api.MapPost("/training/{sessionId:long}/evaluate", (HttpContext http, long sessionId, EvaluateRequest b, ShopFloorService s, CancellationToken ct) =>
            TrySecret(b.TrainerBadge, out var badge) && TrySecret(b.Pin, out var pin)
                ? Command(http, b.Notes, null, CommandSource.Terminal, c => s.EvaluateTrainingAsync(new EvaluateTraining(badge, pin, sessionId, b.Evaluation, b.Notes), c, ct))
                : Task.FromResult(BadBadge()));

        // What happened on the floor in a period: direct labor, quantities, training and attendance.
        api.MapGet("/activity", (HttpContext http, DateTimeOffset? from, DateTimeOffset? to, TimeProvider time, CancellationToken _) =>
            Read(http, Permissions.ShopFloorRead, async principal =>
            {
                var now = time.GetUtcNow();
                var (start, end) = (from ?? now.AddDays(-7), to ?? now);
                if (end <= start || end - start > TimeSpan.FromDays(62))
                {
                    throw new CommandRejectedException("invalid_period", "The period must end after it starts and span at most 62 days.");
                }

                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var exec = uow.Store<IExecutionLookup>();
                var hidden = await HiddenOrdersAsync(exec, principal, http.RequestAborted);
                return new
                {
                    from = start,
                    to = end,
                    reports = (await exec.ListReportsAsync(start, end, null, http.RequestAborted)).Where(r => !hidden.Contains(r.OrderNumber)).ToList(),
                    labor = (await exec.ListLaborAsync(start, end, null, http.RequestAborted)).Where(l => !hidden.Contains(l.OrderNumber)).ToList(),
                    training = (await exec.ListTrainingSessionsAsync(start, end, null, http.RequestAborted)).Where(t => !hidden.Contains(t.OrderNumber)).ToList(),
                    attendance = await uow.Store<IWorkforceLookup>().ListAttendanceAsync(start, end, http.RequestAborted),
                };
            }));

        app.MapGet("/api/orders/{orderNumber:long}/shopfloor", (HttpContext http, long orderNumber, TimeProvider time, CancellationToken _) =>
            Read(http, Permissions.OrdersRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var exec = uow.Store<IExecutionLookup>();
                var order = await exec.FindOrderAsync(orderNumber, http.RequestAborted);
                if (order is null || (order.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p))))
                {
                    throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
                }

                var labor = await exec.ListLaborAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, orderNumber, http.RequestAborted);
                var reports = await exec.ListReportsAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, orderNumber, http.RequestAborted);
                var stoppages = await uow.Store<IProductionActualsLookup>().ListOrderStoppagesAsync(orderNumber, http.RequestAborted);
                return new
                {
                    orderNumber = order.OrderNumber,
                    order.PartNumber,
                    order.Revision,
                    order.Quantity,
                    materials = order.Materials,
                    labor,
                    reports,
                    training = await exec.ListTrainingSessionsAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, orderNumber, http.RequestAborted),
                    laborMetrics = ProductionLaborMetrics.Calculate(order, labor, reports, time.GetUtcNow(), stoppages),
                };
            })).RequireAuthorization();

        app.MapPost("/api/workforce/people/{personId:guid}/badge", (HttpContext http, Guid personId, IssueBadgeRequest b, AttendanceService s, CancellationToken ct) =>
            TrySecret(b.Badge, out var badge)
                ? Command(http, b.Reason, c => s.IssueBadgeAsync(new IssueBadge(new PersonId(personId), badge), c, ct))
                : Task.FromResult(BadBadge())).RequireAuthorization();

        app.MapPost("/api/workforce/people/{personId:guid}/badge/revoke", (HttpContext http, Guid personId, ReasonRequest b, AttendanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RevokeBadgeAsync(new RevokeBadge(new PersonId(personId)), c, ct))).RequireAuthorization();

        app.MapGet("/api/planning/attainment", async (HttpContext http, DateTimeOffset? from, DateTimeOffset? to, ReadinessService s, TimeProvider time, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null)
            {
                return NotRegistered();
            }

            var now = time.GetUtcNow();
            return Results.Ok(await s.EvaluateAttainmentAsync(principal, from ?? now.AddDays(-1), to ?? now, ct));
        }).RequireAuthorization();
    }

    private static async Task<object?> IdentifyAsync(HttpContext http, ForgePrincipal principal, BadgeRequest b, TimeProvider time)
    {
        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(ct);
        var workforce = uow.Store<IWorkforceLookup>();
        var holder = (string.IsNullOrWhiteSpace(b.Badge) ? null : await workforce.FindBadgeHolderAsync(b.Badge, ct))
            ?? throw new CommandRejectedException("unknown_badge", "Badge not recognized. See your supervisor.");
        var state = (await workforce.GetWorkerStatesAsync(now, ct)).FirstOrDefault(s => s.PersonId == holder.PersonId);
        var attendance = holder.IsWorker ? await workforce.FindOpenAttendanceAsync(holder.PersonId, ct) : null;

        var exec = uow.Store<IExecutionLookup>();
        var labor = (await exec.ListOpenLaborAsync(ct)).FirstOrDefault(l => l.PersonId == holder.PersonId);
        var sessions = (await exec.ListTrainingSessionsAsync(now.AddDays(-30), now.AddMinutes(1), null, ct))
            .Where(t => (t.TrainerId == holder.PersonId || t.TraineeId == holder.PersonId) && (t.EndedAt is null || t.Evaluation is null))
            .ToList();

        // The person's work queue: their home work center, their assignment, and what the approved schedule has them on.
        var person = await uow.People.LoadPrincipalAsync(new PersonId(holder.PersonId), ct);
        bool Visible(Guid? program) => program is not { } p || (principal.Programs.Contains(new ProgramId(p)) && person is not null && person.Programs.Contains(new ProgramId(p)));
        var schedule = (await uow.Store<IPlanningStore>().LatestAsync(ScheduleStatus.Approved, true, ct))?.Result;
        var scheduledForMe = (schedule?.Operations ?? [])
            .Where(o => o.Segments.Any(s => s.Operators.Contains(holder.PersonId) && s.End > now))
            .ToDictionary(o => (o.OrderNumber, o.Sequence), o => o.Start);
        var scheduleStart = (schedule?.Operations ?? []).Where(o => o.Scheduled).ToDictionary(o => (o.OrderNumber, o.Sequence), o => (o.Start, o.MachineCode));
        var home = state?.HomeWorkCenterCode;
        var assigned = state?.Assignment;
        var mine = (await exec.ListOpenOperationsAsync(ct))
            .Where(o => Visible(o.ProgramId))
            .Where(o => o.Operation.WorkCenterCode == home
                || o.Operation.WorkCenterCode == assigned?.WorkCenterCode
                || (assigned?.MachineCode is { } m && o.Operation.EligibleMachines.Contains(m))
                || scheduledForMe.ContainsKey((o.OrderNumber, o.Operation.Sequence)))
            .ToList();
        var holds = new Dictionary<long, IReadOnlyList<WorkHold>>();
        foreach (var o in mine.DistinctBy(o => o.OrderNumber))
        {
            holds[o.OrderNumber] = await uow.Store<IWorkHoldLookup>().ListHoldsAsync(o.OrderNumber, o.PartNumber, ct);
        }

        var kernelHolds = await uow.Store<Forge.Kernel.Holds.IHoldLookup>().ListHoldsAsync(false, ct);
        var holdPolicies = await uow.Store<Forge.Kernel.Holds.IHoldLookup>().ListKindPoliciesAsync(ct);
        var queue = mine
            .Select(o => new
            {
                o.OrderNumber,
                o.PartNumber,
                o.OrderQuantity,
                o.DueDate,
                o.Priority,
                operation = o.Operation,
                scheduledStart = scheduleStart.TryGetValue((o.OrderNumber, o.Operation.Sequence), out var at) ? at.Start : null,
                scheduledMachine = scheduleStart.TryGetValue((o.OrderNumber, o.Operation.Sequence), out var sm) ? sm.MachineCode : null,
                scheduledForYou = scheduledForMe.ContainsKey((o.OrderNumber, o.Operation.Sequence)),
                hold = string.Join(" ", holds[o.OrderNumber].Where(h => h.Blocks(o.Operation.Sequence)).Select(h => h.Detail)
                    .Concat(kernelHolds.Where(h => h.Stops(new Forge.Kernel.Holds.HoldCheck(Forge.Kernel.Holds.HoldActivity.StartWork, o.OrderNumber, o.Operation.Sequence, [o.PartNumber], null, o.Operation.WorkCenterCode, null, ProgramId: o.ProgramId), holdPolicies))
                        .Select(h => h.Describe() + "."))) is { Length: > 0 } why ? why : null,
            })
            .OrderByDescending(o => o.scheduledForYou)
            .ThenBy(o => o.scheduledStart ?? DateTimeOffset.MaxValue)
            .ThenBy(o => o.Priority)
            .ThenBy(o => o.OrderNumber)
            .Take(25)
            .ToList();

        return new
        {
            personId = holder.PersonId,
            holder.DisplayName,
            holder.IsWorker,
            holder.HasPin,
            homeWorkCenter = home,
            assignment = assigned,
            presence = state?.PresenceKind,
            presentUntil = state?.PresentUntil,
            clockedInSince = attendance?.ClockInAt,
            labor,
            training = sessions,
            queue,
        };
    }

    private static async Task<HashSet<long>> HiddenOrdersAsync(IExecutionLookup exec, ForgePrincipal principal, CancellationToken ct) =>
        (await exec.ListOrdersAsync(ct)).Where(o => o.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p))).Select(o => o.OrderNumber).ToHashSet();

    private static bool TrySecret(string? value, out Secret secret)
    {
        secret = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        secret = new Secret(value);
        return true;
    }

    private static IResult BadBadge() => Results.Problem(
        title: "Badge required",
        detail: "Scan a badge (and enter a PIN where asked).",
        statusCode: StatusCodes.Status400BadRequest);
}
