using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Resources;

public sealed record DefineToolType(string Code, string Name, ToolKind Kind, int? CalibrationDays, int? UsageLimit, int? ServiceUses);

public sealed record RegisterTool(string Code, string TypeCode, string Description, string? Serial, string? Location, IReadOnlyList<string>? EligibleMachines, IReadOnlyList<string>? EligibleActivities,
    DateOnly? CalibratedOn = null, DateOnly? CalibrationDue = null, string? Certificate = null);

public sealed record MoveTool(string Code, string? Location, ToolCustody Custody, string? Custodian);

public sealed record CalibrateTool(string Code, DateOnly CalibratedOn, bool Passed, DateOnly? DueOn, string? Certificate);

public sealed record SendToolForService(string Code, string Vendor, string? Note);

public sealed record ReturnToolFromService(string Code, ToolCondition Condition, string? Location, DateOnly? CalibratedOn, DateOnly? DueOn, string? Certificate);

public sealed record ChangeToolCondition(string Code, ToolCondition Condition);

/// <param name="Reference">What it was used for: an order operation, an inspection.</param>
public sealed record UseTool(string Code, int Uses, string? MachineCode, string? ActivityCode, string? Reference);

public sealed record RetireTool(string Code);

public sealed record ToolChanged(string Code, string Change) : IDomainEvent
{
    public static string EventType => nameof(ToolChanged);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Tools as resources (V3.8, V3 brief Z, AA). A tool has identity, a type, a serial, a place, a custodian, a condition,
/// the machines and activities it is for, and a usage count against its type's limits. Calibrations, service cycles
/// (sent out, returned), moves and uses are events kept for good; a tool's validity is judged as of the moment it is
/// used. Gauges are tools of the Gauge kind.
/// </summary>
public sealed class ToolService(CommandPipeline pipeline)
{
    public const string SourceContext = "Resources";
    private const string Entity = "Tool";

