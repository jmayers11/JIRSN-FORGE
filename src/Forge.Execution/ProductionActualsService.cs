using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Execution;

public sealed record DefineReasonCode(string Code, ReasonKind Kind, StoppageCategory? Category, string Description);

public sealed record ChangeReasonCode(string Code, string Description, bool Active);

/// <param name="StartedAt">When the machine stopped, if earlier than now (reported after the fact, at most 7 days back).</param>
public sealed record StartStoppage(string MachineCode, StoppageCategory Category, string ReasonCode, long? OrderNumber = null, int? Sequence = null, string? Note = null, DateTimeOffset? StartedAt = null);

public sealed record EndStoppage(long StoppageId, DateTimeOffset? EndedAt = null);

public sealed record ProposeRootCause(long StoppageId, string Cause, string Evidence);

public sealed record ConfirmRootCause(long RootCauseId);

public sealed record ReasonCodeChanged(string Code, string Change) : IDomainEvent
{
    public static string EventType => nameof(ReasonCodeChanged);

    public static int SchemaVersion => 1;
}

public sealed record StoppageStarted(long StoppageId, string MachineCode, string Category, string ReasonCode, long? OrderNumber, int? Sequence) : IDomainEvent
{
    public static string EventType => nameof(StoppageStarted);

    public static int SchemaVersion => 1;
}

public sealed record StoppageEnded(long StoppageId, string MachineCode, string Category, decimal Minutes, decimal? ExpectedMinutes) : IDomainEvent
{
    public static string EventType => nameof(StoppageEnded);

    public static int SchemaVersion => 1;
}

public sealed record RootCauseRecorded(long RootCauseId, long StoppageId, bool Proven) : IDomainEvent
{
    public static string EventType => nameof(RootCauseRecorded);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Production actuals (V3.5, V3 brief L, K, AT). Reason codes are the plant's own words for why something
/// happened. A stoppage records a period a machine did not run, with one category and one reason code; setups and
/// changeovers carry the minutes the routing expected. A reason code is what someone reported; a root cause is
/// proposed with evidence and becomes proven only when a different person confirms it.
/// </summary>
public sealed class ProductionActualsService(CommandPipeline pipeline)
{
    public const string SourceContext = "Execution";
    private const string StoppageEntity = "Stoppage";
    private static readonly TimeSpan MaxBackdate = TimeSpan.FromDays(7);

