using System.Text.Json;
using Forge.Quality.Contracts;
using static Forge.Integration.Tests.QmsPlanningScenario;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class QmsEditionMappingTests(PostgresFixture postgres)
{
    private const string Api = "/api/quality/qms/review/editions/assessments";

    [Fact]
    public async Task Unverified_many_to_many_edition_mapping_requires_independent_human_decisions_and_stays_pending()
    {
        await using var f = await AuditCapaScenario.SetupAsync(postgres);
        var id = Guid.NewGuid();
        var approver = f.Plant.People["approver"];
        await f.Plant.Ok(f.Author, "/api/competency/people/" + approver + "/training", new { code = "AUDITOR" });
        await f.Plant.Ok(f.Observer, "/api/competency/people/" + approver + "/certifications", new
        {
            code = "AUDITOR",
            evidence = "Actual independent assessment qualification for this edition-mapping reviewer",
            reason = "A qualified certifier recorded the approver's actual competence"
        });
        var from = new QmsStandardReference("ISO 9001", "2015", "8.5.1",
            QmsStandardVerification.PendingAuthorizedVerification,
            "Authorized licensed source is not available for this assessment", null);
        var to = new QmsStandardReference("ISO 9001", "2026", "8.5.1",
            QmsStandardVerification.PendingAuthorizedVerification,
            "Authorized licensed source is not available for this assessment", null);
        var create = new
        {
            id,
            facilityCode = "PLANT1",
            programId = (Guid?)null,
            markings = "None",
            standard = "ISO 9001",
            fromEdition = "2015",
            toEdition = "2026",
            fromCriteria = new[] { from },
            toCriteria = new[] { to },
            relationship = QmsEditionRelationship.Unresolved.ToString(),
            basis = "The organization recorded an unresolved candidate relationship for qualified review.",
            changes = "Exact licensed source comparison remains outstanding.",
            applicabilityImpact = "No applicability or conformity conclusion is made while the relationship is unresolved.",
            requiredActions = "Obtain and review authorized copies, determine applicability, and record a separate human assessment.",
            auditorValidation = Validation(f.Plant.People["planning-observer"]),
            evidence = Array.Empty<QmsPlanningEvidenceReference>(),
            reason = "Record an explicit, limited edition-mapping proposal"
        };

        await f.Plant.Ok(f.Observer, Api, create);
        var created = await ReadAsync(f.Observer, Api + "/" + id);
        Assert.Equal("Draft", created.GetProperty("state").GetString());
        Assert.True(created.GetProperty("requiresLicensedVerification").GetBoolean());
        Assert.Equal("Unresolved", created.GetProperty("relationship").GetString());
        Assert.All(created.GetProperty("fromCriteria").EnumerateArray(), item =>
            Assert.Equal("PendingAuthorizedVerification", item.GetProperty("verification").GetString()));
        Assert.All(created.GetProperty("toCriteria").EnumerateArray(), item =>
            Assert.Equal("PendingAuthorizedVerification", item.GetProperty("verification").GetString()));

        await f.Plant.Ok(f.Observer, Api + $"/{id}/submit", Transition(1, f.Plant.People["planning-observer"]));
        await f.Plant.Ok(f.Reviewer, Api + $"/{id}/review", Transition(2, approver));
        await f.Plant.Ok(f.Reviewer, Api + $"/{id}/approve", Transition(3, approver));

        var approved = await ReadAsync(f.Reviewer, Api + "/" + id);
        Assert.Equal("Approved", approved.GetProperty("state").GetString());
        Assert.True(approved.GetProperty("requiresLicensedVerification").GetBoolean());
        Assert.Equal("Unresolved", approved.GetProperty("relationship").GetString());
        Assert.NotEqual(JsonValueKind.Null, approved.GetProperty("approvedAt").ValueKind);
    }

    private static QmsAuditorValidation Validation(Guid personId) => new(personId,
        DateTimeOffset.UtcNow, new("INTERNAL-AUDIT", 1), null,
        "No process ownership or evidence contribution; qualified independent review.");

    private static object Transition(int version, Guid person) => new
    {
        expectedVersion = version,
        note = "Independent qualified human reviewed only the recorded, unresolved relationship.",
        auditorValidation = Validation(person),
        reason = "Attribute the exact independent review decision"
    };
}
