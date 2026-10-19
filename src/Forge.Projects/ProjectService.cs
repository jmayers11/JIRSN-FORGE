using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Projects.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Projects;

public sealed record CreateProject(string Code, string Title, ProjectType Type, ProjectFunding Funding, string? Description, string? Owner, decimal? Budget, DateOnly StartDate, DateOnly EndDate, string? FinancialReference);

/// <summary>Title, description, budget, dates and financial reference. Applied at once on a draft; requested with a reason on an approved project.</summary>
public sealed record ChangeProject(string Code, string Title, string? Description, decimal? Budget, DateOnly StartDate, DateOnly EndDate, string? FinancialReference);

public sealed record AddProjectLine(string Code, string PartNumber, decimal Quantity, DateOnly NeedDate, string? Description);

public sealed record ChangeProjectLine(string Code, int LineNumber, decimal Quantity, DateOnly NeedDate);

public sealed record CancelProjectLine(string Code, int LineNumber);

public sealed record ApproveProject(string Code);

public sealed record DecideProjectChange(long ChangeId, bool Approve);

public sealed record AddProjectTask(string Code, string Title, DateOnly DueDate, bool Milestone);

public sealed record CompleteProjectTask(long TaskId, DateOnly CompletedOn);

/// <summary>Completes (nothing more is bought or issued) or cancels a project.</summary>
public sealed record CloseProject(string Code, bool Cancel);

public sealed record FacilityProjectCreated(string Code, string Type, string Funding, string Title) : IDomainEvent
{
    public static string EventType => nameof(FacilityProjectCreated);

    public static int SchemaVersion => 1;
}

public sealed record FacilityProjectChanged(string Code, string Change, long? ChangeId, int? LineNumber) : IDomainEvent
{
    public static string EventType => nameof(FacilityProjectChanged);

    public static int SchemaVersion => 1;
}

public sealed record FacilityProjectCompleted(string Code, string Status) : IDomainEvent
{
    public static string EventType => nameof(FacilityProjectCompleted);

    public static int SchemaVersion => 1;
}

public interface IProjectsStore
{
    Task<bool> InsertProjectAsync(ProjectInfo project, CancellationToken ct);

    Task LockProjectAsync(string code, CancellationToken ct);

    Task UpdateProjectAsync(ProjectInfo project, CancellationToken ct);

    Task InsertLineAsync(ProjectLine line, CancellationToken ct);

    Task UpdateLineAsync(ProjectLine line, CancellationToken ct);

    Task<long> InsertTaskAsync(ProjectTask task, CancellationToken ct);

    Task<ProjectTask?> FindTaskAsync(long taskId, CancellationToken ct);

    Task UpdateTaskAsync(ProjectTask task, CancellationToken ct);

    Task<long> InsertChangeAsync(ProjectChange change, CancellationToken ct);

    Task<ProjectChange?> FindChangeAsync(long changeId, CancellationToken ct);

    Task DecideChangeAsync(ProjectChange change, CancellationToken ct);
}

/// <summary>
/// Facility and capital projects (V2.10). A draft is edited by whoever manages projects; a different person with
/// project approval authority approves it, with a budget and — for capital funding — a financial reference.
/// From then on every change to what the project is (header or material) is requested with a reason and
/// applied only when someone other than the requester approves it. Material lines carry the source key that
/// demand, requisitions, purchase lines, receipts and issues are recorded against.
/// </summary>
public sealed class ProjectService(CommandPipeline pipeline, IKernelUnitOfWorkFactory unitOfWork, ISiteClock siteClock)
{
    public const string SourceContext = "Projects";
    private const string Entity = "Project";
    private const string ChangeEntity = "ProjectChange";

