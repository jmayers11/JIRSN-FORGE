using System.Text.Json;

namespace Forge.Simulation.Tests.Engine;

/// <summary>
/// The shop floor of the planning plant: badges for Sarah and Michael, a kiosk (shop-floor terminal), and optionally
/// Alex, a worker in training for SLIT-II. Terminals identify people by badge; the kiosk is the caller.
/// </summary>
internal static class Floor
{
    public const string Sarah = "B-SARAH-0001";
    public const string Michael = "B-MICHAEL-0001";
    public const string Alex = "B-ALEX-0001";

    /// <summary>Issues badges and registers the kiosk. Returns the kiosk's client.</summary>
    public static async Task<HttpClient> SetUpAsync(SimFactory f)
    {
        foreach (var (person, badge) in new[] { ("sarah", Sarah), ("michael", Michael) })
        {
            await f.Plant.Ok(f.Plant.Admin, $"/api/workforce/people/{f.Plant.People[person]}/badge", new { badge });
        }

        await f.Plant.Person("kiosk", "ShopFloorTerminal");
        return f.Api.As("kiosk");
    }

    /// <summary>Alex: a Slitting worker in training for SLIT-II, on the around-the-clock shift, with a badge.</summary>
    public static async Task TraineeAsync(SimFactory f)
    {
        var e = f.Plant.Engineer;
        var alex = await f.Plant.Person("alex");
        await f.Plant.Ok(e, $"/api/workforce/workers/{alex}", new { homeWorkCenterCode = "SLIT-WC" });
        await f.Plant.Ok(e, $"/api/competency/people/{alex}/training", new { code = "SLIT-II" });
        await f.Plant.Ok(e, $"/api/workforce/workers/{alex}/shift", new { shiftCode = "ALL" });
        await f.Plant.Ok(f.Plant.Admin, $"/api/workforce/people/{alex}/badge", new { badge = Alex });
    }

    /// <summary>Good quantity reported for one operation of an order, as Forge records it.</summary>
    public static async Task<decimal> GoodAsync(HttpClient reader, long order, int sequence) =>
        (await SimApi.Get(reader, $"/api/orders/{order}/shopfloor")).GetProperty("reports").EnumerateArray()
            .Where(r => r.GetProperty("sequence").GetInt32() == sequence).Sum(r => r.GetProperty("goodQuantity").GetDecimal());

    /// <summary>Whether the person's direct labor is open now, as the terminal shows it.</summary>
    public static async Task<bool> LaborOpenAsync(SimApi api, HttpClient kiosk, string badge)
    {
        var me = await api.Send(kiosk, "/api/shopfloor/identify", new { badge });
        return me.Succeeded && me.Json.TryGetProperty("labor", out var labor) && labor.ValueKind != JsonValueKind.Null;
    }

    /// <summary>The order's status as Forge reports it.</summary>
    public static async Task<string> OrderStatusAsync(HttpClient reader, long order) =>
        (await SimApi.Get(reader, $"/api/orders/{order}")).GetProperty("status").GetString() ?? "?";
}
