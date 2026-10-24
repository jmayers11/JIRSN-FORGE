using Forge.Host.Demo;
using Forge.Kernel.Security;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record DemoDesignateBody(string DemoCode, string SiteCode, string? Reason);

public sealed record DemoAdvanceBody(int Minutes, string? Reason);

public sealed record DemoReasonBody(string? Reason);

/// <summary>Sprint 9 Demo Scenario Control Center: simulated clock, baseline and reset for a designated demo database only.</summary>
public static class DemoEndpoints
{
    public static void MapDemo(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup("/api/demo").RequireAuthorization();

        // Everyone signed in sees whether the clock is simulated, so simulated time is never mistaken for real time.
        api.MapGet("/status", async (HttpContext http, DemoService demo, CancellationToken ct) =>
            await PrincipalAsync(http) is null ? NotRegistered() : Results.Ok(await demo.StatusAsync(ct)));
        api.MapGet("/kpis", (HttpContext http, DemoService demo, CancellationToken ct) =>
            Read(http, Permissions.DemoControl, async principal => await demo.KpisAsync(principal, ct)));
        api.MapGet("/compare", (HttpContext http, DemoService demo, CancellationToken ct) =>
            Read(http, Permissions.DemoControl, async principal => await demo.CompareAsync(principal, ct)));
        api.MapPost("/designate", (HttpContext http, DemoDesignateBody b, DemoService demo, CancellationToken ct) =>
            Command(http, b.Reason, c => demo.DesignateAsync(b.DemoCode, b.SiteCode, c, ct)));
        api.MapPost("/clock/advance", (HttpContext http, DemoAdvanceBody b, DemoService demo, CancellationToken ct) =>
            Command(http, b.Reason, c => demo.AdvanceAsync(b.Minutes, c, ct)));
        api.MapPost("/baseline", (HttpContext http, DemoReasonBody b, DemoService demo, CancellationToken ct) =>
            Command(http, b.Reason, c => demo.CaptureBaselineAsync(c, ct)));
        api.MapPost("/reset", (HttpContext http, DemoReasonBody b, DemoService demo, CancellationToken ct) =>
            Command(http, b.Reason, c => demo.ResetAsync(c, ct)));
    }
}
