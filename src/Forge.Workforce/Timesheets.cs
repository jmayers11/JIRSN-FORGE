using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Workforce;

public sealed record CreateChargeCode(string Code, string Name, ChargeCodeKind Kind, string? ContractNumber = null, string? Clin = null);

public sealed record SetChargeCodeActive(string Code, bool Active);

/// <summary>An hours entry by the employee for themself: an order operation, or a charge code.</summary>
public sealed record RecordTime(DateOnly WorkDate, decimal Hours, long? OrderNumber, int? Sequence, string? ChargeCode, string? Note, string? LateReason = null);

/// <summary>Replaces one of the employee's entries; zero hours withdraws it. The command's reason is kept as the correction reason.</summary>
public sealed record CorrectTime(Guid EntryId, decimal Hours, long? OrderNumber, int? Sequence, string? ChargeCode, string? Note);

/// <summary>The employee certifies that the week's entries are complete and correct.</summary>
public sealed record SignTimesheet(DateOnly WeekStart);

public sealed record ApproveTimesheet(Guid TimesheetId);

public sealed record ReturnTimesheet(Guid TimesheetId);

public sealed record ChargeCodeChanged(string Code, string Change) : IDomainEvent
{
    public static string EventType => nameof(ChargeCodeChanged);

    public static int SchemaVersion => 1;
}

public sealed record TimesheetChanged(Guid TimesheetId, Guid PersonId, DateOnly WeekStart, string Change) : IDomainEvent
{
    public static string EventType => nameof(TimesheetChanged);

    public static int SchemaVersion => 1;
}

public interface ITimesheetStore
{
    Task<bool> InsertChargeCodeAsync(ChargeCodeInfo code, CancellationToken ct);

    Task SetChargeCodeActiveAsync(string code, bool active, DateTimeOffset at, string actor, CancellationToken ct);

    /// <summary>Locks the person's sheet for the week, creating it open when there is none.</summary>
    Task<TimesheetInfo> LockOrCreateTimesheetAsync(Guid timesheetId, Guid personId, DateOnly weekStart, CancellationToken ct);

    Task LockTimesheetAsync(Guid timesheetId, CancellationToken ct);

    Task InsertEntryAsync(TimeEntryInfo entry, CancellationToken ct);

    /// <summary>Moves an active entry to superseded or withdrawn; false when it is no longer active.</summary>
    Task<bool> RetireEntryAsync(Guid entryId, TimeEntryState state, string? correctionReason, CancellationToken ct);

    Task SetTimesheetStatusAsync(Guid timesheetId, TimesheetStatus status, DateTimeOffset at, string actor, string? reason, CancellationToken ct);
}

/// <summary>
/// DCAA-style timekeeping (owner decision 2026-10-31). Each employee records their own hours daily against an order operation or a
/// charge code; time recorded more than a day late needs a reason. The employee signs the week; a supervisor who is not the employee
/// approves or returns it. Entries are never rewritten: corrections supersede with a reason, and correcting a signed or approved week
/// reopens it for signature and approval. Every step is in the audit trail.
/// </summary>
public sealed class TimesheetService(CommandPipeline pipeline, ITimeChargeResolver orders)
{
    private const string SourceContext = "Workforce";
    private const string Entity = "Timesheet";
    private const string CodeEntity = "ChargeCode";

    public Task<CommandResult> CreateChargeCodeAsync(CreateChargeCode c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Charge");
        var spec = new CommandSpec(SourceContext, nameof(CreateChargeCode), Permissions.TimekeepingCodesManage, ResourceRef.Of(CodeEntity, code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (!Enum.IsDefined(c.Kind)) throw new CommandRejectedException("invalid_kind", "Choose what the charge code collects.");
            var contract = Text(c.ContractNumber, 60);
            if ((c.Kind == ChargeCodeKind.Contract) != (contract is not null))
                throw new CommandRejectedException("contract_required", "A contract charge code names its contract; other kinds do not.");
            var info = new ChargeCodeInfo(code, BusinessCode.RequireName(c.Name, "Charge code"), c.Kind, contract, c.Kind == ChargeCodeKind.Contract ? Text(c.Clin, 20) : null,
                true, context.Actor, now);
            if (!await uow.Store<ITimesheetStore>().InsertChargeCodeAsync(info, ct))
                throw new CommandRejectedException("charge_code_exists", $"Charge code {code} already exists.");
            return new CommandOutcome(CommandResult.Ok(code, $"Charge code {code} ({info.Name}, {info.Kind}) added."),
                [pipeline.Event(new ChargeCodeChanged(code, "Created"), context, SourceContext, CodeEntity, code)],
                new AuditChange(CodeEntity, code, null, CanonicalJson.Serialize(info)));
        }, ct);
    }

