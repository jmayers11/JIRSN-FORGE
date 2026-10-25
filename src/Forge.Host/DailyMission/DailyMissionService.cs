using Forge.Host.WorkInbox;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Engines;
using Forge.Kernel.Holds;
using Forge.Kernel.Security;
using Forge.Inventory.Contracts;
using Forge.Execution.Contracts;
using Forge.Maintenance.Contracts;
using Forge.Planning;
using Forge.Planning.Engines;
using Forge.Resources.Contracts;
using Forge.Workforce;
using Forge.Workforce.Contracts;

namespace Forge.Host.DailyMission;

public sealed record DailyMissionSource(string Code, string Status, DateTimeOffset? AsOf, string Scope, string? Note = null);

/// <summary>A pointer into an existing authorized workflow; this record owns no workflow state.</summary>
public sealed record DailyMissionItem(
    string Id,
    string Group,
    string Title,
    string Reason,
    string SourceType,
    string SourceId,
    string DeepLink,
    string SourceLifecycle,
    string SourceStatus,
    DateTimeOffset SourceTimestamp,
    DateOnly? Deadline = null);

public sealed record DailyMissionActor(string PersonId, string DisplayName, IReadOnlyList<string> Roles);
public sealed record DailyMissionOperationalContext(
    string SiteCode,
    string SiteName,
    string TimeZone,
    string ShiftCode,
    string ShiftName,
    DateOnly OperationalDate,
    DateTimeOffset ShiftStartsAt,
    DateTimeOffset ShiftEndsAt,
    string Relation,
    string OperationalDayId);

