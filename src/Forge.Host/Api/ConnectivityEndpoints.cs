using Forge.Connectivity;
using Forge.Connectivity.Contracts;
using Forge.Execution;
using Forge.Execution.Contracts;
using Forge.Host.Connectivity;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Maintenance;
using Forge.Workforce.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ConnectionRequest(ConnectorKind Kind, Dictionary<string, string>? Settings, int PollSeconds, int StopAfterSeconds, bool Active, string? Reason);

/// <param name="RequestMaintenance">Also tell maintenance the machine is down (the same as "Report machine down").</param>
public sealed record KioskDowntimeRequest(string Badge, string MachineCode, StoppageCategory Category, string ReasonCode, string? Note, bool RequestMaintenance, string? MaintenanceDescription);

public sealed record KioskDowntimeEndRequest(string Badge, string MachineCode);

public sealed record ReclassifyRequest(StoppageCategory Category, string ReasonCode, string? Note, Guid? ReportedBy, string? Reason);

/// <summary>Machine connections, downtime at the terminal and OEE per shift (owner decisions 2026-11-01).</summary>
internal static class ConnectivityEndpoints
{
    public const string Mask = "••••••";

    private static MachineConnection Masked(MachineConnection c)
    {
        var secret = ConnectorCatalog.Of(c.Kind).Settings.Where(s => s.Secret).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        return c with { Settings = c.Settings.ToDictionary(p => p.Key, p => secret.Contains(p.Key) ? Mask : p.Value, StringComparer.Ordinal) };
    }

