using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PlantVisualTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Plant_map_positions_and_worker_photos_are_role_gated_and_protected()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var plant = new SlittingPlant.Plant(app);
        await plant.Person("eng", "ManufacturingEngineer", "TrainingCoordinator");
        await plant.Person("facilities", "FacilitiesManager");
        await plant.Person("planner", "Planner");
        var engineer = plant.Engineer;
        var facilitiesManager = plant.As("facilities");
        var planner = plant.As("planner");

        await plant.Ok(engineer, "/api/resources/sites", new { code = "VISUAL", name = "Visual plant" });
        await plant.Ok(engineer, "/api/resources/safety-areas", new { code = "VISUAL-FLOOR", name = "Main floor", parent = "VISUAL" });
        await plant.Ok(engineer, "/api/resources/work-centers", new { code = "VISUAL-A", name = "Assembly", parent = "VISUAL-FLOOR" });
        await plant.Ok(engineer, "/api/resources/work-centers", new { code = "VISUAL-B", name = "Inspection", parent = "VISUAL-FLOOR" });

        var noMapReason = await plant.Try(engineer, "/api/resources/work-centers/map-layout", new
        {
            placements = new[] { new { code = "VISUAL-A", mapX = 200, mapY = 300 }, new { code = "VISUAL-B", mapX = 700, mapY = 300 } },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noMapReason.StatusCode);
        var forbiddenMap = await plant.Try(planner, "/api/resources/work-centers/map-layout", new
        {
            placements = new[] { new { code = "VISUAL-A", mapX = 200, mapY = 300 }, new { code = "VISUAL-B", mapX = 700, mapY = 300 } }, reason = "Move markers",
        });
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenMap.StatusCode);
        await plant.Ok(facilitiesManager, "/api/resources/work-centers/map-layout", new
        {
            placements = new[] { new { code = "VISUAL-A", mapX = 200, mapY = 300 }, new { code = "VISUAL-B", mapX = 700, mapY = 300 } }, reason = "Match the floor map",
        });
        var resources = await facilitiesManager.GetFromJsonAsync<JsonElement>("/api/resources/");
        Assert.Equal(200, resources.GetProperty("workCenters").EnumerateArray().Single(w => w.GetProperty("code").GetString() == "VISUAL-A").GetProperty("mapX").GetInt32());

        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/2eQAAAAASUVORK5CYII=");
        var uploadMap = await UploadImage(facilitiesManager, "/api/resources/sites/VISUAL/map", png, "image/png", "Upload current site floor plan");
        Assert.Equal(HttpStatusCode.OK, uploadMap.StatusCode);
        var mapImage = await facilitiesManager.GetAsync("/api/resources/sites/VISUAL/map/image");
        Assert.Equal("image/png", mapImage.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await mapImage.Content.ReadAsByteArrayAsync());
        var deniedMap = await UploadImage(planner, "/api/resources/sites/VISUAL/map", png, "image/png", "Unauthorized map upload");
        Assert.Equal(HttpStatusCode.Forbidden, deniedMap.StatusCode);

        var personId = await plant.Person("operator");
        await plant.Ok(engineer, $"/api/workforce/workers/{personId}", new { homeWorkCenterCode = "VISUAL-A" });
        var uploadPhoto = await UploadImage(facilitiesManager, $"/api/workforce/workers/{personId}/photo", png, "image/png", "Add worker profile photo");
        Assert.Equal(HttpStatusCode.OK, uploadPhoto.StatusCode);
        var workers = await engineer.GetFromJsonAsync<JsonElement[]>("/api/workforce/workers");
        Assert.True(workers!.Single(w => w.GetProperty("personId").GetGuid() == personId).GetProperty("hasPhoto").GetBoolean());
        var photo = await facilitiesManager.GetAsync($"/api/workforce/workers/{personId}/photo");
        Assert.Equal(png, await photo.Content.ReadAsByteArrayAsync());
        var deniedPhoto = await UploadImage(planner, $"/api/workforce/workers/{personId}/photo", png, "image/png", "Unauthorized worker photo");
        Assert.Equal(HttpStatusCode.Forbidden, deniedPhoto.StatusCode);

        using var invalid = await UploadImage(engineer, "/api/resources/sites/VISUAL/map", [1, 2, 3, 4], "image/png", "Reject malformed image");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private static async Task<HttpResponseMessage> UploadImage(HttpClient client, string path, byte[] bytes, string contentType, string reason)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        request.Headers.Add("X-Change-Reason", reason);
        return await client.SendAsync(request);
    }
}
