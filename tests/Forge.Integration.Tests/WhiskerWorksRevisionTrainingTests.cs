using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Host.History;
using Forge.Host.Propagation;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>WhiskerWorks S09 through the released engineering change, exact document revision, and kiosk start path.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WhiskerWorksRevisionTrainingTests(PostgresFixture postgres)
{
    private static int key;

    private static string Day(int offset) => DateTime.UtcNow.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<string> UploadAsync(HttpClient client, string number, string revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes($"FICTIONAL / SAMPLE DATA — {number} revision {revision}; not a production instruction.")),
        };
        request.Headers.Add("Idempotency-Key", $"ww-s09-file-{Interlocked.Increment(ref key)}");
        request.Headers.Add("X-File-Name", $"{number}-{revision}-fictional-reference.txt");
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("entityId").GetString()!;
    }

    private static async Task ReleasePropagationAsync(ForgeApp app)
    {
        var relay = app.Services.GetRequiredService<OutboxRelay>();
        while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0) { }
        await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
        while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0) { }
    }

    private static async Task<JsonElement> OperationAnswer(HttpClient planner, long orderNumber) =>
        (await Get(planner, "/api/readiness/operations")).EnumerateArray()
            .Single(row => row.GetProperty("orderNumber").GetInt64() == orderNumber && row.GetProperty("sequence").GetInt32() == 10);

    [Fact]
    public async Task Released_WhiskerWorks_Rev_B_blocks_Rev_A_only_operator_until_exact_document_training_is_recorded()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var engineer = plant.Engineer;
        await plant.Person("dc", "DocumentController");
        await plant.Person("kiosk", "ShopFloorTerminal");
        var kiosk = plant.As("kiosk");

        await plant.Ok(engineer, "/api/resources/safety-areas", new { code = "ELE", name = "Electronics", parent = "PLANT1" });
        await plant.Ok(engineer, "/api/resources/work-centers", new { code = "WC-ELE-PCB", name = "PCB Assembly", parent = "ELE" });
        await plant.Ok(engineer, "/api/resources/activity-types", new { code = "PCB_ASSEMBLE", name = "PCB assembly" });
        await plant.Ok(engineer, "/api/resources/machines", new { code = "ELE-SMT-01", name = "Electronics assembly station", parent = "WC-ELE-PCB" });
        foreach (var (code, name, kind) in new[]
        {
            ("QUAL-IPC-A610", "IPC-A-610 assembly qualification", "Process"),
            ("QUAL-ESD-SAFE", "ESD handling qualification", "Safety"),
        })
        {
            await plant.Ok(engineer, "/api/competency/qualification-types", new { code, name, kind, validityDays = 365 });
        }
        await plant.Ok(engineer, "/api/resources/machines/ELE-SMT-01/activities", new
        {
            activityCode = "PCB_ASSEMBLE", operatorQualifications = new[] { "QUAL-IPC-A610", "QUAL-ESD-SAFE" },
        });
        await plant.Worker("pcb-operator", "WC-ELE-PCB", "QUAL-IPC-A610", "QUAL-ESD-SAFE");
        await plant.Assign("pcb-operator", new { machineCode = "ELE-SMT-01", activityCode = "PCB_ASSEMBLE", reason = "WhiskerWorks S09 baseline assignment" });
        var operatorId = plant.People["pcb-operator"];
        const string badge = "WW-PCB-OP-01";
        await plant.Ok(plant.Admin, $"/api/workforce/people/{operatorId}/badge", new { badge });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge });

        await plant.Ok(engineer, "/api/engineering/documents", new
        {
            number = "WI-ELE-610", title = "Fictional electronics assembly work-instruction reference",
            type = "WorkInstruction", authority = "Internal",
        });
        var fileA = await UploadAsync(engineer, "WI-ELE-610", "A");
        await plant.Ok(engineer, "/api/engineering/documents/WI-ELE-610/revisions", new
        {
            revision = "A", fileSha256 = fileA, fileName = "WI-ELE-610-A-fictional-reference.txt",
            changeNote = "Fictional reference placeholder; not an approved manufacturing procedure.",
        });
        await plant.Ok(engineer, "/api/engineering/documents/WI-ELE-610/revisions/A/submit", new { });
        await plant.Ok(plant.Approver, "/api/engineering/documents/WI-ELE-610/revisions/A/approve", new { });
        await plant.Ok(plant.Approver, "/api/engineering/documents/WI-ELE-610/revisions/A/release", new { reason = "Release fictional baseline reference" });

        await plant.Ok(engineer, "/api/engineering/document-training/policy", new
        {
            policy = "Enforce", documentTypes = new[] { "WorkInstruction" }, reason = "Verify WhiskerWorks revision-specific training gate",
        });
        await plant.Ok(engineer, "/api/engineering/items", new
        {
            partNumber = "WW-PCB-CTRL-200", description = "Fictional laser arena controller PCB", unitOfMeasure = "EA",
            makeBuy = "Make", leadTimeDays = 1, lotTracked = false, serialized = false,
        });
        await plant.Ok(engineer, "/api/engineering/items/WW-PCB-CTRL-200/revisions", new { revision = "A" });
        var route = new
        {
            sequence = 10, description = "Populate and inspect controller PCB", workCenterCode = "WC-ELE-PCB", activityCode = "PCB_ASSEMBLE",
            eligibleMachines = new[] { "ELE-SMT-01" }, requiredQualifications = new[] { "QUAL-IPC-A610", "QUAL-ESD-SAFE" },
            setupMinutes = 3m, runMinutesPerUnit = 2m, crewSize = 1,
        };
        await plant.Ok(engineer, "/api/engineering/items/WW-PCB-CTRL-200/revisions/A/routing", route);
        await plant.Ok(engineer, "/api/engineering/items/WW-PCB-CTRL-200/revisions/A/documents", new
        {
            documentNumber = "WI-ELE-610", documentRevision = "A", operationSequence = 10,
            note = "Fictional controlled training reference; placeholder content is not a work instruction.",
        });
        await plant.Ok(plant.Approver, "/api/engineering/items/WW-PCB-CTRL-200/revisions/A/release", new { reason = "Release fictional WhiskerWorks PCB baseline" });

        var orderNumber = Id(await plant.Ok(engineer, "/api/orders/", new
        {
            partNumber = "WW-PCB-CTRL-200", quantity = 2m, dueDate = Day(10), priority = 3,
        }));
        await plant.Ok(planner, $"/api/orders/{orderNumber}/release", new { reason = "Release fictional S09 baseline order" });
        await plant.Ok(engineer, "/api/engineering/document-training", new
        {
            personId = operatorId, documentNumber = "WI-ELE-610", revision = "A", trainedOn = Day(0),
        });

        var fileB = await UploadAsync(engineer, "WI-ELE-610", "B");
        await plant.Ok(engineer, "/api/engineering/documents/WI-ELE-610/revisions", new
        {
            revision = "B", fileSha256 = fileB, fileName = "WI-ELE-610-B-fictional-reference.txt",
            changeNote = "ECN-2026-04 fictional Rev B reference; placeholder content is not an approved manufacturing procedure.",
        });
        await plant.Ok(engineer, "/api/engineering/items/WW-PCB-CTRL-200/revisions", new { revision = "B" });
        await plant.Ok(engineer, "/api/engineering/items/WW-PCB-CTRL-200/revisions/B/routing", route);
        await plant.Ok(engineer, "/api/engineering/items/WW-PCB-CTRL-200/revisions/B/documents", new
        {
            documentNumber = "WI-ELE-610", documentRevision = "B", operationSequence = 10,
            note = "Fictional Rev B training reference; placeholder content is not a work instruction.",
        });

        var changeNumber = (await plant.Ok(engineer, "/api/engineering/changes", new
        {
            title = "ECN-2026-04 · WW-PCB-CTRL-200 Rev B", @class = "Minor", type = "Design",
            changeReason = "Fictional WhiskerWorks controlled revision and operator retraining scenario",
        })).GetProperty("entityId").GetString()!;
        await plant.Ok(engineer, $"/api/engineering/changes/{changeNumber}/lines", new
        {
            kind = "DocumentRevision", subject = "WI-ELE-610", toRevision = "B", note = "Controlled assembly instruction revision",
        });
        await plant.Ok(engineer, $"/api/engineering/changes/{changeNumber}/lines", new
        {
            kind = "ItemRevision", subject = "WW-PCB-CTRL-200", toRevision = "B", note = "PCB assembly revision uses WI-ELE-610 Rev B",
        });
        await plant.Ok(engineer, $"/api/engineering/changes/{changeNumber}/submit", new { });
        await plant.Ok(plant.Approver, $"/api/engineering/changes/{changeNumber}/reviews", new
        {
            kind = "Technical", decision = "Approve", notes = "Reviewed fictional WhiskerWorks revision package",
        });
        await plant.Ok(plant.Approver, $"/api/engineering/changes/{changeNumber}/approve", new { });
        await plant.Ok(plant.As("dc"), $"/api/engineering/changes/{changeNumber}/release", new
        {
            effectiveFrom = (DateOnly?)null, reason = "Release fictional ECN-2026-04 after independent review",
        });
        await ReleasePropagationAsync(app);

        var movedOrder = await Get(planner, $"/api/orders/{orderNumber}");
        Assert.Equal("B", movedOrder.GetProperty("revision").GetString());
        Assert.Equal("Released", movedOrder.GetProperty("status").GetString());

        var readiness = await OperationAnswer(planner, orderNumber);
        var training = readiness.GetProperty("dimensions").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "Training");
        Assert.Equal("Fail", training.GetProperty("outcome").GetString());
        Assert.Contains("WI-ELE-610 rev B", training.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains("Pcb-operator", training.GetProperty("detail").GetString(), StringComparison.Ordinal);

        var denied = await plant.Try(kiosk, "/api/shopfloor/start", new { badge, orderNumber, sequence = 10, machineCode = "ELE-SMT-01" });
        var deniedBody = await denied.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);
        Assert.Contains("Training", deniedBody, StringComparison.OrdinalIgnoreCase);
        Assert.Empty((await Get(planner, $"/api/orders/{orderNumber}/shopfloor")).GetProperty("labor").EnumerateArray());

        await plant.Ok(engineer, "/api/engineering/document-training", new
        {
            personId = operatorId, documentNumber = "WI-ELE-610", revision = "B", trainedOn = Day(0),
        });
        var trainedReadiness = await OperationAnswer(planner, orderNumber);
        Assert.True(trainedReadiness.GetProperty("executable").GetBoolean(), trainedReadiness.GetRawText());
        Assert.Equal("Pass", trainedReadiness.GetProperty("dimensions").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "Training").GetProperty("outcome").GetString());

        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge, orderNumber, sequence = 10, machineCode = "ELE-SMT-01" });
        Assert.Single((await Get(planner, $"/api/orders/{orderNumber}/shopfloor")).GetProperty("labor").EnumerateArray());
    }
}
