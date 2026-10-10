using System.Globalization;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Projects.Contracts;

namespace Forge.Inventory;

public sealed record IssueToProject(string ProjectCode, int LineNumber, string PartNumber, decimal Quantity, string LocationCode, string? LotNumber);

public sealed record ReturnFromProject(string ProjectCode, int LineNumber, string PartNumber, decimal Quantity, string LocationCode, string? LotNumber);

/// <summary>Material issued to, or returned from, a project line (V2.10).</summary>
public sealed record ProjectMaterialMoved(long TransactionId, string ProjectCode, int LineNumber, string PartNumber, decimal Quantity, string Kind) : IDomainEvent
{
    public static string EventType => nameof(ProjectMaterialMoved);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Project material (V2.10) moves through the ledger like any stock: issued from released stock to a line of an
/// approved project — never more than the line still needs — and returned, never more than was issued to that
/// line. Issues less returns is what the project has used.
/// </summary>
public sealed partial class InventoryService
{
    public Task<CommandResult> IssueToProjectAsync(IssueToProject c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return PostAsync(nameof(IssueToProject), c.PartNumber, c, context, Permissions.InventoryTransact, false, async (uow, item, _) =>
        {
            var (project, line) = await ProjectLineAsync(uow, c.ProjectCode, c.LineNumber, item.PartNumber, ct);
            var location = await CompanyLocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            var quantity = RequireQuantity(item, c.Quantity);
            var issued = await NetIssuedAsync(uow, project.Code, line.LineNumber, ct);
            if (issued + quantity > line.Quantity)
            {
                throw new CommandRejectedException("over_issue", $"{project.Code} line {line.LineNumber} needs {Q(line.Quantity)} {item.PartNumber}; {Q(issued)} already issued, so at most {Q(line.Quantity - issued)} more.");
            }

            await RequireStockAsync(uow, item.PartNumber, location.Code, lot, StockStatus.Released, quantity, ct);
            var txn = new NewTransaction(InventoryTransactionKind.ProjectIssue, item.PartNumber, quantity, location.Code, StockStatus.Released, null, null, lot,
                Reason: $"Project {project.Code} line {line.LineNumber}: {project.Title}", ProjectCode: project.Code, ProjectLine: line.LineNumber);
            return (txn, $"Issued {Q(quantity)} {item.UnitOfMeasure} {item.PartNumber} to project {project.Code} line {line.LineNumber} ({Q(issued + quantity)} of {Q(line.Quantity)}).",
                id => pipeline.Event(new ProjectMaterialMoved(id, project.Code, line.LineNumber, item.PartNumber, quantity, nameof(InventoryTransactionKind.ProjectIssue)), context, SourceContext, StockEntity, item.PartNumber));
        }, ct);
    }

    public Task<CommandResult> ReturnFromProjectAsync(ReturnFromProject c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return PostAsync(nameof(ReturnFromProject), c.PartNumber, c, context, Permissions.InventoryTransact, true, async (uow, item, _) =>
        {
            var code = BusinessCode.Require(c.ProjectCode, "Project");
            var project = await uow.Store<IProjectLookup>().FindProjectAsync(code, ct) ?? throw new CommandRejectedException("project_not_found", $"Project {code} does not exist.");
            var line = (await uow.Store<IProjectLookup>().ListLinesAsync(code, ct)).FirstOrDefault(l => l.LineNumber == c.LineNumber)
                ?? throw new CommandRejectedException("line_not_found", $"{code} has no line {c.LineNumber}.");
            if (line.PartNumber != item.PartNumber)
            {
                throw new CommandRejectedException("line_part", $"{code} line {line.LineNumber} is for {line.PartNumber}, not {item.PartNumber}.");
            }

            var issued = await NetIssuedAsync(uow, code, line.LineNumber, ct);
            var quantity = RequireQuantity(item, c.Quantity);
            if (quantity > issued)
            {
                throw new CommandRejectedException("over_return", $"{code} line {line.LineNumber} has {Q(issued)} {item.PartNumber} issued; return at most that.");
            }

            var location = await CompanyLocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            var txn = new NewTransaction(InventoryTransactionKind.ProjectReturn, item.PartNumber, quantity, null, null, location.Code, StockStatus.Released, lot,
                Reason: context.Reason, ProjectCode: code, ProjectLine: line.LineNumber);
            return (txn, $"Returned {Q(quantity)} {item.UnitOfMeasure} {item.PartNumber} from project {code} line {line.LineNumber} to {location.Code}.",
                id => pipeline.Event(new ProjectMaterialMoved(id, code, line.LineNumber, item.PartNumber, quantity, nameof(InventoryTransactionKind.ProjectReturn)), context, SourceContext, StockEntity, item.PartNumber));
        }, ct);
    }

    private static async Task<(ProjectInfo Project, ProjectLine Line)> ProjectLineAsync(IKernelUnitOfWork uow, string rawCode, int lineNumber, string part, CancellationToken ct)
    {
        var code = BusinessCode.Require(rawCode, "Project");
        var lookup = uow.Store<IProjectLookup>();
        var project = await lookup.FindProjectAsync(code, ct) ?? throw new CommandRejectedException("project_not_found", $"Project {code} does not exist.");
        if (!project.IsOpen)
        {
            throw new CommandRejectedException("project_not_open", $"Project {code} is {project.Status}; material is issued only to approved projects.");
        }

        var line = (await lookup.ListLinesAsync(code, ct)).FirstOrDefault(l => l.LineNumber == lineNumber)
            ?? throw new CommandRejectedException("line_not_found", $"{code} has no line {lineNumber}.");
        if (line.Status != ProjectLineStatus.Active)
        {
            throw new CommandRejectedException("line_not_active", $"{code} line {lineNumber} is cancelled.");
        }

        return line.PartNumber == part ? (project, line) : throw new CommandRejectedException("line_part", $"{code} line {lineNumber} is for {line.PartNumber}, not {part}.");
    }

    private static async Task<decimal> NetIssuedAsync(IKernelUnitOfWork uow, string code, int lineNumber, CancellationToken ct) =>
        (await uow.Store<IInventoryLookup>().ListProjectMaterialAsync(code, ct)).Where(m => m.LineNumber == lineNumber).Sum(m => m.NetIssued);

    private static string Q(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
