using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Holds;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Quality.Contracts;

namespace Forge.Planning;

/// <summary>A stable identifier of a source record. It is never inferred from a description or free text.</summary>
public sealed record OperationalRecordId(string Context, string RecordType, string RecordId)
{
    public string Key => $"{Context}:{RecordType}:{RecordId}";
}

public enum OperationalEvidenceState
{
    Open,
    Active,
    Upcoming,
    Ended,
    Proposed,
    Confirmed,
    Shortfall,
    OverdueSupply,
    LateSupply,
    Blocked,
    Indeterminate,
}

public enum OperationalCauseState
{
    Unattributed,
    Proposed,
    Confirmed,
}

public enum OperationalEvidenceSourceStatus
{
    Included,
    NotAuthorized,
}

public enum OperationalEvidenceRelation
{
    ExplicitOrderLink,
    ExplicitOperationLink,
    DirectHoldTarget,
    HoldPropagationScope,
    AllocatedSupply,
    CalculatedPartRequirement,
    ExplicitSourceLink,
    SameSourceRecord,
    SameOrderContext,
    SamePartContext,
    SameMachineContext,
    SameCauseIdentifier,
}

/// <summary>
/// A typed relationship to a record or operational scope. IsExactReference says the target was recorded or computed
/// as an exact relationship; it does not by itself prove causation. CauseIdentifier carries that separate judgment.
/// </summary>
public sealed record OperationalEvidenceLink(
    string TargetType,
    string TargetId,
    OperationalEvidenceRelation Relationship,
    bool IsExactReference,
    OperationalRecordId? TargetRecord = null);

/// <summary>
/// One source fact or an explicitly marked calculation. CauseIdentifier is populated only when a root-cause
/// record exists; proposed root causes remain Proposed and are not represented as proven.
/// </summary>
public sealed record OperationalEvidenceSignal(
    string SignalId,
    string Category,
    OperationalEvidenceState State,
    OperationalRecordId? SourceRecord,
    OperationalRecordId? CauseIdentifier,
    OperationalCauseState CauseState,
    DateTimeOffset? ObservedAt,
    string? ObservedAtMeaning,
    DateTimeOffset? LifecycleAt,
    string? LifecycleAtMeaning,
    IReadOnlyList<OperationalEvidenceLink> Links);

public sealed record OperationalEvidenceAssociation(
    string AssociationId,
    OperationalEvidenceRelation Relationship,
    string ScopeType,
    string ScopeId,
    IReadOnlyList<string> SignalIds,
    bool CommonCauseProven);

/// <summary>
/// These timestamps bound the API read. SnapshotId is the compatibility token: included sources were read
/// through the same repeatable-read unit of work. The interval is deliberately not presented as a database timestamp.
/// </summary>
public sealed record OperationalEvidenceReadWindow(
    Guid SnapshotId,
    DateTimeOffset ReadStartedAt,
    DateTimeOffset ReadCompletedAt,
    string Consistency);

public sealed record OperationalEvidenceSource(
    string Code,
    OperationalEvidenceSourceStatus Status,
    Guid? SnapshotId,
    string TimestampSemantics);

public sealed record OperationalEvidenceResponse(
    OperationalEvidenceReadWindow Read,
    IReadOnlyList<OperationalEvidenceSource> Sources,
    IReadOnlyList<OperationalEvidenceSignal> Signals,
    IReadOnlyList<OperationalEvidenceAssociation> Associations,
    IReadOnlyList<string> Limitations,
    bool SourcesShareReadSnapshot);

