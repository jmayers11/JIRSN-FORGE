using Forge.Integration.Tests;
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

    public static async Task<SimFactory> BuildAsync(PostgresFixture postgres, string keyPrefix)
    {
        var clock = SimClock.AtCurrentHour();
        var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
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
