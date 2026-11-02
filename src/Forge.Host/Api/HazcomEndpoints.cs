using System.IO.Compression;
using System.Net;
using System.Text;
using Forge.Competency.Contracts;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Hazcom;
using Forge.Hazcom.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Workforce;
using Forge.Workforce.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ChemicalRequest(bool Hazardous, SignalWord SignalWord, string[]? Pictograms, string? HazardStatements, string? Ppe, string? Storage, string? Reason);

public sealed record SdsRequest(string PartNumber, string Manufacturer, string ProductName, DateOnly RevisionDate, string FileSha256, string? FileName, string? Reason);

public sealed record SdsDecisionRequest(bool Approve, string? Note, string? Reason);

public sealed record SdsReviewRequest(string Note, string? Reason);

public sealed record HazcomTrainingReviewRequest(bool RefresherNeeded, string Note, string? Reason);

public sealed record HazcomTrainingRequestBody(Guid[]? PersonIds, DateOnly? DueOn, string? Reason);

/// <summary>One active employee's HazCom training: Current, Expiring (within 30 days), Expired, InTraining, Suspended or Missing.</summary>
public sealed record HazcomTrainingPerson(Guid PersonId, string DisplayName, string WorkCenterCode, string? Department, string Status, DateTimeOffset? CertifiedAt,
    DateTimeOffset? ExpiresAt, Guid? OpenRequestId, string? OpenRequestStatus, DateOnly? OpenRequestDue);

/// <summary>The site's HazCom training at a glance: the qualification and its renewal period, the plant's onboarding requirements for it, and everyone's standing.</summary>
public sealed record HazcomTrainingView(string QualificationCode, string? QualificationName, int? ValidityDays, IReadOnlyList<OnboardingRequirement> Requirements,
    IReadOnlyList<HazcomTrainingPerson> People, IReadOnlyList<string> ChemicalsAwaitingTrainingReview);

/// <summary>Where a chemical is kept, from stock on hand.</summary>
public sealed record ChemicalLocation(string LocationCode, decimal Quantity);

/// <summary>One register entry with its SDS in force and where it is kept.</summary>
public sealed record ChemicalView(string PartNumber, string? Description, ChemicalInfo Chemical, SdsInfo? Current, int Waiting, IReadOnlyList<ChemicalLocation> Locations);

/// <summary>
/// Hazard communication (owner decisions 2026-11-01). Everyone signed in may read the register and open approved SDS, at the kiosk too
/// (the right to know does not depend on a role); the EHS Coordinator keeps them; the binder is a download for when systems are down.
/// </summary>
internal static class HazcomEndpoints
{
    /// <summary>The seeded site qualification for HazCom training (competency migration 0005).</summary>
    public const string HazcomQualification = "HAZCOM";

