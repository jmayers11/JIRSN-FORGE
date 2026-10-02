using System.Globalization;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Planning.Reporting;

/// <param name="DefinitionId">Null to save a new definition; an id to change one you own.</param>
public sealed record SaveReportDefinition(long? DefinitionId, string Name, string ReportId, ReportParameters Parameters, bool Shared);

public sealed record DeleteReportDefinition(long DefinitionId);

public sealed record ReportDefinitionChanged(long DefinitionId, string ReportId, string Change) : IDomainEvent
{
    public static string EventType => nameof(ReportDefinitionChanged);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Saved report definitions: a standard report with its filters and grouping, under a name. You can
/// save only a report you may run; a shared definition is listed for everyone, but each person still
/// sees only the rows their own access allows. Only the owner changes or deletes a definition.
/// </summary>
public sealed class ReportDefinitionService(CommandPipeline pipeline)
{
    public const string SourceContext = "Planning";
    private const string Entity = "ReportDefinition";

    public Task<CommandResult> SaveAsync(SaveReportDefinition c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var spec = new CommandSpec(SourceContext, nameof(SaveReportDefinition), ReportService.PermissionOf(c.ReportId), ResourceRef.Of(Entity, c.DefinitionId));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var name = (c.Name ?? string.Empty).Trim();
            if (name.Length is 0 or > 120)
            {
                throw new CommandRejectedException("invalid_name", "Give the report a name of 1 to 120 characters.");
            }

            var parameters = c.Parameters ?? new ReportParameters();
            var entry = StandardReports.Find(c.ReportId)!.Entry;
            if (parameters.GroupBy is { Length: > 0 } g && !entry.GroupBy.Contains(g, StringComparer.Ordinal))
            {
                throw new CommandRejectedException("invalid_grouping", $"{entry.Title} cannot be grouped by '{g}'.");
            }

            if (parameters.ProgramId is { } program && !context.Principal.Programs.Contains(new Kernel.Identity.ProgramId(program)))
            {
                throw new CommandRejectedException("program_not_found", $"Program {program} does not exist.");
            }

            if (parameters.LastDays is { } days && (days < 1 || days > entry.MaxDays))
            {
                throw new CommandRejectedException("invalid_period", $"The last-days window must be 1 to {entry.MaxDays} days.");
            }

            var store = uow.Store<IReportDefinitionStore>();
            if (await store.NameTakenAsync(context.Actor, name, c.DefinitionId ?? 0, ct))
            {
                throw new CommandRejectedException("duplicate_name", $"You already have a saved report called '{name}'.");
            }

            if (c.DefinitionId is not { } existingId)
            {
                var created = new ReportDefinition(0, name, entry.Id, parameters, c.Shared, context.Actor, now, now);
                var newId = await store.InsertAsync(created, ct);
                var id = newId.ToString(CultureInfo.InvariantCulture);
                return new CommandOutcome(
                    CommandResult.Ok(id, $"Saved '{name}'."),
                    [pipeline.Event(new ReportDefinitionChanged(newId, entry.Id, "Saved"), context, SourceContext, Entity, id)],
                    new AuditChange(Entity, id, null, CanonicalJson.Serialize(created with { DefinitionId = newId })));
            }

            var existing = await OwnedAsync(store, existingId, context, ct);
            if (existing.ReportId != entry.Id)
            {
                throw new CommandRejectedException("report_changed", "A saved definition keeps its report; save a new one instead.");
            }

            var updated = existing with { Name = name, Parameters = parameters, Shared = c.Shared };
            if (updated == existing)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(Id(existingId), "Nothing changed."));
            }

            updated = updated with { UpdatedAt = now };
            await store.UpdateAsync(updated, ct);
            return new CommandOutcome(
                CommandResult.Ok(Id(existingId), $"Saved '{name}'."),
                [pipeline.Event(new ReportDefinitionChanged(existingId, entry.Id, "Changed"), context, SourceContext, Entity, Id(existingId))],
                new AuditChange(Entity, Id(existingId), CanonicalJson.Serialize(existing), CanonicalJson.Serialize(updated)));
        }, ct);
    }

    public Task<CommandResult> DeleteAsync(DeleteReportDefinition c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = Id(c.DefinitionId);
        var spec = new CommandSpec(SourceContext, nameof(DeleteReportDefinition), Permissions.ReportsSave, ResourceRef.Of(Entity, id));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IReportDefinitionStore>();
            var existing = await OwnedAsync(store, c.DefinitionId, context, ct);
            await store.DeleteAsync(c.DefinitionId, now, ct);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Deleted '{existing.Name}'."),
                [pipeline.Event(new ReportDefinitionChanged(c.DefinitionId, existing.ReportId, "Deleted"), context, SourceContext, Entity, id)],
                new AuditChange(Entity, id, CanonicalJson.Serialize(existing), null));
        }, ct);
    }

    private static async Task<ReportDefinition> OwnedAsync(IReportDefinitionStore store, long definitionId, CommandContext context, CancellationToken ct)
    {
        var existing = await store.FindAsync(definitionId, ct);
        if (existing is null || (existing.Owner != context.Actor && !existing.Shared))
        {
            throw new CommandRejectedException("report_definition_not_found", $"Saved report {definitionId} does not exist.");
        }

        if (existing.Owner != context.Actor)
        {
            throw new CommandRejectedException("not_owner", "Only the person who saved this report can change or delete it.");
        }

        return existing;
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}
