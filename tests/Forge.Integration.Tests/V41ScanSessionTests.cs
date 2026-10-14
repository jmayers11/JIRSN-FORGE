using System.Globalization;
using System.Net;
using System.Text.Json;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Forge.Integration.Tests.ScanSessionScenario;

namespace Forge.Integration.Tests;

/// <summary>Normal API commands create policies, workers and production. SQL only observes the actual durable links.</summary>
[Collection(PostgresCollection.Name)]
public sealed class V41ScanSessionTests(PostgresFixture postgres)
{
    private static Guid SessionId(JsonElement session) => session.GetProperty("id").GetGuid();
    private static string StagePath(JsonElement session) => $"/api/identification/scan-sessions/{SessionId(session):D}/stages";
    private static object StageBody(JsonElement session, string code) => new
        { expectedVersion = session.GetProperty("version").GetInt32(), code, inputMode = "KeyboardWedge", reason = "Delayed old-generation source selection" };

    [Fact]
    public async Task Mandatory_sessions_gate_direct_commands_and_real_acceptance_links_survive_exact_replay_once()
    {
        await using var flow = await CreateAsync(postgres);
        var beforeRequirements = await RowsAsync(flow, EvidenceTables);
        var requirements = await JsonAsync(await flow.Terminal.GetAsync("/api/identification/scan-sessions/requirements?operationCode="
            + Uri.EscapeDataString(Operation(flow.OrderA)) + "&machineCode=SLITTER-A"));
        Assert.True(requirements.GetProperty("adopted").GetBoolean()); Assert.True(requirements.GetProperty("required").GetBoolean());
        Assert.Equal(beforeRequirements, await RowsAsync(flow, EvidenceTables));
        var business = await RowsAsync(flow, BusinessTables);
        await RejectedAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", Start(flow), "omitted-start"), "scan_session_required");
        Assert.Equal(business, await RowsAsync(flow, BusinessTables));
        var session = await StageAsync(flow, await CreateSessionAsync(flow));
        var initialVersion = session.GetProperty("version").GetInt32();
        var startCorrelation = Guid.NewGuid();
        var started = await JsonAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", Start(flow), "accepted-start", session, startCorrelation));
        var afterStart = await ReadAsync(flow.Terminal, SessionId(session));
        Assert.Equal(initialVersion, afterStart.GetProperty("version").GetInt32()); // accepted history does not change the selection generation
        var start = Assert.Single(afterStart.GetProperty("accepted").EnumerateArray());
        await AssertRealLinksAsync(flow, start, "StartOperation", startCorrelation, flow.Plant.People["sarah"]);
        var stableStart = await RowsAsync(flow, BusinessTables.Concat(EvidenceTables).ToArray());
        Assert.Equal(started.GetRawText(), (await JsonAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", Start(flow), "accepted-start", session))).GetRawText());
        Assert.Equal(stableStart, await RowsAsync(flow, BusinessTables.Concat(EvidenceTables).ToArray()));
        business = await RowsAsync(flow, BusinessTables);
        await RejectedAsync(await SendAsync(flow.Terminal, "/api/shopfloor/report", Report(flow), "omitted-report"), "scan_session_required");
        Assert.Equal(business, await RowsAsync(flow, BusinessTables));
        var reportCorrelation = Guid.NewGuid();
        var reportBody = Report(flow, 10m, complete: true);
        var reported = await JsonAsync(await SendAsync(flow.Terminal, "/api/shopfloor/report", reportBody, "accepted-report", session, reportCorrelation));
        var accepted = await ReadAsync(flow.Terminal, SessionId(session));
        Assert.Equal(initialVersion, accepted.GetProperty("version").GetInt32());
        Assert.Equal(2, accepted.GetProperty("accepted").GetArrayLength());
        var report = accepted.GetProperty("accepted").EnumerateArray().Single(row => row.GetProperty("commandType").GetString() == "ReportProduction");
        await AssertRealLinksAsync(flow, report, "ReportProduction", reportCorrelation, flow.Plant.People["sarah"]);
        var stableReport = await RowsAsync(flow, BusinessTables.Concat(EvidenceTables).ToArray());
        Assert.Equal(reported.GetRawText(), (await JsonAsync(await SendAsync(flow.Terminal, "/api/shopfloor/report", reportBody, "accepted-report", session))).GetRawText());
        Assert.Equal(stableReport, await RowsAsync(flow, BusinessTables.Concat(EvidenceTables).ToArray()));
        await AssertProductionAsync(flow, 1, 10m);
        await RejectedAsync(await SendAsync(flow.Terminal, "/api/shopfloor/report", Report(flow, 9m, true), "accepted-report", session), "idempotency_conflict", HttpStatusCode.Conflict);
        Assert.Equal(stableReport, await RowsAsync(flow, BusinessTables.Concat(EvidenceTables).ToArray()));
        var next = await StageAsync(flow, await CreateSessionAsync(flow));
        var priorEvidence = await RowsAsync(flow, "kernel.identification_scan_acceptance");
        await RejectedAsync(await SendAsync(flow.Terminal, "/api/shopfloor/report", reportBody, "accepted-report", next), "idempotency_conflict", HttpStatusCode.Conflict);
        Assert.Equal(priorEvidence, await RowsAsync(flow, "kernel.identification_scan_acceptance"));
        await AssertProductionAsync(flow, 1, 10m);
    }

    [Fact]
    public async Task B_current_pointer_blocks_delayed_A_stage_start_report_replay_and_stale_create_without_changing_business()
    {
        await using var flow = await CreateAsync(postgres);
        var a = await StageAsync(flow, await CreateSessionAsync(flow));
        await JsonAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", Start(flow), "A-start-original", a));
        var aAccepted = (await ReadAsync(flow.Terminal, SessionId(a))).GetProperty("accepted").GetRawText();
        var b = await StageAsync(flow, await CreateSessionAsync(flow, workerB: true));
        var business = await RowsAsync(flow, BusinessTables);
        var evidence = await RowsAsync(flow, "kernel.identification_scan_acceptance");
        await RejectedAsync(await SendAsync(flow.Terminal, StagePath(a), StageBody(a, Operation(flow.OrderA)), "late-A-stage"), "scan_session_inactive");
        await RejectedAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", Start(flow), "late-A-start", a), "scan_session_inactive");
        await RejectedAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", Start(flow), "A-start-original", a), "scan_session_inactive");
        await RejectedAsync(await SendAsync(flow.Terminal, "/api/shopfloor/report", Report(flow), "late-A-report", a), "scan_session_inactive");
        await RejectedAsync(await SendAsync(flow.Terminal, "/api/identification/scan-sessions", new
        {
            operationCode = Operation(flow.OrderA), badge = SarahBadge, machineCode = "SLITTER-A", previousSessionId = SessionId(a), reason = "A captured this pointer before B became current"
        }, "late-A-create"), "scan_channel_generation_changed");
        Assert.Equal(business, await RowsAsync(flow, BusinessTables)); Assert.Equal(evidence, await RowsAsync(flow, "kernel.identification_scan_acceptance"));
        var pointer = await JsonAsync(await flow.Terminal.GetAsync("/api/identification/scan-sessions/current")); Assert.Equal(SessionId(b), pointer.GetProperty("id").GetGuid());
        var history = await ReadAsync(flow.Terminal, SessionId(a)); Assert.Equal("Superseded", history.GetProperty("state").GetString()); Assert.Equal(aAccepted, history.GetProperty("accepted").GetRawText());
        var refreshedB = await StageAsync(flow, b); Assert.Equal(SessionId(b), SessionId(refreshedB)); Assert.Equal(flow.Plant.People["michael"], refreshedB.GetProperty("operatorPersonId").GetGuid());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_and_explicitly_invalidated_sessions_reject_old_actions_without_read_side_effects(bool invalidate)
    {
        await using var flow = await CreateAsync(postgres); var session = await StageAsync(flow, await CreateSessionAsync(flow));
        if (invalidate)
            await JsonAsync(await SendAsync(flow.Terminal, $"/api/identification/scan-sessions/{SessionId(session):D}/invalidate", new { expectedVersion = session.GetProperty("version").GetInt32(), reason = "Operator explicitly ended this staged work context" }, "explicit-invalidate"));
        else flow.Time.UtcNow = flow.Time.UtcNow.AddSeconds(121);
        var beforeRead = await RowsAsync(flow, EvidenceTables);
        Assert.Equal(invalidate ? "Invalidated" : "Expired", (await ReadAsync(flow.Terminal, SessionId(session))).GetProperty("state").GetString());
        Assert.Equal(beforeRead, await RowsAsync(flow, EvidenceTables));
        var business = await RowsAsync(flow, BusinessTables); var code = invalidate ? "scan_session_inactive" : "scan_session_expired";
        await RejectedAsync(await SendAsync(flow.Terminal, StagePath(session), StageBody(session, Operation(flow.OrderA)), "expired-stage"), code);
        await RejectedAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", Start(flow), "expired-start", session), code);
        await RejectedAsync(await SendAsync(flow.Terminal, "/api/shopfloor/report", Report(flow), "expired-report", session), code);
        Assert.Equal(business, await RowsAsync(flow, BusinessTables));
    }

    [Theory]
    [InlineData("Adoption", "governed_policy_unavailable")]
    [InlineData("Policy", "governed_policy_source_unavailable")]
    [InlineData("Document", "governed_policy_source_unavailable")]
    public async Task Current_governance_withdrawal_blocks_original_replays_and_keeps_retained_accepted_evidence(string target, string code)
    {
        await using var flow = await CreateAsync(postgres); var session = await StageAsync(flow, await CreateSessionAsync(flow));
        await JsonAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", Start(flow), "withdrawal-start", session));
        await JsonAsync(await SendAsync(flow.Terminal, "/api/shopfloor/report", Report(flow), "withdrawal-report", session));
        var accepted = (await ReadAsync(flow.Terminal, SessionId(session))).GetProperty("accepted").GetRawText();
        if (target == "Document")
            await flow.Plant.Ok(flow.Plant.Approver, $"/api/engineering/documents/{PolicyDocument}/revisions/A/obsolete", new { reason = "Withdraw current governed procedure before an old retry" });
        else
            await flow.Plant.Ok(flow.Plant.Approver, $"/api/quality/qms/{(target == "Adoption" ? "adoptions/" + AdoptionCode : "records/" + PolicyCode)}/revisions/1/withdraw",
                new { expectedVersion = 5, note = "Withdraw the exact effective operating source", reason = "Current governance no longer authorizes a replay" });
        var business = await RowsAsync(flow, BusinessTables); var durableAccepted = await RowsAsync(flow, "kernel.identification_scan_acceptance");
        await RejectedAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", Start(flow), "withdrawal-start", session), code);
        await RejectedAsync(await SendAsync(flow.Terminal, "/api/shopfloor/report", Report(flow), "withdrawal-report", session), code);
        Assert.Equal(business, await RowsAsync(flow, BusinessTables)); Assert.Equal(durableAccepted, await RowsAsync(flow, "kernel.identification_scan_acceptance"));
        Assert.Equal(accepted, (await ReadAsync(flow.Terminal, SessionId(session))).GetProperty("accepted").GetRawText());
    }

    [Fact]
    public async Task Exact_instruction_successor_blocks_A_references_and_replay_while_B_session_can_continue_real_labor()
    {
        await using var flow = await CreateAsync(postgres, instruction: true);
        var a = await StageAsync(flow, await StageAsync(flow, await CreateSessionAsync(flow)), InstructionCode("A"));
        await JsonAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", Start(flow), "instruction-A-start", a));
        await JsonAsync(await SendAsync(flow.Terminal, "/api/shopfloor/report", Report(flow), "instruction-A-report", a));
        var accepted = (await ReadAsync(flow.Terminal, SessionId(a))).GetProperty("accepted").GetRawText();
        await ReleaseDocumentAsync(flow.Plant, Instruction, "B");
        var business = await RowsAsync(flow, BusinessTables); var retained = await RowsAsync(flow, "kernel.identification_scan_acceptance");
        using (var replay = await SendAsync(flow.Terminal, "/api/shopfloor/report", Report(flow), "instruction-A-report", a))
        { Assert.Equal(HttpStatusCode.UnprocessableEntity, replay.StatusCode); Assert.Contains("instruction_not_current", await replay.Content.ReadAsStringAsync(), StringComparison.Ordinal); }
        using (var changed = await SendAsync(flow.Terminal, "/api/shopfloor/report", Report(flow), "instruction-A-new-report", a))
            Assert.Equal(HttpStatusCode.UnprocessableEntity, changed.StatusCode);
        Assert.Equal(business, await RowsAsync(flow, BusinessTables)); Assert.Equal(retained, await RowsAsync(flow, "kernel.identification_scan_acceptance"));
        Assert.Equal(accepted, (await ReadAsync(flow.Terminal, SessionId(a))).GetProperty("accepted").GetRawText());
        var b = await StageAsync(flow, await StageAsync(flow, await CreateSessionAsync(flow)), InstructionCode("B"));
        await JsonAsync(await SendAsync(flow.Terminal, "/api/shopfloor/report", Report(flow), "instruction-B-report", b));
        await AssertProductionAsync(flow, 2, 2m);
    }

    [Fact]
    public async Task Adapter_configuration_withdrawal_rejects_original_action_replay_without_erasing_declared_capture_history()
    {
        await using var flow = await CreateAsync(postgres); var session = await StageAsync(flow, await CreateSessionAsync(flow), mode: "ConfiguredAdapter");
        var scannerConfiguration = await JsonAsync(await flow.Terminal.GetAsync("/api/identification/scanners/config"));
        Assert.Equal(AdapterId, Assert.Single(scannerConfiguration.GetProperty("adapters").EnumerateArray()).GetProperty("id").GetString());
        await JsonAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", Start(flow), "adapter-start", session));
        var accepted = (await ReadAsync(flow.Terminal, SessionId(session))).GetProperty("accepted").GetRawText();
        flow.Configuration[AdapterEnabledSetting] = "false";
        var business = await RowsAsync(flow, BusinessTables); var retained = await RowsAsync(flow, "kernel.identification_scan_acceptance");
        await RejectedAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", Start(flow), "adapter-start", session), "scan_adapter_unavailable");
        Assert.Equal(business, await RowsAsync(flow, BusinessTables)); Assert.Equal(retained, await RowsAsync(flow, "kernel.identification_scan_acceptance"));
        Assert.Equal(accepted, (await ReadAsync(flow.Terminal, SessionId(session))).GetProperty("accepted").GetRawText());
        var scanner = await JsonAsync(await flow.Terminal.GetAsync("/api/identification/scanners/config"));
        var adapter = Assert.Single(scanner.GetProperty("adapters").EnumerateArray()); Assert.False(adapter.GetProperty("enabled").GetBoolean()); Assert.Equal("Disabled", adapter.GetProperty("availability").GetString());
    }

    [Fact]
    public async Task In_use_machine_retirement_is_refused_and_current_machine_read_revocation_blocks_Report_and_replay()
    {
        await using var flow = await CreateAsync(postgres); var session = await StageAsync(flow, await CreateSessionAsync(flow));
        await JsonAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", Start(flow), "machine-start", session));
        await JsonAsync(await SendAsync(flow.Terminal, "/api/shopfloor/report", Report(flow), "machine-report", session));
        await RejectedAsync(await SendAsync(flow.Plant.Engineer, "/api/resources/machines/SLITTER-A/retire",
            new { reason = "An in-use machine cannot be retired while actual orders and routing still depend on it" }, "retire-in-use-machine"), "in_use");
        await flow.Plant.Ok(flow.Plant.Admin, "/api/kernel/roles/ExplicitScanExecution/permissions/resources.read/revoke",
            new { reason = "Revoke actual current machine source access before any original retry" });
        await flow.Plant.Ok(flow.Plant.Admin, $"/api/kernel/people/{flow.Plant.People["scan-terminal"]}/roles/ShopFloorTerminal/revoke",
            new { reason = "Remove the other machine-read role while retaining explicit current scan and production permissions" });
        var business = await RowsAsync(flow, BusinessTables); var retained = await RowsAsync(flow, "kernel.identification_scan_acceptance");
        using (var replay = await SendAsync(flow.Terminal, "/api/shopfloor/report", Report(flow), "machine-report", session)) Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        using (var next = await SendAsync(flow.Terminal, "/api/shopfloor/report", Report(flow), "machine-new-report", session)) Assert.Equal(HttpStatusCode.Forbidden, next.StatusCode);
        Assert.Equal(business, await RowsAsync(flow, BusinessTables)); Assert.Equal(retained, await RowsAsync(flow, "kernel.identification_scan_acceptance"));
    }

    [Fact]
    public async Task Provider_operator_and_workstation_authority_comes_only_from_explicit_server_development_claims()
    {
        await using var flow = await CreateAsync(postgres, currentAuthenticatedOperator: true, trustedWorkstation: true, providerClaims: false);
        object ForgedRequest() => new { operationCode = Operation(flow.OrderA), machineCode = "SLITTER-A", badge = SarahBadge, previousSessionId = (Guid?)null,
            workstationId = "BODY-CANNOT-ATTEST-A-STATION", providerSessionId = "BODY-CANNOT-ATTEST-A-SESSION", reason = "Actual trusted context must come from the authentication provider" };
        await RejectedAsync(await SendAsync(flow.Terminal, "/api/identification/scan-sessions", ForgedRequest(), "wrong-provider-person"), "scan_operator_authentication_required");
        var worker = flow.Plant.As("sarah");
        await RejectedAsync(await SendAsync(worker, "/api/identification/scan-sessions", ForgedRequest(), "untrusted-body-station"), "scan_trusted_terminal_required");
        flow.Configuration[ProviderSessionSetting] = "EXPLICIT-SERVER-PROVIDER-SESSION"; flow.Configuration[WorkstationSetting] = "EXPLICIT-SERVER-WORKSTATION";
        var session = await StageAsync(flow, await CreateSessionAsync(flow, client: worker), client: worker);
        Assert.Equal(flow.Plant.People["sarah"], session.GetProperty("operatorPersonId").GetGuid());
        await JsonAsync(await SendAsync(worker, "/api/shopfloor/start", Start(flow), "worker-provider-start", session));
        var accepted = Assert.Single((await ReadAsync(worker, SessionId(session))).GetProperty("accepted").EnumerateArray());
        Assert.Equal("person:" + flow.Plant.People["sarah"].ToString("D"), accepted.GetProperty("actor").GetString());
        var persisted = string.Join("\n", await RowsAsync(flow, EvidenceTables));
        foreach (var privateValue in new[] { "BODY-CANNOT-ATTEST-A-STATION", "BODY-CANNOT-ATTEST-A-SESSION", "EXPLICIT-SERVER-PROVIDER-SESSION", "EXPLICIT-SERVER-WORKSTATION" }) Assert.DoesNotContain(privateValue, persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Badge_pin_and_token_payloads_rejected_as_codes_are_neither_echoed_nor_persisted_in_session_or_evidence()
    {
        await using var flow = await CreateAsync(postgres); var session = await StageAsync(flow, await CreateSessionAsync(flow));
        const string pin = "63729481"; const string token = "PRIVATE-SCAN-TOKEN.HEADER.PAYLOAD";
        flow.Terminal.DefaultRequestHeaders.Authorization = new("Bearer", token);
        await flow.Plant.Ok(flow.Terminal, "/api/shopfloor/pin", new { badge = SarahBadge, newPin = pin });
        foreach (var value in new[] { SarahBadge, pin, token })
        {
            using var response = await SendAsync(flow.Terminal, StagePath(session), StageBody(session, value), "private-input-" + Guid.NewGuid().ToString("N"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode); var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(value, text, StringComparison.Ordinal); Assert.Contains("invalid_identification", text, StringComparison.Ordinal);
        }
        await JsonAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", Start(flow), "private-safe-start", session));
        var persisted = string.Join("\n", await RowsAsync(flow, EvidenceTables));
        foreach (var value in new[] { SarahBadge, MichaelBadge, pin, token }) Assert.DoesNotContain(value, persisted, StringComparison.Ordinal);
        var publicView = (await ReadAsync(flow.Terminal, SessionId(session))).GetRawText();
        foreach (var value in new[] { SarahBadge, pin, token }) Assert.DoesNotContain(value, publicView, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retained_session_reads_recheck_new_actual_operation_classification_and_recovery_preserves_recorded_bytes()
    {
        // An explicitly approved empty scan list still requires a server session. There is no
        // selected Operation row to accidentally provide the missing current-owner read check.
        await using var flow = await CreateAsync(postgres, requireOperationScan: false); var session = await CreateSessionAsync(flow);
        await JsonAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", Start(flow), "source-classification-start", session));
        var history = (await ReadAsync(flow.Terminal, SessionId(session))).GetProperty("accepted").GetRawText();
        await flow.Plant.Ok(flow.Plant.Engineer, "/api/engineering/documents", new { number = Instruction, title = "Actual operation instruction", type = "WorkInstruction", authority = "Internal" });
        await ReleaseDocumentAsync(flow.Plant, Instruction, "A");
        await flow.Plant.Ok(flow.Plant.Engineer, "/api/engineering/items/ROLL-100/revisions/A/documents", new { documentNumber = Instruction, operationSequence = 10, reason = "Add the current actual operation source" });
        var person = await flow.Plant.Ok(flow.Plant.Admin, "/api/kernel/people", new { idpSubject = "scan-document-classifier", displayName = "Explicit classified-source owner", markings = new[] { "Proprietary" }, reason = "Actual document control clearance for this source" });
        await flow.Plant.Ok(flow.Plant.Admin, $"/api/kernel/people/{person.GetProperty("entityId").GetString()}/roles", new { role = Roles.ManufacturingEngineer, reason = "Explicit current document control authority" });
        var owner = flow.Plant.As("scan-document-classifier");
        await flow.Plant.Ok(owner, $"/api/engineering/documents/{Instruction}", new { title = "Actual operation instruction", markings = "Proprietary", reason = "Raise the current operational source classification" });
        var retained = await RowsAsync(flow, "kernel.identification_scan_session", "kernel.identification_scan_acceptance"); var business = await RowsAsync(flow, BusinessTables);
        using (var concealed = await flow.Terminal.GetAsync($"/api/identification/scan-sessions/{SessionId(session):D}"))
            Assert.Equal(HttpStatusCode.NotFound, concealed.StatusCode);
        using (var replay = await SendAsync(flow.Terminal, "/api/shopfloor/start", Start(flow), "source-classification-start", session))
            Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal(business, await RowsAsync(flow, BusinessTables)); Assert.Equal(retained, await RowsAsync(flow, "kernel.identification_scan_session", "kernel.identification_scan_acceptance"));
        await flow.Plant.Ok(owner, $"/api/engineering/documents/{Instruction}", new { title = "Actual operation instruction", markings = "None", reason = "Explicit authorized current-source recovery" });
        Assert.Equal(history, (await ReadAsync(flow.Terminal, SessionId(session))).GetProperty("accepted").GetRawText());
        Assert.Equal(retained, await RowsAsync(flow, "kernel.identification_scan_session", "kernel.identification_scan_acceptance"));
    }

    private static async Task AssertProductionAsync(Flow flow, long reports, decimal good)
    {
        await using var connection = new NpgsqlConnection(flow.App.ConnectionString); await connection.OpenAsync();
        await using var reportCount = new NpgsqlCommand("SELECT count(*) FROM execution.production_report WHERE order_number=$1 AND sequence=10", connection); reportCount.Parameters.AddWithValue(flow.OrderA);
        Assert.Equal(reports, (long)(await reportCount.ExecuteScalarAsync())!);
        await using var quantity = new NpgsqlCommand("SELECT good_quantity FROM execution.order_operation WHERE order_number=$1 AND sequence=10", connection); quantity.Parameters.AddWithValue(flow.OrderA);
        Assert.Equal(good, (decimal)(await quantity.ExecuteScalarAsync())!);
    }

    private static async Task AssertRealLinksAsync(Flow flow, JsonElement accepted, string commandType, Guid correlation, Guid worker)
    {
        Assert.Equal(commandType, accepted.GetProperty("commandType").GetString()); Assert.Equal(worker, accepted.GetProperty("operatorPersonId").GetGuid()); Assert.Equal(correlation, accepted.GetProperty("correlationId").GetGuid());
        Assert.Equal("person:" + flow.Plant.People["scan-terminal"].ToString("D"), accepted.GetProperty("actor").GetString()); Assert.Equal(flow.OrderA.ToString(CultureInfo.InvariantCulture) + "/10", accepted.GetProperty("entityId").GetString());
        var auditId = accepted.GetProperty("auditId").GetGuid(); var events = accepted.GetProperty("eventIds").EnumerateArray().Select(row => row.GetGuid()).ToArray(); Assert.NotEmpty(events);
        Assert.NotEmpty(accepted.GetProperty("policyFingerprint").GetString()!); Assert.NotEmpty(accepted.GetProperty("selections").EnumerateArray());
        var relay = flow.App.Services.GetRequiredService<OutboxRelay>();
        while (await relay.RelayBatchAsync(1000, CancellationToken.None) > 0) { }
        await using var connection = new NpgsqlConnection(flow.App.ConnectionString); await connection.OpenAsync();
        await using (var command = new NpgsqlCommand("SELECT action,outcome,actor,correlation_id,entity_id,hash,context_fingerprint FROM kernel.audit_log WHERE audit_id=$1", connection))
        {
            command.Parameters.AddWithValue(auditId); await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            Assert.Equal(commandType, reader.GetString(0)); Assert.Equal("Succeeded", reader.GetString(1)); Assert.Equal(accepted.GetProperty("actor").GetString(), reader.GetString(2)); Assert.Equal(correlation, reader.GetGuid(3)); Assert.Equal(accepted.GetProperty("entityId").GetString(), reader.GetString(4)); Assert.Equal(64, reader.GetString(5).Length);
            Assert.Equal(accepted.GetProperty("evidenceFingerprint").GetString(), reader.GetString(6)); Assert.False(await reader.ReadAsync());
        }
        await using (var sessionAudit = new NpgsqlCommand("SELECT new_value FROM kernel.audit_log WHERE action='CreateScanSession' AND outcome='Succeeded'", connection))
        {
            await using var reader = await sessionAudit.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Assert.DoesNotContain("channelHash", reader.GetString(0), StringComparison.Ordinal);
                Assert.DoesNotContain("authenticationBindingHash", reader.GetString(0), StringComparison.Ordinal);
            }
        }
        foreach (var id in events)
        {
            await using var command = new NpgsqlCommand("SELECT envelope FROM kernel.outbox WHERE event_id=$1", connection); command.Parameters.AddWithValue(id);
            var text = Assert.IsType<string>(await command.ExecuteScalarAsync()); using var envelope = JsonDocument.Parse(text);
            Assert.Equal(id, envelope.RootElement.GetProperty("eventId").GetGuid()); Assert.Equal(correlation, envelope.RootElement.GetProperty("correlationId").GetGuid()); Assert.Equal(accepted.GetProperty("actor").GetString(), envelope.RootElement.GetProperty("actor").GetString());
            await using var published = new NpgsqlCommand("SELECT envelope FROM kernel.event_log WHERE event_id=$1 AND correlation_id=$2", connection); published.Parameters.AddWithValue(id); published.Parameters.AddWithValue(correlation);
            Assert.Equal(text, Assert.IsType<string>(await published.ExecuteScalarAsync()));
        }
    }
}
