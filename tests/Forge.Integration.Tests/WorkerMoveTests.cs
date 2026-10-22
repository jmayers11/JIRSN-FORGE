using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Host.Moves;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// ADR 0057 §5 through the real API: a temporary move has a required estimated end date and can be extended or
/// renewed; nobody acting means the worker is back home and the supervisor is told; a transfer is a separate action.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkerMoveTests(PostgresFixture postgres)
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Set(DateTimeOffset at) => now = at;
    }

    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    private static async Task PropagateAsync(ForgeApp app)
    {
        var relay = app.Services.GetRequiredService<OutboxRelay>();
        while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
        {
        }

        await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
    }

    [Fact]
    public async Task A_temporary_move_ends_by_itself_unless_extended_and_a_transfer_is_separate()
    {
        // Monday 2026-10-05, 10:00 in New York.
        var monday = new DateOnly(2026, 10, 5);
        var clock = new Clock(new DateTimeOffset(2026, 10, 5, 14, 0, 0, TimeSpan.Zero));
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app, TestSite.NewYork);
        await plant.Person("sup", "Supervisor");
        var sup = plant.As("sup");
        var sarah = plant.People["sarah"];
        var reconciler = app.Services.GetRequiredService<TemporaryMoveReconciler>();

        // The estimated end date is required, and moves do not overlap.
        var noEnd = await plant.Try(sup, $"/api/workforce/workers/{sarah}/moves", new { workCenterCode = "PAINT-WC", startsOn = D(monday), reason = "Cover for paint" });
        Assert.Contains("invalid_move_end", await noEnd.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var move = (await plant.Ok(sup, $"/api/workforce/workers/{sarah}/moves", new { workCenterCode = "PAINT-WC", startsOn = D(monday), estimatedEndOn = D(monday.AddDays(2)), reason = "Cover for paint" }))
            .GetProperty("entityId").GetString()!;
        var overlap = await plant.Try(sup, $"/api/workforce/workers/{sarah}/moves", new { workCenterCode = "ASSY-WC", startsOn = D(monday.AddDays(1)), estimatedEndOn = D(monday.AddDays(3)), reason = "Assembly" });
        Assert.Contains("move_overlaps", await overlap.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Two working days before the end the supervisor is reminded, once.
        var first = await reconciler.RunAsync(CancellationToken.None);
        Assert.Contains("1 reminder(s)", first.Message, StringComparison.Ordinal);
        Assert.Equal("no_change", (await reconciler.RunAsync(CancellationToken.None)).Code);

        // Extended with a reason: the new end date is recorded, and the reminder comes again before it.
        await plant.Ok(sup, $"/api/workforce/moves/{move}/extend", new { newEndOn = D(monday.AddDays(4)), reason = "Paint backlog not cleared" });
        var active = Assert.Single((await Get(sup, $"/api/workforce/moves?personId={sarah}")).EnumerateArray());
        Assert.Equal(D(monday.AddDays(4)), active.GetProperty("endsOn").GetString());
        Assert.Equal("Extended", Assert.Single(active.GetProperty("changes").EnumerateArray()).GetProperty("kind").GetString());

        // Nobody extends it again: on Saturday Sarah is treated as back in Slitting and the supervisor is told.
        clock.Set(new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero));
        var back = await reconciler.RunAsync(CancellationToken.None);
        Assert.Contains("1 worker(s) returned home", back.Message, StringComparison.Ordinal);
        Assert.Empty((await Get(sup, $"/api/workforce/moves?personId={sarah}")).EnumerateArray());
        await PropagateAsync(app);
        var inbox = (await sup.GetFromJsonAsync<JsonElement[]>("/api/notifications/inbox"))!;
        Assert.Contains(inbox, a => a.GetProperty("message").GetString()!.Contains("has returned to SLIT-WC", StringComparison.Ordinal));
        Assert.Contains(inbox, a => a.GetProperty("message").GetString()!.Contains("ends 2026-10-07", StringComparison.Ordinal));

        // A move that ran out can be renewed: a new move from today, the old one keeps its dates.
        var renewed = (await plant.Ok(sup, $"/api/workforce/moves/{move}/renew", new { newEndOn = D(monday.AddDays(9)), reason = "Paint still short" })).GetProperty("entityId").GetString()!;
        var again = Assert.Single((await Get(sup, $"/api/workforce/moves?personId={sarah}")).EnumerateArray());
        Assert.Equal(renewed, again.GetProperty("moveId").GetInt64().ToString(CultureInfo.InvariantCulture));
        Assert.Equal("2026-10-10", again.GetProperty("startsOn").GetString());
        await plant.Ok(sup, $"/api/workforce/moves/{renewed}/end", new { reason = "Paint caught up" });
        var history = (await Get(sup, $"/api/workforce/moves?personId={sarah}&includeClosed=true")).EnumerateArray().ToList();
        Assert.Contains(history, m => m.GetProperty("status").GetString() == "Returned" && m.GetProperty("endsOn").GetString() == "2026-10-09");
        Assert.Contains(history, m => m.GetProperty("status").GetString() == "EndedEarly");

        // A transfer is a separate action, for people who manage the workforce, and never back-dated.
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await plant.Try(sup, $"/api/workforce/workers/{sarah}/transfer", new { workCenterCode = "PAINT-WC", effectiveFrom = "2026-10-12", reason = "Transfer" })).StatusCode);
        var past = await plant.Try(plant.Engineer, $"/api/workforce/workers/{sarah}/transfer", new { workCenterCode = "PAINT-WC", effectiveFrom = "2026-10-09", reason = "Transfer" });
        Assert.Contains("transfer_in_past", await past.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{sarah}/transfer", new { workCenterCode = "PAINT-WC", effectiveFrom = "2026-10-12", reason = "Moves to paint for good" });
        var transfer = Assert.Single((await Get(plant.Engineer, $"/api/workforce/transfers?personId={sarah}")).EnumerateArray());
        Assert.Equal(("SLIT-WC", "PAINT-WC"), (transfer.GetProperty("fromWorkCenter").GetString(), transfer.GetProperty("workCenter").GetString()));

        // The home changes on the effective date, not before.
        Assert.Equal("SLIT-WC", WorkerHome(await Get(plant.Engineer, "/api/workforce/workers"), sarah));
        clock.Set(new DateTimeOffset(2026, 10, 12, 14, 0, 0, TimeSpan.Zero));
        Assert.Equal("PAINT-WC", WorkerHome(await Get(plant.Engineer, "/api/workforce/workers"), sarah));
    }

    private static string? WorkerHome(JsonElement workers, Guid person) =>
        workers.EnumerateArray().Single(w => w.GetProperty("personId").GetGuid() == person).GetProperty("homeWorkCenterCode").GetString();
}
