using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Kernel.WorkAssignments;

public sealed record WorkAssignmentRecord(Guid AssignmentId, string WorkType, string WorkId, string Title, string DeepLink,
    string Status, PersonId Assignee, string AssignedBy, DateTimeOffset AssignedAt, DateTimeOffset UpdatedAt, string Reason);
public sealed record WorkSource(string ReadPermission, IReadOnlyList<ResourceRef> Resources, bool IsActive = true, IReadOnlyList<string>? DecisionExcludedActors = null, bool DecisionAvailable = true);
public interface IWorkSourceResolver
{
    Task<WorkSource?> ResolveAsync(IKernelUnitOfWork uow, string type, string id, CancellationToken ct);
}
public interface IWorkAssignmentStore
{
    Task<IReadOnlyList<WorkAssignmentRecord>> ListAsync(PersonId assignee, CancellationToken ct);
    Task<WorkAssignmentRecord?> FindAsync(Guid id, bool lockRow, CancellationToken ct);
    Task<WorkAssignmentRecord?> FindAsync(string type, string id, PersonId assignee, CancellationToken ct);
    Task<bool> InsertAsync(WorkAssignmentRecord assignment, CancellationToken ct);
    Task SetStatusAsync(Guid id, string oldStatus, string status, DateTimeOffset at, string actor, string reason, CancellationToken ct);
}
public sealed record AssignWork(string WorkType, string WorkId, string Title, string DeepLink, PersonId Assignee);
public sealed record SetWorkAssignmentStatus(Guid AssignmentId, string Status);
public sealed record WorkAssigned(Guid AssignmentId, string WorkType, string WorkId, Guid Assignee) : IDomainEvent
{
    public static string EventType => nameof(WorkAssigned);
    public static int SchemaVersion => 1;
}
public sealed record WorkAssignmentStatusChanged(Guid AssignmentId, string Status) : IDomainEvent
{
    public static string EventType => nameof(WorkAssignmentStatusChanged);
    public static int SchemaVersion => 1;
}

/// <summary>The existing assignment table is updated through Forge's shared command transaction.</summary>
public sealed class WorkAssignmentService(CommandPipeline pipeline, IKernelUnitOfWorkFactory unitOfWork, IWorkSourceResolver sources, IForgeAuthorizer authorizer)
{
    public async Task<CommandResult> AssignAsync(AssignWork command, CommandContext context, CancellationToken ct)
    {
        Validate(command);
        if (context.Reason?.Trim().Length > 500) throw new CommandRejectedException("invalid_assignment_reason", "An assignment reason is at most 500 characters.");
        WorkSource? source;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
            source = await sources.ResolveAsync(read, command.WorkType, command.WorkId, ct);
        var sourceResource = source?.Resources is { Count: > 0 } contexts ? contexts[0] : ResourceRef.Of("WorkAssignment");
        var resource = sourceResource with { Type = "WorkAssignment", Id = $"{command.WorkType}/{command.WorkId}" };
        return await pipeline.ExecuteAsync(new CommandSpec("Kernel", nameof(AssignWork), Permissions.WorkAssignmentsManage, resource, ReasonRequired: true), command, context, async (uow, now) =>
        {
            var currentSource = await AccessibleAsync(uow, command.WorkType, command.WorkId, context.Principal, true, ct);
            if (source is null || !currentSource.Resources.SequenceEqual(source.Resources))
                throw new CommandRejectedException("assignment_source_changed", "The source context changed. Refresh the source and retry.");
            foreach (var scope in currentSource.Resources)
                if (!authorizer.Authorize(context.Principal, Permissions.WorkAssignmentsManage, scope).IsPermitted)
                    throw new CommandRejectedException("assignment_source_not_accessible", "Assignment management authority does not cover the complete source scope.");
            var assignee = await uow.People.LoadPrincipalAsync(command.Assignee, ct);
            if (assignee is not { IsActive: true }) throw new CommandRejectedException("inactive_assignee", "Choose an active registered assignee.");
            await AccessibleAsync(uow, command.WorkType, command.WorkId, assignee, true, ct);
            var store = uow.Store<IWorkAssignmentStore>();
            if (await store.FindAsync(command.WorkType, command.WorkId, command.Assignee, ct) is { } existing)
                return CommandOutcome.NoChange(CommandResult.NoChange(existing.AssignmentId.ToString("D"), "This work is already assigned; its progress is preserved."));
            var record = new WorkAssignmentRecord(ForgeIds.New(pipeline.Time), command.WorkType, command.WorkId, command.Title.Trim(), command.DeepLink,
                "Assigned", command.Assignee, context.Actor, now, now, context.Reason!);
            if (!await store.InsertAsync(record, ct)) throw new CommandRejectedException("assignment_exists", "This source was assigned concurrently. Refresh the inbox.");
            return new CommandOutcome(CommandResult.Ok(record.AssignmentId.ToString("D"), "Work assigned."),
                [pipeline.Event(new WorkAssigned(record.AssignmentId, record.WorkType, record.WorkId, record.Assignee.Value), context, "Kernel", "WorkAssignment", record.AssignmentId.ToString("D"), resource.Program, resource.Markings)],
                new AuditChange("WorkAssignment", record.AssignmentId.ToString("D"), null, CanonicalJson.Serialize(record), resource.Program));
        }, ct);
    }

