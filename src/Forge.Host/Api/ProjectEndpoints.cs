using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Maintenance;
using Forge.Maintenance.Contracts;
using Forge.Planning;
using Forge.Procurement;
using Forge.Procurement.Contracts;
using Forge.Projects;
using Forge.Projects.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ProjectRequest(string Code, string Title, ProjectType Type, ProjectFunding Funding, string? Description, string? Owner, decimal? Budget, DateOnly StartDate, DateOnly EndDate, string? FinancialReference, string? Reason);

public sealed record ProjectChangeRequest(string Title, string? Description, decimal? Budget, DateOnly StartDate, DateOnly EndDate, string? FinancialReference, string? Reason);

public sealed record ProjectLineRequest(string PartNumber, decimal Quantity, DateOnly NeedDate, string? Description, string? Reason);

public sealed record ProjectLineChangeRequest(decimal Quantity, DateOnly NeedDate, string? Reason);

public sealed record ProjectTaskRequest(string Title, DateOnly DueDate, bool Milestone, string? Reason);

public sealed record ProjectTaskDoneRequest(DateOnly CompletedOn, string? Reason);

public sealed record ProjectIssueRequest(int LineNumber, string PartNumber, decimal Quantity, string LocationCode, string? LotNumber, string? Reason);

public sealed record ProjectOutageRequest(string MachineCode, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Description, string? Reason);

/// <summary>A project in the list: its header, material lines and what waits for a decision.</summary>
public sealed record ProjectSummary(ProjectInfo Project, int Lines, int PendingChanges, int OpenTasks);

/// <summary>A purchase line bought for one of the project's lines.</summary>
public sealed record ProjectPurchase(long PoNumber, int PoLine, string Supplier, PurchaseOrderStatus OrderStatus, PurchaseLineStatus LineStatus, string PartNumber, decimal Quantity, DateOnly PromisedDate, string SourceKey, long? RequisitionId);

public sealed record ProjectDetail(
    ProjectInfo Project,
    IReadOnlyList<ProjectLine> Lines,
    IReadOnlyList<ProjectTask> Tasks,
    IReadOnlyList<ProjectChange> Changes,
    ProjectMaterialView Material,
    IReadOnlyList<ProjectPurchase> Purchases,
    IReadOnlyList<RequisitionInfo> Requisitions,
    IReadOnlyList<OutageInfo> Outages);

/// <summary>What asking for a project's shortages did, line by line.</summary>
public sealed record MaterialRequestLine(int LineNumber, string PartNumber, decimal Shortage, string Outcome, string? DemandId, string? RequisitionId);

