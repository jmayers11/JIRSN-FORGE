using System.Globalization;
using System.Net;
using System.Text.Json;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// New-employee training and training requests through the real API: onboarding requirements open requests when a worker is
/// registered (or onboarding is started), supervisors ask for training, expiring qualifications and approved development packets
/// open requests, technical trainers are notified, schedule and deliver it; nobody is certified by a request.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TrainingRequestTests(PostgresFixture postgres)
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private static async Task PropagateAsync(ForgeApp app)
    {
        var relay = app.Services.GetRequiredService<OutboxRelay>();
        while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
        {
        }

        await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
    }

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string Id(JsonElement result) => result.GetProperty("entityId").GetString()!;

    [Fact]
    public async Task New_hires_supervisors_expiring_qualifications_and_approved_packets_open_requests_that_trainers_schedule()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var plant = await SlittingPlant.BuildAsync(app);
        var e = plant.Engineer; // also a Training Coordinator
        await plant.Person("trainer", "TechnicalTrainer");
        await plant.Person("sup2", "Supervisor");
        await plant.Person("hr", "HumanResources");
        var (trainer, sup, hr) = (plant.As("trainer"), plant.As("sup2"), plant.As("hr"));

        // What every new hire needs, and what new hires in Slitting need on top.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(sup, "/api/workforce/onboarding-requirements", new { qualificationCode = "FIRST-AID", dueDays = 14, reason = "x" })).StatusCode);
        await plant.Ok(e, "/api/workforce/onboarding-requirements", new { qualificationCode = "FIRST-AID", dueDays = 14, note = "Before working alone", reason = "EHS onboarding" });
        await plant.Ok(trainer, "/api/workforce/onboarding-requirements", new { workCenterCode = "SLIT-WC", qualificationCode = "SLIT-II", dueDays = 30, reason = "Slitting onboarding" });
        Assert.Contains("requirement_exists", await (await plant.Try(e, "/api/workforce/onboarding-requirements", new { qualificationCode = "FIRST-AID", dueDays = 7, reason = "Again" })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(2, (await Get(sup, "/api/workforce/onboarding-requirements")).GetArrayLength());

        // Registering a new hire in Slitting opens both requests, and technical trainers hear about them.
        await plant.Worker("newbie", "SLIT-WC");
        var newbie = plant.People["newbie"];
        var opened = await Get(trainer, $"/api/workforce/training-requests?personId={newbie}&open=true");
        Assert.Equal(new[] { "FIRST-AID", "SLIT-II" }, opened.EnumerateArray().Select(r => r.GetProperty("qualificationCode").GetString()).ToArray());
        Assert.All(opened.EnumerateArray(), r => Assert.Equal("Onboarding", r.GetProperty("source").GetString()));
        var firstAid = opened[0];
        var due = DateTimeOffset.Parse(firstAid.GetProperty("dueOn").GetString()!, CultureInfo.InvariantCulture);
        Assert.InRange((due - clock.GetUtcNow()).TotalDays, 12.5, 14.5);
        await PropagateAsync(app);
        var inbox = await Get(trainer, "/api/notifications/inbox");
        Assert.Contains(inbox.EnumerateArray(), a => a.GetProperty("message").GetString()!.StartsWith("Newbie needs First aid (FIRST-AID) scheduled (new employee training, due ", StringComparison.Ordinal));
        Assert.DoesNotContain((await Get(sup, "/api/notifications/inbox")).EnumerateArray(), a => a.GetProperty("title").GetString() == "Training needs scheduling");

        // Starting onboarding for someone already employed skips what they hold and what is already open.
        var dana = plant.People["dana"];
        var started = await plant.Ok(sup, $"/api/workforce/workers/{dana}/onboarding", new { reason = "Moved to the floor" });
        Assert.Contains("Opened 1 new-employee training request", started.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("no_change", (await plant.Ok(sup, $"/api/workforce/workers/{dana}/onboarding", new { reason = "Again" })).GetProperty("code").GetString());

        // A supervisor asks for cross-training; a second open request for the same qualification is refused.
        var paint = Id(await plant.Ok(sup, "/api/workforce/training-requests", new { personId = dana, qualificationCode = "PAINT-I", reason = "Cover the paint booth on nights" }));
        Assert.Contains("training_already_requested", await (await plant.Try(sup, "/api/workforce/training-requests", new { personId = dana, qualificationCode = "PAINT-I", reason = "Again" })).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Supervisors ask; trainers schedule. Scheduling clears the trainers' notice; nothing is certified by delivering training.
        var firstAidId = firstAid.GetProperty("requestId").GetString();
        clock.Advance(TimeSpan.FromMinutes(5)); // history is ordered by time; the fixed test clock would tie the steps
        var start = clock.GetUtcNow().AddDays(2);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(sup, $"/api/workforce/training-requests/{firstAidId}/schedule", new { startsAt = start, endsAt = start.AddHours(4), reason = "x" })).StatusCode);
        Assert.Contains("separation_of_duties", await (await plant.Try(trainer, $"/api/workforce/training-requests/{firstAidId}/schedule", new { trainerPersonId = newbie, startsAt = start, endsAt = start.AddHours(4), reason = "x" })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(trainer, $"/api/workforce/training-requests/{firstAidId}/schedule", new { startsAt = start, endsAt = start.AddHours(4), location = "Training room", reason = "Next class" });
        Assert.Contains("training_not_started", await (await plant.Try(trainer, $"/api/workforce/training-requests/{firstAidId}/complete", new { note = "Done" })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await PropagateAsync(app);
        var alert = (await Get(trainer, "/api/notifications/inbox")).EnumerateArray().Single(a => a.GetProperty("entityId").GetString() == firstAidId);
        Assert.NotEqual(JsonValueKind.Null, alert.GetProperty("resolvedAt").ValueKind);
        clock.Advance(TimeSpan.FromDays(3));
        await plant.Ok(trainer, $"/api/workforce/training-requests/{firstAidId}/complete", new { note = "Delivered with the practical demonstration" });
        var history = await Get(sup, $"/api/workforce/training-requests/{firstAidId}/history");
        Assert.Equal(new[] { "Requested", "Scheduled", "Completed" }, history.EnumerateArray().Select(h => h.GetProperty("toStatus").GetString()).ToArray());
        var newbieQuals = await Get(sup, $"/api/competency/people/{newbie}");
        Assert.DoesNotContain(newbieQuals.EnumerateArray(), q => q.GetProperty("code").GetString() == "FIRST-AID" && q.GetProperty("status").GetString() == "Qualified");
        await plant.Ok(trainer, $"/api/workforce/training-requests/{paint}/cancel", new { note = "Dana moved to assembly days" });

        // An approved development packet asks for its training to be scheduled.
        var michael = plant.People["michael"];
        var packet = Id(await plant.Ok(e, "/api/workforce/development-nominations", new
        {
            personId = michael, kind = "AdvancedTraining", pathway = "Slitter setup technician", rationale = "Covers SLIT-II on two shifts",
            expectedPlantBenefit = "Second setup tech", evidenceReferences = Array.Empty<string>(), reason = "Development review",
        }));
        await plant.Ok(e, $"/api/workforce/development-nominations/{packet}/advance", new { status = "Submitted", note = "Ready", reason = "Ready" });
        await plant.Ok(hr, $"/api/workforce/development-nominations/{packet}/advance", new { status = "InReview", note = "Reviewing", reason = "Reviewing" });
        // The packet is advisory: whoever wrote it cannot decide it, and nothing is opened until another person approves it.
        Assert.Contains("independent_review_required", await (await plant.Try(e, $"/api/workforce/development-nominations/{packet}/advance", new { status = "Approved", note = "My own packet", reason = "Self" })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty((await Get(trainer, $"/api/workforce/training-requests?personId={michael}&open=true")).EnumerateArray());
        await plant.Ok(hr, $"/api/workforce/development-nominations/{packet}/advance", new { status = "Approved", note = "Approved for Q1", reason = "Approved" });
        var development = Assert.Single((await Get(trainer, $"/api/workforce/training-requests?personId={michael}&open=true")).EnumerateArray());
        Assert.Equal("Development", development.GetProperty("source").GetString());
        Assert.Equal("Slitter setup technician", development.GetProperty("title").GetString());

        // Qualifications about to expire open one reassessment request each, once.
        clock.Advance(TimeSpan.FromDays(340));
        var scan = await plant.Ok(trainer, "/api/workforce/training-requests/expiry-scan", new { reason = "Monthly check" });
        Assert.Contains("Opened 5 reassessment request(s)", scan.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("no_change", (await plant.Ok(trainer, "/api/workforce/training-requests/expiry-scan", new { reason = "Again" })).GetProperty("code").GetString());
        var sarah = await Get(sup, $"/api/workforce/training-requests?personId={plant.People["sarah"]}&open=true");
        Assert.Equal(new[] { "Expiry", "Expiry" }, sarah.EnumerateArray().Select(r => r.GetProperty("source").GetString()).ToArray());

        // Supervisors run the reviews, so they read attendance rates now.
        Assert.Equal(HttpStatusCode.OK, (await sup.GetAsync("/api/workforce/attendance-intelligence")).StatusCode);
    }
}