    public Task<CommandResult> SetChargeCodeActiveAsync(SetChargeCodeActive c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Charge");
        var spec = new CommandSpec(SourceContext, nameof(SetChargeCodeActive), Permissions.TimekeepingCodesManage, ResourceRef.Of(CodeEntity, code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var current = await uow.Store<ITimesheetLookup>().FindChargeCodeAsync(code, ct)
                ?? throw new CommandRejectedException("charge_code_not_found", $"Charge code {code} does not exist.");
            if (current.Active == c.Active) return CommandOutcome.NoChange(CommandResult.NoChange(code, "Nothing changed."));
            await uow.Store<ITimesheetStore>().SetChargeCodeActiveAsync(code, c.Active, now, context.Actor, ct);
            return new CommandOutcome(CommandResult.Ok(code, $"Charge code {code} {(c.Active ? "takes time again" : "takes no new time")}."),
                [pipeline.Event(new ChargeCodeChanged(code, c.Active ? "Activated" : "Deactivated"), context, SourceContext, CodeEntity, code)],
                new AuditChange(CodeEntity, code, current.Active.ToString(), c.Active.ToString()));
        }, ct);
    }

    public Task<CommandResult> RecordAsync(RecordTime c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var person = context.Principal.PersonId.Value;
        var week = TimesheetInfo.WeekOf(c.WorkDate);
        var spec = new CommandSpec(SourceContext, nameof(RecordTime), Permissions.TimekeepingRecord, ResourceRef.Of(Entity, Key(person, week)));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var today = await uow.SiteDateAsync(now, ct);
            if (c.WorkDate > today) throw new CommandRejectedException("future_time", "Record time you have worked, not time you plan to work.");
            var (order, sequence, code) = await ChargeAsync(uow, context, c.OrderNumber, c.Sequence, c.ChargeCode, ct);
            Hours(c.Hours, allowZero: false);
            var late = Text(c.LateReason, 500);
            if (c.WorkDate < today.AddDays(-1) && late is null)
                throw new CommandRejectedException("late_reason_required", $"Time for {c.WorkDate:yyyy-MM-dd} is recorded after the next day; say why.");
            var sheet = await uow.Store<ITimesheetStore>().LockOrCreateTimesheetAsync(ForgeIds.New(pipeline.Time), person, week, ct);
            if (sheet.Status is TimesheetStatus.Signed or TimesheetStatus.Approved)
                throw new CommandRejectedException("timesheet_signed", $"The week of {week:yyyy-MM-dd} is {sheet.Status}; record a correction with a reason instead.");
            DayTotal(sheet, c.WorkDate, c.Hours, null);
            var entry = new TimeEntryInfo(ForgeIds.New(pipeline.Time), sheet.TimesheetId, c.WorkDate, order, sequence, code, c.Hours, Text(c.Note, 500), TimeEntryState.Active,
                context.Actor, now, c.WorkDate < today.AddDays(-1) ? late : null, null, null);
            await uow.Store<ITimesheetStore>().InsertEntryAsync(entry, ct);
            var id = sheet.TimesheetId.ToString("D");
            return new CommandOutcome(
                CommandResult.Ok(id, string.Create(CultureInfo.InvariantCulture, $"{c.Hours:0.##} h on {c.WorkDate:yyyy-MM-dd} charged to {entry.Charge}; the week now has {sheet.Hours + c.Hours:0.##} h.")),
                [pipeline.Event(new TimesheetChanged(sheet.TimesheetId, person, week, "TimeRecorded"), context, SourceContext, Entity, id)],
                new AuditChange(Entity, id, null, CanonicalJson.Serialize(entry)));
        }, ct);
    }

    public Task<CommandResult> CorrectAsync(CorrectTime c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var person = context.Principal.PersonId.Value;
        var spec = new CommandSpec(SourceContext, nameof(CorrectTime), Permissions.TimekeepingRecord, ResourceRef.Of("TimeEntry", c.EntryId.ToString("D")), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var lookup = uow.Store<ITimesheetLookup>();
            var sheet = (await lookup.ListTimesheetsAsync(null, person, ct)).FirstOrDefault(s => s.Entries.Any(e => e.EntryId == c.EntryId))
                ?? throw new CommandRejectedException("entry_not_found", "That entry is not one of your time entries.");
            var store = uow.Store<ITimesheetStore>();
            await store.LockTimesheetAsync(sheet.TimesheetId, ct);
            sheet = await lookup.FindTimesheetAsync(sheet.TimesheetId, ct) ?? sheet;
            var original = sheet.Entries.Single(e => e.EntryId == c.EntryId);
            if (original.State != TimeEntryState.Active)
                throw new CommandRejectedException("entry_not_active", $"That entry is {original.State}; correct the entry that replaced it.");
            Hours(c.Hours, allowZero: true);
            var reason = context.Reason!;
            string message;
            TimeEntryInfo? replacement = null;
            if (c.Hours == 0)
            {
                if (!await store.RetireEntryAsync(original.EntryId, TimeEntryState.Withdrawn, reason, ct))
                    throw new CommandRejectedException("entry_changed", "The entry changed meanwhile; reload it.");
                message = string.Create(CultureInfo.InvariantCulture, $"{original.Hours:0.##} h on {original.WorkDate:yyyy-MM-dd} ({original.Charge}) withdrawn");
            }
            else
            {
                var (order, sequence, code) = await ChargeAsync(uow, context, c.OrderNumber, c.Sequence, c.ChargeCode, ct);
                DayTotal(sheet, original.WorkDate, c.Hours, original.EntryId);
                if (!await store.RetireEntryAsync(original.EntryId, TimeEntryState.Superseded, null, ct))
                    throw new CommandRejectedException("entry_changed", "The entry changed meanwhile; reload it.");
                replacement = new TimeEntryInfo(ForgeIds.New(pipeline.Time), sheet.TimesheetId, original.WorkDate, order, sequence, code, c.Hours, Text(c.Note, 500), TimeEntryState.Active,
                    context.Actor, now, original.LateReason, original.EntryId, reason);
                await store.InsertEntryAsync(replacement, ct);
                message = string.Create(CultureInfo.InvariantCulture, $"{original.WorkDate:yyyy-MM-dd} corrected from {original.Hours:0.##} h ({original.Charge}) to {c.Hours:0.##} h ({replacement.Charge})");
            }

            var reopened = sheet.Status is TimesheetStatus.Signed or TimesheetStatus.Approved;
            if (reopened) await store.SetTimesheetStatusAsync(sheet.TimesheetId, TimesheetStatus.Open, now, context.Actor, reason, ct);
            var id = sheet.TimesheetId.ToString("D");
            return new CommandOutcome(
                CommandResult.Ok(id, $"{message}.{(reopened ? $" The week was {sheet.Status}; sign it again for approval." : string.Empty)}"),
                [pipeline.Event(new TimesheetChanged(sheet.TimesheetId, person, sheet.WeekStart, reopened ? "CorrectedAndReopened" : "Corrected"), context, SourceContext, Entity, id)],
                new AuditChange(Entity, id, CanonicalJson.Serialize(original), replacement is null ? null : CanonicalJson.Serialize(replacement)));
        }, ct);
    }

    public Task<CommandResult> SignAsync(SignTimesheet c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var person = context.Principal.PersonId.Value;
        var week = TimesheetInfo.WeekOf(c.WeekStart);
        var spec = new CommandSpec(SourceContext, nameof(SignTimesheet), Permissions.TimekeepingRecord, ResourceRef.Of(Entity, Key(person, week)));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var sheet = await uow.Store<ITimesheetLookup>().FindTimesheetAsync(person, week, ct)
                ?? throw new CommandRejectedException("timesheet_not_found", $"You have no time recorded for the week of {week:yyyy-MM-dd}.");
            await uow.Store<ITimesheetStore>().LockTimesheetAsync(sheet.TimesheetId, ct);
            if (sheet.Status is not (TimesheetStatus.Open or TimesheetStatus.Returned))
                throw new CommandRejectedException("not_open", $"The week of {week:yyyy-MM-dd} is already {sheet.Status}.");
            if (sheet.Hours == 0) throw new CommandRejectedException("no_time", "Record the week's time before signing it.");
            await uow.Store<ITimesheetStore>().SetTimesheetStatusAsync(sheet.TimesheetId, TimesheetStatus.Signed, now, context.Actor, null, ct);
            var id = sheet.TimesheetId.ToString("D");
            return new CommandOutcome(
                CommandResult.Ok(id, string.Create(CultureInfo.InvariantCulture, $"Week of {week:yyyy-MM-dd} signed: you certified {sheet.Hours:0.##} h as complete and correct. Your supervisor approves it.")),
                [pipeline.Event(new TimesheetChanged(sheet.TimesheetId, person, week, "Signed"), context, SourceContext, Entity, id)],
                new AuditChange(Entity, id, sheet.Status.ToString(), nameof(TimesheetStatus.Signed)));
        }, ct);
    }

    public Task<CommandResult> ApproveAsync(ApproveTimesheet c, CommandContext context, CancellationToken ct) => DecideAsync(c.TimesheetId, true, c, context, ct);

    public Task<CommandResult> ReturnAsync(ReturnTimesheet c, CommandContext context, CancellationToken ct) => DecideAsync(c.TimesheetId, false, c, context, ct);

    private Task<CommandResult> DecideAsync<TCommand>(Guid timesheetId, bool approve, TCommand command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = timesheetId.ToString("D");
        var spec = new CommandSpec(SourceContext, approve ? nameof(ApproveTimesheet) : nameof(ReturnTimesheet), Permissions.TimekeepingApprove, ResourceRef.Of(Entity, id), ReasonRequired: !approve);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            await uow.Store<ITimesheetStore>().LockTimesheetAsync(timesheetId, ct);
            var sheet = await uow.Store<ITimesheetLookup>().FindTimesheetAsync(timesheetId, ct)
                ?? throw new CommandRejectedException("timesheet_not_found", "That timesheet does not exist.");
            if (sheet.PersonId == context.Principal.PersonId.Value)
                throw new CommandRejectedException("separation_of_duties", "Someone other than the employee approves or returns their timesheet.");
            if (approve ? sheet.Status != TimesheetStatus.Signed : sheet.Status is not (TimesheetStatus.Signed or TimesheetStatus.Approved))
                throw new CommandRejectedException("not_signed", $"This timesheet is {sheet.Status}; {(approve ? "only a signed week is approved" : "only a signed or approved week is returned")}.");
            var status = approve ? TimesheetStatus.Approved : TimesheetStatus.Returned;
            await uow.Store<ITimesheetStore>().SetTimesheetStatusAsync(timesheetId, status, now, context.Actor, approve ? null : context.Reason, ct);
            return new CommandOutcome(
                CommandResult.Ok(id, approve
                    ? string.Create(CultureInfo.InvariantCulture, $"Week of {sheet.WeekStart:yyyy-MM-dd} approved: {sheet.Hours:0.##} h.")
                    : $"Week of {sheet.WeekStart:yyyy-MM-dd} returned to the employee: {context.Reason}"),
                [pipeline.Event(new TimesheetChanged(timesheetId, sheet.PersonId, sheet.WeekStart, status.ToString()), context, SourceContext, Entity, id)],
                new AuditChange(Entity, id, sheet.Status.ToString(), status.ToString()));
        }, ct);
    }

    private async Task<(long? Order, int? Sequence, string? Code)> ChargeAsync(IKernelUnitOfWork uow, CommandContext context, long? order, int? sequence, string? rawCode, CancellationToken ct)
    {
        var code = string.IsNullOrWhiteSpace(rawCode) ? null : BusinessCode.Require(rawCode, "Charge");
        if ((order is null) == (code is null) || (order is null) != (sequence is null))
            throw new CommandRejectedException("charge_required", "Charge the time to an order and its operation, or to a charge code.");
        if (code is not null)
        {
            var charge = await uow.Store<ITimesheetLookup>().FindChargeCodeAsync(code, ct)
                ?? throw new CommandRejectedException("charge_code_not_found", $"Charge code {code} does not exist.");
            if (!charge.Active) throw new CommandRejectedException("charge_code_inactive", $"Charge code {code} takes no new time.");
            return (null, null, code);
        }

        if (await orders.OrderChargeProblemAsync(uow, context.Principal, order!.Value, sequence!.Value, ct) is { } problem)
            throw new CommandRejectedException("order_not_chargeable", problem);
        return (order, sequence, null);
    }

    private static void Hours(decimal hours, bool allowZero)
    {
        if ((allowZero ? hours < 0 : hours <= 0) || hours > 24 || hours * 4 != decimal.Truncate(hours * 4))
            throw new CommandRejectedException("invalid_hours", "Hours are recorded in quarter hours, up to 24 in a day.");
    }

    private static void DayTotal(TimesheetInfo sheet, DateOnly day, decimal adding, Guid? replacing)
    {
        var total = sheet.Entries.Where(e => e.State == TimeEntryState.Active && e.WorkDate == day && e.EntryId != replacing).Sum(e => e.Hours) + adding;
        if (total > 24) throw new CommandRejectedException("day_over_24", string.Create(CultureInfo.InvariantCulture, $"{day:yyyy-MM-dd} would have {total:0.##} h; a day has at most 24."));
    }

    private static string Key(Guid person, DateOnly week) => string.Create(CultureInfo.InvariantCulture, $"{person:D}/{week:yyyy-MM-dd}");

    private static string? Text(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }
}