    public static void MapConnectivityEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/connectivity").RequireAuthorization();
        api.MapGet("/connectors", (HttpContext http, CancellationToken _) =>
            Read(http, Permissions.ConnectivityRead, principal => Task.FromResult<object?>(ConnectorCatalog.All.Select(c => new
            {
                kind = c.Kind.ToString(), c.Name, c.Description, c.Settings,
                available = true,
            }))));
        api.MapGet("/machines", (HttpContext http, CancellationToken _) =>
            ReadStore<IConnectivityLookup>(http, Permissions.ConnectivityRead, async (s, ct) => new
            {
                connections = (await s.ListConnectionsAsync(ct)).Select(Masked).ToList(),
                status = await s.ListStatusAsync(ct),
            }));
        api.MapPost("/machines/{machine}", async (HttpContext http, string machine, ConnectionRequest b, ConnectivityService s, Forge.Host.Security.ISecretProtector secrets, IKernelUnitOfWorkFactory unitOfWork, CancellationToken ct) =>
        {
            // Secret settings are encrypted before they are stored; the mask sent back unchanged keeps the stored value.
            MachineConnection? current;
            await using (var read = await unitOfWork.BeginSnapshotAsync(ct)) current = await read.Store<IConnectivityLookup>().FindConnectionAsync(machine.Trim().ToUpperInvariant(), ct);
            var secretNames = ConnectorCatalog.Of(b.Kind).Settings.Where(x => x.Secret).Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in b.Settings ?? new Dictionary<string, string>())
            {
                if (!secretNames.Contains(name) || string.IsNullOrEmpty(value)) settings[name] = value;
                else if (value == Mask) { if (current?.Settings.TryGetValue(name, out var kept) == true) settings[name] = kept; }
                else settings[name] = secrets.Protect(value);
            }

            return await Command(http, b.Reason, c => s.ConfigureAsync(new ConfigureConnection(machine, b.Kind, settings, b.PollSeconds, b.StopAfterSeconds, b.Active), c, ct));
        });
        api.MapPost("/poll", (HttpContext http, MachinePoller poller, CancellationToken _) =>
            Read(http, Permissions.ConnectivityManage, async principal => new { polled = await poller.RunOnceAsync(true, http.RequestAborted) }));

        app.MapGroup("/api/oee").RequireAuthorization().MapGet(string.Empty, (HttpContext http, DateOnly? from, DateOnly? to, string? machine, OeeService s, CancellationToken _) =>
            Read(http, Permissions.ResourcesRead, async principal => await s.ReportAsync(from, to, string.IsNullOrWhiteSpace(machine) ? null : machine.Trim().ToUpperInvariant(), http.RequestAborted)));

        app.MapGroup("/api/execution").RequireAuthorization().MapPost("/stoppages/{id:long}/reclassify", (HttpContext http, long id, ReclassifyRequest b, ProductionActualsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReclassifyStoppageAsync(new ReclassifyStoppage(id, b.Category, b.ReasonCode, b.Note, b.ReportedBy is { } p ? $"person:{p:D}" : null), c, ct)));

        // At the terminal: the operator reports why the machine stopped (or explains a stop the machine reported), and can ask maintenance to come.
        var kiosk = app.MapGroup("/api/shopfloor").RequireAuthorization();
        kiosk.MapPost("/downtime", async (HttpContext http, KioskDowntimeRequest b, ProductionActualsService actuals, MaintenanceService maintenance, IKernelUnitOfWorkFactory unitOfWork, CancellationToken ct) =>
        {
            var (context, error) = await ContextAsync(http, b.Note, null, CommandSource.Terminal);
            if (context is null) return error!;
            var machine = (b.MachineCode ?? string.Empty).Trim().ToUpperInvariant();
            BadgeHolder? holder;
            StoppageInfo? open;
            await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
            {
                holder = string.IsNullOrWhiteSpace(b.Badge) ? null : await read.Store<IWorkforceLookup>().FindBadgeHolderAsync(b.Badge.Trim(), ct);
                open = (await read.Store<IProductionActualsLookup>().ListStoppagesAsync(DateTimeOffset.MinValue, DateTimeOffset.MinValue, true, ct)).FirstOrDefault(s => s.MachineCode == machine);
            }

            if (holder is null) throw new CommandRejectedException("unknown_badge", "Badge not recognized. See your supervisor.");
            var reporter = $"person:{holder.PersonId:D}";
            CommandResult result;
            if (open is { Unexplained: true })
                result = await actuals.ReclassifyStoppageAsync(new ReclassifyStoppage(open.StoppageId, b.Category, b.ReasonCode, b.Note, reporter), context, ct);
            else if (open is not null)
                throw new CommandRejectedException("machine_already_stopped", $"{machine} is already recorded as stopped ({open.ReasonCode}) since {open.StartedAt:u}.");
            else
                result = await actuals.StartStoppageAsync(new StartStoppage(machine, b.Category, b.ReasonCode, null, null, b.Note, null, reporter, StoppageSource.Kiosk), context, ct);
            if (b.RequestMaintenance)
            {
                var description = string.IsNullOrWhiteSpace(b.MaintenanceDescription) ? b.Note : b.MaintenanceDescription;
                if (string.IsNullOrWhiteSpace(description)) throw new CommandRejectedException("description_required", "Tell maintenance what happened.");
                await maintenance.ReportDownAsync(new ReportMachineDown(machine, $"{description.Trim()} (reported at the terminal by {holder.DisplayName})"),
                    context with { IdempotencyKey = context.IdempotencyKey + ":maintenance", Reason = description.Trim() }, ct);
            }

            return Results.Ok(result);
        });
        kiosk.MapPost("/downtime/end", async (HttpContext http, KioskDowntimeEndRequest b, ProductionActualsService actuals, IKernelUnitOfWorkFactory unitOfWork, CancellationToken ct) =>
        {
            var (context, error) = await ContextAsync(http, null, null, CommandSource.Terminal);
            if (context is null) return error!;
            var machine = (b.MachineCode ?? string.Empty).Trim().ToUpperInvariant();
            StoppageInfo? open;
            BadgeHolder? holder;
            await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
            {
                holder = string.IsNullOrWhiteSpace(b.Badge) ? null : await read.Store<IWorkforceLookup>().FindBadgeHolderAsync(b.Badge.Trim(), ct);
                open = (await read.Store<IProductionActualsLookup>().ListStoppagesAsync(DateTimeOffset.MinValue, DateTimeOffset.MinValue, true, ct)).FirstOrDefault(s => s.MachineCode == machine);
            }

            if (holder is null) throw new CommandRejectedException("unknown_badge", "Badge not recognized. See your supervisor.");
            if (open is null) throw new CommandRejectedException("not_stopped", $"{machine} has no open stoppage.");
            return Results.Ok(await actuals.EndStoppageAsync(new EndStoppage(open.StoppageId), context, ct));
        });
    }
}
