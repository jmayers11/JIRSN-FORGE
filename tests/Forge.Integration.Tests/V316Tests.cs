using System.Net;
using System.Net.Http.Json;
using Forge.Host.Security;
using Forge.Kernel.Events;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V316Tests(PostgresFixture postgres)
{
    [Fact]
    public async Task Repeated_wrong_badge_pin_attempts_persist_lockout_and_block_the_correct_pin()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var app = new ForgeApp(connectionString);
        var plant = await SlittingPlant.BuildAsync(app);
        var personId = await plant.Person("pin-worker");
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{personId}", new { homeWorkCenterCode = "SLIT-WC" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{personId}/badge", new { badge = "B-PIN-LOCK-01" });
        await plant.Person("pin-kiosk", "ShopFloorTerminal");
        var kiosk = plant.As("pin-kiosk");

        await plant.Ok(kiosk, "/api/shopfloor/pin", new { badge = "B-PIN-LOCK-01", newPin = "4826" });
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var rejected = await plant.Try(kiosk, "/api/shopfloor/pin", new { badge = "B-PIN-LOCK-01", currentPin = "0000", newPin = "4826" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var state = new NpgsqlCommand(
            "SELECT pin_failed_attempts, pin_locked_until > now() FROM workforce.badge WHERE badge_digest = $1",
            connection))
        {
            state.Parameters.AddWithValue(Forge.Workforce.Badges.Digest("B-PIN-LOCK-01"));
            await using var reader = await state.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(5, reader.GetInt32(0));
            Assert.True(reader.GetBoolean(1));
        }

        var correctWhileLocked = await plant.Try(kiosk, "/api/shopfloor/pin", new { badge = "B-PIN-LOCK-01", currentPin = "4826", newPin = "2718" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, correctWhileLocked.StatusCode);
        Assert.Contains("pin_mismatch", await correctWhileLocked.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(5, await Scalar(connectionString, "SELECT count(*) FROM kernel.outbox WHERE envelope LIKE '%BadgePinVerificationFailed%'"));
    }

    [Fact]
    public async Task Step_up_gates_reject_stale_or_unaccepted_provider_assurance()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using (var fresh = new ForgeApp(connectionString))
        {
            var admin = fresh.As(ForgeApp.AdminSubject);
            var registered = await admin.CommandAsync("/api/kernel/people",
                new { idpSubject = "fresh-step-up-approver", displayName = "Fresh Step-up Approver", markings = Array.Empty<string>(), reason = "Step-up test" },
                "v316-step-up-register");
            var registeredJson = await registered.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            var personId = registeredJson.GetProperty("entityId").GetString();
            _ = await admin.CommandAsync($"/api/kernel/people/{personId}/roles",
                new { role = "QualityManager", reason = "Step-up test" }, "v316-step-up-role");

            var response = await fresh.As("fresh-step-up-approver").CommandAsync(
                "/api/inventory/rejections/1/approve", new { reason = "Fresh proof test" }, "v316-fresh-step-up");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("step_up_required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        await using (var stale = new ForgeApp(connectionString, configureBuilder: builder =>
        {
            builder.UseSetting("Forge:Authentication:DevelopmentAuthTime", "1");
        }))
        {
            var response = await stale.As(ForgeApp.AdminSubject).CommandAsync(
                "/api/inventory/rejections/1/approve", new { reason = "Step-up test" }, "v316-stale-step-up");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("step_up_required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Contains("insufficient_user_authentication", response.Headers.WwwAuthenticate!.ToString(), StringComparison.Ordinal);
        }

        await using var weak = new ForgeApp(connectionString, configureBuilder: builder =>
        {
            builder.UseSetting("Forge:Authentication:DevelopmentAssurance", "urn:forge:assurance:low");
        });
        var weakResponse = await weak.As(ForgeApp.AdminSubject).CommandAsync(
            "/api/inventory/rejections/1/approve", new { reason = "Step-up test" }, "v316-weak-step-up");
        Assert.Equal(HttpStatusCode.Unauthorized, weakResponse.StatusCode);
        Assert.Contains("step_up_required", await weakResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scim_deactivation_requires_provisioning_scope_and_blocks_forge_access()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        Guid personId;
        await using (var unscoped = new ForgeApp(connectionString, configureBuilder: builder =>
        {
            builder.UseSetting("Forge:Authentication:Scim:Enabled", "true");
        }))
        {
            var admin = unscoped.As(ForgeApp.AdminSubject);
            var created = await admin.CommandAsync("/api/kernel/people",
                new { idpSubject = "nexus-user-316", displayName = "Nexus User", markings = Array.Empty<string>(), reason = "SCIM contract test" },
                "v316-register-provisioned-person");
            var json = await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            personId = Guid.Parse(json.GetProperty("entityId").GetString()!);
            _ = await admin.CommandAsync($"/api/workforce/people/{personId}/badge",
                new { badge = "B-NEXUS-DISABLED", reason = "SCIM deactivation test" }, "v316-scim-badge");
            var kioskCreated = await admin.CommandAsync("/api/kernel/people",
                new { idpSubject = "scim-kiosk", displayName = "SCIM Kiosk", markings = Array.Empty<string>(), reason = "SCIM deactivation test" },
                "v316-scim-kiosk");
            var kioskCreatedJson = await kioskCreated.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            var kioskId = Guid.Parse(kioskCreatedJson.GetProperty("entityId").GetString()!);
            _ = await admin.CommandAsync($"/api/kernel/people/{kioskId}/roles",
                new { role = "ShopFloorTerminal", reason = "SCIM deactivation test" }, "v316-scim-kiosk-role");

            using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/scim/v2/Users/nexus-user-316")
            {
                Content = JsonContent.Create(new
                {
                    schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
                    Operations = new[] { new { op = "replace", path = "active", value = false } },
                }),
            };
            request.Headers.Add("Idempotency-Key", "v316-scim-unscoped");
            var denied = await unscoped.As("nexus-provisioner").SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        await using var provisioned = new ForgeApp(connectionString, configureBuilder: builder =>
        {
            builder.UseSetting("Forge:Authentication:Scim:Enabled", "true");
            builder.UseSetting("Forge:Authentication:DevelopmentScopes", "forge.provisioning");
        });
        using var deactivation = new HttpRequestMessage(HttpMethod.Patch, "/api/scim/v2/Users/nexus-user-316")
        {
            Content = JsonContent.Create(new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
                Operations = new[] { new { op = "replace", path = "active", value = false } },
            }),
        };
        deactivation.Headers.Add("Idempotency-Key", "v316-scim-deactivate");
        var response = await provisioned.As("nexus-provisioner").SendAsync(deactivation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("active").GetBoolean());
        Assert.Equal(0, await Scalar(connectionString, $"SELECT count(*) FROM kernel.person WHERE person_id = '{personId}' AND is_active"));

        var userResponse = await provisioned.As("nexus-user-316").GetAsync("/api/kernel/me");
        Assert.Equal(HttpStatusCode.Forbidden, userResponse.StatusCode);
        var identify = await provisioned.As("scim-kiosk").CommandAsync(
            "/api/shopfloor/identify", new { badge = "B-NEXUS-DISABLED" }, "v316-inactive-badge");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, identify.StatusCode);
        Assert.Contains("unknown_badge", await identify.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sentinel_subscriber_sends_only_durable_security_event_data_with_an_idempotency_key()
    {
        HttpRequestMessage? captured = null;
        var responseBody = string.Empty;
        var handler = new RecordingHandler(async request =>
        {
            captured = request;
            responseBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        using var client = new HttpClient(handler);
        var subscriber = new SentinelSecurityEventSubscriber(client, new Uri("https://sentinel.example/security-events"));
        var envelope = new EventEnvelope
        {
            EventId = Guid.NewGuid(),
            EventType = "BadgePinVerificationFailed",
            SchemaVersion = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            RecordedAt = DateTimeOffset.UtcNow,
            SourceContext = "Workforce",
            EntityType = "Person",
            EntityId = Guid.NewGuid().ToString(),
            Actor = "system:badge-pin-verifier",
            CorrelationId = Guid.NewGuid(),
            Payload = "{\"consecutiveFailures\":5,\"locked\":true}",
        };

        Assert.True(subscriber.Handles(envelope.EventType));
        Assert.False(subscriber.Handles("EmployeeClockedIn"));
        await subscriber.HandleAsync(42, envelope, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(envelope.EventId.ToString("D"), captured.Headers.GetValues("Idempotency-Key").Single());
        Assert.Contains("BadgePinVerificationFailed", responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("pin_value", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("badge_value", responseBody, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private static async Task<long> Scalar(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
