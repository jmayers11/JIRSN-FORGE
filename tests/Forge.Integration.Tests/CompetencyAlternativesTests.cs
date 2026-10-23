using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CompetencyAlternativesTests(PostgresFixture postgres)
{
    [Fact]
    public async Task CNC_setup_accepts_either_machine_qualification_and_rejects_neither()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var plant = new SlittingPlant.Plant(app);
        await plant.Person("eng", "ManufacturingEngineer", "TrainingCoordinator");
        await plant.Person("certifier", "Certifier");
        var millOperator = await plant.Person("mill-operator");
        var unqualified = await plant.Person("unqualified");

        foreach (var (code, name, kind) in new[]
        {
            ("QUAL-CNC-MILL", "CNC mill authorization", "Machine"),
            ("QUAL-CNC-LATHE", "CNC lathe authorization", "Machine"),
            ("QUAL-CNC-SETUP", "CNC setup authorization", "SpecialProcess"),
        })
        {
            await plant.Ok(plant.Engineer, "/api/competency/qualification-types", new { code, name, kind, validityDays = 365 });
        }

        await plant.Ok(plant.Engineer, "/api/competency/qualification-types/QUAL-CNC-SETUP/prerequisite-alternatives", new
        {
            groupCode = "MILL-OR-LATHE", name = "CNC mill or lathe authorization",
            prerequisiteCodes = new[] { "QUAL-CNC-MILL", "QUAL-CNC-LATHE" },
        });

        var catalogResponse = await plant.Engineer.GetAsync("/api/competency/qualification-types");
        var catalogText = await catalogResponse.Content.ReadAsStringAsync();
        Assert.True(catalogResponse.IsSuccessStatusCode, catalogText);
        using (var catalog = JsonDocument.Parse(catalogText))
        {
            var setup = catalog.RootElement.EnumerateArray().Single(item => item.GetProperty("code").GetString() == "QUAL-CNC-SETUP");
            var group = setup.GetProperty("prerequisiteAlternatives").EnumerateArray().Single();
            Assert.Equal("MILL-OR-LATHE", group.GetProperty("groupCode").GetString());
            Assert.Equal(new[] { "QUAL-CNC-LATHE", "QUAL-CNC-MILL" }, group.GetProperty("qualificationCodes").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray());
        }

        await plant.Ok(plant.Engineer, $"/api/competency/people/{millOperator}/training", new { code = "QUAL-CNC-MILL" });
        await plant.Ok(plant.As("certifier"), $"/api/competency/people/{millOperator}/certifications", new
        {
            code = "QUAL-CNC-MILL", evidence = "Fictional mill assessment", reason = "Assessment passed",
        });
        await plant.Ok(plant.Engineer, $"/api/competency/people/{millOperator}/training", new { code = "QUAL-CNC-SETUP" });
        await plant.Ok(plant.As("certifier"), $"/api/competency/people/{millOperator}/certifications", new
        {
            code = "QUAL-CNC-SETUP", evidence = "Fictional setup assessment", reason = "Mill prerequisite is current",
        });

        await plant.Ok(plant.Engineer, $"/api/competency/people/{unqualified}/training", new { code = "QUAL-CNC-SETUP" });
        var rejected = await plant.Try(plant.As("certifier"), $"/api/competency/people/{unqualified}/certifications", new
        {
            code = "QUAL-CNC-SETUP", evidence = "Fictional setup assessment", reason = "Verify prerequisite gate",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Contains("prerequisites_missing", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var cycle = await plant.Try(plant.Engineer, "/api/competency/qualification-types/QUAL-CNC-MILL/prerequisites", new
        {
            prerequisiteCode = "QUAL-CNC-SETUP", reason = "Verify alternative prerequisite cycle protection",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cycle.StatusCode);
        Assert.Contains("prerequisite_cycle", await cycle.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
