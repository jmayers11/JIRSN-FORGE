using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Forge.Integration.Tests;

/// <summary>The web UI is static files over the same API; these checks cover what it needs from the host.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WebUiTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(null, 60)]
    [InlineData("2", 2)]
    [InlineData("3600", 3600)]
    [InlineData("0", 60)]
    [InlineData("3601", 60)]
    [InlineData("invalid", 60)]
    [InlineData("1.5", 60)]
    public async Task Registered_identity_exposes_the_validated_terminal_inactivity_timeout(string? configured, int expected)
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: builder =>
        {
            if (configured is not null) builder.UseSetting("Forge:ShopFloor:IdleTimeoutSeconds", configured);
        });
        var me = JsonDocument.Parse(await app.As(ForgeApp.AdminSubject).GetStringAsync("/api/kernel/me")).RootElement;
        Assert.Equal(expected, me.GetProperty("shopFloorIdleTimeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task Ui_is_served_and_says_how_to_sign_in_and_who_is_who()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var anonymous = app.CreateClient();

        var page = await anonymous.GetStringAsync("/");
        Assert.Contains("/js/app.js", page, StringComparison.Ordinal);
        var script = await anonymous.GetAsync("/js/model.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        var terminalModule = await anonymous.GetAsync("/js/terminal-state.mjs");
        Assert.Equal(HttpStatusCode.OK, terminalModule.StatusCode);
        Assert.Contains("javascript", terminalModule.Content.Headers.ContentType?.MediaType ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var config = JsonDocument.Parse(await anonymous.GetStringAsync("/api/ui/config")).RootElement;
        Assert.Equal("Development", config.GetProperty("authMode").GetString());

        // The name directory needs a registered person.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/kernel/directory")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.As("not-registered").GetAsync("/api/kernel/directory")).StatusCode);
        var directory = JsonDocument.Parse(await app.As(ForgeApp.AdminSubject).GetStringAsync("/api/kernel/directory")).RootElement;
        Assert.Contains(directory.EnumerateArray(), p => p.GetProperty("displayName").GetString() == "Test Administrator");
    }
}
