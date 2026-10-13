using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Host.Api;
using Forge.Host.Security;
using Forge.Planning.Analyst;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.Integration.Tests;

/// <summary>
/// Phase 9 exit gate through the real API: Forge Analyst answers spec §58 questions from Forge's own
/// services, shows what it understood, respects the asker's permissions and program access, runs
/// what-if scenarios without changing anything, turns report requests into confirmable report
/// definitions, compiles an operational brief, and uses a generative AI provider only to restate
/// answers it already computed.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class Phase9Tests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-0001";

    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Ask(HttpClient client, string question, bool narrate = false)
    {
        var response = await client.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question, narrate });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{question} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static string Headline(JsonElement answer) => answer.GetProperty("headline").GetString()!;

    private static string Intent(JsonElement answer) => answer.GetProperty("interpretation").GetProperty("intent").GetString()!;

    private static async Task<(SlittingPlant.Plant Plant, HttpClient Planner, HttpClient Planner2)> PlantAsync(ForgeApp app)
    {
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        foreach (var wc in new[] { "SLIT-WC", "PAINT-WC", "ASSY-WC" })
        {
            await plant.Ok(e, $"/api/resources/work-centers/{wc}/shifts", new { shiftCode = "ALL" });
        }

        foreach (var person in new[] { "sarah", "michael", "jennifer", "dana" })
        {
            await plant.Ok(e, $"/api/workforce/workers/{plant.People[person]}/shift", new { shiftCode = "ALL" });
        }

        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Person("planner2", "Planner");
        await plant.Person("tech", "MaintenanceTechnician");
        await plant.Person("kiosk", "ShopFloorTerminal");
        await plant.Person("ship", "Shipper");
        return (plant, planner, plant.As("planner2"));
    }

    private static async Task<long> ReleasedOrderAsync(SlittingPlant.Plant plant, HttpClient planner, decimal quantity, int dueInDays, Guid? program = null)
    {
        var number = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity, dueDate = Day(dueInDays), priority = 3, programId = program }));
        await plant.Ok(planner, $"/api/orders/{number}/release", new { reason = "Customer order" });
        return number;
    }

    [Fact]
    public async Task Analyst_answers_from_forge_services_with_the_askers_access()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner, planner2) = await PlantAsync(app);
        await plant.Person("pm", "ProgramManager");
        await plant.Person("schedule-reviewer", "Planner");
        var (tech, ship, kiosk, eng) = (plant.As("tech"), plant.As("ship"), plant.As("kiosk"), plant.Engineer);

        var falcon = Guid.Parse((await plant.Ok(plant.As("pm"), "/api/programs/", new { code = "FALCON", name = "Program Falcon" })).GetProperty("entityId").GetString()!);
        foreach (var who in new[] { "pm", "planner", "eng", "schedule-reviewer" })
        {
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[who]}/programs", new { programId = falcon, reason = "Falcon team" });
        }

        var order = await ReleasedOrderAsync(plant, planner, 20m, 10);
        var secret = await ReleasedOrderAsync(plant, planner, 5m, 12, falcon);
        await plant.Ok(tech, "/api/maintenance/machines/SLITTER-B/down", new { description = "Blade drive fault" });
        var version = Id(await plant.Ok(planner, "/api/planning/schedules", new { horizonDays = 14 }));
        await plant.Ok(plant.As("schedule-reviewer"), $"/api/planning/schedules/{version}/approve", new { reason = "Source-authorized independent review" });

        // A terminal has no Analyst; the status lists suggestions and says no AI is configured.
        Assert.Equal(HttpStatusCode.Forbidden, (await kiosk.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question = "What is late?" })).StatusCode);
        var status = await Get(planner, "/api/analyst/status");
        Assert.False(status.GetProperty("narration").GetBoolean());
        Assert.Equal("none", status.GetProperty("provider").GetString());

        // "Why is order N late?" — the approved schedule's projection, with the operations and what fixed them.
        var why = await Ask(planner, $"Why is order {order} late?");
        Assert.Equal("OrderStatus", Intent(why));
        Assert.Equal(order, why.GetProperty("interpretation").GetProperty("orderNumber").GetInt64());
        Assert.StartsWith($"Order {order} is", Headline(why), StringComparison.Ordinal);
        Assert.Contains(why.GetProperty("facts").EnumerateArray(), f => f.GetProperty("trace").GetString() == $"schedule:{version}");
        Assert.Contains(why.GetProperty("facts").EnumerateArray(), f => f.GetProperty("text").GetString()!.StartsWith("Op 10", StringComparison.Ordinal));

        // A program order is invisible to someone without the program, exactly as on the screens.
        Assert.Contains("does not exist", Headline(await Ask(planner2, $"Why is order {secret} late?")), StringComparison.Ordinal);
        Assert.StartsWith($"Order {secret}", Headline(await Ask(planner, $"Why is order {secret} late?")), StringComparison.Ordinal);

        // Machines: what is down, and what stops one from running.
        var down = await Ask(planner, "What machines are unavailable?");
        Assert.Equal("UnavailableMachines", Intent(down));
        Assert.Contains("SLITTER-B", Headline(down), StringComparison.Ordinal);
        var blocked = await Ask(planner, "What is preventing Slitter B from running?");
        Assert.Equal("SLITTER-B", blocked.GetProperty("interpretation").GetProperty("machine").GetString());
        Assert.Contains("cannot run", Headline(blocked), StringComparison.Ordinal);
        Assert.Contains(blocked.GetProperty("facts").EnumerateArray(), f => f.GetProperty("text").GetString()!.Contains("Equipment", StringComparison.Ordinal));

        // Permission-aware: a shipper cannot read the schedule, so "what is late?" is not authorized and carries no data.
        var denied = await Ask(ship, "What is late?");
        Assert.True(denied.GetProperty("notAuthorized").GetBoolean());
        Assert.Equal(JsonValueKind.Null, denied.GetProperty("rows").ValueKind);
        Assert.Empty(denied.GetProperty("facts").EnumerateArray());
        var late = await Ask(planner, "What is late?");
        Assert.False(late.GetProperty("notAuthorized").GetBoolean());
        Assert.Contains("schedule", Headline(late), StringComparison.Ordinal);

        // People: names only for those who may read the workforce and qualifications.
        Assert.True((await Ask(planner, "Who is qualified to work Slitting tonight?")).GetProperty("notAuthorized").GetBoolean());
        var qualified = await Ask(eng, "Who is qualified to work Slitting tonight?");
        Assert.Equal("SLIT-WC", qualified.GetProperty("interpretation").GetProperty("workCenter").GetString());
        Assert.Contains("qualified for SLIT-WC", Headline(qualified), StringComparison.Ordinal);
        Assert.Contains(qualified.GetProperty("rows").EnumerateArray(), r => r.GetProperty("cells")[0].GetString() == "Sarah");

        // A what-if never changes production state.
        var scenario = await Ask(planner, "What happens if SLITTER-A goes down tomorrow for 8 hours?");
        Assert.Equal("Simulate", Intent(scenario));
        Assert.Contains(scenario.GetProperty("notes").EnumerateArray(), n => n.GetString()!.StartsWith("Scenario only", StringComparison.Ordinal));
        Assert.Equal(version, (await Get(planner, "/api/planning/schedules/approved")).GetProperty("versionId").GetInt64());
        var downtime = await Get(planner, $"/api/maintenance/downtime?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(3).ToString("o", CultureInfo.InvariantCulture))}");
        Assert.DoesNotContain(downtime.EnumerateArray(), w => w.GetProperty("machineCode").GetString() == "SLITTER-A");

        // Person impact: names stay out for readers without workforce access, and absences carry no reason.
        var impact = await Ask(planner, "Which orders are affected by Sarah's PTO?");
        Assert.Equal("PersonImpact", Intent(impact));
        Assert.Contains("Sarah", Headline(impact), StringComparison.Ordinal);
        Assert.DoesNotContain("PTO", Headline(impact), StringComparison.Ordinal);

        // Report requests are shown for confirmation and preview the reporting engine's own figures.
        var request = await Ask(planner, "Create an Excel report of downtime this week by machine");
        Assert.Equal("Report", Intent(request));
        Assert.True(request.GetProperty("interpretation").GetProperty("needsConfirmation").GetBoolean());
        var report = request.GetProperty("interpretation").GetProperty("report");
        Assert.Equal("downtime", report.GetProperty("reportId").GetString());
        Assert.Equal("xlsx", report.GetProperty("output").GetString());
        Assert.Equal("machine", report.GetProperty("parameters").GetProperty("groupBy").GetString());
        Assert.Contains(request.GetProperty("rows").EnumerateArray(), r => r.GetProperty("cells")[1].GetString() == "SLITTER-B");

        // Not a manufacturing question.
        var off = await Ask(planner, "What is the capital of France?");
        Assert.Equal("Unsupported", Intent(off));
        Assert.Equal("I can answer questions about your Forge manufacturing operation.", Headline(off));
    }

    [Fact]
    public async Task Brief_me_compiles_only_the_sections_the_reader_may_see()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner, planner2) = await PlantAsync(app);
        var order = await ReleasedOrderAsync(plant, planner, 20m, 10);
        await plant.Ok(plant.As("tech"), "/api/maintenance/machines/SLITTER-B/down", new { description = "Blade drive fault" });
        var version = Id(await plant.Ok(planner, "/api/planning/schedules", new { horizonDays = 14 }));
        await plant.Ok(planner2, $"/api/planning/schedules/{version}/approve", new { reason = "Reviewed" });

        var brief = await Get(planner, "/api/analyst/brief");
        Assert.Equal("ALL", brief.GetProperty("shiftCode").GetString());
        var titles = brief.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("title").GetString()).ToList();
        Assert.Contains("Orders needing attention", titles);
        Assert.Contains("Machines and maintenance", titles);
        Assert.Contains("Material arrivals", titles);
        Assert.DoesNotContain("Training expirations (next 14 days)", titles);
        Assert.Contains(brief.GetProperty("omitted").EnumerateArray(), o => o.GetString()!.Contains("qualification", StringComparison.Ordinal));
        var machines = brief.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("title").GetString() == "Machines and maintenance");
        Assert.Contains(machines.GetProperty("facts").EnumerateArray(), f => f.GetProperty("text").GetString()!.StartsWith("SLITTER-B unavailable now", StringComparison.Ordinal));
        var receipts = brief.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("title").GetString() == "Material arrivals");
        Assert.Contains(receipts.GetProperty("facts").EnumerateArray(), f => f.GetProperty("text").GetString()!.Contains("COIL-AL", StringComparison.Ordinal));

        // Asked in words, the same brief comes back inside an answer.
        var asked = await Ask(planner, "Brief me");
        Assert.Equal("Brief", Intent(asked));
        Assert.Equal(brief.GetProperty("title").GetString(), Headline(asked));

        // A shipper sees no schedule sections.
        var shipper = await Get(plant.As("ship"), "/api/analyst/brief");
        Assert.DoesNotContain(shipper.GetProperty("sections").EnumerateArray(), s => s.GetProperty("title").GetString() == "Orders needing attention");
        _ = order;
    }

    [Fact]
    public async Task A_configured_ai_provider_only_restates_answers_forge_computed()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner, _) = await PlantAsync(app);
        await plant.Ok(plant.As("tech"), "/api/maintenance/machines/SLITTER-B/down", new { description = "Blade drive fault" });

        var narrator = new RecordingNarrator();
        await using var withAi = app.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IAnalystNarrator>(narrator)));
        var client = withAi.CreateClient();
        client.DefaultRequestHeaders.Add(DevelopmentAuthenticationHandler.HeaderName, "planner");
        var ship = withAi.CreateClient();
        ship.DefaultRequestHeaders.Add(DevelopmentAuthenticationHandler.HeaderName, "ship");

        Assert.True((await Get(client, "/api/analyst/status")).GetProperty("narration").GetBoolean());
        var plain = await Ask(client, "What machines are unavailable?");
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("narrative").ValueKind);
        Assert.Empty(narrator.Calls);

        var narrated = await Ask(client, "What machines are unavailable?", narrate: true);
        Assert.Equal("Summary.", narrated.GetProperty("narrative").GetString());
        Assert.Equal("test", narrated.GetProperty("narrativeProvider").GetString());
        var (question, facts) = Assert.Single(narrator.Calls);
        Assert.Equal("What machines are unavailable?", question);
        Assert.Contains("SLITTER-B", facts, StringComparison.Ordinal);
        Assert.Equal(Headline(plain), Headline(narrated));

        // Nothing is sent for an answer the reader may not see.
        Assert.True((await Ask(ship, "What is late?", narrate: true)).GetProperty("notAuthorized").GetBoolean());
        Assert.Single(narrator.Calls);
    }

    [Fact]
    public async Task Anthropic_narrator_sends_only_question_and_facts_and_fails_closed()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"content":[{"type":"text","text":"Two machines are down."}]}""");
        var options = new AnalystNarratorOptions { Provider = "anthropic", ApiKey = "test-key", Model = "test-model" };
        var narrator = new AnthropicNarrator(new HttpClient(handler), options, NullLogger<AnthropicNarrator>.Instance);
        Assert.Equal("Two machines are down.", await narrator.NarrateAsync("What is down?", """{"Headline":"2 machines"}""", CancellationToken.None));
        Assert.Equal("test-key", handler.Request!.Headers.GetValues("x-api-key").Single());
        using (var body = JsonDocument.Parse(handler.Body!))
        {
            Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
            Assert.Contains("Use only the facts", body.RootElement.GetProperty("system").GetString(), StringComparison.Ordinal);
            Assert.Contains("2 machines", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        }

        var failing = new AnthropicNarrator(new HttpClient(new StubHandler(HttpStatusCode.InternalServerError, "{}")), options, NullLogger<AnthropicNarrator>.Instance);
        Assert.Null(await failing.NarrateAsync("q", "{}", CancellationToken.None));
    }

    private sealed class RecordingNarrator : IAnalystNarrator
    {
        public List<(string Question, string Facts)> Calls { get; } = [];

        public string Name => "test";

        public bool Enabled => true;

        public Task<string?> NarrateAsync(string question, string factsJson, CancellationToken ct)
        {
            Calls.Add((question, factsJson));
            return Task.FromResult<string?>("Summary.");
        }
    }

    private sealed class StubHandler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(response) };
        }
    }
}