/// <summary>
/// Source-linked, read-only evidence for future operational risk views. It does not produce a risk score or merge
/// independent blockers. Every included source is read in the same repeatable-read database unit of work.
/// </summary>
public sealed class OperationalEvidenceService(
    IKernelUnitOfWorkFactory unitOfWork,
    IForgeAuthorizer authorizer,
    TimeProvider time)
{
    private static readonly (string Code, string Permission)[] SourcePermissions =
    [
        ("readiness", Permissions.ReadinessRead),
        ("material", Permissions.MaterialPlanRead),
        ("quality", Permissions.QualityRead),
        ("execution-stoppages", Permissions.ShopFloorRead),
        ("maintenance", Permissions.MaintenanceRead),
        ("holds", Permissions.HoldsRead),
    ];

    public async Task<OperationalEvidenceResponse> ReadAsync(
        ForgePrincipal principal,
        long? orderNumber,
        int lookbackDays,
        int horizonDays,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (orderNumber is <= 0) throw new CommandRejectedException("order_not_found", "The production order does not exist.");
        if (lookbackDays is < 1 or > 90) throw new CommandRejectedException("lookback_days", "Lookback days must be between 1 and 90.");
        if (horizonDays is < 1 or > 31) throw new CommandRejectedException("horizon_days", "Horizon days must be between 1 and 31.");
        Demand(principal, Permissions.ReadinessRead, "Readiness");

        var startedAt = time.GetUtcNow();
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var snapshotId = ForgeIds.New(time);
        var included = SourcePermissions.ToDictionary(
            item => item.Code,
            item => item.Code == "readiness" || authorizer.Authorize(principal, item.Permission, ResourceRef.Of(item.Permission)).IsPermitted,
            StringComparer.Ordinal);
        var signals = new List<OperationalEvidenceSignal>();
        var limitations = new List<string>
        {
            "This response is a compatible read of current Forge records, not a future-horizon risk score or approved schedule projection.",
            "A shared order, part, or machine is context only. It does not prove that two signals share a physical cause.",
            "Computed readiness and material conditions have no cause identifier unless a stable source record is explicitly linked.",
        };

        var operations = await ExecutableOperations.EvaluateAsync(uow, time, principal, ct);
        var execution = uow.Store<IExecutionLookup>();
        var orderSummaries = (await execution.ListOrdersAsync(ct))
            .Where(order => order.ProgramId is not { } program || principal.Programs.Contains(new ProgramId(program)))
            .Where(order => order.Status is OrderStatus.Released or OrderStatus.Held)
            .Where(order => orderNumber is null || order.OrderNumber == orderNumber)
            .ToList();
        var orderHeaders = new Dictionary<long, OrderInfo>();
        foreach (var summary in orderSummaries)
        {
            if (await execution.FindOrderAsync(summary.OrderNumber, ct) is { } order)
                orderHeaders[order.OrderNumber] = order;
        }

        if (orderNumber is { } requested && !orderHeaders.ContainsKey(requested))
            throw new CommandRejectedException("order_not_found", $"Order {requested} does not exist.");

        if (included["readiness"])
        {
            foreach (var operation in operations.Where(operation => orderHeaders.ContainsKey(operation.OrderNumber)))
            {
                foreach (var dimension in operation.Dimensions.Where(dimension => dimension.Outcome is Forge.Kernel.Engines.EvidenceOutcome.Fail or Forge.Kernel.Engines.EvidenceOutcome.Indeterminate))
                {
                    var state = dimension.Outcome == Forge.Kernel.Engines.EvidenceOutcome.Fail
                        ? OperationalEvidenceState.Blocked
                        : OperationalEvidenceState.Indeterminate;
                    var subjectId = $"{operation.OrderNumber.ToString(CultureInfo.InvariantCulture)}/{operation.Sequence.ToString(CultureInfo.InvariantCulture)}";
                    signals.Add(new OperationalEvidenceSignal(
                        $"planning:ReadinessBlocker:{subjectId}:{dimension.Name}",
                        "ReadinessBlocker",
                        state,
                        null,
                        null,
                        OperationalCauseState.Unattributed,
                        null,
                        null,
                        null,
                        null,
                        [new("Operation", subjectId, OperationalEvidenceRelation.ExplicitOperationLink, true)]));
                }
            }
        }

        if (included["material"])
        {
            var material = await MaterialPlanningService.RunMaterialAsync(uow, time, ct, principal);
            var orders = orderHeaders.Values.ToDictionary(order => order.OrderNumber);
            var materialVisibility = await MaterialSourceVisibility.LoadAsync(uow, principal, ct, orderHeaders.Values.ToList(), authorizer);
            var today = await MaterialData.TodayAsync(uow, time, ct);
            foreach (var status in material.Output.Where(status => orderHeaders.ContainsKey(status.OrderNumber)))
            {
                var order = orders.GetValueOrDefault(status.OrderNumber);
                if (order is null || MaterialRiskService.Assess(status, order) is not { } risk) continue;
                var links = new List<OperationalEvidenceLink>
                {
                    new("Order", status.OrderNumber.ToString(CultureInfo.InvariantCulture), OperationalEvidenceRelation.ExplicitOrderLink, true),
                };
                if (risk.Kind == "Shortfall")
                {
                    links.AddRange(status.Lines.Where(line => line.Shortfall > 0)
                        .Select(line => new OperationalEvidenceLink("Part", line.PartNumber, OperationalEvidenceRelation.CalculatedPartRequirement, true)));
                }
                foreach (var allocation in RiskContributingAllocations(status, risk.Kind, order.DueDate, today))
                {
                    if (TrySupplyIdentifier(allocation.Source, out var supply) && CanExposeSupply(supply, materialVisibility))
                        links.Add(new("Supply", supply.Key, OperationalEvidenceRelation.AllocatedSupply, true, supply));
                }

                var state = risk.Kind switch
                {
                    "Shortfall" => OperationalEvidenceState.Shortfall,
                    "OverdueSupply" => OperationalEvidenceState.OverdueSupply,
                    _ => OperationalEvidenceState.LateSupply,
                };
                signals.Add(new OperationalEvidenceSignal(
                    $"planning:MaterialRisk:Order:{status.OrderNumber.ToString(CultureInfo.InvariantCulture)}:{risk.Kind}",
                    "MaterialRisk",
                    state,
                    null,
                    null,
                    OperationalCauseState.Unattributed,
                    null,
                    "ComputedFromCurrentMaterialAllocation",
                    null,
                    null,
                    links));
            }
        }

        if (included["quality"])
        {
            var quality = uow.Store<IQualityLookup>();
            foreach (var ncr in await quality.ListNcrsAsync(false, ct))
            {
                if (ncr.OrderNumber is not { } number || !orderHeaders.ContainsKey(number)) continue;
                var links = new List<OperationalEvidenceLink>
                {
                    new("Order", number.ToString(CultureInfo.InvariantCulture), OperationalEvidenceRelation.ExplicitOrderLink, true),
                };
                if (ncr.Sequence is { } sequence)
                    links.Add(new("Operation", $"{number.ToString(CultureInfo.InvariantCulture)}/{sequence.ToString(CultureInfo.InvariantCulture)}", OperationalEvidenceRelation.ExplicitOperationLink, true));
                var source = new OperationalRecordId("quality", "Ncr", ncr.NcrId.ToString(CultureInfo.InvariantCulture));
                links.Add(new("Part", ncr.PartNumber, OperationalEvidenceRelation.ExplicitSourceLink, true));
                signals.Add(new OperationalEvidenceSignal(
                    source.Key, "QualityNcr", OperationalEvidenceState.Open, source, null, OperationalCauseState.Unattributed,
                    ncr.RaisedAt, "NcrRaisedAt", ncr.ClosedAt, "NcrClosedAt", links));
            }
        }

        if (included["execution-stoppages"])
        {
            var now = time.GetUtcNow();
            var from = now.AddDays(-lookbackDays);
            var to = now.AddDays(horizonDays);
            var lookup = uow.Store<IProductionActualsLookup>();
            var stoppages = await lookup.ListStoppagesAsync(from, to, false, ct);
            var visibleStoppages = stoppages.Where(stoppage => stoppage.OrderNumber is null
                ? orderNumber is null
                : orderHeaders.ContainsKey(stoppage.OrderNumber.Value)).ToList();
            var causes = await lookup.ListRootCausesAsync(visibleStoppages.Select(stoppage => stoppage.StoppageId).Distinct().ToArray(), ct);
            var causesByStoppage = causes.ToLookup(cause => cause.StoppageId);
            foreach (var stoppage in visibleStoppages)
            {
                var source = new OperationalRecordId("execution", "Stoppage", stoppage.StoppageId.ToString(CultureInfo.InvariantCulture));
                var links = StoppageLinks(stoppage);
                var state = stoppage.StartedAt > now
                    ? OperationalEvidenceState.Upcoming
                    : stoppage.EndedAt is null
                        ? OperationalEvidenceState.Open
                        : stoppage.EndedAt > now
                            ? OperationalEvidenceState.Active
                            : OperationalEvidenceState.Ended;
                signals.Add(new OperationalEvidenceSignal(
                    source.Key, "ProductionStoppage", state,
                    source, null, OperationalCauseState.Unattributed, stoppage.StartedAt, "StoppageStartedAt", stoppage.EndedAt, "StoppageEndedAt", links));

                foreach (var cause in causesByStoppage[stoppage.StoppageId])
                {
                    var causeId = new OperationalRecordId("execution", "RootCause", cause.RootCauseId.ToString(CultureInfo.InvariantCulture));
                    var causeLinks = StoppageLinks(stoppage).Append(new OperationalEvidenceLink("Stoppage", stoppage.StoppageId.ToString(CultureInfo.InvariantCulture), OperationalEvidenceRelation.ExplicitSourceLink, true, source)).ToArray();
                    signals.Add(new OperationalEvidenceSignal(
                        causeId.Key, "RootCauseAssessment", cause.Proven ? OperationalEvidenceState.Confirmed : OperationalEvidenceState.Proposed,
                        causeId, causeId, cause.Proven ? OperationalCauseState.Confirmed : OperationalCauseState.Proposed,
                        cause.ProposedAt, "RootCauseProposedAt", cause.ConfirmedAt, "RootCauseConfirmedAt", causeLinks));
                }
            }
        }

        if (included["maintenance"])
        {
            var now = time.GetUtcNow();
            var windows = await uow.Store<IMaintenanceLookup>().ListDowntimeAsync(now.AddDays(-lookbackDays), now.AddDays(horizonDays), ct);
            foreach (var window in windows.Where(window => window.StartsAt <= now.AddDays(horizonDays) && (window.EndsAt is null || window.EndsAt > now)))
            {
                var state = window.StartsAt <= now ? OperationalEvidenceState.Active : OperationalEvidenceState.Upcoming;
                var source = MaintenanceRecordIdentifier(window);
                signals.Add(new OperationalEvidenceSignal(
                    source.Key, "MachineDowntime", state, source, null, OperationalCauseState.Unattributed,
                    window.StartsAt, "DowntimeStartsAt", window.EndsAt, "DowntimeEndsAt",
                    [new("Machine", window.MachineCode, OperationalEvidenceRelation.ExplicitSourceLink, true)]));
            }
        }

        if (included["holds"])
        {
            var holdLookup = uow.Store<IHoldLookup>();
            var holds = await holdLookup.ListHoldsAsync(false, ct);
            var policies = await holdLookup.ListKindPoliciesAsync(ct);
            foreach (var hold in holds)
            {
                var links = HoldLinks(hold, orderHeaders.Values);
                if (hold.TargetType == HoldTargetType.Lot && HoldService.PolicyOf(policies, hold.Kind).PropagateDownstream)
                {
                    var scope = await uow.Store<IHoldPropagationLookup>().ScopeAsync(hold, ct);
                    foreach (var affected in scope.Orders.Where(orderHeaders.ContainsKey).Order())
                    {
                        var id = affected.ToString(CultureInfo.InvariantCulture);
                        if (!links.Any(link => link.TargetType == "Order" && link.TargetId == id))
                            links.Add(new("Order", id, OperationalEvidenceRelation.HoldPropagationScope, true));
                    }
                }

                if (links.Count == 0) continue;
                var source = new OperationalRecordId("kernel", "Hold", hold.HoldId.ToString(CultureInfo.InvariantCulture));
                signals.Add(new OperationalEvidenceSignal(
                    source.Key, $"{hold.Kind}Hold", OperationalEvidenceState.Active, source, null, OperationalCauseState.Unattributed,
                    hold.PlacedAt, "HoldPlacedAt", hold.ReleasedAt, "HoldReleasedAt", links));
            }
        }

        var completedAt = time.GetUtcNow();
        var read = new OperationalEvidenceReadWindow(snapshotId, startedAt, completedAt, "RepeatableReadUnitOfWork");
        var sourceStates = SourcePermissions.Select(item => new OperationalEvidenceSource(
            item.Code,
            included[item.Code] ? OperationalEvidenceSourceStatus.Included : OperationalEvidenceSourceStatus.NotAuthorized,
            included[item.Code] ? snapshotId : null,
            included[item.Code] ? "ReadInRepeatableReadUnitOfWork; record timestamps retain their stated event meaning" : "SourceDetailsOmittedBecausePermissionWasUnavailable"))
            .ToList();
        return new OperationalEvidenceResponse(read, sourceStates, signals.OrderBy(signal => signal.SignalId, StringComparer.Ordinal).ToList(),
            OperationalEvidenceBuilder.Associations(signals), limitations,
            OperationalEvidenceBuilder.SourcesShareReadSnapshot(sourceStates));
    }

    private static List<OperationalEvidenceLink> StoppageLinks(StoppageInfo stoppage)
    {
        var links = new List<OperationalEvidenceLink>
        {
            new("Machine", stoppage.MachineCode, OperationalEvidenceRelation.ExplicitSourceLink, true),
        };
        if (stoppage.OrderNumber is not { } number) return links;
        links.Add(new("Order", number.ToString(CultureInfo.InvariantCulture), OperationalEvidenceRelation.ExplicitOrderLink, true));
        if (stoppage.Sequence is { } sequence)
            links.Add(new("Operation", $"{number.ToString(CultureInfo.InvariantCulture)}/{sequence.ToString(CultureInfo.InvariantCulture)}", OperationalEvidenceRelation.ExplicitOperationLink, true));
        return links;
    }

    internal static List<OperationalEvidenceLink> HoldLinks(Hold hold, IEnumerable<OrderInfo> orders)
    {
        var links = new List<OperationalEvidenceLink>();
        foreach (var order in orders)
        {
            if (hold.TargetType == HoldTargetType.Order && hold.TargetId == order.OrderNumber.ToString(CultureInfo.InvariantCulture))
                links.Add(new("Order", hold.TargetId, OperationalEvidenceRelation.DirectHoldTarget, true));
            else if (hold.TargetType == HoldTargetType.Operation
                && hold.TargetId.Split('/', StringSplitOptions.None) is [var orderText, var sequenceText]
                && long.TryParse(orderText, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && int.TryParse(sequenceText, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
                && number == order.OrderNumber
                && order.Operations.Any(operation => operation.Sequence == sequence))
                links.Add(new("Operation", hold.TargetId, OperationalEvidenceRelation.DirectHoldTarget, true));
            else if (hold.TargetType == HoldTargetType.Part && (hold.TargetId == order.PartNumber || order.Materials.Any(material => material.ComponentPartNumber == hold.TargetId)))
            {
                links.Add(new("Part", hold.TargetId, OperationalEvidenceRelation.DirectHoldTarget, true));
                links.Add(new("Order", order.OrderNumber.ToString(CultureInfo.InvariantCulture), OperationalEvidenceRelation.DirectHoldTarget, true));
            }
        }
        return links;
    }

    internal static IEnumerable<MaterialAllocation> RiskContributingAllocations(OrderMaterialStatus status, string kind, DateOnly dueDate, DateOnly today)
    {
        var allocations = status.Lines.SelectMany(line => line.Allocations.Where(allocation => allocation.DueDate is not null)
            .Select(allocation => (Line: line, Allocation: allocation))).ToList();
        return kind switch
        {
            "OverdueSupply" => allocations.Where(item => item.Allocation.DueDate is { } due && due < today).Select(item => item.Allocation),
            "LateSupply" => allocations.Where(item => item.Allocation.DueDate is { } due && status.ReadyOn == due && due > dueDate).Select(item => item.Allocation),
            _ => [],
        };
    }

    internal static bool TrySupplyIdentifier(string key, out OperationalRecordId id)
    {
        var split = key.Split('/', StringSplitOptions.TrimEntries);
        if (split.Length == 2 && split[0].StartsWith("PO", StringComparison.Ordinal)
            && long.TryParse(split[0].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var po)
            && int.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out var line))
        {
            id = new OperationalRecordId("procurement", "PurchaseOrderLine", $"{po.ToString(CultureInfo.InvariantCulture)}/{line.ToString(CultureInfo.InvariantCulture)}");
            return true;
        }

        if (split.Length == 1 && split[0].StartsWith("WO", StringComparison.Ordinal)
            && long.TryParse(split[0].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var order))
        {
            id = new OperationalRecordId("execution", "ProductionOrder", order.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        id = null!;
        return false;
    }

    private static bool CanExposeSupply(OperationalRecordId supply, MaterialSourceVisibility visibility)
    {
        if (supply.Context == "procurement" && supply.RecordType == "PurchaseOrderLine")
        {
            var po = long.Parse(supply.RecordId.Split('/')[0], CultureInfo.InvariantCulture);
            return visibility.CanSeePurchaseOrder(po);
        }

        if (supply.Context == "execution" && supply.RecordType == "ProductionOrder")
            return visibility.CanSeeOrder(long.Parse(supply.RecordId, CultureInfo.InvariantCulture));
        return false;
    }

    internal static OperationalRecordId MaintenanceRecordIdentifier(DowntimeWindow window)
    {
        if (window.SourceReference is { } reference && reference.Split('/', 2) is [var type, var id] && !string.IsNullOrWhiteSpace(id))
            return new OperationalRecordId(window.SourceContext.ToLowerInvariant(), type, id);
        return new OperationalRecordId(window.SourceContext.ToLowerInvariant(), "DowntimeProjection", window.Id.ToString(CultureInfo.InvariantCulture));
    }

    private void Demand(ForgePrincipal principal, string permission, string resource)
    {
        var decision = authorizer.Authorize(principal, permission, ResourceRef.Of(resource));
        if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision);
    }

}

internal static class OperationalEvidenceBuilder
{
    public static bool SourcesShareReadSnapshot(IReadOnlyList<OperationalEvidenceSource> sources)
    {
        var included = sources.Where(source => source.Status == OperationalEvidenceSourceStatus.Included).ToList();
        return included.Count > 0 && included[0].SnapshotId is { } snapshot
            && included.All(source => source.SnapshotId == snapshot);
    }

    public static IReadOnlyList<OperationalEvidenceAssociation> Associations(IReadOnlyList<OperationalEvidenceSignal> signals)
    {
        var associations = new List<OperationalEvidenceAssociation>();
        foreach (var group in signals.SelectMany(signal => signal.Links.Where(link => link.IsExactReference && link.TargetRecord is not null)
                     .Select(link => (Signal: signal, TargetRecord: link.TargetRecord!)))
                 .GroupBy(item => item.TargetRecord.Key, StringComparer.Ordinal)
                 .Where(group => group.Select(item => item.Signal.SignalId).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            associations.Add(new OperationalEvidenceAssociation(
                $"shared-source:{group.Key}", OperationalEvidenceRelation.SameSourceRecord, "SourceRecord", group.Key,
                group.Select(item => item.Signal.SignalId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), false));
        }

        foreach (var scope in new[]
                 {
                     (Type: "Order", Relation: OperationalEvidenceRelation.SameOrderContext),
                     (Type: "Part", Relation: OperationalEvidenceRelation.SamePartContext),
                     (Type: "Machine", Relation: OperationalEvidenceRelation.SameMachineContext),
                 })
        {
            foreach (var group in signals.SelectMany(signal => signal.Links.Where(link => link.TargetType == scope.Type)
                         .Select(link => (Signal: signal, link.TargetId)))
                     .GroupBy(item => item.TargetId, StringComparer.Ordinal).Where(group => group.Select(item => item.Signal.SignalId).Distinct(StringComparer.Ordinal).Count() > 1))
            {
                associations.Add(new OperationalEvidenceAssociation(
                    $"shared-{scope.Type.ToLowerInvariant()}:{group.Key}", scope.Relation, scope.Type, group.Key,
                    group.Select(item => item.Signal.SignalId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), false));
            }
        }

        foreach (var group in signals.Where(signal => signal.CauseIdentifier is not null)
                     .GroupBy(signal => signal.CauseIdentifier!.Key, StringComparer.Ordinal)
                     .Where(group => group.Select(signal => signal.SignalId).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            associations.Add(new OperationalEvidenceAssociation(
                $"same-cause:{group.Key}", OperationalEvidenceRelation.SameCauseIdentifier, "Cause", group.Key,
                group.Select(signal => signal.SignalId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                group.All(signal => signal.CauseState == OperationalCauseState.Confirmed)));
        }

        return associations.OrderBy(association => association.AssociationId, StringComparer.Ordinal).ToArray();
    }
}