    public async Task<CommandResult> SetStatusAsync(SetWorkAssignmentStatus command, CommandContext context, CancellationToken ct)
    {
        if (command.Status is not ("Acknowledged" or "InProgress" or "Completed"))
            throw new CommandRejectedException("invalid_assignment_transition", "Choose Acknowledged, InProgress or Completed in sequence.");
        if (context.Reason?.Trim().Length > 500) throw new CommandRejectedException("invalid_assignment_reason", "An assignment reason is at most 500 characters.");
        WorkAssignmentRecord record;
        WorkSource source;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            record = await read.Store<IWorkAssignmentStore>().FindAsync(command.AssignmentId, false, ct)
                ?? throw new CommandRejectedException("assignment_not_found", "Assignment does not exist.");
            if (record.Assignee != context.Principal.PersonId) throw new CommandRejectedException("assignment_not_found", "Assignment does not exist.");
            source = await sources.ResolveAsync(read, record.WorkType, record.WorkId, ct)
                ?? throw new CommandRejectedException("assignment_source_not_accessible", "The source is not accessible.");
        }
        var resource = source.Resources[0] with { Type = "WorkAssignment", Id = command.AssignmentId.ToString("D") };
        return await pipeline.ExecuteAsync(new CommandSpec("Kernel", nameof(SetWorkAssignmentStatus), source.ReadPermission, resource, ReasonRequired: true), command, context, async (uow, now) =>
        {
            var store = uow.Store<IWorkAssignmentStore>();
            var current = await store.FindAsync(command.AssignmentId, true, ct) ?? throw new CommandRejectedException("assignment_not_found", "Assignment does not exist.");
            if (current.Assignee != context.Principal.PersonId) throw new CommandRejectedException("assignment_not_found", "Assignment does not exist.");
            var currentSource = await AccessibleAsync(uow, current.WorkType, current.WorkId, context.Principal, false, ct);
            if (!currentSource.Resources.SequenceEqual(source.Resources))
                throw new CommandRejectedException("assignment_source_changed", "The source context changed. Refresh the source and retry.");
            var next = current.Status switch { "Assigned" => "Acknowledged", "Acknowledged" => "InProgress", "InProgress" => "Completed", _ => null };
            if (command.Status == current.Status) return CommandOutcome.NoChange(CommandResult.NoChange(resource.Id!, "The assignment already has that status."));
            if (command.Status != next) throw new CommandRejectedException("invalid_assignment_transition", $"Cannot move an assignment from {current.Status} to {command.Status}.");
            await store.SetStatusAsync(command.AssignmentId, current.Status, command.Status, now, context.Actor, context.Reason!, ct);
            return new CommandOutcome(CommandResult.Ok(resource.Id!, $"Assignment {command.Status}."),
                [pipeline.Event(new WorkAssignmentStatusChanged(command.AssignmentId, command.Status), context, "Kernel", "WorkAssignment", resource.Id!, resource.Program, resource.Markings)],
                new AuditChange("WorkAssignment", resource.Id!, CanonicalJson.Serialize(current), CanonicalJson.Serialize(current with { Status = command.Status, UpdatedAt = now }), resource.Program));
        }, ct);
    }

    private async Task<WorkSource> AccessibleAsync(IKernelUnitOfWork uow, string type, string id, ForgePrincipal principal, bool active, CancellationToken ct)
    {
        var source = await sources.ResolveAsync(uow, type, id, ct);
        if (source is null || source.Resources.Count == 0 || active && !source.IsActive
            || source.Resources.Any(resource => !authorizer.Authorize(principal, source.ReadPermission, resource).IsPermitted))
            throw new CommandRejectedException("assignment_source_not_found", "The source is not accessible or is no longer available for assignment.");
        return source;
    }
    private static void Validate(AssignWork command)
    {
        if (command.WorkType is not ("NCR" or "Inspection" or "WorkOrder" or "RFQ" or "ProjectReview" or "MaterialRequest")
            || string.IsNullOrWhiteSpace(command.WorkId) || command.WorkId.Length > 100 || string.IsNullOrWhiteSpace(command.Title) || command.Title.Trim().Length > 240
            || command.DeepLink is not ("#/quality" or "#/procurement" or "#/maintenance" or "#/projects" or "#/inventory" or "#/engineering" or "#/shopfloor"))
            throw new CommandRejectedException("invalid_assignment", "Choose a supported source, identifier, title and local Forge page.");
    }
}