/// <summary>
/// V2.10: facility and capital projects — approval and controlled changes, material lines checked against stock,
/// shortages turned into project demand and requisitions sourced to the line, issues and returns against lines,
/// and outages the project needs, proposed like any other.
/// </summary>
public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/projects").RequireAuthorization();

        api.MapGet("/", (HttpContext http, CancellationToken _) =>
            Read(http, Permissions.ProjectsRead, async _ =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var lookup = uow.Store<IProjectLookup>();
                var ct = http.RequestAborted;
                var lines = (await lookup.ListLinesAsync(null, ct)).Where(l => l.Status == ProjectLineStatus.Active).ToLookup(l => l.ProjectCode);
                var pending = (await lookup.ListChangesAsync(null, true, ct)).ToLookup(c => c.ProjectCode);
                var list = new List<ProjectSummary>();
                foreach (var p in await lookup.ListProjectsAsync(ct))
                {
                    var tasks = await lookup.ListTasksAsync(p.Code, ct);
                    list.Add(new ProjectSummary(p, lines[p.Code].Count(), pending[p.Code].Count(), tasks.Count(t => t.CompletedOn is null)));
                }

                return list;
            }));

        api.MapGet("/{code}", async (HttpContext http, string code, ProjectPlanningService planning, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null)
            {
                return NotRegistered();
            }

            var material = await planning.MaterialAsync(principal, code, ct);
            var project = material.Project;
            await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(ct);
            var lookup = uow.Store<IProjectLookup>();
            var prefix = ProjectSource.Prefix + project.Code + "/";
            var purchases = (await uow.Store<IProcurementLookup>().ListPurchaseOrdersAsync(ct))
                .SelectMany(o => o.Lines.Where(l => l.SourceKey?.StartsWith(prefix, StringComparison.Ordinal) == true)
                    .Select(l => new ProjectPurchase(o.PoNumber, l.LineNumber, o.Supplier, o.Status, l.Status, l.PartNumber, l.Quantity, l.PromisedDate, l.SourceKey!, l.RequisitionId)))
                .OrderBy(p => p.PoNumber).ThenBy(p => p.PoLine).ToList();
            var requisitions = (await uow.Store<IRequisitionLookup>().ListRequisitionsAsync(false, ct)).Where(r => r.SourceKey.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            var outages = (await uow.Store<IMaintenanceLookup>().ListOutagesAsync(true, ct)).Where(o => o.ProjectCode == project.Code).ToList();
            return Results.Ok(new ProjectDetail(project, await lookup.ListLinesAsync(project.Code, ct), await lookup.ListTasksAsync(project.Code, ct),
                await lookup.ListChangesAsync(project.Code, false, ct), material, purchases, requisitions, outages));
        });

        api.MapPost("/", (HttpContext http, ProjectRequest b, ProjectService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateAsync(new CreateProject(b.Code, b.Title, b.Type, b.Funding, b.Description, b.Owner, b.Budget, b.StartDate, b.EndDate, b.FinancialReference), c, ct)));
        api.MapPost("/{code}", (HttpContext http, string code, ProjectChangeRequest b, ProjectService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ChangeAsync(new ChangeProject(code, b.Title, b.Description, b.Budget, b.StartDate, b.EndDate, b.FinancialReference), c, ct)));
        api.MapPost("/{code}/approve", (HttpContext http, string code, ReasonRequest b, ProjectService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveAsync(new ApproveProject(code), c, ct)));
        api.MapPost("/{code}/complete", (HttpContext http, string code, ReasonRequest b, ProjectService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CloseAsync(new CloseProject(code, false), c, ct)));
        api.MapPost("/{code}/cancel", (HttpContext http, string code, ReasonRequest b, ProjectService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CloseAsync(new CloseProject(code, true), c, ct)));

        api.MapPost("/{code}/lines", (HttpContext http, string code, ProjectLineRequest b, ProjectService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddLineAsync(new AddProjectLine(code, b.PartNumber, b.Quantity, b.NeedDate, b.Description), c, ct)));

        // A line is never reduced below, or cancelled with, material still issued to it: return the material first.
        api.MapPost("/{code}/lines/{line:int}", async (HttpContext http, string code, int line, ProjectLineChangeRequest b, ProjectService s, IKernelUnitOfWorkFactory uows, CancellationToken ct) =>
        {
            await RequireIssuedAtMostAsync(uows, code, line, b.Quantity, ct);
            return await Command(http, b.Reason, c => s.ChangeLineAsync(new ChangeProjectLine(code, line, b.Quantity, b.NeedDate), c, ct));
        });
        api.MapPost("/{code}/lines/{line:int}/cancel", async (HttpContext http, string code, int line, ReasonRequest b, ProjectService s, IKernelUnitOfWorkFactory uows, CancellationToken ct) =>
        {
            await RequireIssuedAtMostAsync(uows, code, line, 0, ct);
            return await Command(http, b.Reason, c => s.CancelLineAsync(new CancelProjectLine(code, line), c, ct));
        });

        api.MapPost("/changes/{id:long}/approve", async (HttpContext http, long id, ReasonRequest b, ProjectService s, IKernelUnitOfWorkFactory uows, CancellationToken ct) =>
        {
            await using (var uow = await uows.BeginSnapshotAsync(ct))
            {
                if ((await uow.Store<IProjectLookup>().ListChangesAsync(null, true, ct)).FirstOrDefault(c => c.ChangeId == id) is { Kind: ProjectChangeKind.ChangeLine or ProjectChangeKind.CancelLine } change)
                {
                    await RequireIssuedAtMostAsync(uows, change.ProjectCode, change.LineNumber!.Value, change.Kind == ProjectChangeKind.CancelLine ? 0 : change.Quantity!.Value, ct);
                }
            }

            return await Command(http, b.Reason, c => s.DecideChangeAsync(new DecideProjectChange(id, true), c, ct));
        });
        api.MapPost("/changes/{id:long}/reject", (HttpContext http, long id, ReasonRequest b, ProjectService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DecideChangeAsync(new DecideProjectChange(id, false), c, ct)));

        api.MapPost("/{code}/tasks", (HttpContext http, string code, ProjectTaskRequest b, ProjectService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddTaskAsync(new AddProjectTask(code, b.Title, b.DueDate, b.Milestone), c, ct)));
        api.MapPost("/tasks/{id:long}/complete", (HttpContext http, long id, ProjectTaskDoneRequest b, ProjectService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CompleteTaskAsync(new CompleteProjectTask(id, b.CompletedOn), c, ct)));

        api.MapPost("/{code}/issues", (HttpContext http, string code, ProjectIssueRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.IssueToProjectAsync(new IssueToProject(code, b.LineNumber, b.PartNumber, b.Quantity, b.LocationCode, b.LotNumber), c, ct)));
        api.MapPost("/{code}/returns", (HttpContext http, string code, ProjectIssueRequest b, InventoryService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReturnFromProjectAsync(new ReturnFromProject(code, b.LineNumber, b.PartNumber, b.Quantity, b.LocationCode, b.LotNumber), c, ct)));

        api.MapPost("/{code}/outages", (HttpContext http, string code, ProjectOutageRequest b, MaintenanceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ProposeProjectOutageAsync(new ProposeProjectOutage(code, b.MachineCode, b.StartsAt, b.EndsAt, b.Description), c, ct)));

        // Scenario 6: shortages become project demand (for MRP) and requisitions (for the buyer), sourced to the line.
        api.MapPost("/{code}/request-material", async (HttpContext http, string code, ReasonRequest b, ProjectPlanningService planning, DemandService demands, RequisitionService requisitions,
            IKernelUnitOfWorkFactory uows, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null)
            {
                return NotRegistered();
            }

            var view = await planning.MaterialAsync(principal, code, ct);
            var project = view.Project;
            if (!project.IsOpen)
            {
                throw new CommandRejectedException("project_not_open", $"Project {project.Code} is {project.Status}; material is requested for approved projects.");
            }

            var kind = project.Type == ProjectType.Capital ? DemandKind.CapitalProject : DemandKind.FacilityProject;
            var source = project.Type == ProjectType.Capital ? PurchaseSourceKind.CapitalProject : PurchaseSourceKind.FacilityProject;
            var lines = new List<MaterialRequestLine>();
            foreach (var line in view.Material.Lines.Where(l => l.Status == "Short"))
            {
                // The demand stands for the whole line (MRP nets what is issued to it); the requisition asks for the shortage.
                string? demandId;
                DemandInfo? existing;
                await using (var read = await uows.BeginSnapshotAsync(ct))
                {
                    existing = await read.Store<IDemandStore>().FindBySourceAsync(line.SourceKey, ct);
                }

                if (existing is null)
                {
                    var (context, error) = await ContextAsync(http, b.Reason, $"demand:{line.LineNumber}", CommandSource.Api);
                    if (context is null)
                    {
                        return error!;
                    }

                    demandId = (await demands.RecordAsync(new RecordDemand(line.PartNumber, line.Required, line.NeedDate, 4, kind, null, $"{project.Code} line {line.LineNumber}", null, line.SourceKey), context, ct)).EntityId;
                }
                else
                {
                    demandId = existing.DemandId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (existing.Status == DemandStatus.Open && (existing.Quantity != line.Required || existing.NeedDate != line.NeedDate))
                    {
                        var (context, error) = await ContextAsync(http, b.Reason ?? "Project line changed", $"demand-change:{line.LineNumber}", CommandSource.Api);
                        if (context is null)
                        {
                            return error!;
                        }

                        await demands.ChangeAsync(new ChangeDemand(existing.DemandId, line.Required, line.NeedDate, existing.Priority), context, ct);
                    }
                }

                var (raise, raiseError) = await ContextAsync(http, b.Reason, $"requisition:{line.LineNumber}", CommandSource.Api);
                if (raise is null)
                {
                    return raiseError!;
                }

                try
                {
                    var label = $"{project.Code} line {line.LineNumber}: {line.Description ?? line.PartNumber} ({project.Title})";
                    var result = await requisitions.RaiseAsync(new RaiseRequisition(line.PartNumber, line.Shortage, line.NeedDate, source, line.SourceKey, label,
                        long.Parse(demandId!, System.Globalization.CultureInfo.InvariantCulture), null, null, null, null), raise, ct);
                    lines.Add(new MaterialRequestLine(line.LineNumber, line.PartNumber, line.Shortage, "Requested", demandId, result.EntityId));
                }
                catch (CommandRejectedException ex) when (ex.Code == "requisition_exists")
                {
                    lines.Add(new MaterialRequestLine(line.LineNumber, line.PartNumber, line.Shortage, ex.Message, demandId, null));
                }
            }

            return Results.Ok(new { projectCode = project.Code, lines, summary = lines.Count == 0 ? "Nothing is short." : $"{lines.Count(l => l.RequisitionId is not null)} requisition(s) raised." });
        });
    }

    private static async Task RequireIssuedAtMostAsync(IKernelUnitOfWorkFactory uows, string rawCode, int line, decimal quantity, CancellationToken ct)
    {
        var code = (rawCode ?? string.Empty).Trim().ToUpperInvariant();
        await using var uow = await uows.BeginSnapshotAsync(ct);
        var issued = (await uow.Store<IInventoryLookup>().ListProjectMaterialAsync(code, ct)).Where(m => m.LineNumber == line).Sum(m => m.NetIssued);
        if (issued > quantity)
        {
            throw new CommandRejectedException("below_issued", $"{code} line {line} has {issued.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} issued; return material before reducing or cancelling the line.");
        }
    }
}
