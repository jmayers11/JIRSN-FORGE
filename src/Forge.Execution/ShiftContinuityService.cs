using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;
using NodaTime;

namespace Forge.Execution;

/// <summary>Audited shift pass-on, acknowledgment, actions and immutable operational closeout.</summary>
public sealed class ShiftContinuityService(
    CommandPipeline pipeline,
    IKernelUnitOfWorkFactory unitOfWork,
    IShiftFactsProvider factsProvider,
    IForgeAuthorizer authorizer)
{
    private const string Entity = "ShiftHandoff";

    public async Task<IReadOnlyList<ShiftChecklistItem>> ChecklistAsync(string workCenter, ForgePrincipal principal, CancellationToken ct)
    {
        var resource = WorkCenter(workCenter);
        Demand(principal, Permissions.ResourcesRead, resource);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        await ValidateWorkCenterAsync(uow, workCenter, ct);
        return await uow.Store<IShiftContinuityStore>().ListChecklistAsync(workCenter, ct);
    }

    public Task<CommandResult> ConfigureChecklistAsync(ConfigureShiftChecklistItem command, CommandContext context, CancellationToken ct)
    {
        var workCenter = Code(command.WorkCenterCode, "Work center");
        var code = Code(command.Code, "Checklist code");
        var description = Required(command.Description, 240, "Checklist description");
        if (command.SortOrder < 0) throw Invalid("invalid_sort_order", "Checklist order cannot be negative.");
        var item = new ShiftChecklistItem(code, description, command.Required, command.SortOrder, command.Active);
        var spec = new CommandSpec("Execution", nameof(ConfigureChecklistAsync), Permissions.ResourcesManage, WorkCenter(workCenter), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            await ValidateWorkCenterAsync(uow, workCenter, ct);
            var previous = (await uow.Store<IShiftContinuityStore>().ListChecklistAsync(workCenter, ct)).SingleOrDefault(i => i.Code == code);
            await uow.Store<IShiftContinuityStore>().SaveChecklistItemAsync(workCenter, item, now, context.Actor, ct);
            return new CommandOutcome(CommandResult.Ok(code, "Shift checklist item saved for future handoffs."),
                [pipeline.Event(new ShiftChecklistChanged(workCenter, code, item.Active), context, "Execution", "ShiftChecklistItem", $"{workCenter}/{code}", null)],
                new AuditChange("ShiftChecklistItem", $"{workCenter}/{code}", previous is null ? null : Forge.Kernel.Serialization.CanonicalJson.Serialize(previous), Forge.Kernel.Serialization.CanonicalJson.Serialize(item)));
        }, ct);
    }

    public async Task<IReadOnlyList<ShiftHandoff>> HandoffsAsync(string? workCenter, int limit, ForgePrincipal principal, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        DemandAny(principal, [Permissions.ShopFloorRead, Permissions.WorkforceRead], workCenter is null ? ResourceRef.Of("ShiftHandoff") : WorkCenter(workCenter));
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return (await uow.Store<IShiftContinuityStore>().ListHandoffsAsync(workCenter, limit, ct)).Select(h => FilterVisible(h, principal)).ToList();
    }

    public async Task<ShiftHandoff?> HandoffAsync(long id, ForgePrincipal principal, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var handoff = await uow.Store<IShiftContinuityStore>().FindHandoffAsync(id, ct);
        if (handoff is not null) DemandAny(principal, [Permissions.ShopFloorRead, Permissions.WorkforceRead], WorkCenter(handoff.WorkCenterCode, handoff.ShiftCode));
        return handoff is null ? null : FilterVisible(handoff, principal);
    }

    public async Task<IReadOnlyList<ShiftAcknowledgment>> AcknowledgmentsAsync(long id, ForgePrincipal principal, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var handoff = await uow.Store<IShiftContinuityStore>().FindHandoffAsync(id, ct);
        if (handoff is null) return [];
        DemandAny(principal, [Permissions.ShopFloorRead, Permissions.WorkforceRead], WorkCenter(handoff.WorkCenterCode, handoff.ShiftCode));
        return await uow.Store<IShiftContinuityStore>().ListAcknowledgmentsAsync(id, ct);
    }

    public Task<CommandResult> CreateHandoffAsync(CreateShiftHandoff command, CommandContext context, CancellationToken ct)
    {
        var wc = Code(command.WorkCenterCode, "Work center");
        var shift = Code(command.ShiftCode, "Shift code");
        ValidateWindow(command.ShiftFrom, command.ShiftUntil);
        var spec = new CommandSpec("Execution", nameof(CreateHandoffAsync), Permissions.ShopFloorRecord, WorkCenter(wc, shift), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            await ValidateShiftAsync(uow, wc, shift, command.ShiftFrom, command.ShiftUntil, ct);
            if (command.ShiftUntil > now) throw Invalid("shift_not_ended", "A pass-on can be captured only after the configured shift ends.");
            var snapshot = await factsProvider.BuildAsync(uow, wc, shift, command.ShiftFrom, command.ShiftUntil, context.Principal, ct);
            var template = await uow.Store<IShiftContinuityStore>().ListChecklistAsync(wc, ct);
            var responses = BuildChecklist(template, command.Checklist ?? [], context.Actor, now);
            var contextNote = Required(command.Context, 2000, "Handoff context");
            var id = await uow.Store<IShiftContinuityStore>().InsertHandoffAsync(wc, shift, command.ShiftFrom, command.ShiftUntil, now, context.Actor,
                contextNote, snapshot, responses, ct);
            if (id is null) throw Invalid("shift_handoff_exists", "A handoff already exists for this work center and shift window.");
            var handoffId = id.Value;
            return new CommandOutcome(CommandResult.Ok(handoffId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Shift pass-on captured from current operational records."),
                [pipeline.Event(new ShiftHandoffCreated(handoffId, wc, shift, command.ShiftFrom, command.ShiftUntil), context, "Execution", Entity, handoffId.ToString(), null)],
                new AuditChange(Entity, handoffId.ToString(), null, Forge.Kernel.Serialization.CanonicalJson.Serialize(new { wc, shift, command.ShiftFrom, command.ShiftUntil, snapshot.AsOf })));
        }, ct);
    }

    public Task<CommandResult> ReviewAsync(ReviewShiftHandoff command, CommandContext context, CancellationToken ct) =>
        ExecuteForHandoff(command.HandoffId, nameof(ReviewAsync), Permissions.WorkforceSupervise, command, context, async (uow, handoff, now) =>
        {
            if (handoff.ReviewedAt is not null) throw Invalid("handoff_already_reviewed", "This handoff has already been reviewed.");
            if (!await uow.Store<IShiftContinuityStore>().MarkReviewedAsync(handoff.HandoffId, now, context.Actor, ct))
                throw Invalid("handoff_already_reviewed", "This handoff has already been reviewed.");
            return new CommandOutcome(CommandResult.Ok(handoff.HandoffId.ToString(), "Handoff reviewed."),
                [pipeline.Event(new ShiftHandoffReviewed(handoff.HandoffId), context, "Execution", Entity, handoff.HandoffId.ToString(), null)],
                new AuditChange(Entity, handoff.HandoffId.ToString(), null, "reviewed"));
        }, ct);

    public Task<CommandResult> AcknowledgeAsync(AcknowledgeShiftHandoff command, CommandContext context, CancellationToken ct) =>
        ExecuteForHandoff(command.HandoffId, nameof(AcknowledgeAsync), Permissions.ShopFloorRecord, command, context, async (uow, handoff, now) =>
        {
            if (handoff.ReviewedAt is null) throw Invalid("handoff_not_reviewed", "A supervisor must review the handoff before the incoming shift acknowledges it.");
            var person = context.Principal.PersonId.Value;
            if (handoff.CreatedBy == context.Actor || handoff.ReviewedBy == context.Actor)
                throw Invalid("independent_acknowledgment_required", "The handoff creator or reviewer cannot acknowledge the incoming handoff.");
            var ack = new ShiftAcknowledgment(handoff.HandoffId, person, now, Optional(command.Note, 1000));
            if (!await uow.Store<IShiftContinuityStore>().InsertAcknowledgmentAsync(ack, ct))
                throw Invalid("handoff_already_acknowledged", "You have already acknowledged this handoff.");
            return new CommandOutcome(CommandResult.Ok(handoff.HandoffId.ToString(), "Handoff acknowledged. Assigned actions remain open until each is completed."),
                [pipeline.Event(new ShiftHandoffAcknowledged(handoff.HandoffId, person), context, "Execution", Entity, handoff.HandoffId.ToString(), null)],
                new AuditChange(Entity, handoff.HandoffId.ToString(), null, Forge.Kernel.Serialization.CanonicalJson.Serialize(ack)));
        }, ct);

    public Task<CommandResult> AssignActionAsync(AssignShiftAction command, CommandContext context, CancellationToken ct) =>
        ExecuteForHandoff(command.HandoffId, nameof(AssignActionAsync), Permissions.WorkforceSupervise, command, context, async (uow, handoff, now) =>
        {
            if (await uow.Store<IWorkforceLookup>().WorkerExistsAsync(command.Assignee, ct) is false)
                throw Invalid("unknown_assignee", "An action can be assigned only to a registered worker.");
            if (handoff.ReviewedAt is null) throw Invalid("handoff_not_reviewed", "Review the handoff before assigning follow-up actions.");
            var description = Required(command.Description, 1000, "Action description");
            var kind = Optional(command.SourceKind, 80);
            var reference = Optional(command.SourceReference, 160);
            if ((kind is null) != (reference is null)) throw Invalid("incomplete_source_reference", "Provide both source kind and source reference, or neither.");
            var blockedBy = command.BlockedByActionIds ?? [];
            if (blockedBy.Count > 20 || blockedBy.Any(id => id <= 0) || blockedBy.Distinct().Count() != blockedBy.Count)
                throw Invalid("invalid_action_dependencies", "Choose at most 20 distinct, valid action dependencies.");
            Guid? programId = null;
            if (kind is not null && reference is not null)
            {
                var source = await ValidateSourceAsync(uow, handoff.WorkCenterCode, kind, reference, context.Principal, ct);
                kind = source.Kind;
                reference = source.Reference;
                programId = source.ProgramId;
            }
            var existingActions = VisibleActions(await uow.Store<IShiftContinuityStore>().ListActionsAsync(handoff.HandoffId, ct), context.Principal)
                .Where(action => action.ProgramId == programId).ToList();
            if (blockedBy.Except(existingActions.Select(action => action.ActionId)).Any())
                throw Invalid("unknown_action_dependency", "Dependencies must be existing actions on this handoff that are visible in the same program scope.");
            var id = await uow.Store<IShiftContinuityStore>().InsertActionAsync(handoff.HandoffId, description, kind, reference, command.Assignee, programId,
                command.DueAt, blockedBy, now, context.Actor, ct);
            var action = await uow.Store<IShiftContinuityStore>().FindActionAsync(id, ct) ?? throw new InvalidOperationException("Created action could not be reloaded.");
            return new CommandOutcome(CommandResult.Ok(id.ToString(), "Shift action assigned; the source record remains unchanged."),
                [pipeline.Event(new ShiftActionChanged(handoff.HandoffId, id, action.Status), context, "Execution", "ShiftAction", id.ToString(), programId is { } p ? new ProgramId(p) : null)],
                new AuditChange("ShiftAction", id.ToString(), null, Forge.Kernel.Serialization.CanonicalJson.Serialize(action), programId is { } auditProgram ? new ProgramId(auditProgram) : null));
        }, ct);

    public async Task<IReadOnlyList<ShiftAction>> ActionsAsync(long handoffId, ForgePrincipal principal, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var handoff = await uow.Store<IShiftContinuityStore>().FindHandoffAsync(handoffId, ct);
        if (handoff is null) return [];
        DemandAny(principal, [Permissions.ShopFloorRead, Permissions.WorkforceRead], WorkCenter(handoff.WorkCenterCode, handoff.ShiftCode));
        return VisibleActions(await uow.Store<IShiftContinuityStore>().ListActionsAsync(handoffId, ct), principal);
    }

    public Task<CommandResult> TransitionActionAsync(TransitionShiftAction command, CommandContext context, CancellationToken ct) =>
        ExecuteForAction(command.ActionId, nameof(TransitionActionAsync), Permissions.ShopFloorRecord, command, context, async (uow, action, now) =>
        {
            var person = context.Principal.PersonId.Value;
            if (action.Assignee != person && !context.Principal.HasPermission(Permissions.WorkforceSupervise, WorkCenter(
                    (await uow.Store<IShiftContinuityStore>().FindHandoffAsync(action.HandoffId, ct))!.WorkCenterCode)))
                throw Invalid("not_action_assignee", "Only the assigned worker or a supervisor may update this action.");
            if (!Next(command.Expected, command.Status)) throw Invalid("invalid_action_transition", "Action status advances one step at a time: assigned, acknowledged, in progress, completed.");
            if ((command.Status is ShiftActionStatus.InProgress or ShiftActionStatus.Completed) && (action.BlockedByActionIds?.Count ?? 0) > 0)
            {
                var actions = await uow.Store<IShiftContinuityStore>().ListActionsAsync(action.HandoffId, ct);
                var unresolved = actions.Where(candidate => (action.BlockedByActionIds ?? []).Contains(candidate.ActionId) && candidate.Status != ShiftActionStatus.Completed).ToList();
                if (unresolved.Count > 0)
                    throw Invalid("action_dependencies_open", $"Complete blocking action(s) {string.Join(", ", unresolved.Select(candidate => candidate.ActionId))} before starting or completing this action.");
            }
            var completionEvidence = command.Status == ShiftActionStatus.Completed ? Required(command.Evidence, 2000, "Completion evidence") : null;
            if (!await uow.Store<IShiftContinuityStore>().UpdateActionStatusAsync(action.ActionId, command.Expected, command.Status, completionEvidence, now, context.Actor, ct))
                throw Invalid("stale_action_status", "Action status changed; reload and try again.");
            var updated = await uow.Store<IShiftContinuityStore>().FindActionAsync(action.ActionId, ct) ?? throw new InvalidOperationException("Updated action could not be reloaded.");
            return new CommandOutcome(CommandResult.Ok(action.ActionId.ToString(), $"Action marked {command.Status}; referenced issue records are unchanged."),
                [pipeline.Event(new ShiftActionChanged(action.HandoffId, action.ActionId, command.Status), context, "Execution", "ShiftAction", action.ActionId.ToString(), action.ProgramId is { } p ? new ProgramId(p) : null)],
                new AuditChange("ShiftAction", action.ActionId.ToString(), Forge.Kernel.Serialization.CanonicalJson.Serialize(action),
                    Forge.Kernel.Serialization.CanonicalJson.Serialize(updated), action.ProgramId is { } auditProgram ? new ProgramId(auditProgram) : null));
        }, command.Expected, ct);

    public async Task<IReadOnlyList<ShiftCloseout>> CloseoutsAsync(string? workCenter, int limit, ForgePrincipal principal, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        Demand(principal, Permissions.WorkforceSupervise, workCenter is null ? ResourceRef.Of("ShiftCloseout") : WorkCenter(workCenter));
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return (await uow.Store<IShiftContinuityStore>().ListCloseoutsAsync(workCenter, limit, ct))
            .Select(c => c with { Facts = FilterFacts(c.Facts, principal) }).ToList();
    }

    public Task<CommandResult> CloseShiftAsync(CloseShift command, CommandContext context, CancellationToken ct)
    {
        var wc = Code(command.WorkCenterCode, "Work center");
        var shift = Code(command.ShiftCode, "Shift code");
        ValidateWindow(command.ShiftFrom, command.ShiftUntil);
        var spec = new CommandSpec("Execution", nameof(CloseShiftAsync), Permissions.WorkforceSupervise, WorkCenter(wc, shift), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            await ValidateShiftAsync(uow, wc, shift, command.ShiftFrom, command.ShiftUntil, ct);
            if (command.ShiftUntil > now) throw Invalid("shift_not_ended", "A shift cannot be closed before its configured end.");
            var facts = await factsProvider.BuildAsync(uow, wc, shift, command.ShiftFrom, command.ShiftUntil, context.Principal, ct);
            var shiftHandoff = (await uow.Store<IShiftContinuityStore>().ListHandoffsAsync(wc, 100, ct))
                .FirstOrDefault(h => h.ShiftCode == shift && h.ShiftFrom == command.ShiftFrom && h.ShiftUntil == command.ShiftUntil);
            if (shiftHandoff is null)
            {
                facts = facts with { Facts = [.. facts.Facts, new ShiftOperationalFact("Handoff", $"work-center:{wc}:shift:{shift}", "Missing", "No pass-on was recorded for this shift window.")] };
            }
            else
            {
                var handoffFacts = new List<ShiftOperationalFact>();
                foreach (var item in shiftHandoff.Checklist.Where(i => i.State == "Open"))
                    handoffFacts.Add(new ShiftOperationalFact("Checklist", $"handoff:{shiftHandoff.HandoffId}:checklist:{item.Code}", "Open", $"Checklist remains open: {item.Description}"));
                var acknowledgments = await uow.Store<IShiftContinuityStore>().ListAcknowledgmentsAsync(shiftHandoff.HandoffId, ct);
                if (acknowledgments.Count == 0)
                    handoffFacts.Add(new ShiftOperationalFact("Handoff", $"handoff:{shiftHandoff.HandoffId}", "Unacknowledged", "Incoming shift has not acknowledged the pass-on."));
                foreach (var action in await uow.Store<IShiftContinuityStore>().ListActionsAsync(shiftHandoff.HandoffId, ct))
                {
                    if (action.Status != ShiftActionStatus.Completed && (action.ProgramId is not { } program || context.Principal.Programs.Contains(new ProgramId(program))))
                        handoffFacts.Add(new ShiftOperationalFact("ShiftAction", $"shift-action:{action.ActionId}", action.Status.ToString(), action.Description, action.ProgramId));
                }
                facts = facts with { Facts = [.. facts.Facts, .. handoffFacts] };
            }
            var id = await uow.Store<IShiftContinuityStore>().InsertCloseoutAsync(wc, shift, command.ShiftFrom, command.ShiftUntil, now, context.Actor, facts, Optional(command.Note, 2000), ct);
            if (id is null) throw Invalid("shift_already_closed", "This work center shift has already been closed.");
            return new CommandOutcome(CommandResult.Ok(id.Value.ToString(), "Shift closeout recorded from production, material, WIP, downtime, maintenance and open-issue records."),
                [pipeline.Event(new ShiftClosed(id.Value, wc, shift, command.ShiftFrom, command.ShiftUntil), context, "Execution", "ShiftCloseout", id.Value.ToString(), null)],
                new AuditChange("ShiftCloseout", id.Value.ToString(), null, Forge.Kernel.Serialization.CanonicalJson.Serialize(new { wc, shift, command.ShiftFrom, command.ShiftUntil, asOf = facts.AsOf, note = command.Note })));
        }, ct);
    }

    private async Task<CommandResult> ExecuteForHandoff<T>(long id, string commandType, string permission, T command, CommandContext context,
        Func<IKernelUnitOfWork, ShiftHandoff, DateTimeOffset, Task<CommandOutcome>> body, CancellationToken ct)
    {
        await using var read = await unitOfWork.BeginSnapshotAsync(ct);
        var handoff = await read.Store<IShiftContinuityStore>().FindHandoffAsync(id, ct)
            ?? throw Invalid("handoff_not_found", $"Handoff {id} does not exist.");
        var spec = new CommandSpec("Execution", commandType, permission, WorkCenter(handoff.WorkCenterCode, handoff.ShiftCode), ReasonRequired: commandType == nameof(AssignActionAsync));
        return await pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var current = await uow.Store<IShiftContinuityStore>().FindHandoffAsync(id, ct)
                ?? throw Invalid("handoff_not_found", $"Handoff {id} does not exist.");
            return await body(uow, current, now);
        }, ct);
    }

    private async Task<CommandResult> ExecuteForAction<T>(long id, string commandType, string permission, T command, CommandContext context,
        Func<IKernelUnitOfWork, ShiftAction, DateTimeOffset, Task<CommandOutcome>> body, ShiftActionStatus? expected, CancellationToken ct)
    {
        await using var read = await unitOfWork.BeginSnapshotAsync(ct);
        var action = await read.Store<IShiftContinuityStore>().FindActionAsync(id, ct) ?? throw Invalid("action_not_found", $"Action {id} does not exist.");
        if (action.ProgramId is { } program && !context.Principal.Programs.Contains(new ProgramId(program)))
            throw Invalid("action_not_found", $"Action {id} does not exist.");
        var handoff = await read.Store<IShiftContinuityStore>().FindHandoffAsync(action.HandoffId, ct) ?? throw Invalid("handoff_not_found", "The action's handoff no longer exists.");
        var elevated = context.Principal.HasPermission(Permissions.WorkforceSupervise, WorkCenter(handoff.WorkCenterCode, handoff.ShiftCode));
        var effectivePermission = elevated ? Permissions.WorkforceSupervise : Permissions.ShopFloorRecord;
        var spec = new CommandSpec("Execution", commandType, effectivePermission, WorkCenter(handoff.WorkCenterCode, handoff.ShiftCode), ReasonRequired: true);
        return await pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var current = await uow.Store<IShiftContinuityStore>().FindActionAsync(id, ct) ?? throw Invalid("action_not_found", $"Action {id} does not exist.");
            if (current.ProgramId is { } program && !context.Principal.Programs.Contains(new ProgramId(program)))
                throw Invalid("action_not_found", $"Action {id} does not exist.");
            if (expected is { } e && current.Status != e) throw Invalid("stale_action_status", "Action status changed; reload and try again.");
            return await body(uow, current, now);
        }, ct);
    }

    private static async Task ValidateShiftAsync(IKernelUnitOfWork uow, string workCenter, string shiftCode, DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        await ValidateWorkCenterAsync(uow, workCenter, ct);
        var resources = uow.Store<IResourcesLookup>();
        var shift = (await resources.ListShiftsAsync(ct)).SingleOrDefault(s => s.Code == shiftCode)
            ?? throw Invalid("unknown_shift", $"Shift {shiftCode} does not exist.");
        if (!(await resources.ListWorkCenterShiftsAsync(ct)).Contains(new WorkCenterShiftInfo(workCenter, shiftCode)))
            throw Invalid("shift_not_assigned", $"Shift {shiftCode} is not assigned to work center {workCenter}.");
        var center = await resources.FindWorkCenterAsync(workCenter, ct) ?? throw Invalid("unknown_work_center", $"Work center {workCenter} does not exist.");
        var area = await resources.FindSafetyAreaAsync(center.SafetyAreaCode, ct) ?? throw Invalid("unknown_site", "The work center's site is unavailable.");
        var sites = await resources.ListSitesAsync(ct);
        var site = sites.SingleOrDefault(s => s.Code == area.SiteCode) ?? throw Invalid("unknown_site", "The work center's site is unavailable.");
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(site.TimeZone) ?? throw Invalid("invalid_site_timezone", $"Site time zone {site.TimeZone} is invalid.");
        var local = Instant.FromDateTimeOffset(from).InZone(zone).LocalDateTime;
        var weekday = local.Date.DayOfWeek == IsoDayOfWeek.Sunday ? 7 : (int)local.Date.DayOfWeek;
        if (!shift.Days.Contains(weekday) || local.TimeOfDay.Hour != shift.Start.Hour || local.TimeOfDay.Minute != shift.Start.Minute || local.TimeOfDay.Second != shift.Start.Second || from.AddMinutes(shift.DurationMinutes) != until)
            throw Invalid("invalid_shift_window", $"Shift window must match {shift.Code}: local start {shift.Start:HH\\:mm\\:ss} on ISO weekdays {string.Join(",", shift.Days)}, duration {shift.DurationMinutes} minutes in {site.TimeZone}; received {local:yyyy-MM-dd HH:mm:ss} to {until:O}.");
    }

    private static IReadOnlyList<ShiftChecklistEntry> BuildChecklist(IReadOnlyList<ShiftChecklistItem> template, IReadOnlyList<ShiftChecklistResponse> submitted, string actor, DateTimeOffset at)
    {
        if (submitted.Select(i => i.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != submitted.Count)
            throw Invalid("duplicate_checklist_entry", "Each checklist item may appear only once.");
        var active = template.Where(i => i.Active).ToDictionary(i => i.Code, StringComparer.OrdinalIgnoreCase);
        if (submitted.Any(i => !active.ContainsKey(i.Code))) throw Invalid("unknown_checklist_entry", "The checklist includes an item that is not active for this work center.");
        foreach (var item in submitted)
        {
            if (item.State is not ("Complete" or "NotApplicable" or "Open")) throw Invalid("invalid_checklist_state", "Checklist state must be Complete, NotApplicable or Open.");
            if (item.State != "Complete" && string.IsNullOrWhiteSpace(item.Note)) throw Invalid("checklist_note_required", "Open and not-applicable checklist entries need a note.");
        }
        var result = new List<ShiftChecklistEntry>();
        foreach (var item in template.Where(i => i.Active).OrderBy(i => i.SortOrder).ThenBy(i => i.Code))
        {
            var supplied = submitted.SingleOrDefault(i => string.Equals(i.Code, item.Code, StringComparison.OrdinalIgnoreCase));
            if (supplied is null && item.Required) throw Invalid("required_checklist_missing", $"Required checklist item {item.Code} must be recorded.");
            result.Add(new ShiftChecklistEntry(item.Code, item.Description, item.Required, supplied?.State ?? "Open", Optional(supplied?.Note, 1000), supplied is null ? null : actor, supplied is null ? null : at));
        }
        return result;
    }

    private static bool Next(ShiftActionStatus before, ShiftActionStatus after) => (before, after) is
        (ShiftActionStatus.Assigned, ShiftActionStatus.Acknowledged) or
        (ShiftActionStatus.Acknowledged, ShiftActionStatus.InProgress) or
        (ShiftActionStatus.InProgress, ShiftActionStatus.Completed);

    private static async Task<(string Kind, string Reference, Guid? ProgramId)> ValidateSourceAsync(
        IKernelUnitOfWork uow, string workCenter, string kind, string reference, ForgePrincipal principal, CancellationToken ct)
    {
        switch (kind.Trim().ToUpperInvariant())
        {
            case "ORDER" when long.TryParse(reference, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var orderNumber):
            {
                var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
                if (order is null || order.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program)) || !order.Operations.Any(o => o.WorkCenterCode == workCenter))
                    throw Invalid("source_not_found", "The linked order is not visible or does not use this work center.");
                return ("Order", orderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), order.ProgramId);
            }
            case "MACHINE":
            {
                var machine = await uow.Store<IResourcesLookup>().FindMachineAsync(reference.Trim().ToUpperInvariant(), ct);
                if (machine is null || machine.WorkCenterCode != workCenter) throw Invalid("source_not_found", "The linked machine is not in this work center.");
                return ("Machine", machine.Code, null);
            }
            case "WORKORDER" when long.TryParse(reference, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var workOrderId):
            {
                var workOrder = await uow.Store<IMaintenanceLookup>().FindWorkOrderAsync(workOrderId, ct);
                var machine = workOrder?.MachineCode is { } machineCode ? await uow.Store<IResourcesLookup>().FindMachineAsync(machineCode, ct) : null;
                if (workOrder is null || machine?.WorkCenterCode != workCenter) throw Invalid("source_not_found", "The linked maintenance work order is not in this work center.");
                return ("WorkOrder", workOrderId.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
            }
            case "TOOL":
            {
                var tool = await uow.Store<IToolLookup>().FindToolAsync(reference.Trim().ToUpperInvariant(), ct);
                var machines = (await uow.Store<IResourcesLookup>().ListMachinesAsync(ct)).Where(m => m.WorkCenterCode == workCenter).Select(m => m.Code).ToHashSet(StringComparer.Ordinal);
                if (tool is null || tool.Location != workCenter && !tool.EligibleMachines.Any(machines.Contains)) throw Invalid("source_not_found", "The linked tool is not located or eligible at this work center.");
                return ("Tool", tool.Code, null);
            }
            default:
                throw Invalid("unsupported_source_kind", "Shift actions may link to an existing Order, Machine, WorkOrder or Tool record.");
        }
    }

    private static ShiftHandoff FilterVisible(ShiftHandoff handoff, ForgePrincipal principal)
    {
        var snapshot = FilterFacts(handoff.Snapshot, principal);
        var facts = snapshot.Facts;
        return handoff with { Snapshot = snapshot, Facts = facts };
    }

    private static ShiftCloseoutFacts FilterFacts(ShiftCloseoutFacts facts, ForgePrincipal principal)
    {
        var visible = facts.Facts.Where(f => f.ProgramId is not { } program || principal.Programs.Contains(new ProgramId(program))).ToList();
        var unknowns = visible.Count == facts.Facts.Count
            ? facts.Unknowns
            : facts.Unknowns.Append("Some program-scoped source facts are hidden under your current access.").Distinct(StringComparer.Ordinal).ToList();
        var reports = visible.Where(f => f.Kind == "ProductionReport").ToList();
        return facts with
        {
            Facts = visible,
            Unknowns = unknowns,
            Production = new ShiftProductionTotals(reports.Count, reports.Sum(f => f.GoodQuantity ?? 0), reports.Sum(f => f.ScrapQuantity ?? 0),
                reports.Count(f => f.CompletedOperation == true), visible.Count(f => f.Kind == "WorkInProgress")),
        };
    }

    private static IReadOnlyList<ShiftAction> VisibleActions(IReadOnlyList<ShiftAction> actions, ForgePrincipal principal) =>
        actions.Where(a => a.ProgramId is not { } program || principal.Programs.Contains(new ProgramId(program))).ToList();

    private static async Task ValidateWorkCenterAsync(IKernelUnitOfWork uow, string workCenter, CancellationToken ct)
    {
        if (await uow.Store<IResourcesLookup>().FindWorkCenterAsync(workCenter, ct) is null)
            throw Invalid("unknown_work_center", $"Work center {workCenter} does not exist.");
    }

    private void Demand(ForgePrincipal principal, string permission, ResourceRef resource)
    {
        var decision = authorizer.Authorize(principal, permission, resource);
        if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision);
    }
    private void DemandAny(ForgePrincipal principal, IReadOnlyList<string> permissions, ResourceRef resource)
    {
        if (!permissions.Any(p => authorizer.Authorize(principal, p, resource).IsPermitted))
            throw new ForgeAuthorizationException(authorizer.Authorize(principal, permissions[0], resource));
    }
    private static ResourceRef WorkCenter(string code, string? shift = null) => new("WorkCenter", code, WorkCenterId: code, ShiftId: shift);
    private static void ValidateWindow(DateTimeOffset from, DateTimeOffset until)
    {
        if (until <= from || until - from > TimeSpan.FromHours(24)) throw Invalid("invalid_shift_window", "A shift window must be positive and no longer than 24 hours.");
    }
    private static string Code(string value, string field)
    {
        var code = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length is < 1 or > 40 || code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw Invalid("invalid_code", $"{field} must be 1–40 letters, digits, hyphens, underscores or periods.");
        return code;
    }
    private static string Required(string? value, int max, string label)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > max) throw Invalid("invalid_text", $"{label} is required and must be at most {max} characters.");
        return text;
    }
    private static string? Optional(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text.Length <= max ? text : throw Invalid("invalid_text", $"Text must be at most {max} characters.");
    }
    private static CommandRejectedException Invalid(string code, string message) => new(code, message);
}
