using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Engines;
using Forge.Procurement.Contracts;
using Forge.Projects.Contracts;

namespace Forge.Planning;

/// <summary>Material for one project, and what each line used: issues and returns by line, from the ledger.</summary>
public sealed record ProjectMaterialView(ProjectInfo Project, ProjectMaterialCheck Material, IReadOnlyList<InventoryTransaction> Movements);

/// <summary>
/// Read side of project material (V2.10): the material check of one project from one consistent snapshot,
/// with every ledger movement recorded against its lines. Nothing is stored here.
/// </summary>
public sealed class ProjectPlanningService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer)
{
    public async Task<ProjectMaterialView> MaterialAsync(ForgePrincipal principal, string projectCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decision = authorizer.Authorize(principal, Permissions.ProjectsRead, ResourceRef.Of("ProjectMaterial"));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return await MaterialAsync(uow, projectCode, ct);
    }

    internal static async Task<ProjectMaterialView> MaterialAsync(IKernelUnitOfWork uow, string projectCode, CancellationToken ct)
    {
        var code = BusinessCode.Require(projectCode, "Project");
        var projects = uow.Store<IProjectLookup>();
        var project = await projects.FindProjectAsync(code, ct) ?? throw new CommandRejectedException("project_not_found", $"Project {code} does not exist.");
        var lines = await projects.ListLinesAsync(code, ct);
        var inventory = uow.Store<IInventoryLookup>();
        var moved = await inventory.ListProjectMaterialAsync(code, ct);
        var available = StockPosition.From(await inventory.ListBalancesAsync(null, ct), await inventory.ListLocationsAsync(ct))
            .Where(b => b.AvailableForPlanning)
            .GroupBy(b => b.PartNumber, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(b => b.Quantity), StringComparer.Ordinal);
        var prefix = ProjectSource.Prefix + code + "/";
        var demands = (await uow.Store<IDemandStore>().ListAsync(false, ct)).Where(d => d.SourceKey?.StartsWith(prefix, StringComparison.Ordinal) == true).ToList();
        var requisitions = (await uow.Store<IRequisitionLookup>().ListRequisitionsAsync(false, ct)).Where(q => q.SourceKey.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        var orders = (await uow.Store<IProcurementLookup>().ListPurchaseOrdersAsync(ct)).Where(o => o.Lines.Any(l => l.SourceKey?.StartsWith(prefix, StringComparison.Ordinal) == true)).ToList();
        var check = ProjectMaterialEngine.Check(new ProjectMaterialRequest(project, lines, moved, available, demands, requisitions, orders));

        var parts = lines.Select(l => l.PartNumber).Distinct(StringComparer.Ordinal).ToList();
        var movements = new List<InventoryTransaction>();
        foreach (var part in parts)
        {
            movements.AddRange((await inventory.ListTransactionsAsync(part, null, 1000, ct)).Where(t => t.ProjectCode == code));
        }

        return new ProjectMaterialView(project, check, movements.OrderByDescending(t => t.TransactionId).ToList());
    }
}
