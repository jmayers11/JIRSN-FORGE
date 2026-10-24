using System.Net;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class WhiskerWorksS13QualificationMatrixTests(PostgresFixture postgres)
{
    private sealed record Gate(string Title, string HeldQualification, string RequiredQualification);

    private static readonly Gate[] Gates =
    [
        new("CNC mill does not authorize a lathe", "QUAL-CNC-MILL", "QUAL-CNC-LATHE"),
        new("CNC mill does not authorize toolpath or first-article work", "QUAL-CNC-MILL", "QUAL-CNC-TOOLPATH"),
        new("IPC-A-610 does not authorize J-STD-001 soldering", "QUAL-IPC-A610", "QUAL-IPC-JSTD001"),
        new("IPC-A-610 does not replace the solder practical", "QUAL-IPC-A610", "QUAL-SOLD-AUTH"),
        new("IPC-A-610 does not authorize harness work", "QUAL-IPC-A610", "QUAL-IPC-A620"),
        new("IPC training does not authorize energized electrical test", "QUAL-IPC-A610", "QUAL-ELEC-TEST-SAFE"),
        new("Fabrication cutting does not authorize qualified joining", "QUAL-FAB-CUT", "QUAL-FAB-JOIN"),
        new("Standard assembly torque does not authorize heavy assembly", "QUAL-ASM-TORQUE", "QUAL-ASM-HVY"),
        new("Warehouse kitting does not authorize forklift operation", "QUAL-MAT-KIT", "QUAL-PIT-FORKLIFT"),
        new("Inspection competence does not authorize MRB disposition", "QUAL-QAL-INSP", "QUAL-MRB-DISP"),
        new("LOTO training does not authorize maintenance return to service", "QUAL-MNT-LOTO", "QUAL-MNT-RTS"),
        new("ESD qualification does not authorize electronics acceptance", "QUAL-ESD-SAFE", "QUAL-IPC-A610"),
    ];

    [Fact]
    public async Task S13_rejects_each_documented_cross_qualification_assignment()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var plant = await SlittingPlant.BuildAsync(app);
        var qualifications = Gates.SelectMany(gate => new[] { gate.HeldQualification, gate.RequiredQualification })
            .Distinct(StringComparer.Ordinal).ToArray();
        foreach (var code in qualifications)
        {
            await plant.Ok(plant.Engineer, "/api/competency/qualification-types", new
            {
                code, name = code, kind = "Machine", validityDays = 365,
            });
        }

        for (var index = 0; index < Gates.Length; index++)
        {
            var gate = Gates[index];
            var workerSubject = $"s13-worker-{index + 1:00}";
            var activityCode = $"WW-S13-ACT-{index + 1:00}";
            var machineCode = $"WW-S13-M-{index + 1:00}";
            await plant.Ok(plant.Engineer, "/api/resources/activity-types", new { code = activityCode, name = gate.Title });
            await plant.Ok(plant.Engineer, "/api/resources/machines", new { code = machineCode, name = gate.Title, parent = "SLIT-WC" });
            await plant.Ok(plant.Engineer, $"/api/resources/machines/{machineCode}/activities", new
            {
                activityCode, operatorQualifications = new[] { gate.RequiredQualification },
            });
            await plant.Worker(workerSubject, "SLIT-WC", gate.HeldQualification);

            var rejected = await plant.Try(plant.Engineer, $"/api/workforce/workers/{plant.People[workerSubject]}/assignment", new
            {
                machineCode, activityCode, reason = $"S13 negative qualification control: {gate.Title}",
            });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            var body = await rejected.Content.ReadAsStringAsync();
            Assert.Contains("qualification_required", body, StringComparison.Ordinal);
            Assert.Contains(gate.RequiredQualification, body, StringComparison.Ordinal);
        }

        Assert.Equal(12, Gates.Length);
    }
}