    public static void MapHazcomEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/hazcom").RequireAuthorization();
        api.MapGet("/chemicals", async (HttpContext http, IKernelUnitOfWorkFactory unitOfWork, CancellationToken ct) =>
        {
            if (await PrincipalAsync(http) is null) return NotRegistered();
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            return Results.Ok(await RegisterAsync(uow, ct));
        });
        api.MapGet("/sds", async (HttpContext http, string? part, IKernelUnitOfWorkFactory unitOfWork, CancellationToken ct) =>
        {
            if (await PrincipalAsync(http) is not { } principal) return NotRegistered();
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            var all = await uow.Store<IHazcomLookup>().ListSdsAsync(string.IsNullOrWhiteSpace(part) ? null : part.Trim().ToUpperInvariant(), ct);
            var authorizer = http.RequestServices.GetRequiredService<IForgeAuthorizer>();
            var reviewer = authorizer.Authorize(principal, Permissions.HazcomSubmit, ResourceRef.Of("SafetyDataSheet")).IsPermitted;
            return Results.Ok(reviewer ? all : all.Where(s => s.Status == SdsStatus.Approved).ToList());
        });
        api.MapGet("/sds/{id:long}/file", async (HttpContext http, long id, IKernelUnitOfWorkFactory unitOfWork, CancellationToken ct) =>
        {
            if (await PrincipalAsync(http) is not { } principal) return NotRegistered();
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            var sds = await uow.Store<IHazcomLookup>().FindSdsAsync(id, ct) ?? throw new CommandRejectedException("sds_not_found", $"SDS {id} does not exist.");
            if (sds.Status != SdsStatus.Approved && !http.RequestServices.GetRequiredService<IForgeAuthorizer>().Authorize(principal, Permissions.HazcomSubmit, ResourceRef.Of("SafetyDataSheet")).IsPermitted)
                throw new CommandRejectedException("sds_not_found", $"SDS {id} does not exist.");
            var bytes = await uow.Store<IDocumentLookup>().ReadFileAsync(sds.FileSha256, ct) ?? throw new CommandRejectedException("file_not_found", "The SDS file is missing.");
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Content-Security-Policy"] = "sandbox";
            var name = sds.FileName ?? $"SDS-{sds.PartNumber}.pdf";
            return Results.File(bytes, name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "application/octet-stream", name);
        });
        api.MapPost("/chemicals/{part}", (HttpContext http, string part, ChemicalRequest b, HazcomService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetChemicalAsync(new SetChemical(part, b.Hazardous, b.SignalWord, b.Pictograms, b.HazardStatements, b.Ppe, b.Storage), c, ct)));
        api.MapPost("/sds", (HttpContext http, SdsRequest b, HazcomService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SubmitAsync(new SubmitSds(b.PartNumber, b.Manufacturer, b.ProductName, b.RevisionDate, b.FileSha256, b.FileName), c, ct)));
        api.MapPost("/sds/{id:long}/decide", (HttpContext http, long id, SdsDecisionRequest b, HazcomService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DecideAsync(new DecideSds(id, b.Approve, b.Note), c, ct)));
        api.MapPost("/sds/{id:long}/review", (HttpContext http, long id, SdsReviewRequest b, HazcomService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReviewAsync(new ReviewSds(id, b.Note), c, ct)));

        // Owner decision 2026-11-01: HazCom training is a site-wide qualification and onboarding requirement each plant can change.
        api.MapPost("/chemicals/{part}/training-review", (HttpContext http, string part, HazcomTrainingReviewRequest b, HazcomService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReviewTrainingAsync(new ReviewHazcomTraining(part, b.RefresherNeeded, b.Note), c, ct)));
        api.MapGet("/training", (HttpContext http, TimeProvider time, CancellationToken _) =>
            Read(http, Permissions.CompetencyRead, async principal =>
            {
                var ct = http.RequestAborted;
                var now = time.GetUtcNow();
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(ct);
                var type = (await uow.Store<ICompetencyLookup>().ListTypesAsync(ct)).FirstOrDefault(t => t.Code == HazcomQualification);
                var workers = (await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(now, ct)).Where(w => w.IsActive).ToList();
                var held = (await uow.Store<ICompetencyLookup>().GetQualificationsAsync(workers.Select(w => new Forge.Kernel.Identity.PersonId(w.PersonId)).ToArray(), ct))
                    .Where(q => q.Code == HazcomQualification).ToDictionary(q => q.PersonId);
                var authorizer = http.RequestServices.GetRequiredService<IForgeAuthorizer>();
                var training = uow.Store<ITrainingRequestStore>();
                var seesRequests = authorizer.Authorize(principal, Permissions.TrainingRequestsRead, ResourceRef.Of("TrainingRequest")).IsPermitted;
                var open = seesRequests
                    ? (await training.ListRequestsAsync(null, true, ct)).Where(r => r.QualificationCode == HazcomQualification).GroupBy(r => r.PersonId).ToDictionary(g => g.Key, g => g.First())
                    : new Dictionary<Guid, TrainingRequest>();
                var requirements = (await training.ListRequirementsAsync(true, ct)).Where(r => r.QualificationCode == HazcomQualification).ToList();
                var people = workers.Select(w =>
                {
                    var q = held.GetValueOrDefault(w.PersonId);
                    var status = q switch
                    {
                        null => "Missing",
                        { Status: QualificationStatus.InTraining } => "InTraining",
                        { Status: QualificationStatus.Suspended } => "Suspended",
                        { Status: QualificationStatus.Revoked } => "Missing",
                        _ when !q.IsValidAt(now) => "Expired",
                        { ExpiresAt: { } exp } when exp <= now.AddDays(30) => "Expiring",
                        _ => "Current",
                    };
                    var request = open.GetValueOrDefault(w.PersonId);
                    return new HazcomTrainingPerson(w.PersonId, w.DisplayName, w.HomeWorkCenterCode, w.Department, status, q?.CertifiedAt, q?.ExpiresAt,
                        request?.RequestId, request?.Status.ToString(), request?.DueOn);
                }).OrderBy(p => p.Status == "Current" ? 1 : 0).ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
                var reviews = (await uow.Store<IHazcomLookup>().ListChemicalsAsync(ct)).Where(c => c.TrainingReviewDue).Select(c => c.PartNumber).ToList();
                return new HazcomTrainingView(HazcomQualification, type?.Name, type?.ValidityDays, requirements, people, reviews);
            }));