    public Task<CommandResult> DefineReasonCodeAsync(DefineReasonCode c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Reason code");
        var spec = new CommandSpec(SourceContext, nameof(DefineReasonCode), Permissions.ProductionReasonsManage, ResourceRef.Of("ReasonCode", code));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if ((c.Kind == ReasonKind.Downtime) != (c.Category is not null))
            {
                throw new CommandRejectedException("invalid_category", c.Kind == ReasonKind.Downtime
                    ? "A downtime reason belongs to a stoppage category: planned, unplanned, setup, changeover, maintenance, waiting, starved or blocked."
                    : $"Only downtime reasons have a stoppage category; {c.Kind.ToString().ToLowerInvariant()} reasons do not.");
            }

            var info = new ReasonCodeInfo(code, c.Kind, c.Category, BusinessCode.RequireName(c.Description, "Description"), true, now, context.Actor);
            if (!await uow.Store<IProductionActualsStore>().InsertReasonCodeAsync(info, ct))
            {
                throw new CommandRejectedException("code_exists", $"Reason code {code} already exists.");
            }

            return new CommandOutcome(
                CommandResult.Ok(code, $"Reason code {code} ({c.Kind}{(c.Category is { } cat ? $", {cat}" : string.Empty)}) defined."),
                [pipeline.Event(new ReasonCodeChanged(code, "Defined"), context, SourceContext, "ReasonCode", code)],
                new AuditChange("ReasonCode", code, null, CanonicalJson.Serialize(info)));
        }, ct);
    }

    public Task<CommandResult> ChangeReasonCodeAsync(ChangeReasonCode c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Reason code");
        var spec = new CommandSpec(SourceContext, nameof(ChangeReasonCode), Permissions.ProductionReasonsManage, ResourceRef.Of("ReasonCode", code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var current = (await uow.Store<IProductionActualsLookup>().ListReasonCodesAsync(ct)).FirstOrDefault(r => r.Code == code)
                ?? throw new CommandRejectedException("reason_code_not_found", $"Reason code {code} does not exist.");
            var changed = current with { Description = BusinessCode.RequireName(c.Description, "Description"), Active = c.Active };
            if (changed == current)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(code, "Nothing to change."));
            }

            changed = changed with { ChangedAt = now, ChangedBy = context.Actor };
            await uow.Store<IProductionActualsStore>().UpdateReasonCodeAsync(changed, ct);
            return new CommandOutcome(
                CommandResult.Ok(code, $"Reason code {code} {(c.Active ? "changed" : "retired; what was recorded with it keeps it")}."),
                [pipeline.Event(new ReasonCodeChanged(code, c.Active ? "Changed" : "Retired"), context, SourceContext, "ReasonCode", code)],
                new AuditChange("ReasonCode", code, CanonicalJson.Serialize(current), CanonicalJson.Serialize(changed)));
        }, ct);
    }

    public Task<CommandResult> StartStoppageAsync(StartStoppage c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var machineCode = BusinessCode.Require(c.MachineCode, "Machine");
        var spec = new CommandSpec(SourceContext, nameof(StartStoppage), Permissions.DowntimeReport, ResourceRef.Of("Machine", machineCode));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var machine = await uow.Store<IResourcesLookup>().FindMachineAsync(machineCode, ct)
                ?? throw new CommandRejectedException("machine_not_found", $"Machine {machineCode} does not exist.");
            if (!machine.Active)
            {
                throw new CommandRejectedException("machine_inactive", $"Machine {machineCode} is retired.");
            }

            var reason = await ActiveReasonAsync(uow, c.ReasonCode, ReasonKind.Downtime, ct);
            if (reason.Category != c.Category)
            {
                throw new CommandRejectedException("category_mismatch", $"{reason.Code} is a {reason.Category} reason, not {c.Category}. Choose a {c.Category} reason or the {reason.Category} category.");
            }

            var startedAt = c.StartedAt ?? now;
            if (startedAt > now || startedAt < now - MaxBackdate)
            {
                throw new CommandRejectedException("invalid_time", "A stoppage starts now or up to 7 days ago, never in the future.");
            }

            var lookup = uow.Store<IProductionActualsLookup>();
            var last = (await lookup.ListStoppagesAsync(startedAt.AddDays(-8), now.AddTicks(1), false, ct)).Where(s => s.MachineCode == machineCode && s.EndedAt is { } e && e > startedAt).ToList();
            if (last.Count > 0)
            {
                throw new CommandRejectedException("overlapping_stoppage", $"{machineCode} was already recorded as stopped until {last.Max(s => s.EndedAt):u}; a stoppage starts after that.");
            }

            var (orderNumber, sequence, expected, program) = await OperationAsync(uow, context, machine, c, ct);
            var stoppage = new StoppageInfo(0, machineCode, machine.WorkCenterCode, c.Category, reason.Code, orderNumber, sequence, expected,
                string.IsNullOrWhiteSpace(c.Note) ? null : c.Note.Trim(), startedAt, context.Actor, null, null);
            var id = await uow.Store<IProductionActualsStore>().InsertStoppageAsync(stoppage, ct)
                ?? throw new CommandRejectedException("machine_already_stopped", $"{machineCode} already has an open stoppage; end it first.");
            var key = id.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(key, $"{machineCode} stopped: {c.Category}, {reason.Code} ({reason.Description}){(orderNumber is null ? string.Empty : $" for {ShopFloorService.OperationId(orderNumber.Value, sequence!.Value)}")}{(expected is { } m ? $"; the routing expects {m:0.#} min" : string.Empty)}."),
                [pipeline.Event(new StoppageStarted(id, machineCode, c.Category.ToString(), reason.Code, orderNumber, sequence), context, SourceContext, StoppageEntity, key, program)],
                new AuditChange(StoppageEntity, key, null, CanonicalJson.Serialize(stoppage with { StoppageId = id }), program));
        }, ct);
    }

    public Task<CommandResult> EndStoppageAsync(EndStoppage c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.StoppageId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(EndStoppage), Permissions.DowntimeReport, ResourceRef.Of(StoppageEntity, key));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var stoppage = await uow.Store<IProductionActualsLookup>().FindStoppageAsync(c.StoppageId, ct)
                ?? throw new CommandRejectedException("stoppage_not_found", $"Stoppage {key} does not exist.");
            if (!stoppage.Open)
            {
                throw new CommandRejectedException("stoppage_ended", $"Stoppage {key} already ended at {stoppage.EndedAt:u}.");
            }

            var endedAt = c.EndedAt ?? now;
            if (endedAt > now || endedAt < stoppage.StartedAt)
            {
                throw new CommandRejectedException("invalid_time", "A stoppage ends after it started and not in the future.");
            }

            await uow.Store<IProductionActualsStore>().EndStoppageAsync(c.StoppageId, endedAt, context.Actor, ct);
            var ended = stoppage with { EndedAt = endedAt, EndedBy = context.Actor };
            var minutes = ended.Minutes(now);
            var variance = ended.Variance(now);
            return new CommandOutcome(
                CommandResult.Ok(key, $"{stoppage.MachineCode} running again after {minutes:0.#} min {stoppage.Category.ToString().ToLowerInvariant()}{(variance is { } v ? $" ({(v > 0 ? "+" : string.Empty)}{v:0.#} min against the expected {stoppage.ExpectedMinutes:0.#})" : string.Empty)}."),
                [pipeline.Event(new StoppageEnded(c.StoppageId, stoppage.MachineCode, stoppage.Category.ToString(), minutes, stoppage.ExpectedMinutes), context, SourceContext, StoppageEntity, key)],
                new AuditChange(StoppageEntity, key, CanonicalJson.Serialize(stoppage), CanonicalJson.Serialize(ended)));
        }, ct);
    }

    public Task<CommandResult> ProposeRootCauseAsync(ProposeRootCause c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.StoppageId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(ProposeRootCause), Permissions.DowntimeReport, ResourceRef.Of(StoppageEntity, key));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var lookup = uow.Store<IProductionActualsLookup>();
            _ = await lookup.FindStoppageAsync(c.StoppageId, ct)
                ?? throw new CommandRejectedException("stoppage_not_found", $"Stoppage {key} does not exist.");
            if ((await lookup.ListRootCausesAsync([c.StoppageId], ct)).FirstOrDefault(r => r.Proven) is { } proven)
            {
                throw new CommandRejectedException("root_cause_proven", $"Stoppage {key} already has a proven root cause: {proven.Cause}.");
            }

            var cause = new RootCauseInfo(0, c.StoppageId, BusinessCode.RequireName(c.Cause, "Root cause"), BusinessCode.RequireName(c.Evidence, "Evidence"), now, context.Actor, null, null);
            var id = await uow.Store<IProductionActualsStore>().InsertRootCauseAsync(cause, ct);
            return new CommandOutcome(
                CommandResult.Ok(id.ToString(CultureInfo.InvariantCulture), $"Root cause proposed for stoppage {key}. It stays a proposal until someone else confirms it from the evidence."),
                [pipeline.Event(new RootCauseRecorded(id, c.StoppageId, false), context, SourceContext, StoppageEntity, key)],
                new AuditChange("RootCause", id.ToString(CultureInfo.InvariantCulture), null, CanonicalJson.Serialize(cause with { RootCauseId = id })));
        }, ct);
    }

    public Task<CommandResult> ConfirmRootCauseAsync(ConfirmRootCause c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.RootCauseId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(ConfirmRootCause), Permissions.RootCauseConfirm, ResourceRef.Of("RootCause", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var lookup = uow.Store<IProductionActualsLookup>();
            var cause = (await lookup.ListRootCausesAsync(null, ct)).FirstOrDefault(r => r.RootCauseId == c.RootCauseId)
                ?? throw new CommandRejectedException("root_cause_not_found", $"Root cause {key} does not exist.");
            if (cause.Proven)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(key, $"Root cause {key} is already proven."));
            }

            if (cause.ProposedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You proposed this root cause; a different person must confirm it.");
            }

            if ((await lookup.ListRootCausesAsync([cause.StoppageId], ct)).Any(r => r.Proven))
            {
                throw new CommandRejectedException("root_cause_proven", $"Stoppage {cause.StoppageId} already has a proven root cause.");
            }

            await uow.Store<IProductionActualsStore>().ConfirmRootCauseAsync(c.RootCauseId, now, context.Actor, ct);
            var proven = cause with { ConfirmedAt = now, ConfirmedBy = context.Actor };
            return new CommandOutcome(
                CommandResult.Ok(key, $"Root cause of stoppage {cause.StoppageId} proven: {cause.Cause}."),
                [pipeline.Event(new RootCauseRecorded(c.RootCauseId, cause.StoppageId, true), context, SourceContext, StoppageEntity, cause.StoppageId.ToString(CultureInfo.InvariantCulture))],
                new AuditChange("RootCause", key, CanonicalJson.Serialize(cause), CanonicalJson.Serialize(proven)));
        }, ct);
    }

    /// <summary>An active reason code of the given kind (shared with shop-floor scrap reporting).</summary>
    internal static async Task<ReasonCodeInfo> ActiveReasonAsync(IKernelUnitOfWork uow, string raw, ReasonKind kind, CancellationToken ct)
    {
        var code = BusinessCode.Require(raw, "Reason code");
        var reason = (await uow.Store<IProductionActualsLookup>().ListReasonCodesAsync(ct)).FirstOrDefault(r => r.Code == code)
            ?? throw new CommandRejectedException("unknown_reason_code", $"Reason code {code} does not exist.");
        if (!reason.Active)
        {
            throw new CommandRejectedException("reason_code_retired", $"Reason code {code} is retired.");
        }

        return reason.Kind == kind ? reason : throw new CommandRejectedException("wrong_reason_kind", $"{code} is a {reason.Kind.ToString().ToLowerInvariant()} reason, not a {kind.ToString().ToLowerInvariant()} reason.");
    }

    /// <summary>The order operation a stoppage names, if any; setups and changeovers must name one on this machine's work center.</summary>
    private static async Task<(long? Order, int? Sequence, decimal? Expected, ProgramId? Program)> OperationAsync(
        IKernelUnitOfWork uow, CommandContext context, MachineInfo machine, StartStoppage c, CancellationToken ct)
    {
        var preparing = c.Category is StoppageCategory.Setup or StoppageCategory.Changeover;
        if (c.OrderNumber is null || c.Sequence is null)
        {
            if (preparing)
            {
                throw new CommandRejectedException("operation_required", $"A {c.Category.ToString().ToLowerInvariant()} names the order operation it prepares the machine for.");
            }

            if (c.OrderNumber is not null || c.Sequence is not null)
            {
                throw new CommandRejectedException("operation_required", "Name both the order and the operation sequence.");
            }

            return (null, null, null, null);
        }

        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(c.OrderNumber.Value, ct);
        var program = order?.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
        if (order is null || (program is { } required && !context.Principal.Programs.Contains(required)))
        {
            throw new CommandRejectedException("order_not_found", $"Order {c.OrderNumber} does not exist.");
        }

        var op = order.Operations.FirstOrDefault(o => o.Sequence == c.Sequence)
            ?? throw new CommandRejectedException("operation_not_found", $"Order {order.OrderNumber} has no operation {c.Sequence}.");
        if (op.WorkCenterCode != machine.WorkCenterCode || (op.EligibleMachines.Count > 0 && !op.EligibleMachines.Contains(machine.Code, StringComparer.Ordinal)))
        {
            throw new CommandRejectedException("machine_not_eligible", $"{machine.Code} does not run operation {op.Sequence} of order {order.OrderNumber} ({op.WorkCenterCode}{(op.EligibleMachines.Count > 0 ? ": " + string.Join(", ", op.EligibleMachines) : string.Empty)}).");
        }

        decimal? expected = null;
        if (preparing)
        {
            var detail = await uow.Store<IEngineeringLookup>().GetRevisionDetailAsync(order.PartNumber, order.Revision, ct);
            expected = detail?.Routing.FirstOrDefault(r => r.Sequence == op.Sequence)?.SetupMinutes;
        }

        return (order.OrderNumber, op.Sequence, expected, program);
    }
}
