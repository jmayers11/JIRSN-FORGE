using Forge.Execution.Contracts;
using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Holds;
using Forge.Kernel.Security;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ShelfLifeRuleRequest(int? ShelfLifeDays, ShelfLifeBasis Basis, int? RetestDays, int? OpenLifeHours, decimal? StorageMinC, decimal? StorageMaxC, string? StorageNote, SelectionRule Selection, string? Reason);

public sealed record ExtensionRequest(DateOnly NewExpires, string Evidence, string? Reason);

public sealed record RetestRequest(bool Passed, DateOnly? NextDue, string Evidence, string? Reason);

public sealed record ExcursionRequest(string LocationCode, string Condition, decimal? MeasuredC, DateTimeOffset StartedAt, DateTimeOffset EndedAt, string? Note, string? Reason);

/// <summary>A lot's shelf-life state with what is on hand of it.</summary>
public sealed record LotShelfLifeView(LotShelfLife ShelfLife, decimal OnHand);

/// <summary>V3.6: shelf-life rules, lot ages and dates, open containers, extensions, retests, storage excursions and selection.</summary>
internal static class ShelfLifeEndpoints
{
    public static void MapShelfLifeEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/inventory").RequireAuthorization();

        api.MapGet("/shelf-life", (HttpContext http, string? part, bool? all, CancellationToken _) =>
            Read(http, Permissions.InventoryRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var p = string.IsNullOrWhiteSpace(part) ? null : part.Trim().ToUpperInvariant();
                var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                var onHand = (await uow.Store<IInventoryLookup>().ListBalancesAsync(p, http.RequestAborted))
                    .Where(b => b.LotNumber is not null).GroupBy(b => (b.PartNumber, b.LotNumber!)).ToDictionary(g => g.Key, g => g.Sum(b => b.Quantity));
                return (await ShelfLifeQueries.StatesAsync(uow, p, now, http.RequestAborted))
                    .Select(s => new LotShelfLifeView(s, onHand.GetValueOrDefault((s.PartNumber, s.LotNumber))))
                    .Where(v => (all ?? false) || v.OnHand > 0)
                    .Where(v => p is not null || v.ShelfLife.Status != ShelfLifeStatus.NotTracked)
                    .ToList();
            }));
        api.MapGet("/shelf-life-rules/{part}", (HttpContext http, string part, CancellationToken _) =>
            ReadStore<IShelfLifeLookup>(http, Permissions.InventoryRead, async (l, ct) => await l.FindRuleAsync(part.Trim().ToUpperInvariant(), ct)));
        api.MapPost("/shelf-life-rules/{part}", (HttpContext http, string part, ShelfLifeRuleRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetShelfLifeRuleAsync(new SetShelfLifeRule(part, b.ShelfLifeDays, b.Basis, b.RetestDays, b.OpenLifeHours, b.StorageMinC, b.StorageMaxC, b.StorageNote, b.Selection), c, ct)));
        api.MapPost("/lots/{part}/{lot}/open", (HttpContext http, string part, string lot, ReasonRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.OpenContainerAsync(new OpenContainer(part, lot), c, ct)));
        api.MapPost("/lots/{part}/{lot}/extensions", (HttpContext http, string part, string lot, ExtensionRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RequestShelfLifeExtensionAsync(new RequestShelfLifeExtension(part, lot, b.NewExpires, b.Evidence), c, ct)));
        api.MapGet("/extensions", (HttpContext http, bool? pending, CancellationToken _) =>
            ReadStore<IShelfLifeLookup>(http, Permissions.InventoryRead, async (l, ct) => (await l.ListExtensionsAsync(null, ct)).Where(e => !(pending ?? false) || e.Approved is null).ToList()));
        api.MapPost("/extensions/{id:long}/approve", (HttpContext http, long id, ReasonRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DecideShelfLifeExtensionAsync(new DecideShelfLifeExtension(id, true), c, ct)))
            .WithMetadata(new StepUpRequiredAttribute());
        api.MapPost("/extensions/{id:long}/reject", (HttpContext http, long id, ReasonRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DecideShelfLifeExtensionAsync(new DecideShelfLifeExtension(id, false), c, ct)));
        api.MapPost("/lots/{part}/{lot}/retests", (HttpContext http, string part, string lot, RetestRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordRetestAsync(new RecordRetest(part, lot, b.Passed, b.NextDue, b.Evidence), c, ct)));
        api.MapGet("/excursions", (HttpContext http, CancellationToken _) =>
            ReadStore<IShelfLifeLookup>(http, Permissions.InventoryRead, async (l, ct) => await l.ListExcursionsAsync(ct)));
        api.MapPost("/excursions", (HttpContext http, ExcursionRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordStorageExcursionAsync(new RecordStorageExcursion(b.LocationCode, b.Condition, b.MeasuredC, b.StartedAt, b.EndedAt, b.Note), c, ct)));

        // Which lots to use first: the hard rules, then FIFO or FEFO. With an order, the revision it needs is a hard rule too.
        api.MapGet("/selection", (HttpContext http, string part, long? order, CancellationToken _) =>
            Read(http, Permissions.InventoryRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var p = BusinessCode.Require(part, "Part");
                string? revision = null;
                OrderInfo? sourceOrder = null;
                if (order is { } n)
                {
                    var o = await uow.Store<IExecutionLookup>().FindOrderAsync(n, http.RequestAborted);
                    if (o is null || (o.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program))))
                    {
                        throw new CommandRejectedException("order_not_found", $"Order {n} does not exist.");
                    }

                    revision = o.Materials.FirstOrDefault(m => m.ComponentPartNumber == p)?.ComponentRevision;
                    sourceOrder = o;
                }

                var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                var lots = (await uow.Store<IInventoryLookup>().ListLotsAsync(p, http.RequestAborted)).ToDictionary(l => l.LotNumber, StringComparer.Ordinal);
                var states = (await ShelfLifeQueries.StatesAsync(uow, p, now, http.RequestAborted)).ToDictionary(s => s.LotNumber, StringComparer.Ordinal);
                var rule = await uow.Store<IShelfLifeLookup>().FindRuleAsync(p, http.RequestAborted);
                var positions = await ShelfLifeQueries.PositionsAsync(uow, p, now, http.RequestAborted);
                var candidates = new List<SelectionCandidate>();
                foreach (var candidate in LotSelection.Rank(positions, lots, states, rule?.Selection ?? SelectionRule.Fifo, revision))
                {
                    IReadOnlyList<Hold> stopping = candidate.ExcludedBecause is null ? await HoldGate.StoppingAsync(uow,
                        new HoldCheck(HoldActivity.IssueMaterial, sourceOrder?.OrderNumber, Parts: [p],
                            LotList: [new HoldLot(p, candidate.LotNumber)], ProgramId: sourceOrder?.ProgramId), http.RequestAborted) : [];
                    candidates.Add(stopping.Count == 0 ? candidate : candidate with
                    {
                        Rank = 0,
                        ExcludedBecause = "Not available for issue: " + string.Join("; ", stopping.Select(hold => hold.Describe())),
                    });
                }
                var ranked = candidates.Where(candidate => candidate.ExcludedBecause is null)
                    .Select((candidate, index) => candidate with { Rank = index + 1 })
                    .Concat(candidates.Where(candidate => candidate.ExcludedBecause is not null)).ToArray();
                return new
                {
                    partNumber = p,
                    rule = (rule?.Selection ?? SelectionRule.Fifo).ToString(),
                    requiredRevision = revision,
                    candidates = ranked,
                };
            }));
    }
}
