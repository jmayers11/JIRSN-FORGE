using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 17 (backup and recovery): the plant runs for a while (receipts, an inspection with a rejection, scrap, a
/// hold), the database is backed up, and more work is done after the backup. The backup is restored into a new database
/// and a new host is started on it. The restored plant must be exactly the plant at the moment of the backup: the same
/// business state, an intact audit chain, the open rejection still open, none of the later work, and the idempotency
/// record intact, so a command replayed from before the backup has no second effect. The restored host then accepts new
/// work. Expectations come from the V3.28 backup and restore drill and the idempotency contract, never from Forge.
/// </summary>
internal static class RecoveryScenario
{
    public const string Name = "S17-backup-and-recovery";
    private const string Drill = "V3.28 backup and restore drill";
    private const string Part = SimFactory.LotPart;

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        log.Add(TimeSpan.Zero, "Receive", ("lot", "REC-1"), ("quantity", log.Between(20, 90)));
        log.Add(TimeSpan.FromMinutes(5), "Receive", ("lot", "REC-2"), ("quantity", log.Between(20, 90)));
        log.Add(TimeSpan.FromMinutes(10), "Scrap", ("lot", "REC-1"), ("quantity", log.Between(1, 9)));
        log.Add(TimeSpan.FromMinutes(15), "Rejection", ("quantity", log.Between(30, 90)), ("rejected", log.Between(2, 12)));
        log.Add(TimeSpan.FromMinutes(20), "Backup");
        log.Add(TimeSpan.FromMinutes(30), "Receive", ("lot", "REC-3"), ("quantity", log.Between(20, 90)));
        log.Add(TimeSpan.FromMinutes(35), "Scrap", ("lot", "REC-2"), ("quantity", log.Between(1, 9)));
        log.Add(TimeSpan.FromMinutes(40), "Restore");
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildAsync(postgres, "s17-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Recovery, Coverage.Warehouse, Coverage.Mrb);
        var (api, insp, qe) = (f.Api, f.Api.As("insp"), f.Api.As("qe"));
        var parts = new[] { Part, SimFactory.BulkPart };
        var target = "forge_test_restore_" + Guid.NewGuid().ToString("N")[..12];
        var archive = Path.Combine(Path.GetTempPath(), "forge-sim-" + Guid.NewGuid().ToString("N") + ".dump");
        var restoredCreated = false;
        string hashAtBackup = string.Empty, replayKey = string.Empty, finalHash = string.Empty;
        object? replayBody = null;
        long openRejection = 0;

        static async Task<HashSet<long>> OpenRejections(HttpClient reader) =>
            (await SimApi.Get(reader, "/api/inventory/rejections?open=true")).EnumerateArray().Select(r => r.GetProperty("rejectionId").GetInt64()).ToHashSet();

        try
        {
            var sinceStart = TimeSpan.Zero;
            foreach (var ev in log.Events)
            {
                f.Clock.Advance(ev.At - sinceStart);
                sinceStart = ev.At;
                var step = $"#{ev.Index} {ev.Type}";
                switch (ev.Type)
                {
                    case "Receive":
                    {
                        report.Rule(Basis.Plan, "Setup: a lot is received into stores");
                        var key = api.NextKey();
                        var body = new { partNumber = Part, quantity = ev.Number("quantity"), locationCode = SimFactory.Stores, lotNumber = ev.Text("lot"), revision = "A", reason = "Stock" };
                        report.Expect($"{step} {ev.Text("lot")}: received", "200", (await api.Send(f.StoresUser, "/api/inventory/receipts", body, key)).Label);
                        if (replayBody is null)
                        {
                            (replayKey, replayBody) = (key, body);
                        }

                        break;
                    }

                    case "Scrap":
                        report.Rule(Basis.Plan, "Setup: part of a lot is scrapped");
                        report.Expect($"{step} {ev.Text("lot")}: scrapped", "200", (await api.Send(f.StoresUser, "/api/inventory/scrap",
                            new { partNumber = Part, quantity = ev.Number("quantity"), locationCode = SimFactory.Stores, lotNumber = ev.Text("lot"), reason = "Damaged" })).Label);
                        break;

                    case "Rejection":
                    {
                        report.Rule(Basis.Plan, "Setup: a delivery is inspected with a rejection that stays open");
                        var (quantity, rejected) = (ev.Number("quantity"), ev.Number("rejected"));
                        var receipt = await api.Send(f.StoresUser, "/api/inventory/receipts", new { partNumber = SimFactory.BulkPart, quantity, locationCode = SimFactory.Stores, inspect = true, reason = "Delivery" });
                        report.Expect($"{step}: receipt", "200", receipt.Label);
                        var seen = await OpenRejections(qe);
                        report.Expect($"{step}: inspection", "200", (await api.Send(insp, $"/api/inventory/receiving/{receipt.Id}/inspections", new { accepted = quantity - rejected, rejected, defect = "Dented", reason = "Inspection" })).Label);
                        openRejection = (await OpenRejections(qe)).Single(id => !seen.Contains(id));
                        break;
                    }

                    case "Backup":
                        hashAtBackup = await Invariants.StateHash(f.StoresUser, parts);
                        report.Rule(Basis.Spec, Drill + ": a backup of the running plant is taken with the database's own tool");
                        await postgres.BackupDatabaseAsync(f.App.ConnectionString, archive);
                        report.Expect($"{step}: backup written", "True", new FileInfo(archive).Length > 0 ? "True" : "False", "Recovery");
                        break;

                    case "Restore":
                    {
                        report.Rule(Basis.Plan, "Setup: the backup is restored into a new, empty database and a new host starts on it");
                        var source = new NpgsqlConnectionStringBuilder(f.App.ConnectionString);
                        var maintenance = new NpgsqlConnectionStringBuilder(source.ConnectionString) { Database = "postgres", Pooling = false };
                        await using (var admin = new NpgsqlConnection(maintenance.ConnectionString))
                        {
                            await admin.OpenAsync();
                            await using var create = new NpgsqlCommand($"CREATE DATABASE {target}", admin);
                            await create.ExecuteNonQueryAsync();
                            restoredCreated = true;
                        }

                        await postgres.RestoreDatabaseAsync(f.App.ConnectionString, target, archive);
                        var restoredConnection = new NpgsqlConnectionStringBuilder(source.ConnectionString) { Database = target }.ConnectionString;
                        await using var restored = new ForgeApp(restoredConnection, services => services.AddSingleton<TimeProvider>(f.Clock));
                        var rapi = new SimApi(restored, "s17r-" + seed.ToString(CultureInfo.InvariantCulture));
                        report.Evidence(rapi);
                        var rstores = restored.As("stores");

                        report.Rule(Basis.Spec, Drill + ": the restored plant is exactly the plant at the backup: same balances and ledger, none of the later work");
                        report.Expect($"{step}: business state equals the state at backup", hashAtBackup, await Invariants.StateHash(rstores, parts), "Recovery");
                        report.Expect($"{step}: work after the backup is absent (lot REC-3)", 0m,
                            (await Invariants.ReportedBalances(rstores, [Part])).Where(b => b.Key.Lot == "REC-3").Sum(b => b.Value));
                        await Invariants.Inventory(report, step + " (restored)", rstores, parts);
                        await Invariants.AuditChain(report, step + " (restored)", restored.As(ForgeApp.AdminSubject));
                        report.Rule(Basis.Spec, Drill + ": open quality decisions survive the restore");
                        report.Expect($"{step}: the open rejection is still open", "True", (await OpenRejections(restored.As("qe"))).Contains(openRejection) ? "True" : "False", "Recovery");

                        report.Rule(Basis.Spec, "Idempotency contract: the key record is part of the database, so a command from before the backup replayed after the restore has no second effect");
                        var before = await Invariants.StateHash(rstores, parts);
                        var replay = await rapi.Send(rstores, "/api/inventory/receipts", replayBody!, replayKey);
                        report.Expect($"{step}: replayed command answered without a new effect", "True", (replay.Succeeded || (int)replay.Status == 409) && before == await Invariants.StateHash(rstores, parts) ? "True" : "False", "Recovery");

                        report.Rule(Basis.Spec, Drill + ": the restored host accepts new work");
                        report.Expect($"{step}: new receipt on the restored host", "200", (await rapi.Send(rstores, "/api/inventory/receipts",
                            new { partNumber = Part, quantity = 5m, locationCode = SimFactory.Stores, lotNumber = "REC-AFTER", revision = "A", reason = "First receipt after restore" })).Label);
                        await Invariants.Inventory(report, step + " (restored, new work)", rstores, parts);
                        await Invariants.AuditChain(report, step + " (restored, new work)", restored.As(ForgeApp.AdminSubject));
                        finalHash = await Invariants.StateHash(rstores, parts);
                        break;
                    }
                }

                if (ev.Type != "Restore")
                {
                    await Invariants.Inventory(report, step, f.StoresUser, parts);
                }
            }

            await Invariants.AuditChain(report, "End (original)", f.Plant.Admin);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (restoredCreated)
            {
                await postgres.DropDatabaseAsync(target);
            }

            if (File.Exists(archive))
            {
                File.Delete(archive);
            }
        }

        report.Note("Point-in-time recovery: work done after the backup is lost by design; the drill shows exactly what was lost (lot REC-3 and the second scrap).");
        return (report, log, finalHash);
    }
}