        // Request HazCom training for the people chosen (new hazard, expired or missing). Each is an ordinary training request; one already open is skipped.
        api.MapPost("/training/requests", async (HttpContext http, HazcomTrainingRequestBody b, TrainingRequestService s, CancellationToken ct) =>
        {
            var ids = (b.PersonIds ?? []).Distinct().ToList();
            if (ids.Count is 0 or > 500) throw new CommandRejectedException("people_required", "Choose between 1 and 500 people.");
            var (opened, already) = (0, 0);
            foreach (var id in ids)
            {
                var (context, error) = await ContextAsync(http, b.Reason, "hazcom:" + id.ToString("N"), CommandSource.Api);
                if (context is null) return error!;
                try
                {
                    await s.RequestAsync(new RequestTraining(id, HazcomQualification, null, b.DueOn), context, ct);
                    opened++;
                }
                catch (CommandRejectedException ex) when (ex.Code == "training_already_requested")
                {
                    already++;
                }
            }

            var message = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Requested HazCom training for {opened} {(opened == 1 ? "person" : "people")}{(already > 0 ? $"; {already} already had an open request" : string.Empty)}. Technical trainers were notified.");
            return Results.Ok(new { opened, alreadyRequested = already, message });
        });

        // The binder: an index and every approved SDS for the hazardous chemicals kept at a location (or everywhere), as one download.
        api.MapGet("/binder.zip", async (HttpContext http, string? location, IKernelUnitOfWorkFactory unitOfWork, TimeProvider time, CancellationToken ct) =>
        {
            if (await PrincipalAsync(http) is null) return NotRegistered();
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            var register = (await RegisterAsync(uow, ct)).Where(v => v.Chemical.Hazardous
                && (string.IsNullOrWhiteSpace(location) || v.Locations.Any(l => string.Equals(l.LocationCode, location.Trim(), StringComparison.OrdinalIgnoreCase)))).ToList();
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                var index = new StringBuilder();
                index.Append("<!doctype html><meta charset=\"utf-8\"><title>Safety data sheets</title><style>body{font-family:sans-serif}td,th{border:1px solid #999;padding:4px 8px}table{border-collapse:collapse}</style>");
                index.Append(System.Globalization.CultureInfo.InvariantCulture, $"<h1>Safety data sheets{(string.IsNullOrWhiteSpace(location) ? string.Empty : " — " + WebUtility.HtmlEncode(location))}</h1><p>Printed {time.GetUtcNow():yyyy-MM-dd HH:mm} UTC from JISRN Forge.</p>");
                index.Append("<table><tr><th>Part</th><th>Product</th><th>Manufacturer</th><th>Revised</th><th>Signal word</th><th>Pictograms</th><th>PPE</th><th>Kept at</th><th>SDS file</th></tr>");
                foreach (var v in register)
                {
                    string? file = null;
                    if (v.Current is { } sds && await uow.Store<IDocumentLookup>().ReadFileAsync(sds.FileSha256, ct) is { } bytes)
                    {
                        file = $"sds/{v.PartNumber}{Path.GetExtension(sds.FileName ?? ".pdf")}";
                        var entry = zip.CreateEntry(file);
                        await using var s = entry.Open();
                        await s.WriteAsync(bytes, ct);
                    }

                    static string E(string? t) => WebUtility.HtmlEncode(t ?? string.Empty);
                    index.Append(System.Globalization.CultureInfo.InvariantCulture,
                        $"<tr><td>{E(v.PartNumber)}</td><td>{E(v.Current?.ProductName ?? v.Description)}</td><td>{E(v.Current?.Manufacturer)}</td><td>{v.Current?.RevisionDate:yyyy-MM-dd}</td><td>{v.Chemical.SignalWord}</td><td>{E(string.Join(", ", v.Chemical.Pictograms))}</td><td>{E(v.Chemical.Ppe)}</td><td>{E(string.Join(", ", v.Locations.Select(l => l.LocationCode)))}</td><td>{(file is null ? "<b>NO APPROVED SDS</b>" : $"<a href=\"{E(file)}\">{E(file)}</a>")}</td></tr>");
                }

                index.Append("</table>");
                var indexEntry = zip.CreateEntry("index.html");
                await using var w = new StreamWriter(indexEntry.Open(), new UTF8Encoding(false));
                await w.WriteAsync(index.ToString());
            }

