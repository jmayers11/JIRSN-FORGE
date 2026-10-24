using System.Globalization;
using System.Text.Json;
using Forge.Execution.Contracts;
using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record PartialBuildBody(
    int? Sequence = null,
    decimal? Quantity = null,
    decimal? ReceivingHours = null,
    decimal? InspectionHours = null,
    decimal? PutAwayHours = null,
    decimal? StagingHours = null,
    decimal? SetupHours = null,
    decimal? MinimumBatch = null,
    decimal? PackMultiple = null,
    bool? PartialAllowed = null,
    string? Reason = null)
{
    public PartialBuildOptions Options() =>
        new(Sequence, Quantity, ReceivingHours, InspectionHours, PutAwayHours, StagingHours ?? 0, SetupHours ?? 0, MinimumBatch ?? 1, PackMultiple ?? 1, PartialAllowed ?? true);
}

public sealed record StorageRuleBody(string LocationCode, string PartNumber, decimal MaxUnits, decimal? MaxUtilizationPercent, int? MaxDwellHours, string SafetyReference, string? Reason);

public sealed record StorageRuleDecisionBody(bool Approve, string? Reason);

public sealed record PartialBuildCloseBody(string? Reason);

/// <summary>Approved WIP storage and partial builds (Sprint 10 §17–19): previews from Planning, approval and reservations in Inventory.</summary>
public static class PartialBuildEndpoints
{
    private static readonly JsonSerializerOptions Evidence = new(JsonSerializerDefaults.Web);

    public static void MapPartialBuilds(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/inventory/storage-rules", (HttpContext http, string? part, CancellationToken _) =>
            Read(http, Permissions.InventoryRead, async _ =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                var wanted = string.IsNullOrWhiteSpace(part) ? null : part.Trim().ToUpperInvariant();
                var rules = (await uow.Store<IWipStorageLookup>().ListStorageRulesAsync(http.RequestAborted)).Where(r => wanted is null || r.PartNumber == wanted).ToList();
                var result = new List<object>();
                foreach (var rule in rules)
                {
                    StorageAvailability? availability = rule.State == StorageRuleState.Active
                        ? WipStorageQueries.Availability(rule, await WipStorageQueries.OccupancyAsync(uow, rule.LocationCode, rule.PartNumber, now, null, http.RequestAborted))
                        : null;
                    result.Add(new { rule, availability });
                }

                return result;
            }));
        api.MapPost("/inventory/storage-rules", (HttpContext http, StorageRuleBody b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ProposeStorageRuleAsync(new ProposeStorageRule(b.LocationCode, b.PartNumber, b.MaxUnits, b.MaxUtilizationPercent, b.MaxDwellHours, b.SafetyReference), c, ct)));
        api.MapPost("/inventory/storage-rules/{id:long}/decision", (HttpContext http, long id, StorageRuleDecisionBody b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DecideStorageRuleAsync(new DecideStorageRule(id, b.Approve), c, ct)));
        api.MapPost("/inventory/storage-rules/{id:long}/retire", (HttpContext http, long id, PartialBuildCloseBody b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RetireStorageRuleAsync(new RetireStorageRule(id), c, ct)));

        // The preview never writes; the same options are recomputed at approval so the approver signs what the current facts allow.
        api.MapPost("/planning/orders/{order:long}/partial-build-preview", (HttpContext http, long order, PartialBuildBody? b, PartialBuildService plans, CancellationToken ct) =>
            Read(http, Permissions.ScheduleRead, async principal => await plans.PreviewAsync(principal, order, (b ?? new PartialBuildBody()).Options(), ct)));
        api.MapGet("/planning/partial-builds", (HttpContext http, long? order, bool? active, CancellationToken _) =>
            Read(http, Permissions.ScheduleRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var hidden = await HiddenOrdersAsync(uow, principal, http.RequestAborted);
                return (await uow.Store<IWipStorageLookup>().ListPartialBuildsAsync(active ?? false, order, http.RequestAborted))
                    .Where(p => !hidden.Contains(p.OrderNumber))
                    .OrderByDescending(p => p.BuildId)
                    .Take(500)
                    .ToList();
            }));
        api.MapPost("/planning/orders/{order:long}/partial-builds", (HttpContext http, long order, PartialBuildBody b, PartialBuildService plans, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, async c =>
            {
                var plan = await plans.PreviewAsync(c.Principal, order, b.Options(), ct);
                if (!plan.CanApprove || plan.StopSequence is not { } sequence)
                {
                    throw new CommandRejectedException("partial_build_not_feasible",
                        "This partial build cannot be approved now: " + string.Join(" ", plan.Blockers.DefaultIfEmpty(plan.Comparison)));
                }

                var evidence = JsonSerializer.Serialize(new
                {
                    plan.RuleVersion,
                    asOf = plan.AsOf.ToString("O", CultureInfo.InvariantCulture),
                    plan.StopSequence,
                    plan.BlockedSequence,
                    plan.Quantity,
                    plan.Materials,
                    plan.StorageLocation,
                    plan.StorageQuantity,
                    plan.ExpectedRestart,
                    plan.ExpectedDwellHours,
                    limits = plan.Feasibility?.LimitingConstraints,
                    plan.Comparison,
                }, Evidence);
                return await s.ApprovePartialBuildAsync(new ApprovePartialBuild(order, sequence, plan.Quantity, plan.StorageLocation, plan.StorageQuantity,
                    [.. plan.Materials.Select(m => new PartialBuildMaterialLine(m.PartNumber, m.Quantity))], evidence), c, ct);
            }));
        api.MapPost("/planning/partial-builds/{id:long}/complete", (HttpContext http, long id, PartialBuildCloseBody b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CompletePartialBuildAsync(new CompletePartialBuild(id), c, ct)));
        api.MapPost("/planning/partial-builds/{id:long}/cancel", (HttpContext http, long id, PartialBuildCloseBody b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelPartialBuildAsync(new CancelPartialBuild(id), c, ct)));
    }

    private static async Task<HashSet<long>> HiddenOrdersAsync(IKernelUnitOfWork uow, ForgePrincipal principal, CancellationToken ct) =>
        (await uow.Store<IExecutionLookup>().ListOrdersAsync(ct))
            .Where(o => o.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program)))
            .Select(o => o.OrderNumber)
            .ToHashSet();
}
