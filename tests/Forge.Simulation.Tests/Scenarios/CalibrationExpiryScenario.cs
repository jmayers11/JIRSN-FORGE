using System.Globalization;
using System.Text.Json;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 5 (calibration expiry mid-run): a gauge is used while its calibration is in force, then simulated time passes
/// its due date. From then on the gauge is not valid and a use is refused; a failed calibration does not restore it; a
/// passing one does. The tool-use history keeps, for every use, whether the gauge was calibrated at that moment.
/// Expectations come from the V3.8 tools specification (a gauge needs a calibration in force; judged as of the moment
/// of use), computed from the plan's dates, never read back from Forge.
/// </summary>
internal static class CalibrationExpiryScenario
{
    public const string Name = "S05-calibration-expiry";
    private const string Spec = "V3.8 tools and calibration";
    private const string Gauge = "SIM-CAL";
    private const int CalibrationDays = 180;

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        var dueIn = log.Between(1, 3);
        log.Add(TimeSpan.Zero, "Register", ("gauge", Gauge), ("dueInDays", dueIn));
        var usesBefore = log.Between(1, 4);
        for (var i = 0; i < usesBefore; i++)
        {
            log.Add(TimeSpan.FromHours(1 + i), "Use", ("gauge", Gauge), ("reference", "Inspection before expiry " + (i + 1).ToString(CultureInfo.InvariantCulture)));
        }

        log.Add(TimeSpan.FromDays(dueIn + 1), "Use", ("gauge", Gauge), ("reference", "Inspection after expiry"));
        log.Add(TimeSpan.FromDays(dueIn + 1) + TimeSpan.FromHours(1), "Calibrate", ("gauge", Gauge), ("passed", false));
        log.Add(TimeSpan.FromDays(dueIn + 1) + TimeSpan.FromHours(2), "Use", ("gauge", Gauge), ("reference", "Inspection after a failed calibration"));
        log.Add(TimeSpan.FromDays(dueIn + 1) + TimeSpan.FromHours(3), "Calibrate", ("gauge", Gauge), ("passed", true));
        log.Add(TimeSpan.FromDays(dueIn + 1) + TimeSpan.FromHours(4), "Use", ("gauge", Gauge), ("reference", "Inspection after recalibration"));
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildAsync(postgres, "s05-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Calibration, Coverage.Reporting);
        var qe = f.Api.As("qe");
        var parts = new[] { SimFactory.BulkPart };
        var start = f.Clock.GetUtcNow();
        DateOnly due = default;
        var calibratedNow = false;
        var accepted = new List<bool>();

        async Task<(bool Valid, string Problems)> Validity()
        {
            var tool = (await SimApi.Get(qe, "/api/resources/tools")).EnumerateArray().Single(t => t.GetProperty("tool").GetProperty("code").GetString() == Gauge);
            var validity = tool.GetProperty("validity");
            return (validity.GetProperty("valid").GetBoolean(),
                validity.TryGetProperty("problems", out var p) && p.ValueKind == JsonValueKind.Array ? string.Join("; ", p.EnumerateArray().Select(x => x.GetString())) : string.Empty);
        }

        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            var step = $"#{ev.Index} {ev.Type}";
            var today = DateOnly.FromDateTime(f.Clock.GetUtcNow().UtcDateTime);
            switch (ev.Type)
            {
                case "Register":
                {
                    var dueIn = (int)ev.Number("dueInDays");
                    var calibratedOn = DateOnly.FromDateTime(start.UtcDateTime).AddDays(dueIn - CalibrationDays);
                    due = calibratedOn.AddDays(CalibrationDays);
                    report.Rule(Basis.Plan, "Setup: a gauge type with a 180-day calibration interval and a gauge calibrated so it falls due during the run");
                    report.Expect($"{step}: gauge type", "200", (await f.Api.Send(qe, "/api/resources/tool-types", new { code = "SIM-CALIPER", name = "Caliper", kind = "Gauge", calibrationDays = CalibrationDays })).Label);
                    report.Expect($"{step}: gauge registered", "200", (await f.Api.Send(qe, "/api/resources/tools", new { code = Gauge, typeCode = "SIM-CALIPER", description = "Digital caliper 150 mm", calibratedOn = calibratedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), certificate = "LAB-SIM-1" })).Label);
                    calibratedNow = true;
                    report.Rule(Basis.Spec, Spec + ": the calibration falls due its interval after the calibration date");
                    var listed = (await SimApi.Get(qe, "/api/resources/tools")).EnumerateArray().Single(t => t.GetProperty("tool").GetProperty("code").GetString() == Gauge);
                    report.Expect($"{step}: calibration due date", due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), listed.GetProperty("tool").GetProperty("calibrationDue").GetString() ?? "none");
                    break;
                }

                case "Use":
                {
                    var inForce = calibratedNow && today <= due;
                    report.Rule(Basis.Spec, Spec + ": a gauge is valid only with a calibration in force; an invalid gauge cannot be used");
                    var (valid, problems) = await Validity();
                    report.Expect($"{step}: gauge valid", inForce ? "True" : "False", valid ? "True" : "False");
                    if (!inForce)
                    {
                        report.Expect($"{step}: the reason names the calibration", "True", problems.Contains("calibration", StringComparison.OrdinalIgnoreCase) ? "True" : "False");
                    }

                    var used = await f.Api.Send(qe, $"/api/resources/tools/{Gauge}/uses", new { uses = 1, reference = ev.Text("reference") });
                    report.Expect($"{step}: use {(inForce ? "accepted" : "refused")}", inForce ? "200" : "422", ((int)used.Status).ToString(CultureInfo.InvariantCulture));
                    if (used.Succeeded)
                    {
                        accepted.Add(inForce);
                    }

                    break;
                }

                case "Calibrate":
                {
                    var passed = ev.Text("passed") == "True";
                    report.Rule(Basis.Spec, Spec + ": a failed calibration leaves no calibration in force; a passing one starts a new interval");
                    report.Expect($"{step}: calibration recorded", "200", (await f.Api.Send(qe, $"/api/resources/tools/{Gauge}/calibrations",
                        new { calibratedOn = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), passed, certificate = passed ? "LAB-SIM-3" : "LAB-SIM-2" })).Label);
                    calibratedNow = passed;
                    if (passed)
                    {
                        due = today.AddDays(CalibrationDays);
                    }

                    report.Expect($"{step}: gauge valid", passed ? "True" : "False", (await Validity()).Valid ? "True" : "False");
                    break;
                }
            }

            await Invariants.Inventory(report, step, f.StoresUser, parts);
        }

        report.Rule(Basis.Spec, Spec + ": the tool-use history records every use and whether the gauge was calibrated when it was used");
        var from = DateOnly.FromDateTime(start.UtcDateTime).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = DateOnly.FromDateTime(f.Clock.GetUtcNow().UtcDateTime).AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rows = (await SimApi.Get(qe, $"/api/reports/tool-use-history?search={Gauge}&from={from}&to={to}")).GetProperty("rows").EnumerateArray().ToList();
        report.Expect("End: uses in the history", accepted.Count.ToString(CultureInfo.InvariantCulture), rows.Count.ToString(CultureInfo.InvariantCulture));
        report.Expect("End: every recorded use was calibrated when used", "True", rows.All(r => r.GetProperty("cells")[3].GetString() == "Yes") ? "True" : "False");
        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        report.Note("Blocked inspections and the measurement-finding trace of past uses are not exercised here; they need inspection plans that name the gauge (Phase C).");
        return (report, log, await Invariants.StateHash(f.StoresUser, parts));
    }
}