            return Results.File(buffer.ToArray(), "application/zip", $"sds-binder{(string.IsNullOrWhiteSpace(location) ? string.Empty : "-" + location.Trim())}.zip");
        });

        // At the kiosk: the hazardous materials of the operation in hand, with their SDS.
        app.MapGroup("/api/shopfloor").RequireAuthorization().MapGet("/sds", (HttpContext http, long order, int? sequence, CancellationToken _) =>
            Read(http, Permissions.ShopFloorRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var o = await uow.Store<IExecutionLookup>().FindOrderAsync(order, http.RequestAborted) ?? throw new CommandRejectedException("order_not_found", $"Order {order} does not exist.");
                var parts = o.Materials.Where(m => sequence is null || m.ConsumedAtSequence is null || m.ConsumedAtSequence == sequence).Select(m => m.ComponentPartNumber).Append(o.PartNumber)
                    .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
                return (await RegisterAsync(uow, http.RequestAborted)).Where(v => v.Chemical.Hazardous && parts.Contains(v.PartNumber)).ToList();
            }));
    }

    private static async Task<IReadOnlyList<ChemicalView>> RegisterAsync(IKernelUnitOfWork uow, CancellationToken ct)
    {
        var hazcom = uow.Store<IHazcomLookup>();
        var chemicals = await hazcom.ListChemicalsAsync(ct);
        var sds = (await hazcom.ListSdsAsync(null, ct)).GroupBy(s => s.PartNumber).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var stock = (await uow.Store<IInventoryLookup>().ListBalancesAsync(null, ct)).Where(b => b.Quantity > 0).GroupBy(b => b.PartNumber)
            .ToDictionary(g => g.Key, g => g.GroupBy(b => b.LocationCode).Select(l => new ChemicalLocation(l.Key, l.Sum(b => b.Quantity))).OrderBy(l => l.LocationCode, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var engineering = uow.Store<IEngineeringLookup>();
        var views = new List<ChemicalView>();
        foreach (var c in chemicals)
        {
            var mine = sds.GetValueOrDefault(c.PartNumber) ?? [];
            views.Add(new ChemicalView(c.PartNumber, (await engineering.FindItemAsync(c.PartNumber, ct))?.Description, c, mine.FirstOrDefault(s => s.Status == SdsStatus.Approved),
                mine.Count(s => s.Status == SdsStatus.Submitted), stock.GetValueOrDefault(c.PartNumber) ?? []));
        }

        return views;
    }
}