    public Task<CommandResult> DefineTypeAsync(DefineToolType c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Tool type");
        var spec = new CommandSpec(SourceContext, nameof(DefineToolType), Permissions.ToolsManage, ResourceRef.Of("ToolType", code));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (c.CalibrationDays is <= 0 or > 3650 || c.UsageLimit is <= 0 || c.ServiceUses is <= 0 || (c.UsageLimit is { } l && c.ServiceUses is { } s && s > l))
            {
                throw new CommandRejectedException("invalid_limits", "Calibration days, usage limit and service uses are above zero, and service comes before the end of life.");
            }

            var type = new ToolTypeInfo(code, BusinessCode.RequireName(c.Name, "Tool type"), c.Kind, c.CalibrationDays, c.UsageLimit, c.ServiceUses);
            if (!await uow.Store<IToolStore>().InsertToolTypeAsync(type, now, context.Actor, ct))
            {
                throw new CommandRejectedException("code_exists", $"Tool type {code} already exists.");
            }

            return new CommandOutcome(
                CommandResult.Ok(code, $"Tool type {code} ({c.Kind}) defined."),
                [pipeline.Event(new ToolChanged(code, "TypeDefined"), context, SourceContext, "ToolType", code)],
                new AuditChange("ToolType", code, null, CanonicalJson.Serialize(type)));
        }, ct);
    }

    public Task<CommandResult> RegisterAsync(RegisterTool c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Tool");
        var spec = new CommandSpec(SourceContext, nameof(RegisterTool), Permissions.ToolsManage, ResourceRef.Of(Entity, code));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var lookup = uow.Store<IToolLookup>();
            var type = (await lookup.ListToolTypesAsync(ct)).FirstOrDefault(t => t.Code == BusinessCode.Require(c.TypeCode, "Tool type") && t.Active)
                ?? throw new CommandRejectedException("unknown_tool_type", $"Tool type {c.TypeCode} does not exist.");
            var machines = await KnownMachinesAsync(uow, c.EligibleMachines, ct);
            var activities = await KnownActivitiesAsync(uow, c.EligibleActivities, ct);
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            DateOnly? due = c.CalibrationDue ?? (c.CalibratedOn is { } on && type.CalibrationDays is { } days ? on.AddDays(days) : null);
            if (c.CalibratedOn > today || (c.CalibratedOn is { } o && due is { } d && d <= o))
            {
                throw new CommandRejectedException("invalid_calibration", "A calibration is not in the future and is due again after it was done.");
            }

            var tool = new ToolInfo(code, type.Code, BusinessCode.RequireName(c.Description, "Tool"), Optional(c.Serial), Optional(c.Location), ToolCustody.InHouse, null, ToolCondition.Good,
                machines, activities, 0, 0, due, Optional(c.Certificate), ToolStatus.Available, now, context.Actor);
            if (!await uow.Store<IToolStore>().InsertToolAsync(tool, now, context.Actor, ct))
            {
                throw new CommandRejectedException("code_exists", $"Tool {code} already exists.");
            }

            await uow.Store<IToolStore>().InsertToolEventAsync(new ToolEvent(0, code, ToolEventKind.Registered, now, context.Actor, tool.Location, null, ToolCondition.Good, c.CalibratedOn, due, tool.Certificate), ct);
            return Outcome(context, code, "Registered", $"Tool {code} ({type.Name}) registered{(due is { } at ? $", calibrated until {at:yyyy-MM-dd}" : string.Empty)}.", null, tool);
        }, ct);
    }

    public Task<CommandResult> MoveAsync(MoveTool c, CommandContext context, CancellationToken ct) =>
        ChangeAsync(nameof(MoveTool), c?.Code, c, context, Permissions.ToolsHandle, false, (tool, type, now) =>
        {
            if (tool.Status != ToolStatus.Available)
            {
                throw new CommandRejectedException("tool_unavailable", $"{tool.Code} is {tool.Status}; it moves only once it is back.");
            }

            if (c!.Custody == ToolCustody.AtVendor)
            {
                throw new CommandRejectedException("use_service_cycle", "A tool goes to a vendor by sending it for service.");
            }

            var custodian = c.Custody == ToolCustody.InHouse ? null : Optional(c.Custodian) ?? throw new CommandRejectedException("custodian_required", "Say who has the tool.");
            var moved = tool with { Location = Optional(c.Location), Custody = c.Custody, Custodian = custodian };
            return (moved, new ToolEvent(0, tool.Code, ToolEventKind.Moved, now, context.Actor, moved.Location, custodian), $"{tool.Code} is now {(custodian is null ? $"at {moved.Location ?? "no set place"}" : $"with {custodian}")}.");
        }, ct);

    public Task<CommandResult> CalibrateAsync(CalibrateTool c, CommandContext context, CancellationToken ct) =>
        ChangeAsync(nameof(CalibrateTool), c?.Code, c, context, Permissions.ToolsCalibrate, false, (tool, type, now) =>
        {
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            if (c!.CalibratedOn > today)
            {
                throw new CommandRejectedException("invalid_calibration", "A calibration is not in the future.");
            }

            if (!c.Passed)
            {
                var failed = tool with { CalibrationDue = null, Certificate = Optional(c.Certificate) };
                return (failed, new ToolEvent(0, tool.Code, ToolEventKind.CalibrationFailed, now, context.Actor, CalibratedOn: c.CalibratedOn, Certificate: failed.Certificate),
                    $"{tool.Code} failed calibration; it is not used until it passes.");
            }

            var due = c.DueOn ?? (type.CalibrationDays is { } days ? c.CalibratedOn.AddDays(days) : throw new CommandRejectedException("due_required", $"{type.Code} has no calibration interval; give the next due date."));
            if (due <= c.CalibratedOn)
            {
                throw new CommandRejectedException("invalid_calibration", "The next calibration is due after this one.");
            }

            var calibrated = tool with { CalibrationDue = due, Certificate = Optional(c.Certificate) };
            return (calibrated, new ToolEvent(0, tool.Code, ToolEventKind.Calibrated, now, context.Actor, CalibratedOn: c.CalibratedOn, DueOn: due, Certificate: calibrated.Certificate),
                $"{tool.Code} calibrated {c.CalibratedOn:yyyy-MM-dd}, due again {due:yyyy-MM-dd}.");
        }, ct);

    public Task<CommandResult> SendForServiceAsync(SendToolForService c, CommandContext context, CancellationToken ct) =>
        ChangeAsync(nameof(SendToolForService), c?.Code, c, context, Permissions.ToolsHandle, true, (tool, type, now) =>
        {
            if (tool.Status != ToolStatus.Available)
            {
                throw new CommandRejectedException("tool_unavailable", $"{tool.Code} is {tool.Status}.");
            }

            var vendor = BusinessCode.RequireName(c!.Vendor, "Vendor");
            var away = tool with { Status = ToolStatus.OutForService, Custody = ToolCustody.AtVendor, Custodian = vendor };
            return (away, new ToolEvent(0, tool.Code, ToolEventKind.SentForService, now, context.Actor, Custodian: vendor, Note: Optional(c.Note)), $"{tool.Code} sent to {vendor} for service; it is not used until it is back.");
        }, ct);

    public Task<CommandResult> ReturnFromServiceAsync(ReturnToolFromService c, CommandContext context, CancellationToken ct) =>
        ChangeAsync(nameof(ReturnToolFromService), c?.Code, c, context, Permissions.ToolsHandle, false, (tool, type, now) =>
        {
            if (tool.Status != ToolStatus.OutForService)
            {
                throw new CommandRejectedException("not_out_for_service", $"{tool.Code} is not out for service.");
            }

            var due = c!.DueOn ?? (c.CalibratedOn is { } on && type.CalibrationDays is { } days ? on.AddDays(days) : tool.CalibrationDue);
            var back = tool with
            {
                Status = ToolStatus.Available, Custody = ToolCustody.InHouse, Custodian = null, Location = Optional(c.Location) ?? tool.Location, Condition = c.Condition,
                UsesSinceService = 0, CalibrationDue = due, Certificate = Optional(c.Certificate) ?? tool.Certificate,
            };
            return (back, new ToolEvent(0, tool.Code, ToolEventKind.Returned, now, context.Actor, back.Location, null, c.Condition, c.CalibratedOn, c.CalibratedOn is null ? null : due, Optional(c.Certificate)),
                $"{tool.Code} back from {tool.Custodian} ({c.Condition}); uses since service start again.");
        }, ct);

    public Task<CommandResult> ChangeConditionAsync(ChangeToolCondition c, CommandContext context, CancellationToken ct) =>
        ChangeAsync(nameof(ChangeToolCondition), c?.Code, c, context, Permissions.ToolsHandle, true, (tool, type, now) =>
            (tool with { Condition = c!.Condition }, new ToolEvent(0, tool.Code, ToolEventKind.ConditionChanged, now, context.Actor, Condition: c.Condition, Note: context.Reason),
                $"{tool.Code} is now {c.Condition.ToString().ToLowerInvariant()}."), ct);

    public Task<CommandResult> RetireAsync(RetireTool c, CommandContext context, CancellationToken ct) =>
        ChangeAsync(nameof(RetireTool), c?.Code, c, context, Permissions.ToolsManage, true, (tool, type, now) =>
            tool.Status == ToolStatus.Retired
                ? throw new CommandRejectedException("tool_retired", $"{tool.Code} is already retired.")
                : (tool with { Status = ToolStatus.Retired }, new ToolEvent(0, tool.Code, ToolEventKind.Retired, now, context.Actor, Note: context.Reason), $"{tool.Code} retired; its history stays."), ct);

    /// <summary>Records a use, after checking the tool was valid for it at that moment.</summary>
    public Task<CommandResult> UseAsync(UseTool c, CommandContext context, CancellationToken ct) =>
        ChangeAsync(nameof(UseTool), c?.Code, c, context, Permissions.ToolsHandle, false, (tool, type, now) =>
        {
            if (c!.Uses is < 1 or > 100_000)
            {
                throw new CommandRejectedException("invalid_uses", "Record 1 or more uses.");
            }

            return (tool with { UsageCount = tool.UsageCount + c.Uses, UsesSinceService = tool.UsesSinceService + c.Uses },
                new ToolEvent(0, tool.Code, ToolEventKind.Used, now, context.Actor, Uses: c.Uses, Reference: Optional(c.Reference)),
                $"{c.Uses} use(s) of {tool.Code} recorded; {tool.UsageCount + c.Uses} in all.");
        }, c?.MachineCode, c?.ActivityCode, true, ct);

    private Task<CommandResult> ChangeAsync<TCommand>(
        string commandType,
        string? rawCode,
        TCommand command,
        CommandContext context,
        string permission,
        bool reasonRequired,
        Func<ToolInfo, ToolTypeInfo, DateTimeOffset, (ToolInfo Tool, ToolEvent Event, string Message)> change,
        CancellationToken ct) =>
        ChangeAsync(commandType, rawCode, command, context, permission, reasonRequired, change, null, null, false, ct);

    private Task<CommandResult> ChangeAsync<TCommand>(
        string commandType,
        string? rawCode,
        TCommand command,
        CommandContext context,
        string permission,
        bool reasonRequired,
        Func<ToolInfo, ToolTypeInfo, DateTimeOffset, (ToolInfo Tool, ToolEvent Event, string Message)> change,
        string? machine,
        string? activity,
        bool requireValid,
        CancellationToken ct)
    {
        var code = BusinessCode.Require(rawCode ?? string.Empty, "Tool");
        var spec = new CommandSpec(SourceContext, commandType, permission, ResourceRef.Of(Entity, code), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var lookup = uow.Store<IToolLookup>();
            var tool = await lookup.FindToolAsync(code, ct) ?? throw new CommandRejectedException("tool_not_found", $"Tool {code} does not exist.");
            var type = (await lookup.ListToolTypesAsync(ct)).First(t => t.Code == tool.TypeCode);
            if (requireValid)
            {
                var validity = ToolValidity.At(tool, type, await lookup.ListToolEventsAsync(code, ct), now, Optional(machine), Optional(activity));
                if (!validity.Valid)
                {
                    throw new CommandRejectedException("tool_not_valid", string.Join(" ", validity.Problems));
                }
            }
            else if (tool.Status == ToolStatus.Retired)
            {
                throw new CommandRejectedException("tool_retired", $"{tool.Code} is retired.");
            }

            var (changed, e, message) = change(tool, type, now);
            changed = changed with { ChangedAt = now, ChangedBy = context.Actor };
            var store = uow.Store<IToolStore>();
            await store.UpdateToolAsync(changed, ct);
            await store.InsertToolEventAsync(e, ct);
            return Outcome(context, code, e.Kind.ToString(), message, tool, changed);
        }, ct);
    }

    private CommandOutcome Outcome(CommandContext context, string code, string change, string message, ToolInfo? before, ToolInfo after) => new(
        CommandResult.Ok(code, message),
        [pipeline.Event(new ToolChanged(code, change), context, SourceContext, Entity, code)],
        new AuditChange(Entity, code, before is null ? null : CanonicalJson.Serialize(before), CanonicalJson.Serialize(after)));

    private static async Task<IReadOnlyList<string>> KnownMachinesAsync(IKernelUnitOfWork uow, IReadOnlyList<string>? raw, CancellationToken ct)
    {
        var codes = (raw ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => BusinessCode.Require(x, "Machine")).Distinct(StringComparer.Ordinal).ToList();
        var known = (await uow.Store<IResourcesLookup>().ListMachinesAsync(ct)).Select(m => m.Code).ToHashSet(StringComparer.Ordinal);
        return codes.FirstOrDefault(c => !known.Contains(c)) is { } missing ? throw new CommandRejectedException("unknown_machine", $"Machine {missing} does not exist.") : codes;
    }

    private static async Task<IReadOnlyList<string>> KnownActivitiesAsync(IKernelUnitOfWork uow, IReadOnlyList<string>? raw, CancellationToken ct)
    {
        var codes = (raw ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => BusinessCode.Require(x, "Activity")).Distinct(StringComparer.Ordinal).ToList();
        foreach (var code in codes)
        {
            if (!await uow.Store<IResourcesLookup>().ActivityTypeExistsAsync(code, ct))
            {
                throw new CommandRejectedException("unknown_activity", $"Activity {code} does not exist.");
            }
        }

        return codes;
    }

    private static string? Optional(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
}
