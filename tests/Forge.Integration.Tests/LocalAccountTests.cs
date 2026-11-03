using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Forge.Integration.Tests;

/// <summary>
/// Owner decisions 2026-11-03 through the real host in Forge-accounts mode: the first-run wizard needs the installer's setup code and
/// works once; passwords sign people in with tokens Forge signs; an administrator's temporary password must be changed first; repeated
/// failures lock the account; the development header means nothing.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LocalAccountTests(PostgresFixture postgres)
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string> SignIn(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/local/sign-in", new { username, password });
        var body = await Json(response);
        Assert.True(response.IsSuccessStatusCode, body.GetRawText());
        return body.GetProperty("token").GetString()!;
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return request;
    }

    [Fact]
    public async Task An_installed_server_is_set_up_once_and_signs_people_in_with_forge_accounts()
    {
        var codeFile = Path.Combine(Path.GetTempPath(), $"forge-setup-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(codeFile, "ABCD-EFGH-JKLM");
        try
        {
            await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: builder =>
            {
                builder.UseSetting("Forge:Authentication:Mode", "Local");
                builder.UseSetting("Forge:Authentication:Local:SigningKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                builder.UseSetting("Forge:Bootstrap:AdminSubject", "");
                builder.UseSetting("Forge:Setup:CodeFile", codeFile);
            });
            var client = app.CreateClient();
            Assert.True((await Json(await client.GetAsync(new Uri("/api/setup/status", UriKind.Relative)))).GetProperty("needed").GetBoolean());
            Assert.Equal("Local", (await Json(await client.GetAsync(new Uri("/api/ui/config", UriKind.Relative)))).GetProperty("authMode").GetString());

            // The setup code is required; weak passwords are refused; setup works once.
            var first = new { setupCode = "WRONG-CODE", displayName = "Jo Admin", username = "jadmin", password = "correct horse battery" };
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/setup/", first)).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/setup/", first with { setupCode = "abcd-efgh-jklm", password = "short" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/setup/", first with { setupCode = "abcd-efgh-jklm" })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/setup/", first with { setupCode = "ABCD-EFGH-JKLM", username = "intruder" })).StatusCode);
            Assert.False((await Json(await client.GetAsync(new Uri("/api/setup/status", UriKind.Relative)))).GetProperty("needed").GetBoolean());

            // Wrong passwords and the development header get nothing; the right password gets a token Forge accepts.
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/local/sign-in", new { username = "jadmin", password = "wrong password!" })).StatusCode);
            var dev = new HttpRequestMessage(HttpMethod.Get, "/api/kernel/me");
            dev.Headers.Add("X-Forge-Dev-Subject", "jadmin");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(dev)).StatusCode);
            var admin = await SignIn(client, "jadmin", "correct horse battery");
            var me = await Json(await client.SendAsync(Bearer(HttpMethod.Get, "/api/kernel/me", admin)));
            Assert.Equal("Jo Admin", me.GetProperty("displayName").GetString());

            // The administrator registers an operator and sets a temporary password; the operator must change it before anything else.
            var registered = await client.SendAsync(Bearer(HttpMethod.Post, "/api/kernel/people", admin, new { idpSubject = "op1", displayName = "Op One", markings = Array.Empty<string>(), reason = "New hire" }));
            Assert.True(registered.IsSuccessStatusCode, await registered.Content.ReadAsStringAsync());
            var people = await Json(await client.SendAsync(Bearer(HttpMethod.Get, "/api/kernel/people", admin)));
            var op = people.EnumerateArray().Single(p => p.GetProperty("idpSubject").GetString() == "op1").GetProperty("personId").GetString();
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.SendAsync(Bearer(HttpMethod.Post, $"/api/kernel/people/{op}/password", admin, new { password = "Temporary-Pass-1" }))).StatusCode); // no reason
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Bearer(HttpMethod.Post, $"/api/kernel/people/{op}/password", admin, new { password = "Temporary-Pass-1", reason = "First sign-in" }))).StatusCode);
            var temporary = await client.PostAsJsonAsync("/api/auth/local/sign-in", new { username = "op1", password = "Temporary-Pass-1" });
            var temporaryBody = await Json(temporary);
            Assert.True(temporaryBody.GetProperty("mustChangePassword").GetBoolean());
            var limited = temporaryBody.GetProperty("token").GetString()!;
            var blocked = await client.SendAsync(Bearer(HttpMethod.Get, "/api/orders/", limited));
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.Contains("password_change_required", await blocked.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var changed = await Json(await client.SendAsync(Bearer(HttpMethod.Post, "/api/auth/local/change-password", limited, new { currentPassword = "Temporary-Pass-1", newPassword = "my own long password" })));
            var own = changed.GetProperty("token").GetString()!;
            Assert.Equal("Op One", (await Json(await client.SendAsync(Bearer(HttpMethod.Get, "/api/kernel/me", own)))).GetProperty("displayName").GetString());
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/local/sign-in", new { username = "op1", password = "Temporary-Pass-1" })).StatusCode);

            // Five wrong passwords lock the account, so even the right one is refused for a while.
            for (var i = 0; i < 5; i++)
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/local/sign-in", new { username = "op1", password = $"guess number {i}" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/local/sign-in", new { username = "op1", password = "my own long password" })).StatusCode);

            // A tampered token is refused.
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Bearer(HttpMethod.Get, "/api/kernel/me", admin[..^4] + "AAAA"))).StatusCode);
        }
        finally
        {
            File.Delete(codeFile);
        }
    }
}
