using Forge.Integration.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Simulation.Tests.Engine;

/// <summary>
/// A synthetic plant built only through real authenticated commands, on its own disposable database, with the
/// simulation clock as the host's time source. Phase A uses the reference Slitting plant plus the quality,
/// shipping and purchasing people the scenarios need and one lot-tracked bought part.
/// Later phases extend this to the owner-approved size (300 orders, 70 operators).
/// </summary>
internal sealed class SimFactory : IAsyncDisposable
{
    public const string LotPart = "SIM-BRKT";
    public const string BulkPart = "COIL-AL";
    public const string Stores = "STORES";
    public const decimal OpeningCoil = 1_000_000m;
    public const string FlexPart = "ROLL-100";
    public const string WidePart = "ROLL-300";
    public static readonly string[] Workers = ["sarah", "michael", "jennifer", "dana"];

    private SimFactory(ForgeApp app, SimClock clock, SlittingPlant.Plant plant, SimApi api)
    {
        App = app;
        Clock = clock;
        Plant = plant;
        Api = api;
    }

    public ForgeApp App { get; }

    public SimClock Clock { get; }

    public SlittingPlant.Plant Plant { get; }

    public SimApi Api { get; }

    /// <summary>The MaterialHandler and InventoryController from the reference plant; reads stock and the ledger.</summary>
    public HttpClient StoresUser => Api.As("stores");

    /// <summary>The reference plant's planner, who reads capacity, readiness and schedules.</summary>
    public HttpClient Planner => Api.As("planner");

    /// <summary>
    /// The planning plant (Phase B): the reference plant on a 24-hour, 7-day shift (so results do not depend on the time
    /// of day the run starts), a second planner for approvals, a maintenance technician, and ROLL-300, which only
    /// SLITTER-B can slit. This is the calendar the existing Phase 4 tests use.
    /// </summary>
    public static async Task<SimFactory> BuildPlanningAsync(PostgresFixture postgres, string keyPrefix, Action<IWebHostBuilder>? configure = null)
    {
        var f = await BuildAsync(postgres, keyPrefix, configure);
        var (plant, e) = (f.Plant, f.Plant.Engineer);
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        foreach (var wc in new[] { "SLIT-WC", "PAINT-WC", "ASSY-WC" })
        {
            await plant.Ok(e, $"/api/resources/work-centers/{wc}/shifts", new { shiftCode = "ALL" });
        }

        foreach (var person in Workers)
        {
            await plant.Ok(e, $"/api/workforce/workers/{plant.People[person]}/shift", new { shiftCode = "ALL" });
        }

        await plant.Person("planner2", "Planner");
        await plant.Person("tech", "MaintenanceTechnician");
        await plant.Ok(e, "/api/engineering/items", new { partNumber = WidePart, description = "Wide roll", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 2 });
        await plant.Ok(e, $"/api/engineering/items/{WidePart}/revisions", new { revision = "A" });
        await plant.Ok(e, $"/api/engineering/items/{WidePart}/revisions/A/bom", new { lineNumber = 1, componentPartNumber = BulkPart, quantityPer = 4m, scrapPercent = 0m });
        await plant.Ok(e, $"/api/engineering/items/{WidePart}/revisions/A/routing", new { sequence = 10, description = "Wide slit", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-B" }, setupMinutes = 30m, runMinutesPerUnit = 2m, crewSize = 1 });
        await plant.Ok(plant.Approver, $"/api/engineering/items/{WidePart}/revisions/A/release", new { reason = "Simulation part release" });
        return f;
    }

    /// <summary>A local calendar day relative to the simulation clock, as yyyy-MM-dd.</summary>
    public string Day(int fromToday) =>
        DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime).AddDays(fromToday).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Adds people with roles to the plant (a no-op for subjects that already exist in this run).</summary>
    public async Task PeopleAsync(params (string Subject, string Role)[] people)
    {
        foreach (var (subject, role) in people)
        {
            if (!Plant.People.ContainsKey(subject))
            {
                await Plant.Person(subject, role);
            }
        }
    }

    /// <summary>Creates and releases a production order; returns its number.</summary>
    public async Task<long> ReleasedOrderAsync(string part, decimal quantity, int dueInDays, int priority = 3)
    {
        var due = DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime).AddDays(dueInDays).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var created = await Plant.Ok(Plant.Engineer, "/api/orders/", new { partNumber = part, quantity, dueDate = due, priority });
        var number = long.Parse(created.GetProperty("entityId").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        await Plant.Ok(Planner, $"/api/orders/{number}/release", new { reason = "Simulated customer order" });
        return number;
    }

    /// <param name="configure">Host settings a scenario needs before the host starts (for example the label namespace).</param>
    public static async Task<SimFactory> BuildAsync(PostgresFixture postgres, string keyPrefix, Action<IWebHostBuilder>? configure = null)
    {
        var clock = SimClock.AtCurrentHour();
        var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock), configure);
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        foreach (var (who, role) in new[] { ("insp", "Inspector"), ("qe", "QualityEngineer"), ("qm", "QualityManager"), ("ship", "Shipper"), ("pat", "PurchasingManager") })
        {
            await plant.Person(who, role);
        }

        var e = plant.Engineer;
        await plant.Ok(e, "/api/engineering/items", new { partNumber = LotPart, description = "Simulation bracket", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 5, lotTracked = true });
        await plant.Ok(e, $"/api/engineering/items/{LotPart}/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, $"/api/engineering/items/{LotPart}/revisions/A/release", new { reason = "Simulation part release" });
        return new SimFactory(app, clock, plant, new SimApi(app, keyPrefix));
    }

    public ValueTask DisposeAsync() => App.DisposeAsync();
}