public sealed record DailyMissionResponse(
    DateTimeOffset GeneratedAt,
    DailyMissionActor Actor,
    DailyMissionOperationalContext? OperationalContext,
    IReadOnlyList<DailyMissionSource> Sources,
    IReadOnlyList<DailyMissionItem> Items,
    bool IsPartial,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Read-only mission projection. Every item comes from the existing permission-filtered inbox, and
/// workforce context is limited to the authenticated worker. Acting on an item remains in its source workflow.
/// </summary>
public sealed class DailyMissionService(
    WorkInboxStore inbox,
    MaterialPlanningService materialPlanning,
    MaintenancePlanningService maintenancePlanning,
    ReadinessService readiness,
    SkillCapacityService skillCapacity,
    IKernelUnitOfWorkFactory unitOfWork,
    IForgeAuthorizer authorizer,
    TimeProvider time,
    ILogger<DailyMissionService> logger)
{
    public Task<DailyMissionOperationalContext?> GetOperationalContextAsync(ForgePrincipal principal, CancellationToken ct)
    {
        var sources = new List<DailyMissionSource>();
        var warnings = new List<string>();
        return ReadOperationalContextAsync(principal, time.GetUtcNow(), sources, warnings, ct);
    }

    public async Task<IReadOnlyList<DailyMissionWorkspaceOption>> ProgramOptionsAsync(ForgePrincipal principal, CancellationToken ct)
    {
        var fallback = DailyMissionStateStore.ProgramOptionsFor(principal);
        if (principal.Programs.Count == 0
            || !authorizer.Authorize(principal, Permissions.ProgramsRead, ResourceRef.Of(Permissions.ProgramsRead)).IsPermitted)
            return fallback;

        try
        {
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            var visible = (await uow.Store<Forge.Programs.Contracts.IProgramsLookup>().ListProgramsAsync(ct))
                .Where(program => principal.Programs.Any(grant => grant.Value == program.ProgramId))
                .ToDictionary(program => program.ProgramId);
            return [new DailyMissionWorkspaceOption("all", "All authorized programs"), .. principal.Programs
                .Where(grant => visible.ContainsKey(grant.Value))
                .OrderBy(grant => grant.Value)
                .Select(grant => new DailyMissionWorkspaceOption($"program:{grant.Value:D}", $"{visible[grant.Value].Code} · {visible[grant.Value].Name}"))];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Daily Mission could not load authorized program labels for {PersonId}", principal.PersonId);
            return fallback;
        }
    }

    /// <summary>Read-only, source-revalidated safe coverage preview for one workforce-blocked order operation.</summary>
    public async Task<WorkforceRecoveryCapacity> WorkforceRecoveryAsync(ForgePrincipal principal, long orderNumber, int sequence, CancellationToken ct)
    {
        var readinessPermission = authorizer.Authorize(principal, Permissions.ReadinessRead, ResourceRef.Of("Readiness"));
        if (!readinessPermission.IsPermitted) throw new ForgeAuthorizationException(readinessPermission);

        var contextSources = new List<DailyMissionSource>();
        var contextWarnings = new List<string>();
        var operationalContext = await ReadOperationalContextAsync(principal, time.GetUtcNow(), contextSources, contextWarnings, ct);
        if (operationalContext is not { Relation: "Current" or "Upcoming" })
            throw new CommandRejectedException("shift_context_required", "A current or upcoming assigned shift is required for a workforce recovery preview.");

        // Re-run the authoritative order-readiness engine at click time. A stale mission card never
        // supplies candidate names or a move plan unless this exact operation is still workforce-blocked.
        var orderReadiness = await readiness.EvaluateOrderAsync(principal, orderNumber, ct);
        var operationReadiness = orderReadiness.Output.Operations.SingleOrDefault(operation => operation.Sequence == sequence);
        if (operationReadiness is not { Executable: false, PrimaryConstraint: "Workforce" })
            throw new CommandRejectedException("workforce_recovery_not_applicable", "This operation is no longer blocked primarily by workforce readiness. Refresh the mission and review its current source status.");

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var execution = uow.Store<IExecutionLookup>();
        var order = await execution.FindOrderAsync(orderNumber, ct);
        if (order is null || order.Status != OrderStatus.Released || (order.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program))))
            throw new CommandRejectedException("order_not_found", "The released order is no longer available in this program scope.");
        var operation = order.Operations.SingleOrDefault(candidate => candidate.Sequence == sequence && candidate.Status != OperationStatus.Complete)
            ?? throw new CommandRejectedException("operation_not_found", "The open operation is no longer available.");

        var resources = uow.Store<IResourcesLookup>();
        var workCenter = (await resources.ListWorkCentersAsync(ct)).SingleOrDefault(center => center.Code == operation.WorkCenterCode && center.Active)
            ?? throw new CommandRejectedException("work_center_unavailable", "The operation's work center is no longer active.");
        var safetyArea = (await resources.ListSafetyAreasAsync(ct)).SingleOrDefault(area => area.Code == workCenter.SafetyAreaCode);
        if (safetyArea is not { Active: true } || !string.Equals(safetyArea.SiteCode, operationalContext.SiteCode, StringComparison.OrdinalIgnoreCase))
            throw new CommandRejectedException("operation_outside_shift_site", "The operation is outside your assigned site's shift scope.");

        var recoveryScope = new ResourceRef("WorkforceRecovery", $"{orderNumber}:{sequence}", Program: order.ProgramId is { } orderProgram ? new ProgramId(orderProgram) : null,
            FacilityId: operationalContext.SiteCode, WorkCenterId: operation.WorkCenterCode, ShiftId: operationalContext.ShiftCode);
        var supervision = authorizer.Authorize(principal, Permissions.WorkforceSupervise, recoveryScope);
        if (!supervision.IsPermitted)
        {
            var management = authorizer.Authorize(principal, Permissions.WorkforceManage, recoveryScope);
            if (!management.IsPermitted) throw new ForgeAuthorizationException(supervision, recoveryScope);
        }

        var now = time.GetUtcNow();
        var shiftDate = operationalContext.Relation == "Upcoming" ? operationalContext.OperationalDate : (DateOnly?)null;
        var shiftCode = operationalContext.Relation == "Upcoming" ? operationalContext.ShiftCode : null;
        var scheduledMinutes = (int)Math.Clamp(Math.Ceiling((operationalContext.ShiftEndsAt - (shiftDate is null ? now : operationalContext.ShiftStartsAt)).TotalMinutes), 1, 1440);
        var target = new AssignWork(new PersonId(Guid.Empty), safetyArea.Code, operation.WorkCenterCode,
            operationReadiness.BestMachine, operation.ActivityCode, AsTrainee: false);

        return await skillCapacity.RecoveryWithCapacityAsync(principal, target, scheduledMinutes, ct, shiftDate, shiftCode);
    }

    public async Task<DailyMissionResponse> GetAsync(ForgePrincipal principal, CancellationToken ct)
    {
        var generatedAt = time.GetUtcNow();
        var sources = new List<DailyMissionSource>();
        var warnings = new List<string>();
        var items = new List<DailyMissionItem>();

        try
        {
            var assignments = await inbox.AssignmentsAsync(principal, ct);
            var asOf = time.GetUtcNow();
            sources.Add(new("personal-work", "Available", asOf, "Current identity; each source rechecked against current permissions"));
            items.AddRange(assignments.Select(assignment => new DailyMissionItem(
                $"assignment:{assignment.AssignmentId:D}",
                "Assigned Work",
                assignment.Title,
                "This is the recorded work-assignment progress. Completion of the source work must be recorded in its linked workflow.",
                assignment.WorkType,
                assignment.WorkId,
                assignment.DeepLink,
                "WorkAssignment",
                assignment.Status,
                assignment.UpdatedAt)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Daily Mission could not read permission-filtered personal work for {PersonId}", principal.PersonId);
            sources.Add(new("personal-work", "Unavailable", null, "Current identity and source permissions", "Refresh when the work inbox is available."));
            warnings.Add("Assigned work could not be refreshed. Other available mission information is still shown.");
        }

        try
        {
            var approvals = await inbox.ApprovalsAsync(principal, ct);
            var asOf = time.GetUtcNow();
            sources.Add(new("approval-inbox", "Available", asOf, "Only current, authorized decisions are included"));
            items.AddRange(approvals.Select(approval => new DailyMissionItem(
                $"approval:{approval.WorkType}:{approval.WorkId}",
                "Requires Decision",
                approval.Title,
                "This source workflow has a pending decision for which your current permissions and separation-of-duties checks allow review.",
                approval.WorkType,
                approval.WorkId,
                approval.DeepLink,
                "ApprovalInbox",
                "Pending",
                approval.CreatedAt)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Daily Mission could not read authorized approval items for {PersonId}", principal.PersonId);
            sources.Add(new("approval-inbox", "Unavailable", null, "Current role, source, and scope permissions", "Refresh when the approval inbox is available."));
            warnings.Add("Pending decisions could not be refreshed. Other available mission information is still shown.");
        }

        var operationalContext = await ReadOperationalContextAsync(principal, generatedAt, sources, warnings, ct);
        await AddProductionRiskAsync(principal, sources, items, warnings, ct);
        await AddMaterialReadinessAsync(principal, operationalContext, sources, items, warnings, ct);
        await AddQualityHoldsAsync(principal, sources, items, warnings, ct);
        await AddMaintenanceReadinessAsync(principal, operationalContext, sources, items, warnings, ct);

        await AddAttendanceClassificationsAsync(principal, operationalContext, sources, items, warnings, ct);
        await AddReceivingInspectionQueueAsync(principal, operationalContext, sources, items, warnings, ct);
        await AddWorkforceOperationReadinessAsync(principal, operationalContext, sources, items, warnings, ct);
        await AddCurrentShiftEquipmentWindowsAsync(principal, operationalContext, sources, items, warnings, ct);

        // Keep the remaining gaps explicit without describing partially connected domains as wholly
        // absent. An empty mission must not look like an all-clear while coverage remains limited.
        foreach (var (source, scope, note) in new[]
        {
            ("qualified-workforce-coverage-and-reassignment",
                "Existing worker, attendance, and order-readiness sources",
                "Open-order workforce blockers and verified attendance are included. Authorized supervisors can request an on-demand, source-revalidated safe coverage preview with current qualifications, skill recency, capacity range, and origin-station impact; total roster coverage is not projected."),
            ("machine-readiness-and-availability-coverage",
                "Existing maintenance, downtime, and open-order readiness sources",
                "Current/upcoming recorded machine windows and operation equipment blockers are included; comprehensive per-machine readiness is not yet projected."),
        })
        {
            sources.Add(new(source, "Limited", null, scope, note));
        }
        warnings.Add("This is a partial mission projection. Source coverage identifies areas that still need a broader authorized read model.");

        var orderedItems = items
            .OrderBy(item => item.Group switch
            {
                "Requires Decision" => 0,
                "Ready to Execute" => 1,
                "Requires Attention" => 2,
                "Assigned Work" => 3,
                "Watch/Upcoming" => 4,
                _ => 4,
            })
            .ThenBy(item => item.SourceLifecycle is "GoalRiskProjection" or "MaterialReadinessProjection" ? RiskRank(item.SourceStatus) : 0)
            .ThenBy(item => item.Deadline ?? DateOnly.MaxValue)
            .ThenBy(item => item.SourceTimestamp)
            .ThenBy(item => item.SourceType, StringComparer.Ordinal)
            .ThenBy(item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
        var partial = warnings.Count > 0 || sources.Any(source => source.Status is "Limited" or "NoWorkerRecord" or "PendingIntegration");

        return new DailyMissionResponse(
            generatedAt,
            new DailyMissionActor(principal.PersonId.Value.ToString("D"), principal.DisplayName, principal.Roles.Order(StringComparer.Ordinal).ToList()),
            operationalContext,
            sources,
            orderedItems,
            partial,
            warnings);
    }

    private async Task AddCurrentShiftEquipmentWindowsAsync(
        ForgePrincipal principal,
        DailyMissionOperationalContext? operationalContext,
        List<DailyMissionSource> sources,
        List<DailyMissionItem> items,
        List<string> warnings,
        CancellationToken ct)
    {
        const string sourceCode = "current-shift-machine-events";
        if (!authorizer.Authorize(principal, Permissions.MaintenanceRead, ResourceRef.Of(Permissions.MaintenanceRead)).IsPermitted)
        {
            sources.Add(new(sourceCode, "NotAvailableToRole", null, "Maintenance downtime read permission"));
            return;
        }

        if (operationalContext is null || !authorizer.Authorize(principal, Permissions.ResourcesRead, ResourceRef.Of(Permissions.ResourcesRead)).IsPermitted)
        {
            sources.Add(new(sourceCode, "Limited", null, "Current authenticated worker's site, shift and machine map", "Machine events are not summarized because the current site or its resource map is unavailable."));
            return;
        }

        try
        {
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            var resources = uow.Store<IResourcesLookup>();
            var areas = await resources.ListSafetyAreasAsync(ct);
            var siteAreas = areas.Where(area => string.Equals(area.SiteCode, operationalContext.SiteCode, StringComparison.OrdinalIgnoreCase))
                .Select(area => area.Code).ToHashSet(StringComparer.Ordinal);
            var centers = (await resources.ListWorkCentersAsync(ct)).Where(center => siteAreas.Contains(center.SafetyAreaCode))
                .Select(center => center.Code).ToHashSet(StringComparer.Ordinal);
            var siteMachines = (await resources.ListMachinesAsync(ct)).Where(machine => machine.Active && centers.Contains(machine.WorkCenterCode))
                .Select(machine => machine.Code).ToHashSet(StringComparer.Ordinal);
            var asOf = time.GetUtcNow();
            var windows = (await uow.Store<IMaintenanceLookup>().ListDowntimeAsync(
                    operationalContext.ShiftStartsAt, operationalContext.ShiftEndsAt, ct))
                .Where(window => siteMachines.Contains(window.MachineCode)
                    && (window.Kind is DowntimeKind.Unplanned or DowntimeKind.Planned or DowntimeKind.AssetOutOfService)
                    && (window.Covers(asOf) || window.StartsAt > asOf))
                .OrderByDescending(window => window.Covers(asOf))
                .ThenBy(window => window.StartsAt)
                .ThenBy(window => window.MachineCode, StringComparer.Ordinal)
                .ToArray();
            var visibleWindows = windows.Take(10).ToArray();
            sources.Add(new(sourceCode, "Limited", asOf,
                "Recorded unplanned/planned downtime and restricted/decommissioned asset events for active machines at the authenticated worker's current site; open preventive-maintenance readiness is listed separately",
                windows.Length > visibleWindows.Length
                    ? $"Showing 10 of {windows.Length} current or upcoming machine windows for this shift."
                    : windows.Length == 0 ? "No matching current or upcoming machine window was returned; detailed machine readiness remains outside this projection." : null));

            items.AddRange(visibleWindows.Select(window =>
            {
                var reason = window.Reason.Length > 400 ? $"{window.Reason[..400]}…" : window.Reason;
                return new DailyMissionItem(
                    $"equipment-window:{window.Id}",
                    window.Covers(asOf) ? "Requires Attention" : "Watch/Upcoming",
                    $"Machine {(window.Covers(asOf) ? "unavailable" : "window upcoming")} · {window.MachineCode}",
                    $"{window.Kind}: {reason}",
                    "EquipmentDowntime",
                    window.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "#/machine-kanban",
                    "CurrentShiftEquipmentWindowProjection",
                    window.Kind.ToString(),
                    asOf);
            }));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ForgeAuthorizationException)
        {
            sources.Add(new(sourceCode, "NotAvailableToRole", null, "Maintenance downtime and resource read permissions"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Daily Mission could not read current-shift machine windows for {PersonId}", principal.PersonId);
            sources.Add(new(sourceCode, "Unavailable", null, "Maintenance downtime and current site machine map", "Refresh when current-shift machine events are available."));
            warnings.Add("Current-shift machine events could not be refreshed. Other available mission information is still shown.");
        }
    }

    private async Task AddWorkforceOperationReadinessAsync(
        ForgePrincipal principal,
        DailyMissionOperationalContext? operationalContext,
        List<DailyMissionSource> sources,
        List<DailyMissionItem> items,
        List<string> warnings,
        CancellationToken ct)
    {
        const string sourceCode = "workforce-order-readiness";
        const string equipmentSourceCode = "machine-order-readiness";
        const string readyOperationsSourceCode = "ready-open-operations";
        if (!authorizer.Authorize(principal, Permissions.ReadinessRead, ResourceRef.Of("Readiness")).IsPermitted)
        {
            sources.Add(new(sourceCode, "NotAvailableToRole", null, "Production operation readiness read permission"));
            sources.Add(new(equipmentSourceCode, "NotAvailableToRole", null, "Production operation readiness read permission"));
            sources.Add(new(readyOperationsSourceCode, "NotAvailableToRole", null, "Production operation readiness read permission"));
            return;
        }

        try
        {
            var operations = await readiness.EvaluateOperationsAsync(principal, null, ct);
            var asOf = time.GetUtcNow();
            if (operationalContext is null || !authorizer.Authorize(principal, Permissions.ResourcesRead, ResourceRef.Of(Permissions.ResourcesRead)).IsPermitted)
            {
                sources.Add(new(sourceCode, "Limited", asOf, "Program-authorized open operation readiness", "Workforce blockers are omitted because the authenticated worker's site cannot be verified."));
                sources.Add(new(equipmentSourceCode, "Limited", asOf, "Current site machine readiness inferred from authorized open order operations", "Machine blockers are not summarized because the site work-center map is unavailable."));
                sources.Add(new(readyOperationsSourceCode, "Limited", asOf, "Authorized released-order operation readiness", "Operations are omitted because the authenticated worker's site could not be verified."));
                return;
            }

            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            var resources = uow.Store<IResourcesLookup>();
            var siteCenters = DailyMissionSiteScope.WorkCenterCodesForSite(operationalContext.SiteCode,
                await resources.ListSafetyAreasAsync(ct), await resources.ListWorkCentersAsync(ct));
            var workforceBlocked = operations
                .Where(operation => siteCenters.Contains(operation.WorkCenter)
                    && !operation.Executable && operation.PrimaryConstraint == "Workforce")
                .OrderBy(operation => operation.DueDate)
                .ThenBy(operation => operation.Priority)
                .ThenBy(operation => operation.OrderNumber)
                .ThenBy(operation => operation.Sequence)
                .ToArray();
            var visibleBlockers = workforceBlocked.Take(10).ToArray();
            sources.Add(new(sourceCode, "Limited", asOf,
                $"Current workforce-primary blockers on open released/held operations at site {operationalContext.SiteCode}, filtered by the actor's program access; authorized supervisors can request a fresh, read-only safe coverage preview; total roster coverage remains outside this view",
                workforceBlocked.Length > visibleBlockers.Length
                    ? $"Showing the 10 earliest-due blockers of {workforceBlocked.Length} visible operations; open the linked orders for source details."
                    : null));
            items.AddRange(visibleBlockers.Select(operation => new DailyMissionItem(
                $"workforce-readiness:{operation.OrderNumber}:{operation.Sequence}",
                "Requires Attention",
                $"Staffing readiness · Order {operation.OrderNumber} · {operation.WorkCenter}",
                $"Forge's current order-readiness check reports workforce as the primary blocker on operation {operation.Sequence} ({operation.Description}). Individual worker names are omitted from this card; an authorized supervisor can request a fresh qualification- and safety-checked coverage preview.",
                "ProductionOperationReadiness",
                $"{operation.OrderNumber}:{operation.Sequence}",
                $"#/orders/{operation.OrderNumber}",
                "WorkforceOrderReadinessProjection",
                "WorkforceBlocked",
                asOf,
                operation.DueDate)));

                var readyOperations = operations
                    .Where(operation => siteCenters.Contains(operation.WorkCenter) && operation.Executable)
                    .OrderBy(operation => operation.Priority)
                    .ThenBy(operation => operation.DueDate)
                    .ThenBy(operation => operation.OrderNumber)
                    .ThenBy(operation => operation.Sequence)
                    .ToArray();
                var visibleReadyOperations = readyOperations.Take(5).ToArray();
                sources.Add(new(readyOperationsSourceCode, "Limited", asOf,
                    $"Current Forge order-readiness checks for open released operations at site {operationalContext.SiteCode}; scheduling, dispatch and later transaction checks are not implied",
                    readyOperations.Length > visibleReadyOperations.Length
                        ? $"Showing 5 of {readyOperations.Length} operations whose current readiness gates pass."
                        : "All current readiness dimensions passed for each listed operation; this does not mean it is scheduled or dispatched."));
                items.AddRange(visibleReadyOperations.Select(operation => new DailyMissionItem(
                    $"ready-operation:{operation.OrderNumber}:{operation.Sequence}",
                    "Ready to Execute",
                    $"Eligible operation · Order {operation.OrderNumber} · {operation.WorkCenter}",
                    $"All current Forge order-readiness dimensions pass for operation {operation.Sequence} ({operation.Description}). Review its order and approved schedule before dispatch; this projection does not reserve capacity or start work.",
                    "ProductionOperationReadiness",
                    $"{operation.OrderNumber}:{operation.Sequence}",
                    $"#/orders/{operation.OrderNumber}",
                    "CurrentOrderReadinessProjection",
                    "ReadinessPassed",
                    asOf,
                    operation.DueDate)));

                var equipmentBlocked = operations
                    .Where(operation => siteCenters.Contains(operation.WorkCenter)
                        && operation.Dimensions.Any(dimension => dimension.Name == "Equipment" && dimension.Outcome is EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate))
                    .OrderBy(operation => operation.DueDate)
                    .ThenBy(operation => operation.Priority)
                    .ThenBy(operation => operation.OrderNumber)
                    .ThenBy(operation => operation.Sequence)
                    .ToArray();
                var visibleEquipment = equipmentBlocked.Take(10).ToArray();
                sources.Add(new(equipmentSourceCode, "Limited", asOf,
                    $"Equipment dimension on current open released/held operations at site {operationalContext.SiteCode}; this is not a complete machine readiness board",
                    equipmentBlocked.Length > visibleEquipment.Length ? $"Showing 10 operations of {equipmentBlocked.Length} with an equipment failure or indeterminate result." : null));
                items.AddRange(visibleEquipment.Select(operation =>
                {
                    var evidence = operation.Dimensions.First(dimension => dimension.Name == "Equipment" && dimension.Outcome is EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate);
                    return new DailyMissionItem(
                        $"machine-readiness:{operation.OrderNumber}:{operation.Sequence}",
                        "Requires Attention",
                        $"Equipment readiness · {operation.Machine ?? operation.WorkCenter} · Order {operation.OrderNumber}",
                        $"Operation {operation.Sequence} at {operation.WorkCenter}: {evidence.Detail}",
                        "ProductionOperationReadiness",
                        $"{operation.OrderNumber}:{operation.Sequence}",
                        $"#/orders/{operation.OrderNumber}",
                        "MachineOrderReadinessProjection",
                        evidence.Outcome.ToString(),
                        asOf,
                        operation.DueDate);
                }));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ForgeAuthorizationException)
        {
            sources.Add(new(sourceCode, "NotAvailableToRole", null, "Production operation readiness read permission"));
            sources.Add(new(equipmentSourceCode, "NotAvailableToRole", null, "Production operation readiness read permission"));
            sources.Add(new(readyOperationsSourceCode, "NotAvailableToRole", null, "Production operation readiness read permission"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Daily Mission could not read workforce-primary production blockers for {PersonId}", principal.PersonId);
            sources.Add(new(sourceCode, "Unavailable", null, "Production readiness permissions and visible program scope", "Refresh when current order readiness is available."));
            sources.Add(new(equipmentSourceCode, "Unavailable", null, "Production readiness permissions and site work-center map", "Refresh when machine readiness is available."));
            sources.Add(new(readyOperationsSourceCode, "Unavailable", null, "Production readiness permissions and authenticated worker site", "Refresh when operation readiness is available."));
            warnings.Add("Workforce blockers on current production operations could not be refreshed. Other available mission information is still shown.");
        }
    }

    private async Task AddAttendanceClassificationsAsync(
        ForgePrincipal principal,
        DailyMissionOperationalContext? operationalContext,
        List<DailyMissionSource> sources,
        List<DailyMissionItem> items,
        List<string> warnings,
        CancellationToken ct)
    {
        const string sourceCode = "workforce-attendance-classifications";
        if (!authorizer.Authorize(principal, Permissions.AttendanceIntelligenceRead, ResourceRef.Of(Permissions.AttendanceIntelligenceRead)).IsPermitted)
        {
            sources.Add(new(sourceCode, "NotAvailableToRole", null, "Attendance classification read permission"));
            return;
        }

        if (operationalContext is null)
        {
            sources.Add(new(sourceCode, "Limited", null, "Current authenticated worker's assigned site and shift", "Attendance is not summarized because the assigned shift could not be resolved."));
            return;
        }

        if (operationalContext.Relation is not ("Current" or "Upcoming"))
        {
            sources.Add(new(sourceCode, "Limited", time.GetUtcNow(), "Current/upcoming assigned-shift attendance only",
                "Historical attendance is not summarized here."));
            return;
        }

        try
        {
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            var store = uow.Store<IAttendanceReliabilityStore>();
            var opportunities = (await store.ListAttendanceOpportunitiesAsync(
                    operationalContext.ShiftStartsAt, operationalContext.ShiftEndsAt, ct))
                .Where(opportunity => string.Equals(opportunity.ShiftCode, operationalContext.ShiftCode, StringComparison.Ordinal))
                .ToArray();
            var opportunityIds = opportunities.Select(opportunity => opportunity.OpportunityId).ToHashSet();
            var events = (await store.ListAttendanceIntelligenceEventsAsync(
                    operationalContext.ShiftStartsAt, operationalContext.ShiftEndsAt, ct))
                .Where(attendanceEvent => opportunityIds.Contains(attendanceEvent.OpportunityId))
                .ToArray();
            var policies = await store.ListAttendancePoliciesAsync(ct);
            var asOf = time.GetUtcNow();
            var summary = AttendanceReliabilityCalculator.Calculate(
                opportunities,
                events,
                policies,
                operationalContext.ShiftStartsAt,
                operationalContext.ShiftEndsAt,
                asOf);
            var approvedPto = AttendanceReliabilityCalculator.VerifiedApprovedPtoCount(
                opportunities, events, operationalContext.ShiftStartsAt, operationalContext.ShiftEndsAt);

            var superseded = opportunities.Where(opportunity => opportunity.SupersedesOpportunityId is not null)
                .Select(opportunity => opportunity.SupersedesOpportunityId!.Value).ToHashSet();
            var notYetDue = operationalContext.Relation == "Current" ? opportunities.Count(opportunity => opportunity.State == AttendanceOpportunityState.Scheduled
                && !superseded.Contains(opportunity.OpportunityId)
                && opportunity.ScheduledStart > asOf) : 0;
            sources.Add(new(sourceCode, "Limited", asOf,
                $"Aggregate verified classifications and approved PTO for the authenticated worker's {operationalContext.Relation.ToLowerInvariant()} site shift; no employee names or reasons; qualification and staffing coverage remain separate",
                opportunities.Length == 0
                    ? "No scheduled attendance opportunities were found for this shift; this is not an all-clear attendance result."
                    : operationalContext.Relation == "Upcoming"
                        ? "Only explicit verified approved PTO is shown for an upcoming shift. No arrival, call-out, or no-show is inferred in advance."
                        : "Only explicitly verified classifications count as call-outs or no-shows. Missing punches remain pending review; approved PTO is kept separate from unplanned absence."));

            var hasVerifiedAbsence = summary.VerifiedCallOuts > 0 || summary.VerifiedNoShows > 0;
            var hasReview = summary.PendingReviewOpportunities > 0 || summary.MissingPolicyOpportunities > 0;
            if (!hasVerifiedAbsence && !hasReview && notYetDue == 0 && approvedPto == 0) return;

            var reasonParts = new List<string>();
            if (summary.VerifiedCallOuts > 0) reasonParts.Add($"{summary.VerifiedCallOuts} explicitly verified call-out(s)");
            if (summary.VerifiedNoShows > 0) reasonParts.Add($"{summary.VerifiedNoShows} explicitly verified no-show(s)");
            if (summary.PendingReviewOpportunities > 0)
                reasonParts.Add($"{summary.PendingReviewOpportunities} attendance opportunity(ies) pending review; missing punches are not classified as absences");
            if (summary.MissingPolicyOpportunities > 0)
                reasonParts.Add($"{summary.MissingPolicyOpportunities} opportunity(ies) without a unique effective attendance policy");
            if (approvedPto > 0) reasonParts.Add($"{approvedPto} verified approved PTO booking(s); these are planned time away, not call-outs or no-shows");
            if (notYetDue > 0)
                reasonParts.Add($"{notYetDue} scheduled arrival(s) later in this shift are not yet due");

            items.Add(new DailyMissionItem(
                $"attendance-summary:{operationalContext.OperationalDayId}",
                hasVerifiedAbsence ? "Requires Attention" : "Watch/Upcoming",
                $"{(operationalContext.Relation == "Upcoming" ? "Upcoming shift" : "Current shift")} attendance · {operationalContext.ShiftName}",
                string.Join(". ", reasonParts) + ".",
                "AttendanceReliability",
                operationalContext.OperationalDayId,
                "#/performance",
                "AttendanceShiftSummaryProjection",
                hasVerifiedAbsence ? "VerifiedClassification" : hasReview ? "PendingReview" : approvedPto > 0 ? "ApprovedPto" : "NotYetDue",
                asOf));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ForgeAuthorizationException)
        {
            sources.Add(new(sourceCode, "NotAvailableToRole", null, "Attendance classification read permission"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Daily Mission could not read current-shift attendance classifications for {PersonId}", principal.PersonId);
            sources.Add(new(sourceCode, "Unavailable", null, "Attendance classification read permission and configured current shift", "Refresh when the attendance classification source is available."));
            warnings.Add("Current-shift attendance classifications could not be refreshed. Other available mission information is still shown.");
        }
    }

    private async Task AddQualityHoldsAsync(
        ForgePrincipal principal,
        List<DailyMissionSource> sources,
        List<DailyMissionItem> items,
        List<string> warnings,
        CancellationToken ct)
    {
        if (!authorizer.Authorize(principal, Permissions.HoldsRead, ResourceRef.Of(Permissions.HoldsRead)).IsPermitted)
        {
            sources.Add(new("quality-and-mrb", "NotAvailableToRole", null, "Quality hold register read permission"));
            return;
        }

        try
        {
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            var holds = (await HoldRegister.ListAsync(uow, principal, false, ct))
                .Where(hold => hold.Active && hold.Kind == HoldKind.Quality)
                .OrderBy(hold => hold.PlacedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(hold => hold.Reference, StringComparer.Ordinal)
                .ToList();
            var asOf = time.GetUtcNow();
            sources.Add(new("quality-and-mrb", "Available", asOf,
                "Active quality holds from the authorized hold register; restricted program records are omitted"));
            items.AddRange(holds.Select(hold =>
            {
                var orderReference = hold.AffectedRecords.FirstOrDefault(record => record.StartsWith("order:", StringComparison.Ordinal));
                var orderId = orderReference is not null && long.TryParse(orderReference.AsSpan("order:".Length), out var parsed) ? parsed : (long?)null;
                var targetLink = orderId is { } number
                    ? $"#/orders/{number}"
                    : hold.Reference.StartsWith("NCR ", StringComparison.Ordinal) ? "#/quality?tab=ncrs" : "#/quality";
                return new DailyMissionItem(
                    $"quality-hold:{hold.Reference}",
                    "Requires Attention",
                    $"Quality hold · {hold.Reference}",
                    $"{hold.Reason} Required resolution: {hold.RequiredResolution}",
                    "QualityHold",
                    hold.Reference,
                    targetLink,
                    "QualityHoldProjection",
                    "Active",
                    hold.PlacedAt ?? asOf);
            }));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ForgeAuthorizationException)
        {
            sources.Add(new("quality-and-mrb", "NotAvailableToRole", null, "Quality hold register read permission"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Daily Mission could not read quality holds for {PersonId}", principal.PersonId);
            sources.Add(new("quality-and-mrb", "Unavailable", null, "Authorized hold register and program scope", "Refresh when the quality hold register is available."));
            warnings.Add("Quality holds could not be refreshed. Other available mission information is still shown.");
        }
    }

    private async Task AddMaintenanceReadinessAsync(
        ForgePrincipal principal,
        DailyMissionOperationalContext? operationalContext,
        List<DailyMissionSource> sources,
        List<DailyMissionItem> items,
        List<string> warnings,
        CancellationToken ct)
    {
        if (!authorizer.Authorize(principal, Permissions.MaintenanceRead, ResourceRef.Of(Permissions.MaintenanceRead)).IsPermitted)
        {
            sources.Add(new("maintenance-and-equipment", "NotAvailableToRole", null, "Maintenance work-order read permission"));
            return;
        }

        if (operationalContext is null || !authorizer.Authorize(principal, Permissions.ResourcesRead, ResourceRef.Of(Permissions.ResourcesRead)).IsPermitted)
        {
            sources.Add(new("maintenance-and-equipment", "Limited", null, "Authenticated worker site and resource map", "Maintenance readiness is omitted because work orders cannot be safely scoped to the worker's current site."));
            return;
        }

        try
        {
            var readiness = await maintenancePlanning.ReadinessAsync(principal, null, ct);
            var siteMachines = await ReadSiteMachineCodesAsync(operationalContext.SiteCode, ct);
            var siteWorkOrders = await ListOpenMaintenanceWorkOrdersAsync(siteMachines, ct);
            var scopedWorkOrderIds = siteWorkOrders.Select(order => order.WorkOrderId).ToHashSet();
            var blocked = readiness.Where(order => order.Status == "Blocked" && scopedWorkOrderIds.Contains(order.WorkOrderId))
                .OrderBy(order => order.Blocking, StringComparer.Ordinal)
                .ThenBy(order => order.WorkOrderId)
                .ToList();
            var asOf = time.GetUtcNow();
            sources.Add(new("maintenance-and-equipment", "Limited", asOf,
                $"Open maintenance work-order readiness for machines mapped to the authenticated worker's site {operationalContext.SiteCode}; machine availability and downtime event detail remain outside this projection"));
            items.AddRange(blocked.Select(order => new DailyMissionItem(
                $"maintenance-readiness:{order.WorkOrderId}",
                "Requires Attention",
                $"Blocked maintenance · WO {order.WorkOrderId} · {order.Target}",
                order.Summary,
                "MaintenanceWorkOrder",
                order.WorkOrderId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "#/maintenance?tab=orders",
                "MaintenanceReadinessProjection",
                order.Blocking ?? "Blocked",
                asOf)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ForgeAuthorizationException)
        {
            sources.Add(new("maintenance-and-equipment", "NotAvailableToRole", null, "Maintenance work-order read permission"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Daily Mission could not read maintenance readiness for {PersonId}", principal.PersonId);
            sources.Add(new("maintenance-and-equipment", "Unavailable", null, "Maintenance work-order permission and readiness", "Refresh when maintenance readiness is available."));
            warnings.Add("Maintenance readiness could not be refreshed. Other available mission information is still shown.");
        }
    }

    private async Task<HashSet<string>> ReadSiteMachineCodesAsync(string siteCode, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var resources = uow.Store<IResourcesLookup>();
        return DailyMissionSiteScope.MachineCodesForSite(siteCode,
            await resources.ListSafetyAreasAsync(ct), await resources.ListWorkCentersAsync(ct), await resources.ListMachinesAsync(ct));
    }

    private async Task<IReadOnlyList<WorkOrderInfo>> ListOpenMaintenanceWorkOrdersAsync(IReadOnlySet<string> siteMachines, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var assets = (await uow.Store<IAssetLookup>().ListAssetsAsync(ct))
            .Where(asset => asset.MachineCode is not null)
            .ToDictionary(asset => asset.AssetCode, asset => asset.MachineCode!, StringComparer.Ordinal);
        return (await uow.Store<IMaintenanceLookup>().ListWorkOrdersAsync(false, ct))
            .Where(order => DailyMissionSiteScope.MachineIsInSite(order.MachineCode, order.AssetCode, siteMachines, assets))
            .ToArray();
    }

    private async Task AddReceivingInspectionQueueAsync(
        ForgePrincipal principal,
        DailyMissionOperationalContext? operationalContext,
        List<DailyMissionSource> sources,
        List<DailyMissionItem> items,
        List<string> warnings,
        CancellationToken ct)
    {
        const string sourceCode = "receiving-inspection-queue";
        if (!authorizer.Authorize(principal, Permissions.InventoryRead, ResourceRef.Of(Permissions.InventoryRead)).IsPermitted)
        {
            sources.Add(new(sourceCode, "NotAvailableToRole", null, "Inventory read permission"));
            return;
        }

        if (operationalContext is null || !authorizer.Authorize(principal, Permissions.ResourcesRead, ResourceRef.Of(Permissions.ResourcesRead)).IsPermitted)
        {
            sources.Add(new(sourceCode, "Limited", null, "Authenticated worker site and active inventory locations", "Pending inspection receipts are not summarized because the site scope is unavailable."));
            return;
        }

        try
        {
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            var inventory = uow.Store<IInventoryLookup>();
            var siteLocations = (await inventory.ListLocationsAsync(ct))
                .Where(location => location.Active && string.Equals(location.SiteCode, operationalContext.SiteCode, StringComparison.OrdinalIgnoreCase))
                .Select(location => location.Code)
                .ToHashSet(StringComparer.Ordinal);
            var receipts = (await uow.Store<IReceivingLookup>().ListInspectionReceiptsAsync(100, ct))
                .Where(receipt => receipt.ToStatus == StockStatus.Inspection && receipt.ToLocation is { } location && siteLocations.Contains(location))
                .ToArray();
            var pending = new List<(InventoryTransaction Receipt, decimal Remaining)>();
            foreach (var receipt in receipts)
            {
                var state = await ReceivingQueries.ReceiptStateAsync(uow, receipt, ct);
                if (state.Remaining > 0) pending.Add((receipt, state.Remaining));
            }

            var visible = pending.Take(10).ToArray();
            var asOf = time.GetUtcNow();
            sources.Add(new(sourceCode, "Limited", asOf,
                $"Uninspected receipt quantity at active locations for site {operationalContext.SiteCode}; supplier ETA, released stock positions and other sites are not included",
                pending.Count > visible.Length ? $"Showing 10 pending receipts of {pending.Count} at this site." : null));
            items.AddRange(visible.Select(row => new DailyMissionItem(
                $"receiving-inspection:{row.Receipt.TransactionId}",
                "Requires Attention",
                $"Material inspection · {row.Receipt.PartNumber}",
                $"Receipt {row.Receipt.TransactionId} has {row.Remaining:0.###} still awaiting inspection. Inspection stock is not usable for planning until its source workflow records acceptance.",
                "InventoryReceipt",
                row.Receipt.TransactionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "#/receiving",
                "ReceivingInspectionProjection",
                "PendingInspection",
                row.Receipt.At)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ForgeAuthorizationException)
        {
            sources.Add(new(sourceCode, "NotAvailableToRole", null, "Inventory read permission"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Daily Mission could not read pending site receipt inspections for {PersonId}", principal.PersonId);
            sources.Add(new(sourceCode, "Unavailable", null, "Inventory permission and current site location map", "Refresh when the receiving queue is available."));
            warnings.Add("Pending receipt inspections could not be refreshed. Other available mission information is still shown.");
        }
    }

    private async Task AddMaterialReadinessAsync(
        ForgePrincipal principal,
        DailyMissionOperationalContext? operationalContext,
        List<DailyMissionSource> sources,
        List<DailyMissionItem> items,
        List<string> warnings,
        CancellationToken ct)
    {
        if (!authorizer.Authorize(principal, Permissions.ReadinessRead, ResourceRef.Of(Permissions.ReadinessRead)).IsPermitted)
        {
            sources.Add(new("materials-and-inventory", "NotAvailableToRole", null, "Released-order material-readiness permission"));
            return;
        }

        try
        {
            var run = await materialPlanning.EvaluateMaterialAsync(principal, ct);
            var relevant = run.Output
                .Where(order => order.State != MaterialState.Ready)
                .OrderBy(order => order.State == MaterialState.Shortfall ? 0 : order.Overdue ? 1 : 2)
                .ThenBy(order => order.ReadyOn ?? DateOnly.MaxValue)
                .ThenBy(order => order.OrderNumber)
                .ToList();
            sources.Add(new("materials-and-inventory", "Limited", run.AsOf,
                "Released-order allocation engine; authorized program scope; stock-versus-receipt allocations and scheduled supply dates are source-backed; site inventory disposition is summarized separately when permitted"));

            var sitePositions = await ReadSitePositionSummaryAsync(principal, operationalContext, relevant, sources, warnings, ct);
            items.AddRange(relevant.Select(order => new DailyMissionItem(
                $"material-readiness:{order.OrderNumber}",
                order.State == MaterialState.Shortfall || order.Overdue ? "Requires Attention" : "Watch/Upcoming",
                $"Material readiness · Order {order.OrderNumber}",
                MaterialReason(order, sitePositions),
                "ProductionOrder",
                order.OrderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"#/orders/{order.OrderNumber}",
                "MaterialReadinessProjection",
                order.State.ToString(),
                run.AsOf,
                order.ReadyOn)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ForgeAuthorizationException)
        {
            sources.Add(new("materials-and-inventory", "NotAvailableToRole", null, "Released-order material-readiness permission"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Daily Mission could not read material readiness for {PersonId}", principal.PersonId);
            sources.Add(new("materials-and-inventory", "Unavailable", null, "Released-order material readiness and current program scope", "Refresh when material readiness is available."));
            warnings.Add("Material readiness could not be refreshed. Other available mission information is still shown.");
        }
    }

    private static string MaterialReason(Forge.Planning.Engines.OrderMaterialStatus order,
        IReadOnlyDictionary<string, SiteInventoryPositionSummary> sitePositions)
    {
        var detail = new List<string> { order.Summary };
        foreach (var line in order.Lines.Where(line => line.Remaining > 0 && (line.Shortfall > 0 || line.ExpectedOn is not null)))
        {
            var allocation = line.Shortfall > 0 ? $"{line.PartNumber}: {line.Shortfall:0.###} still uncovered." : string.Empty;
            if (allocation.Length > 0) detail.Add(allocation);
            if (sitePositions.TryGetValue(line.PartNumber, out var stock))
            {
                detail.Add($"Current site position for {line.PartNumber}: {stock.AvailableForPlanning:0.###} released and planning-eligible, {stock.Inspection:0.###} awaiting inspection, {stock.Hold:0.###} on hold, and {stock.ReleasedUnavailable:0.###} released but not planning-eligible. These are site totals, not an order reservation; the linked material allocation is authoritative.");
            }

            var receipts = line.Allocations
                .Where(a => a.DueDate is not null && a.Description != MaterialSourceVisibility.Restricted)
                .Select(a => $"{a.Quantity:0.###} from {a.Description}, currently scheduled {a.DueDate:yyyy-MM-dd}")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (receipts.Length > 0) detail.Add($"Recorded incoming supply: {string.Join("; ", receipts)}.");
        }

        return string.Join(" ", detail.Where(text => !string.IsNullOrWhiteSpace(text))).Trim();
    }

    private sealed record SiteInventoryPositionSummary(decimal AvailableForPlanning, decimal Inspection, decimal Hold, decimal ReleasedUnavailable);

    private async Task<IReadOnlyDictionary<string, SiteInventoryPositionSummary>> ReadSitePositionSummaryAsync(
        ForgePrincipal principal,
        DailyMissionOperationalContext? operationalContext,
        IReadOnlyList<Forge.Planning.Engines.OrderMaterialStatus> orders,
        List<DailyMissionSource> sources,
        List<string> warnings,
        CancellationToken ct)
    {
        const string sourceCode = "current-site-inventory-positions";
        if (!authorizer.Authorize(principal, Permissions.InventoryRead, ResourceRef.Of(Permissions.InventoryRead)).IsPermitted)
        {
            sources.Add(new(sourceCode, "NotAvailableToRole", null, "Inventory read permission"));
            return new Dictionary<string, SiteInventoryPositionSummary>(StringComparer.Ordinal);
        }

        if (operationalContext is null || !authorizer.Authorize(principal, Permissions.ResourcesRead, ResourceRef.Of(Permissions.ResourcesRead)).IsPermitted)
        {
            sources.Add(new(sourceCode, "Limited", null, "Authenticated worker's current site and active inventory locations", "Current-site stock disposition could not be scoped."));
            return new Dictionary<string, SiteInventoryPositionSummary>(StringComparer.Ordinal);
        }

        var parts = orders.Where(order => order.State != MaterialState.Ready)
            .SelectMany(order => order.Lines.Where(line => line.Remaining > 0 && (line.Shortfall > 0 || line.ExpectedOn is not null)).Select(line => line.PartNumber))
            .ToHashSet(StringComparer.Ordinal);
        if (parts.Count == 0)
        {
            sources.Add(new(sourceCode, "Limited", time.GetUtcNow(), $"Current-site inventory positions for parts awaiting material on released orders at site {operationalContext.SiteCode}", "No material-constrained part was present to summarize; this is not a site-wide inventory all-clear."));
            return new Dictionary<string, SiteInventoryPositionSummary>(StringComparer.Ordinal);
        }

        try
        {
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            var inventory = uow.Store<IInventoryLookup>();
            var locations = (await inventory.ListLocationsAsync(ct))
                .Where(location => location.Active && string.Equals(location.SiteCode, operationalContext.SiteCode, StringComparison.OrdinalIgnoreCase))
                .Select(location => location.Code)
                .ToHashSet(StringComparer.Ordinal);
            var positions = (await ShelfLifeQueries.PositionsAsync(uow, null, time.GetUtcNow(), ct))
                .Where(position => parts.Contains(position.PartNumber) && locations.Contains(position.LocationCode))
                .ToArray();
            var summary = positions.GroupBy(position => position.PartNumber, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => new SiteInventoryPositionSummary(
                    group.Where(position => position.AvailableForPlanning).Sum(position => position.Quantity),
                    group.Where(position => position.QualityStatus == StockStatus.Inspection).Sum(position => position.Quantity),
                    group.Where(position => position.QualityStatus == StockStatus.Hold).Sum(position => position.Quantity),
                    group.Where(position => position.QualityStatus == StockStatus.Released && !position.AvailableForPlanning).Sum(position => position.Quantity)),
                    StringComparer.Ordinal);
            sources.Add(new(sourceCode, "Limited", time.GetUtcNow(),
                $"Authorized current-site stock positions for material-constrained parts at site {operationalContext.SiteCode}; released planning-eligible, inspection, hold, and released-but-unavailable quantities remain separate",
                "Site totals are informational only and are not order-specific reservations or a replacement for the linked material allocation."));
            return summary;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ForgeAuthorizationException)
        {
            sources.Add(new(sourceCode, "NotAvailableToRole", null, "Inventory and resource read permissions"));
            return new Dictionary<string, SiteInventoryPositionSummary>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Daily Mission could not read site inventory positions for {PersonId}", principal.PersonId);
            sources.Add(new(sourceCode, "Unavailable", null, "Inventory permission and authenticated worker site", "Refresh when site inventory positions are available."));
            warnings.Add("Current-site inventory positions could not be refreshed. Other available mission information is still shown.");
            return new Dictionary<string, SiteInventoryPositionSummary>(StringComparer.Ordinal);
        }
    }

    private async Task AddProductionRiskAsync(
        ForgePrincipal principal,
        List<DailyMissionSource> sources,
        List<DailyMissionItem> items,
        List<string> warnings,
        CancellationToken ct)
    {
        if (!authorizer.Authorize(principal, Permissions.ScheduleRead, ResourceRef.Of(Permissions.ScheduleRead)).IsPermitted)
        {
            sources.Add(new("production-priorities-capacity", "NotAvailableToRole", null, "Planning schedule read permission"));
            return;
        }

        try
        {
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            var risks = await GoalService.RiskAsync(uow, time, ct, principal);
            var visible = risks.Values
                .Where(line => line.ProgramId is not { } program || principal.Programs.Contains(new ProgramId(program)))
                .Where(line => line.AtRisk || line.BehindPlan)
                .OrderBy(line => RiskRank(line.State.ToString()))
                .ThenBy(line => line.DueDate)
                .ThenBy(line => line.OrderNumber)
                .ToList();
            var asOf = time.GetUtcNow();
            sources.Add(new("production-priorities-capacity", "Available", asOf,
                "Forge goal-risk engine using the current schedule and finite-capacity reforecast; program-filtered"));
            items.AddRange(visible.Select(line => new DailyMissionItem(
                $"goal-risk:{line.OrderNumber}",
                line.AtRisk ? "Requires Attention" : "Watch/Upcoming",
                $"Order {line.OrderNumber} · {line.PartNumber}",
                $"Forge's goal-risk engine reports {Label(line.State)} {string.Join(" ", line.Evidence)}",
                "ProductionOrder",
                line.OrderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"#/orders/{line.OrderNumber}",
                "GoalRiskProjection",
                line.State.ToString(),
                asOf,
                line.DueDate)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Daily Mission could not read schedule and goal-risk information for {PersonId}", principal.PersonId);
            sources.Add(new("production-priorities-capacity", "Unavailable", null, "Planning schedule permission and program scope", "Refresh when the planning risk view is available."));
            warnings.Add("Production schedule risk could not be refreshed. Other available mission information is still shown.");
        }
    }

    private static int RiskRank(string state) => state switch
    {
        nameof(GoalRiskState.Late) => 0,
        nameof(GoalRiskState.Unschedulable) => 1,
        nameof(GoalRiskState.GoalAtRisk) => 2,
        nameof(GoalRiskState.BehindPlanRecoverable) => 3,
        nameof(MaterialState.Shortfall) => 0,
        nameof(MaterialState.Expected) => 3,
        _ => 4,
    };

    private static string Label(GoalRiskState state) => state switch
    {
        GoalRiskState.Late => "projected late.",
        GoalRiskState.Unschedulable => "currently unschedulable in the planning horizon.",
        GoalRiskState.GoalAtRisk => "at risk against its due date.",
        GoalRiskState.BehindPlanRecoverable => "behind plan but still within the current recovery forecast.",
        _ => "requires review in the planning source.",
    };

    private async Task<DailyMissionOperationalContext?> ReadOperationalContextAsync(
        ForgePrincipal principal,
        DateTimeOffset at,
        List<DailyMissionSource> sources,
        List<string> warnings,
        CancellationToken ct)
    {
        if (!authorizer.Authorize(principal, Permissions.WorkforceRead, ResourceRef.Of(Permissions.WorkforceRead)).IsPermitted)
        {
            sources.Add(new("shift-context", "NotAvailableToRole", null, "Workforce and resource read permissions"));
            return null;
        }

        try
        {
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            var workforce = uow.Store<IWorkforceLookup>();
            var worker = (await workforce.GetWorkerStatesAsync(at, ct)).FirstOrDefault(state => state.PersonId == principal.PersonId.Value);
            var sourceAsOf = time.GetUtcNow();
            if (worker is null)
            {
                sources.Add(new("shift-context", "NoWorkerRecord", sourceAsOf, "Authenticated identity only", "No workforce record is linked to this identity."));
                return null;
            }

            if (!authorizer.Authorize(principal, Permissions.ResourcesRead, ResourceRef.Of(Permissions.ResourcesRead)).IsPermitted)
            {
                sources.Add(new("shift-context", "Limited", sourceAsOf, "Worker shift assignment is recorded; site calendar requires resource-read permission"));
                return null;
            }

            var resources = uow.Store<IResourcesLookup>();
            var sites = await resources.ListSitesAsync(ct);
            var centers = await resources.ListWorkCentersAsync(ct);
            var areas = await resources.ListSafetyAreasAsync(ct);
            var shifts = await resources.ListShiftsAsync(ct);
            var homeCenter = centers.FirstOrDefault(center => center.Code == worker.HomeWorkCenterCode);
            var homeArea = homeCenter is null ? null : areas.FirstOrDefault(area => area.Code == homeCenter.SafetyAreaCode);
            var shift = worker.ShiftCode is null ? null : shifts.FirstOrDefault(item => item.Code == worker.ShiftCode);
            var siteCode = shift?.SiteCode ?? homeArea?.SiteCode;
            var site = siteCode is null ? null : sites.FirstOrDefault(item => item.Code == siteCode);

            if (site is null || shift is null || !string.Equals(shift.SiteCode, site.Code, StringComparison.OrdinalIgnoreCase))
            {
                sources.Add(new("shift-context", "Limited", sourceAsOf, "Authenticated worker record and current site/shift configuration", "A site and assigned shift are not both configured for this worker."));
                return null;
            }

            var closures = await resources.ListSiteClosuresAsync(ct);
            var occurrence = OperationalDayResolver.Resolve(shift, site.TimeZone, at, closures);
            if (occurrence is null)
            {
                sources.Add(new("shift-context", "Limited", sourceAsOf, "Assigned shift, site time zone, and site closure calendar", "No scheduled occurrence was found in the site's shift calendar."));
                return null;
            }

            sources.Add(new("shift-context", "Available", time.GetUtcNow(), "Authenticated worker's home site and assigned shift"));
            return new DailyMissionOperationalContext(
                site.Code,
                site.Name,
                site.TimeZone,
                shift.Code,
                shift.Name,
                occurrence.OperationalDate,
                occurrence.StartsAt,
                occurrence.EndsAt,
                occurrence.Relation,
                string.Join(":", site.Code, shift.Code, occurrence.OperationalDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Daily Mission could not resolve the assigned shift context for {PersonId}", principal.PersonId);
            sources.Add(new("shift-context", "Unavailable", null, "Current workforce, resource, and site-calendar permissions", "Refresh when the shift calendar is available."));
            warnings.Add("Your site and shift context could not be refreshed.");
            return null;
        }
    }
}