    public Task<CommandResult> CreateAsync(CreateProject c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Project");
        var spec = new CommandSpec(SourceContext, nameof(CreateProject), Permissions.ProjectsManage, ResourceRef.Of(Entity, code));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var (start, end) = Dates(c.StartDate, c.EndDate);
            var project = new ProjectInfo(code, BusinessCode.RequireName(c.Title, "Project"), c.Type, c.Funding, Optional(c.Description, 2000), Optional(c.Owner, 200), Budget(c.Budget),
                start, end, Optional(c.FinancialReference, 60), ProjectStatus.Draft, context.Actor, now, null, null, null, null, null);
            if (!await uow.Store<IProjectsStore>().InsertProjectAsync(project, ct))
            {
                throw new CommandRejectedException("project_exists", $"Project {code} already exists.");
            }

            return new CommandOutcome(
                CommandResult.Ok(code, $"Project {code} ({c.Type}, {c.Funding.ToString().ToLowerInvariant()} funding) created as a draft. A different person with project approval authority approves it."),
                [pipeline.Event(new FacilityProjectCreated(code, c.Type.ToString(), c.Funding.ToString(), project.Title), context, SourceContext, Entity, code)],
                new AuditChange(Entity, code, null, CanonicalJson.Serialize(project)));
        }, ct);
    }

    public Task<CommandResult> ChangeAsync(ChangeProject c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return EditAsync(nameof(ChangeProject), c.Code, c, context, async (uow, project, now) =>
        {
            var (start, end) = Dates(c.StartDate, c.EndDate);
            var title = BusinessCode.RequireName(c.Title, "Project");
            var next = project with { Title = title, Description = Optional(c.Description, 2000), Budget = Budget(c.Budget), StartDate = start, EndDate = end, FinancialReference = Optional(c.FinancialReference, 60) };
            if (next == project)
            {
                throw new CommandRejectedException("no_change", $"Project {project.Code} already reads like that.");
            }

            if (project.Status == ProjectStatus.Draft)
            {
                await uow.Store<IProjectsStore>().UpdateProjectAsync(next, ct);
                return Applied(context, project.Code, "Header", null, $"Project {project.Code} changed.", CanonicalJson.Serialize(project), CanonicalJson.Serialize(next));
            }

            if (next.Funding == ProjectFunding.Capital && next.FinancialReference is null)
            {
                throw new CommandRejectedException("financial_reference_required", "Capital funding keeps its financial reference.");
            }

            return await RequestAsync(uow, context, now, new ProjectChange(0, project.Code, ProjectChangeKind.Header, null, null, null, null, null,
                title, next.Description, next.Budget, start, end, next.FinancialReference, context.Reason!, ProjectChangeStatus.Pending, context.Actor, now, null, null, null), ct);
        }, ct);
    }

    public Task<CommandResult> AddLineAsync(AddProjectLine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return EditAsync(nameof(AddProjectLine), c.Code, c, context, async (uow, project, now) =>
        {
            var part = await PartAsync(uow, c.PartNumber, ct);
            var quantity = Quantity(c.Quantity);
            var description = Optional(c.Description, 500);
            var lines = await uow.Store<IProjectLookup>().ListLinesAsync(project.Code, ct);
            if (project.Status == ProjectStatus.Draft)
            {
                var line = new ProjectLine(project.Code, lines.Count == 0 ? 1 : lines.Max(l => l.LineNumber) + 1, part, quantity, c.NeedDate, description, ProjectLineStatus.Active);
                await uow.Store<IProjectsStore>().InsertLineAsync(line, ct);
                return Applied(context, project.Code, "LineAdded", line.LineNumber, $"Project {project.Code} line {line.LineNumber}: {Q(quantity)} {part} by {c.NeedDate:yyyy-MM-dd}.", null, CanonicalJson.Serialize(line));
            }

            return await RequestAsync(uow, context, now, new ProjectChange(0, project.Code, ProjectChangeKind.AddLine, 0, part, quantity, c.NeedDate, description,
                null, null, null, null, null, null, context.Reason!, ProjectChangeStatus.Pending, context.Actor, now, null, null, null), ct);
        }, ct);
    }

    public Task<CommandResult> ChangeLineAsync(ChangeProjectLine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return EditAsync(nameof(ChangeProjectLine), c.Code, c, context, async (uow, project, now) =>
        {
            var line = await ActiveLineAsync(uow, project.Code, c.LineNumber, ct);
            var quantity = Quantity(c.Quantity);
            if (line.Quantity == quantity && line.NeedDate == c.NeedDate)
            {
                throw new CommandRejectedException("no_change", $"Line {line.LineNumber} already needs {Q(quantity)} by {c.NeedDate:yyyy-MM-dd}.");
            }

            if (project.Status == ProjectStatus.Draft)
            {
                var next = line with { Quantity = quantity, NeedDate = c.NeedDate };
                await uow.Store<IProjectsStore>().UpdateLineAsync(next, ct);
                return Applied(context, project.Code, "LineChanged", line.LineNumber, $"Project {project.Code} line {line.LineNumber}: {Q(quantity)} {line.PartNumber} by {c.NeedDate:yyyy-MM-dd}.", CanonicalJson.Serialize(line), CanonicalJson.Serialize(next));
            }

            return await RequestAsync(uow, context, now, new ProjectChange(0, project.Code, ProjectChangeKind.ChangeLine, line.LineNumber, line.PartNumber, quantity, c.NeedDate, null,
                null, null, null, null, null, null, context.Reason!, ProjectChangeStatus.Pending, context.Actor, now, null, null, null), ct);
        }, ct);
    }

    public Task<CommandResult> CancelLineAsync(CancelProjectLine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return EditAsync(nameof(CancelProjectLine), c.Code, c, context, async (uow, project, now) =>
        {
            var line = await ActiveLineAsync(uow, project.Code, c.LineNumber, ct);
            if (project.Status == ProjectStatus.Draft)
            {
                var next = line with { Status = ProjectLineStatus.Cancelled };
                await uow.Store<IProjectsStore>().UpdateLineAsync(next, ct);
                return Applied(context, project.Code, "LineCancelled", line.LineNumber, $"Project {project.Code} line {line.LineNumber} cancelled.", nameof(ProjectLineStatus.Active), nameof(ProjectLineStatus.Cancelled));
            }

            return await RequestAsync(uow, context, now, new ProjectChange(0, project.Code, ProjectChangeKind.CancelLine, line.LineNumber, line.PartNumber, null, null, null,
                null, null, null, null, null, null, context.Reason!, ProjectChangeStatus.Pending, context.Actor, now, null, null, null), ct);
        }, ct);
    }

    public async Task<CommandResult> ApproveAsync(ApproveProject c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Project");
        decimal? budget;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
            budget = (await read.Store<IProjectLookup>().FindProjectAsync(code, ct))?.Budget;
        var spec = new CommandSpec(SourceContext, nameof(ApproveProject), Permissions.ProjectsApprove,
            new ResourceRef(Entity, code, Amount: budget, Currency: "USD"), ReasonRequired: true);
        return await pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var project = await FindAsync(uow, code, ct);
            if (project.Budget != budget)
                throw new CommandRejectedException("project_budget_changed", "The project budget changed during approval. Review its current amount and retry.");
            if (project.Status != ProjectStatus.Draft)
            {
                throw new CommandRejectedException("not_draft", $"Project {code} is {project.Status}; only a draft is approved.");
            }

            if (project.CreatedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You created this project; a different person approves it.");
            }

            if (project.Budget is not > 0)
            {
                throw new CommandRejectedException("budget_required", $"Project {code} has no budget; a project is approved against one.");
            }

            if (project.Funding == ProjectFunding.Capital && project.FinancialReference is null)
            {
                throw new CommandRejectedException("financial_reference_required", $"Project {code} is capital-funded; record its capital request (financial reference) first.");
            }

            var approved = project with { Status = ProjectStatus.Approved, ApprovedBy = context.Actor, ApprovedAt = now };
            await uow.Store<IProjectsStore>().UpdateProjectAsync(approved, ct);
            var lines = (await uow.Store<IProjectLookup>().ListLinesAsync(code, ct)).Count(l => l.Status == ProjectLineStatus.Active);
            return new CommandOutcome(
                CommandResult.Ok(code, $"Project {code} approved with a budget of {project.Budget.GetValueOrDefault().ToString("N2", CultureInfo.InvariantCulture)} and {lines} material line(s). Changes from now on need a reason and a second person."),
                [pipeline.Event(new FacilityProjectChanged(code, "Approved", null, null), context, SourceContext, Entity, code)],
                new AuditChange(Entity, code, nameof(ProjectStatus.Draft), nameof(ProjectStatus.Approved)));
        }, ct);
    }

    public async Task<CommandResult> DecideChangeAsync(DecideProjectChange c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.ChangeId.ToString(CultureInfo.InvariantCulture);
        decimal? budget = null;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            var proposed = await read.Store<IProjectsStore>().FindChangeAsync(c.ChangeId, ct);
            if (proposed is not null)
            {
                var project = await read.Store<IProjectLookup>().FindProjectAsync(proposed.ProjectCode, ct);
                budget = proposed.Kind == ProjectChangeKind.Header ? proposed.Budget : project?.Budget;
            }
        }
        var spec = new CommandSpec(SourceContext, nameof(DecideProjectChange), Permissions.ProjectsApprove,
            new ResourceRef(ChangeEntity, key, Amount: budget, Currency: "USD"), ReasonRequired: true);
        return await pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IProjectsStore>();
            var change = await store.FindChangeAsync(c.ChangeId, ct) ?? throw new CommandRejectedException("change_not_found", $"Project change {key} does not exist.");
            if (change.Status != ProjectChangeStatus.Pending)
            {
                throw new CommandRejectedException("change_decided", $"Project change {key} is {change.Status}.");
            }

            if (change.RequestedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You requested this change; a different person decides it.");
            }

            var project = await FindAsync(uow, change.ProjectCode, ct);
            // The project lock also serializes decisions on its pending changes.
            change = await store.FindChangeAsync(c.ChangeId, ct) ?? throw new CommandRejectedException("change_not_found", $"Project change {key} does not exist.");
            if (change.Status != ProjectChangeStatus.Pending)
                throw new CommandRejectedException("change_decided", $"Project change {key} is {change.Status}.");
            if ((change.Kind == ProjectChangeKind.Header ? change.Budget : project.Budget) != budget)
                throw new CommandRejectedException("project_budget_changed", "The approval amount changed. Review the current project and retry.");
            if (project.Status != ProjectStatus.Approved)
            {
                throw new CommandRejectedException("project_closed", $"Project {project.Code} is {project.Status}.");
            }

            var decided = change with { Status = c.Approve ? ProjectChangeStatus.Approved : ProjectChangeStatus.Rejected, DecidedBy = context.Actor, DecidedAt = now, DecisionReason = context.Reason };
            string applied;
            int? lineNumber = change.LineNumber;
            if (!c.Approve)
            {
                applied = $"Project change {key} rejected; {project.Code} stays as it was.";
            }
            else
            {
                (applied, lineNumber) = await ApplyAsync(uow, project, change, ct);
            }

            await store.DecideChangeAsync(decided, ct);
            return new CommandOutcome(
                CommandResult.Ok(key, applied),
                [pipeline.Event(new FacilityProjectChanged(project.Code, c.Approve ? $"{change.Kind}Approved" : $"{change.Kind}Rejected", change.ChangeId, lineNumber), context, SourceContext, Entity, project.Code)],
                new AuditChange(ChangeEntity, key, nameof(ProjectChangeStatus.Pending), decided.Status.ToString()));
        }, ct);
    }

    public Task<CommandResult> AddTaskAsync(AddProjectTask c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Project");
        var spec = new CommandSpec(SourceContext, nameof(AddProjectTask), Permissions.ProjectsManage, ResourceRef.Of(Entity, code));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var project = await FindAsync(uow, code, ct);
            if (project.Status is ProjectStatus.Completed or ProjectStatus.Cancelled)
            {
                throw new CommandRejectedException("project_closed", $"Project {code} is {project.Status}.");
            }

            var task = new ProjectTask(0, code, BusinessCode.RequireName(c.Title, "Task"), c.DueDate, c.Milestone, null, null, context.Actor, now);
            var id = await uow.Store<IProjectsStore>().InsertTaskAsync(task, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(key, $"{(c.Milestone ? "Milestone" : "Task")} {key} added to {code}, due {c.DueDate:yyyy-MM-dd}."),
                [pipeline.Event(new FacilityProjectChanged(code, "TaskAdded", null, null), context, SourceContext, Entity, code)],
                new AuditChange("ProjectTask", key, null, CanonicalJson.Serialize(task with { TaskId = id })));
        }, ct);
    }

    public Task<CommandResult> CompleteTaskAsync(CompleteProjectTask c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.TaskId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(CompleteProjectTask), Permissions.ProjectsManage, ResourceRef.Of("ProjectTask", key));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IProjectsStore>();
            var task = await store.FindTaskAsync(c.TaskId, ct) ?? throw new CommandRejectedException("task_not_found", $"Task {key} does not exist.");
            if (task.CompletedOn is not null)
            {
                throw new CommandRejectedException("task_done", $"Task {key} was completed on {task.CompletedOn:yyyy-MM-dd}.");
            }

            if (c.CompletedOn > siteClock.DateAt(now).AddDays(1))
            {
                throw new CommandRejectedException("completed_in_future", "A task is completed on a day that has come.");
            }

            var done = task with { CompletedOn = c.CompletedOn, CompletedBy = context.Actor };
            await store.UpdateTaskAsync(done, ct);
            return new CommandOutcome(
                CommandResult.Ok(key, $"{task.Title} completed {c.CompletedOn:yyyy-MM-dd}."),
                [pipeline.Event(new FacilityProjectChanged(task.ProjectCode, "TaskCompleted", null, null), context, SourceContext, Entity, task.ProjectCode)],
                new AuditChange("ProjectTask", key, null, c.CompletedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }, ct);
    }

    public async Task<CommandResult> CloseAsync(CloseProject c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Project");
        decimal? budget;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
            budget = (await read.Store<IProjectLookup>().FindProjectAsync(code, ct))?.Budget;
        var spec = new CommandSpec(SourceContext, nameof(CloseProject), c.Cancel ? Permissions.ProjectsApprove : Permissions.ProjectsManage,
            new ResourceRef(Entity, code, Amount: budget, Currency: "USD"), ReasonRequired: true);
        return await pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var project = await FindAsync(uow, code, ct);
            if (project.Budget != budget)
                throw new CommandRejectedException("project_budget_changed", "The project budget changed during closeout. Review its current amount and retry.");
            if (project.Status is ProjectStatus.Completed or ProjectStatus.Cancelled)
            {
                throw new CommandRejectedException("project_closed", $"Project {code} is already {project.Status}.");
            }

            if (!c.Cancel && project.Status != ProjectStatus.Approved)
            {
                throw new CommandRejectedException("not_approved", $"Project {code} is a draft; a draft is cancelled, not completed.");
            }

            if ((await uow.Store<IProjectLookup>().ListChangesAsync(code, true, ct)).Count > 0)
            {
                throw new CommandRejectedException("changes_pending", $"Project {code} has changes waiting for a decision; decide them first.");
            }

            var status = c.Cancel ? ProjectStatus.Cancelled : ProjectStatus.Completed;
            await uow.Store<IProjectsStore>().UpdateProjectAsync(project with { Status = status, ClosedBy = context.Actor, ClosedAt = now, CloseReason = context.Reason }, ct);
            return new CommandOutcome(
                CommandResult.Ok(code, $"Project {code} {status.ToString().ToLowerInvariant()}. Nothing more is bought for or issued to it."),
                [pipeline.Event(new FacilityProjectCompleted(code, status.ToString()), context, SourceContext, Entity, code)],
                new AuditChange(Entity, code, project.Status.ToString(), status.ToString()));
        }, ct);
    }

    /// <summary>Shared shape of the edits: on a draft they apply; on an approved project they become a change request with a reason.</summary>
    private Task<CommandResult> EditAsync<TCommand>(string type, string rawCode, TCommand command, CommandContext context, Func<IKernelUnitOfWork, ProjectInfo, DateTimeOffset, Task<CommandOutcome>> body, CancellationToken ct)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(context);
        var code = BusinessCode.Require(rawCode, "Project");
        var spec = new CommandSpec(SourceContext, type, Permissions.ProjectsManage, ResourceRef.Of(Entity, code));
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var project = await FindAsync(uow, code, ct);
            if (project.Status is ProjectStatus.Completed or ProjectStatus.Cancelled)
            {
                throw new CommandRejectedException("project_closed", $"Project {code} is {project.Status}.");
            }

            if (project.Status == ProjectStatus.Approved && string.IsNullOrWhiteSpace(context.Reason))
            {
                throw new CommandRejectedException("reason_required", $"Project {code} is approved: say why it changes; a different person approves the change.");
            }

            return await body(uow, project, now);
        }, ct);
    }

    private async Task<CommandOutcome> RequestAsync(IKernelUnitOfWork uow, CommandContext context, DateTimeOffset now, ProjectChange change, CancellationToken ct)
    {
        var pending = await uow.Store<IProjectLookup>().ListChangesAsync(change.ProjectCode, true, ct);
        if (change.Kind == ProjectChangeKind.Header ? pending.Any(p => p.Kind == ProjectChangeKind.Header)
            : change.Kind != ProjectChangeKind.AddLine && pending.Any(p => p.LineNumber == change.LineNumber && p.Kind != ProjectChangeKind.AddLine))
        {
            throw new CommandRejectedException("change_pending", $"A change to {(change.LineNumber is { } n and > 0 ? $"line {n} of " : string.Empty)}{change.ProjectCode} is already waiting for a decision.");
        }

        // An added line has no number until it is approved (0 here); the event of the approval carries the number.
        var stored = change;
        var id = await uow.Store<IProjectsStore>().InsertChangeAsync(stored, ct);
        var key = id.ToString(CultureInfo.InvariantCulture);
        return new CommandOutcome(
            CommandResult.Ok(key, $"Change {key} to project {change.ProjectCode} requested ({Describe(change)}). It applies when a different person with project approval authority approves it."),
            [pipeline.Event(new FacilityProjectChanged(change.ProjectCode, $"{change.Kind}Requested", id, change.LineNumber is > 0 ? change.LineNumber : null), context, SourceContext, Entity, change.ProjectCode)],
            new AuditChange(ChangeEntity, key, null, CanonicalJson.Serialize(stored with { ChangeId = id })));
    }

    private static async Task<(string Message, int? LineNumber)> ApplyAsync(IKernelUnitOfWork uow, ProjectInfo project, ProjectChange change, CancellationToken ct)
    {
        var store = uow.Store<IProjectsStore>();
        var lines = await uow.Store<IProjectLookup>().ListLinesAsync(project.Code, ct);
        switch (change.Kind)
        {
            case ProjectChangeKind.Header:
                await store.UpdateProjectAsync(project with
                {
                    Title = change.Title ?? project.Title,
                    Description = change.Description,
                    Budget = change.Budget,
                    StartDate = change.StartDate ?? project.StartDate,
                    EndDate = change.EndDate ?? project.EndDate,
                    FinancialReference = change.FinancialReference,
                }, ct);
                return ($"Change {change.ChangeId} approved: {project.Code} now {Describe(change)}.", null);
            case ProjectChangeKind.AddLine:
                var number = lines.Count == 0 ? 1 : lines.Max(l => l.LineNumber) + 1;
                await store.InsertLineAsync(new ProjectLine(project.Code, number, change.PartNumber!, change.Quantity!.Value, change.NeedDate!.Value, change.LineDescription, ProjectLineStatus.Active), ct);
                return ($"Change {change.ChangeId} approved: {project.Code} line {number} added ({Q(change.Quantity.Value)} {change.PartNumber} by {change.NeedDate:yyyy-MM-dd}).", number);
            default:
                var line = lines.FirstOrDefault(l => l.LineNumber == change.LineNumber && l.Status == ProjectLineStatus.Active)
                    ?? throw new CommandRejectedException("line_not_active", $"{project.Code} line {change.LineNumber} is no longer active.");
                if (change.Kind == ProjectChangeKind.CancelLine)
                {
                    await store.UpdateLineAsync(line with { Status = ProjectLineStatus.Cancelled }, ct);
                    return ($"Change {change.ChangeId} approved: {project.Code} line {line.LineNumber} cancelled.", line.LineNumber);
                }

                await store.UpdateLineAsync(line with { Quantity = change.Quantity!.Value, NeedDate = change.NeedDate!.Value }, ct);
                return ($"Change {change.ChangeId} approved: {project.Code} line {line.LineNumber} now {Q(change.Quantity.Value)} {line.PartNumber} by {change.NeedDate:yyyy-MM-dd}.", line.LineNumber);
        }
    }

    private CommandOutcome Applied(CommandContext context, string code, string change, int? line, string message, string? oldValue, string? newValue) => new(
        CommandResult.Ok(code, message),
        [pipeline.Event(new FacilityProjectChanged(code, change, null, line), context, SourceContext, Entity, code)],
        new AuditChange(Entity, code, oldValue, newValue));

    private static string Describe(ProjectChange c) => c.Kind switch
    {
        ProjectChangeKind.Header => $"budget {c.Budget?.ToString("N2", CultureInfo.InvariantCulture) ?? "none"}, {c.StartDate:yyyy-MM-dd} to {c.EndDate:yyyy-MM-dd}, financial reference {c.FinancialReference ?? "none"}",
        ProjectChangeKind.AddLine => $"add {Q(c.Quantity ?? 0)} {c.PartNumber} by {c.NeedDate:yyyy-MM-dd}",
        ProjectChangeKind.ChangeLine => $"line {c.LineNumber} to {Q(c.Quantity ?? 0)} {c.PartNumber} by {c.NeedDate:yyyy-MM-dd}",
        _ => $"cancel line {c.LineNumber} ({c.PartNumber})",
    };

    private static async Task<ProjectInfo> FindAsync(IKernelUnitOfWork uow, string code, CancellationToken ct)
    {
        await uow.Store<IProjectsStore>().LockProjectAsync(code, ct);
        return await uow.Store<IProjectLookup>().FindProjectAsync(code, ct)
            ?? throw new CommandRejectedException("project_not_found", $"Project {code} does not exist.");
    }

    private static async Task<ProjectLine> ActiveLineAsync(IKernelUnitOfWork uow, string code, int number, CancellationToken ct) =>
        (await uow.Store<IProjectLookup>().ListLinesAsync(code, ct)).FirstOrDefault(l => l.LineNumber == number) is { } line
            ? line.Status == ProjectLineStatus.Active ? line : throw new CommandRejectedException("line_not_active", $"{code} line {number} is cancelled.")
            : throw new CommandRejectedException("line_not_found", $"{code} has no line {number}.");

    private static async Task<string> PartAsync(IKernelUnitOfWork uow, string raw, CancellationToken ct)
    {
        var part = BusinessCode.Require(raw, "Part");
        _ = await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct) ?? throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
        return part;
    }

    private static (DateOnly Start, DateOnly End) Dates(DateOnly start, DateOnly end) =>
        end < start ? throw new CommandRejectedException("invalid_dates", "A project ends on or after the day it starts.") : (start, end);

    private static decimal? Budget(decimal? budget) =>
        budget is < 0 or > 10_000_000_000m ? throw new CommandRejectedException("invalid_budget", "A budget is zero or more.") : budget;

    private static decimal Quantity(decimal quantity) =>
        quantity is <= 0 or > 10_000_000 ? throw new CommandRejectedException("invalid_quantity", "Quantity must be above zero.") : quantity;

    private static string? Optional(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }

    private static string Q(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
