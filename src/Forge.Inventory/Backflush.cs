using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Inventory;

/// <param name="LocationCode">Where the part is deducted from (its point-of-use stock); required to turn backflushing on.</param>
public sealed record SetBackflush(string PartNumber, bool Enabled, string? LocationCode);

/// <summary>The system records what a backflush could not deduct.</summary>
public sealed record RecordBackflushException(long OrderNumber, int Sequence, long ReportId, string PartNumber, decimal Quantity, decimal Deducted, string LocationCode, string Problem);

public sealed record ResolveBackflushException(long ExceptionId, string Note);

public sealed record BackflushSettingChanged(string PartNumber, bool Enabled, string LocationCode) : IDomainEvent
{
    public static string EventType => nameof(BackflushSettingChanged);

    public static int SchemaVersion => 1;
}

/// <summary>A backflush came up short; inventory controllers are told (notification rule). Production was not stopped.</summary>
public sealed record BackflushShortage(long ExceptionId, long OrderNumber, string PartNumber, decimal Missing, string Message) : IDomainEvent
{
    public static string EventType => nameof(BackflushShortage);

    public static int SchemaVersion => 1;
}

public sealed record BackflushExceptionResolved(long ExceptionId, long OrderNumber, string PartNumber) : IDomainEvent
{
    public static string EventType => nameof(BackflushExceptionResolved);

    public static int SchemaVersion => 1;
}

public interface IBackflushStore
{
    Task UpsertBackflushSettingAsync(BackflushSetting setting, CancellationToken ct);

    /// <summary>Records the exception once per report, part and problem; returns its id (the existing one on a replay).</summary>
    Task<long> InsertBackflushExceptionAsync(BackflushException exception, CancellationToken ct);

    Task ResolveBackflushExceptionAsync(long exceptionId, string actor, DateTimeOffset at, string note, CancellationToken ct);
}

/// <summary>
/// Automatic material deduction (owner decisions 2026-11-01): inventory controllers choose which parts backflush and from where.
/// Reporting good and scrap quantity at an operation then deducts the components that operation consumes. A shortage never stops
/// production: it is recorded here and inventory controllers are told.
/// </summary>
public sealed class BackflushService(CommandPipeline pipeline)
{
    private const string SettingEntity = "BackflushSetting";
    private const string ExceptionEntity = "BackflushException";

    public Task<CommandResult> SetAsync(SetBackflush c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(InventoryService.SourceContext, nameof(SetBackflush), Permissions.InventoryManage, ResourceRef.Of(SettingEntity, part), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await uow.RequireOnAsync(SiteFeatures.Backflush, ct);
            var item = await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct) ?? throw new CommandRejectedException("item_not_found", $"Part {part} does not exist.");
            if (item.Serialized && c.Enabled)
                throw new CommandRejectedException("serialized_backflush", $"{part} is serialized; each serial number is issued by hand.");
            var before = (await uow.Store<IBackflushLookup>().ListBackflushSettingsAsync(ct)).FirstOrDefault(s => s.PartNumber == part);
            var locationCode = string.IsNullOrWhiteSpace(c.LocationCode) ? before?.LocationCode : BusinessCode.Require(c.LocationCode, "Location");
            if (locationCode is null) throw new CommandRejectedException("location_required", "Choose the location the part is deducted from.");
            if ((await uow.Store<IInventoryLookup>().ListLocationsAsync(ct)).All(l => l.Code != locationCode))
                throw new CommandRejectedException("unknown_location", $"Location {locationCode} does not exist.");
            var setting = new BackflushSetting(part, c.Enabled, locationCode, context.Actor, now);
            if (before is not null && before.Enabled == setting.Enabled && before.LocationCode == setting.LocationCode)
                return CommandOutcome.NoChange(CommandResult.NoChange(part, "Nothing changed."));
            await uow.Store<IBackflushStore>().UpsertBackflushSettingAsync(setting, ct);
            return new CommandOutcome(
                CommandResult.Ok(part, c.Enabled ? $"{part} backflushes from {locationCode} when the operation that uses it reports production." : $"{part} no longer backflushes; it is issued by hand."),
                [pipeline.Event(new BackflushSettingChanged(part, c.Enabled, locationCode), context, InventoryService.SourceContext, SettingEntity, part)],
                new AuditChange(SettingEntity, part, before is null ? null : CanonicalJson.Serialize(before), CanonicalJson.Serialize(setting)));
        }, ct);
    }

    public Task<CommandResult> RecordExceptionAsync(RecordBackflushException c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = string.Create(CultureInfo.InvariantCulture, $"{c.ReportId}/{c.PartNumber}");
        var spec = new CommandSpec(InventoryService.SourceContext, nameof(RecordBackflushException), Permissions.InventoryBackflushRecord, ResourceRef.Of(ExceptionEntity, key));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var problem = c.Problem.Length > 800 ? c.Problem[..800] : c.Problem;
            var exception = new BackflushException(0, c.OrderNumber, c.Sequence, c.ReportId, c.PartNumber, c.Quantity, c.Deducted, c.LocationCode, problem,
                BackflushExceptionStatus.Open, now, null, null, null);
            var id = await uow.Store<IBackflushStore>().InsertBackflushExceptionAsync(exception, ct);
            var missing = c.Quantity - c.Deducted;
            var message = string.Create(CultureInfo.InvariantCulture,
                $"Backflush short on order {c.OrderNumber} operation {c.Sequence}: {missing} {c.PartNumber} could not be deducted from {c.LocationCode} ({problem}). Issue it by hand or correct the stock, then resolve.");
            var entity = id.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(CommandResult.Ok(entity, message),
                [pipeline.Event(new BackflushShortage(id, c.OrderNumber, c.PartNumber, missing, message), context, InventoryService.SourceContext, ExceptionEntity, entity)],
                new AuditChange(ExceptionEntity, entity, null, CanonicalJson.Serialize(exception with { ExceptionId = id })));
        }, ct);
    }

    public Task<CommandResult> ResolveExceptionAsync(ResolveBackflushException c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var entity = c.ExceptionId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(InventoryService.SourceContext, nameof(ResolveBackflushException), Permissions.InventoryTransact, ResourceRef.Of(ExceptionEntity, entity));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var exception = await uow.Store<IBackflushLookup>().FindBackflushExceptionAsync(c.ExceptionId, ct)
                ?? throw new CommandRejectedException("exception_not_found", $"Backflush exception {entity} does not exist.");
            if (exception.Status != BackflushExceptionStatus.Open)
                return CommandOutcome.NoChange(CommandResult.NoChange(entity, "Already resolved."));
            var note = BusinessCode.RequireName(c.Note, "Note");
            await uow.Store<IBackflushStore>().ResolveBackflushExceptionAsync(c.ExceptionId, context.Actor, now, note, ct);
            return new CommandOutcome(CommandResult.Ok(entity, $"Backflush exception {entity} resolved."),
                [pipeline.Event(new BackflushExceptionResolved(c.ExceptionId, exception.OrderNumber, exception.PartNumber), context, InventoryService.SourceContext, ExceptionEntity, entity)],
                new AuditChange(ExceptionEntity, entity, CanonicalJson.Serialize(exception),
                    CanonicalJson.Serialize(exception with { Status = BackflushExceptionStatus.Resolved, ResolvedBy = context.Actor, ResolvedAt = now, ResolutionNote = note })));
        }, ct);
    }
}
