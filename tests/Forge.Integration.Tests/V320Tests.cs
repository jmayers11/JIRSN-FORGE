using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V320Tests(PostgresFixture postgres)
{
    [Fact]
    public async Task Inbox_excludes_own_approval_and_assignment_lifecycle_is_separate_and_sequential()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        var assigneeId = await plant.Person("v320-assignee", "Planner");
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["eng"]}/roles", new { role = "FacilitiesManager", reason = "The creator has approval authority, so the inbox must apply separation of duties" });

        await plant.Ok(plant.Engineer, "/api/projects/", new
        {
            code = "V320-SELF", title = "Self approval exclusion", type = "Facility", funding = "Operating",
            description = "Inbox acceptance test", owner = "Plant", budget = 1000m,
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.Date), endDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(30)),
            financialReference = (string?)null, reason = "Create a draft that the requester cannot decide",
        });
        var inbox = await plant.Engineer.GetFromJsonAsync<JsonElement>("/api/work-inbox");
        Assert.DoesNotContain(inbox!.GetProperty("approvals").EnumerateArray(), x => x.GetProperty("workId").GetString() == "V320-SELF");

        // The supervisor who reads this project routes it; identity administration alone grants no source access.
        await plant.Ok(plant.Engineer, "/api/work-inbox/assignments", new
        {
            workType = "ProjectReview", workId = "V320-SELF", title = "Review V320-SELF", deepLink = "#/projects",
            assigneePersonId = assigneeId, reason = "Assign independent project review",
        });
        var assignee = plant.As("v320-assignee");
        var assigned = await assignee.GetFromJsonAsync<JsonElement>("/api/work-inbox");
        var assignment = Assert.Single(assigned!.GetProperty("assignments").EnumerateArray());
        Assert.Equal("Assigned", assignment.GetProperty("status").GetString());
        var assignmentId = assignment.GetProperty("assignmentId").GetGuid();

        var mission = await assignee.GetFromJsonAsync<JsonElement>("/api/daily-mission");
        var missionAssignment = Assert.Single(mission!.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("sourceType").GetString() == "ProjectReview" && item.GetProperty("sourceId").GetString() == "V320-SELF");
        Assert.True(mission.GetProperty("isPartial").GetBoolean());
        Assert.Contains(mission.GetProperty("sources").EnumerateArray(), source => source.GetProperty("code").GetString() == "qualified-workforce-coverage-and-reassignment"
            && source.GetProperty("status").GetString() == "Limited");
        Assert.Equal("ProjectReview", missionAssignment.GetProperty("sourceType").GetString());
        Assert.Equal("WorkAssignment", missionAssignment.GetProperty("sourceLifecycle").GetString());
        Assert.Equal("Assigned", missionAssignment.GetProperty("sourceStatus").GetString());
        Assert.Equal("#/projects", missionAssignment.GetProperty("deepLink").GetString());

        var skipped = await plant.Try(assignee, $"/api/work-inbox/assignments/{assignmentId:D}/status", new { status = "Completed", reason = "Attempt to skip required steps" });
        Assert.Equal(HttpStatusCode.Conflict, skipped.StatusCode);
        foreach (var status in new[] { "Acknowledged", "InProgress", "Completed" })
            await plant.Ok(assignee, $"/api/work-inbox/assignments/{assignmentId:D}/status", new { status, reason = $"Record {status.ToLowerInvariant()}" });
        var done = await assignee.GetFromJsonAsync<JsonElement>("/api/work-inbox");
        Assert.Equal("Completed", Assert.Single(done!.GetProperty("assignments").EnumerateArray()).GetProperty("status").GetString());
        var completedMission = await assignee.GetFromJsonAsync<JsonElement>("/api/daily-mission");
        var completedMissionAssignment = Assert.Single(completedMission!.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetString() == $"assignment:{assignmentId:D}");
        Assert.Equal("Completed", completedMissionAssignment.GetProperty("sourceStatus").GetString());
        await using (var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("DELETE FROM kernel.work_assignment_history WHERE assignment_id=$1", connection))
        {
            command.Parameters.AddWithValue(assignmentId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        var source = await plant.Engineer.GetFromJsonAsync<JsonElement>("/api/projects/V320-SELF");
        Assert.Equal("Draft", source.GetProperty("project").GetProperty("status").GetString());
    }
}
