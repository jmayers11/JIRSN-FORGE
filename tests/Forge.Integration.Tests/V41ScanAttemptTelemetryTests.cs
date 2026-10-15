using System.Globalization;
using System.Text.Json;
using Forge.Kernel.Postgres;
using static Forge.Integration.Tests.ScanSessionScenario;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V41ScanAttemptTelemetryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Failed_scan_attempts_are_redacted_session_bound_and_pruned_by_configured_age_and_cap()
    {
        await using var flow = await CreateAsync(postgres, attemptRetentionEnabled: true, attemptRetentionDays: 1, attemptMaxPerSession: 2);
        var session = await CreateSessionAsync(flow);
        var sessionId = session.GetProperty("id").GetGuid();
        var sessionVersion = session.GetProperty("version").GetInt32();
        var correlationIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var rawValues = new[] { "SCAN-ATTEMPT-PRIVATE-A", "SCAN-ATTEMPT-PRIVATE-B", "SCAN-ATTEMPT-PRIVATE-C" };

        for (var index = 0; index < rawValues.Length; index++)
        {
            using var failed = await SendAsync(flow.Terminal, $"/api/identification/scan-sessions/{sessionId:D}/stages", new
            {
                expectedVersion = sessionVersion, code = rawValues[index], inputMode = "KeyboardWedge",
                reason = "A rejected raw scan must remain absent from attempt telemetry"
            }, "scan-attempt-invalid-" + index.ToString(CultureInfo.InvariantCulture), correlation: correlationIds[index]);
            var body = await failed.Content.ReadAsStringAsync();
            Assert.Equal(422, (int)failed.StatusCode);
            Assert.Contains("invalid_identification", body, StringComparison.Ordinal);
            Assert.DoesNotContain(rawValues[index], body, StringComparison.Ordinal);
        }

        var afterCap = await ReadAsync(flow.Terminal, sessionId);
        var attempts = afterCap.GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal(correlationIds[1..], attempts.Select(row => row.GetProperty("correlationId").GetGuid()).ToArray());
        foreach (var attempt in attempts)
        {
            Assert.Equal(sessionId, attempt.GetProperty("sessionId").GetGuid());
            Assert.Equal("StageScan", attempt.GetProperty("commandType").GetString());
            Assert.Equal("Rejected", attempt.GetProperty("outcome").GetString());
            Assert.Equal("invalid_identification", attempt.GetProperty("validationCode").GetString());
            Assert.Equal(flow.Plant.People["sarah"], attempt.GetProperty("operatorPersonId").GetGuid());
            Assert.NotEqual(Guid.Empty, attempt.GetProperty("auditId").GetGuid());
        }
        var retained = string.Join("\n", await RowsAsync(flow, EvidenceTables));
        foreach (var raw in rawValues) Assert.DoesNotContain(raw, retained, StringComparison.Ordinal);
        foreach (var row in attempts) Assert.False(row.TryGetProperty("code", out _));

        flow.Time.UtcNow = flow.Time.UtcNow.AddDays(2);
        using (var expired = await SendAsync(flow.Terminal, $"/api/identification/scan-sessions/{sessionId:D}/stages", new
        {
            expectedVersion = sessionVersion, code = "SCAN-ATTEMPT-EXPIRED-PRIVATE", inputMode = "KeyboardWedge",
            reason = "An expired session is an observable rejected attempt"
        }, "scan-attempt-expired", correlation: Guid.NewGuid()))
        {
            var body = await expired.Content.ReadAsStringAsync();
            Assert.Equal(422, (int)expired.StatusCode);
            Assert.Contains("scan_session_expired", body, StringComparison.Ordinal);
            Assert.DoesNotContain("SCAN-ATTEMPT-EXPIRED-PRIVATE", body, StringComparison.Ordinal);
        }

        var afterAge = await ReadAsync(flow.Terminal, sessionId);
        var retainedAfterAge = Assert.Single(afterAge.GetProperty("attempts").EnumerateArray());
        Assert.Equal("scan_session_expired", retainedAfterAge.GetProperty("validationCode").GetString());
        var agedRows = string.Join("\n", await RowsAsync(flow, "kernel.identification_scan_attempt"));
        Assert.DoesNotContain("SCAN-ATTEMPT-PRIVATE", agedRows, StringComparison.Ordinal);
        Assert.Contains(correlationIds[2].ToString("D"), retained);
        Assert.DoesNotContain(correlationIds[0].ToString("D"), agedRows, StringComparison.Ordinal);
        Assert.DoesNotContain(correlationIds[1].ToString("D"), agedRows, StringComparison.Ordinal);
    }
}
