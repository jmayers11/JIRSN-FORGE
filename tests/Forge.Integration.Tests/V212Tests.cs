using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// V2.12 through the real API: "What changed on SL-02?" lists only events recorded for the asset, its machine and
/// its work orders, each linked to its record; Horizon's future events come only from dates stored on records,
/// limited to what the reader may see; the Analyst answers the same question from the same records.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V212Tests(PostgresFixture postgres)
{
    private static readonly string[] LinkKinds = ["asset", "machine", "work-order", "item", "document", "change", "project"];

    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetStatus(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> Ask(HttpClient client, string question)
    {
        var response = await client.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question, narrate = false });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{question} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static List<JsonElement> Events(JsonElement history) => history.GetProperty("events").EnumerateArray().ToList();

    private static void AllLinkedAndPast(IEnumerable<JsonElement> events)
    {
        foreach (var e in events)
        {
            var link = e.GetProperty("link").GetString()!;
            Assert.Contains(link.Split(':')[0], LinkKinds);
            Assert.False(string.IsNullOrWhiteSpace(link.Split(':', 2)[1]), link);
            Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("source").GetString()));
            Assert.True(e.GetProperty("at").GetDateTimeOffset() <= DateTimeOffset.UtcNow.AddMinutes(1), $"{e} is not in the past");
        }
    }

    [Fact]
    public async Task What_changed_on_SL_02_lists_only_recorded_events_each_linked_to_its_record()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("mp", "MaintenancePlanner");
        await plant.Person("mp2", "MaintenancePlanner");
        await plant.Person("tech", "MaintenanceTechnician");
        await plant.Person("ship", "Shipper");
        var (mp, mp2, tech, ship) = (plant.As("mp"), plant.As("mp2"), plant.As("tech"), plant.As("ship"));
        await plant.Ok(mp, "/api/maintenance/assets", new { assetCode = "SL-02", name = "Slitter 2", type = "Production", machineCode = "SLITTER-B", criticality = "Critical" });
        await plant.Ok(mp, "/api/maintenance/assets/SL-02/status", new { status = "Active", reason = "Site acceptance test passed" });

        // Before any work: only the asset's own history, nothing invented.
        var assetHistory = (await Get(mp, "/api/maintenance/assets/SL-02")).GetProperty("history").GetArrayLength();
        var first = await Get(mp, "/api/history/SL-02");
        Assert.Equal(("asset", "SL-02"), (first.GetProperty("subjectKind").GetString(), first.GetProperty("subject").GetString()));
        Assert.Equal(assetHistory, Events(first).Count);
        Assert.All(Events(first), e => Assert.Equal("asset:SL-02", e.GetProperty("link").GetString()));

        // Work, an outage approved by someone else, and a breakdown on its machine.
        var url = "/api/maintenance/work-orders";
        var wo = Id(await plant.Ok(mp, url, new { assetCode = "SL-02", title = "Calibrate tension load cells", dueDate = Day(10), kind = "Calibration" }));
        var starts = DateTimeOffset.UtcNow.AddDays(3);
        await plant.Ok(mp, $"{url}/{wo}/schedule", new { startsAt = starts, endsAt = starts.AddHours(2), reason = "Planned stop" });
        var outage = Assert.Single((await Get(mp, "/api/maintenance/outages")).EnumerateArray()).GetProperty("outageId").GetInt64();
        await plant.Ok(mp2, $"/api/maintenance/outages/{outage}/approve", new { reason = "Night shift has spare capacity on SLITTER-A" });
        await plant.Ok(tech, "/api/maintenance/machines/SLITTER-B/down", new { description = "Rewind motor tripping" });
        await plant.Ok(tech, "/api/maintenance/machines/SLITTER-A/down", new { description = "Not this machine" });

        var history = await Get(mp, "/api/history/SL-02");
        var events = Events(history);
        AllLinkedAndPast(events);
        var kinds = events.Select(e => (e.GetProperty("kind").GetString(), e.GetProperty("link").GetString())).ToList();
        Assert.Contains(("Work order raised", $"work-order:{wo}"), kinds);
        Assert.Contains(("Outage proposed", "machine:SLITTER-B"), kinds);
        Assert.Contains(("Outage approved", "machine:SLITTER-B"), kinds);
        Assert.Contains(events, e => e.GetProperty("kind").GetString()!.StartsWith("Downtime", StringComparison.Ordinal) && e.GetProperty("title").GetString()!.Contains("Rewind motor tripping", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.GetProperty("title").GetString()!.Contains("Not this machine", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.GetProperty("link").GetString() == "machine:SLITTER-A");

        // Every event is one of: the asset history, the two work orders (planned, and the corrective one the breakdown
        // raised), the outage (proposed, approved) and the breakdown — not the planned window still ahead, nothing else.
        var corrective = (await Get(mp, $"{url}?includeClosed=true")).EnumerateArray().Select(v => v.GetProperty("order"))
            .Single(o => o.GetProperty("kind").GetString() == "Corrective" && o.GetProperty("machineCode").GetString() == "SLITTER-B").GetProperty("workOrderId").GetInt64();
        Assert.Contains(("Work order raised", $"work-order:{corrective}"), kinds);
        Assert.Equal((await Get(mp, "/api/maintenance/assets/SL-02")).GetProperty("history").GetArrayLength() + 2 + 2 + 1, events.Count);
        Assert.DoesNotContain(events, e => e.GetProperty("kind").GetString() == "Downtime (Planned)");
        var approval = events.Single(e => e.GetProperty("kind").GetString() == "Outage approved");
        Assert.Contains("Night shift has spare capacity", approval.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Equal("person:", approval.GetProperty("actor").GetString()![..7]);
        Assert.True(events.Zip(events.Skip(1)).All(p => p.First.GetProperty("at").GetDateTimeOffset() >= p.Second.GetProperty("at").GetDateTimeOffset()), "newest first");

        // A period narrows to what was recorded in it; the machine's own view is the same records.
        Assert.Equal(events.Count, Events(await Get(mp, "/api/history/SL-02?days=1")).Count);
        var machine = await Get(mp, "/api/history/SLITTER-B?kind=machine");
        Assert.Equal("machine", machine.GetProperty("subjectKind").GetString());
        Assert.Contains(Events(machine), e => e.GetProperty("link").GetString() == $"work-order:{wo}");
        AllLinkedAndPast(Events(machine));

        // Unknown subjects and wrong kinds say so; readers without maintenance access are refused.
        var (missing, missingBody) = await GetStatus(mp, "/api/history/NOPE-9");
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Contains("subject_not_found", missingBody, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await GetStatus(mp, "/api/history/SL-02?kind=project")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetStatus(ship, "/api/history/SL-02")).Status);

        // The Analyst answers "What changed on SL-02?" from the same records, each fact traced.
        var answer = await Ask(mp, "What changed on SL-02?");
        Assert.Equal(("WhatChanged", "SL-02"), (answer.GetProperty("interpretation").GetProperty("intent").GetString(), answer.GetProperty("interpretation").GetProperty("subject").GetString()));
        var facts = answer.GetProperty("facts").EnumerateArray().ToList();
        Assert.Equal(events.Count, facts.Count);
        Assert.All(facts, f => Assert.Contains(f.GetProperty("trace").GetString()!.Split(':')[0], LinkKinds));
        Assert.Contains($"{events.Count} recorded event", answer.GetProperty("headline").GetString(), StringComparison.Ordinal);
        Assert.Contains("Nothing called NOPE-9", (await Ask(mp, "What changed on NOPE-9?")).GetProperty("headline").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Horizon_shows_future_events_only_from_records_the_reader_may_see()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("mp", "MaintenancePlanner");
        await plant.Person("mp2", "MaintenancePlanner");
        await plant.Person("fm", "FacilitiesManager");
        await plant.Person("ship", "Shipper");
        var (mp, mp2, fm, ship) = (plant.As("mp"), plant.As("mp2"), plant.As("fm"), plant.As("ship"));
        await plant.Ok(mp, "/api/maintenance/assets", new { assetCode = "SL-02", name = "Slitter 2", type = "Production", machineCode = "SLITTER-B", criticality = "Critical" });
        await plant.Ok(mp, "/api/maintenance/assets/SL-02/status", new { status = "Active", reason = "Site acceptance test passed" });

        // Nothing dated yet: nothing ahead, rather than a guess.
        Assert.DoesNotContain((await Get(mp, "/api/horizon/events?days=30")).EnumerateArray(), e => e.GetProperty("link").GetString()!.StartsWith("machine:", StringComparison.Ordinal));

        var url = "/api/maintenance/work-orders";
        var wo = Id(await plant.Ok(mp, url, new { assetCode = "SL-02", title = "Calibrate tension load cells", dueDate = Day(10), kind = "Calibration" }));
        var starts = DateTimeOffset.UtcNow.AddDays(3);
        await plant.Ok(mp, $"{url}/{wo}/schedule", new { startsAt = starts, endsAt = starts.AddHours(2), reason = "Planned stop" });
        var outage = Assert.Single((await Get(mp, "/api/maintenance/outages")).EnumerateArray()).GetProperty("outageId").GetInt64();
        await plant.Ok(mp2, $"/api/maintenance/outages/{outage}/approve", new { reason = "Night shift has spare capacity" });
        var far = Id(await plant.Ok(mp, url, new { machineCode = "SLITTER-A", title = "Inspect guards", dueDate = Day(60), kind = "Inspection" }));

        await plant.Ok(fm, "/api/projects", new { code = "FAC-9", title = "Dust extraction", type = "Facility", funding = "Operating", budget = 5000m, startDate = Day(1), endDate = Day(45) });
        await plant.Ok(fm, "/api/projects/FAC-9/tasks", new { title = "Ductwork installed", dueDate = Day(20), milestone = true });

        var events = (await Get(mp, "/api/horizon/events?days=30")).EnumerateArray().ToList();
        Assert.All(events, e => Assert.True(e.GetProperty("at").GetDateTimeOffset() >= DateTimeOffset.UtcNow.AddMinutes(-5), $"{e} is in the past"));
        Assert.All(events, e => Assert.True(e.GetProperty("at").GetDateTimeOffset() < DateTimeOffset.UtcNow.AddDays(30), $"{e} is beyond the window"));
        Assert.All(events, e => Assert.Contains(e.GetProperty("link").GetString()!.Split(':')[0], LinkKinds));
        var outageEvent = Assert.Single(events, e => e.GetProperty("kind").GetString() == "Outage");
        Assert.Equal("machine:SLITTER-B", outageEvent.GetProperty("link").GetString());
        Assert.Equal(starts.ToUnixTimeSeconds(), outageEvent.GetProperty("at").GetDateTimeOffset().ToUnixTimeSeconds());
        Assert.Contains(events, e => e.GetProperty("kind").GetString() == "Work order due" && e.GetProperty("link").GetString() == $"work-order:{wo}");
        Assert.DoesNotContain(events, e => e.GetProperty("link").GetString() == $"work-order:{far}");
        Assert.Contains(events, e => e.GetProperty("kind").GetString() == "Milestone" && e.GetProperty("link").GetString() == "project:FAC-9");
        Assert.DoesNotContain(events, e => e.GetProperty("kind").GetString() == "Planned downtime" && e.GetProperty("link").GetString() == "machine:SLITTER-B");
        Assert.True(events.Zip(events.Skip(1)).All(p => p.First.GetProperty("at").GetDateTimeOffset() <= p.Second.GetProperty("at").GetDateTimeOffset()), "soonest first");

        // A longer window reaches the later due date; a reader without maintenance or project access sees none of it.
        Assert.Contains((await Get(mp, "/api/horizon/events?days=90")).EnumerateArray(), e => e.GetProperty("link").GetString() == $"work-order:{far}");
        var shipper = (await Get(ship, "/api/horizon/events?days=90")).EnumerateArray().ToList();
        Assert.DoesNotContain(shipper, e => e.GetProperty("link").GetString()!.StartsWith("machine:", StringComparison.Ordinal)
            || e.GetProperty("link").GetString()!.StartsWith("work-order:", StringComparison.Ordinal) || e.GetProperty("link").GetString()!.StartsWith("project:", StringComparison.Ordinal));

        // The same dates as a report, traced to their records.
        var report = await Get(mp, $"/api/reports/dated-events?from={Day(0)}&to={Day(29)}");
        var rows = report.GetProperty("rows").EnumerateArray().ToList();
        Assert.Contains(rows, r => r.GetProperty("trace").EnumerateArray().Any(t => t.GetString() == $"work-order:{wo}"));
        Assert.DoesNotContain(rows, r => r.GetProperty("trace").EnumerateArray().Any(t => t.GetString() == $"work-order:{far}"));
    }
}
