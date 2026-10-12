using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Engines;
using Forge.Programs.Contracts;

namespace Forge.Planning.Reporting;

public enum HorizonLens { Factory, Program, Contract, WorkCenter, Machine, Order, Workforce }
public sealed record HorizonLensRequest(HorizonLens Lens, DateTimeOffset At, DateTimeOffset From, DateTimeOffset To,
    Guid? ProgramId = null, long? ContractId = null, string? WorkCenter = null, string? Machine = null,
    long? OrderNumber = null, Guid? PersonId = null, bool OnlyCommitted = false);
public sealed record HorizonCounts(int Orders, int Operations, int Scheduled, int Unscheduled, int OnTime, int AtRisk, int Late, int Unschedulable);
public sealed record HorizonGroup(string Key, string Label, HorizonCounts Counts, IReadOnlyList<long> OrderNumbers);
public sealed record HorizonChoice(string Key, string Label);
public sealed record HorizonChoices(IReadOnlyList<HorizonChoice> Programs, IReadOnlyList<HorizonChoice> Contracts,
    IReadOnlyList<HorizonChoice> WorkCenters, IReadOnlyList<HorizonChoice> Machines, IReadOnlyList<HorizonChoice> Orders,
    IReadOnlyList<HorizonChoice> People);
public sealed record HorizonLensView(HorizonLens Lens, DateTimeOffset At, DateTimeOffset From, DateTimeOffset To,
    long? ScheduleVersion, DateTimeOffset? ApprovedAt, HorizonCounts Counts, IReadOnlyList<ScheduledOrder> Orders,
    IReadOnlyList<ScheduledOperation> Operations, IReadOnlyList<HorizonGroup> Groups, HorizonChoices Choices,
    IReadOnlyList<OrderCommitment> Commitments, int ExcludedUnlinkedOperations, string Source);

/// <summary>Every lens filters one archived approved projection and the explicit commitments recorded at the same instant.</summary>
public sealed class HorizonLensService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer)
{
    public async Task<HorizonLensView> ReadAsync(ForgePrincipal principal, HorizonLensRequest request, CancellationToken ct)
    {
        var requestedScope = new ResourceRef("Schedule", Program: request.ProgramId is { } requestedId ? new ProgramId(requestedId) : null,
            WorkCenterId: request.WorkCenter, ContractId: request.ContractId?.ToString(CultureInfo.InvariantCulture));
        if (!HasDeclaredPermission(principal, Permissions.ScheduleRead)) Demand(principal, Permissions.ScheduleRead, requestedScope);
        if (request.To <= request.From || request.To - request.From > TimeSpan.FromDays(31))
            throw new CommandRejectedException("invalid_horizon_period", "Choose a positive Horizon window of at most 31 days.");
        if (request.ProgramId is { } requestedProgram && !principal.Programs.Contains(new ProgramId(requestedProgram)))
            throw new CommandRejectedException("program_not_found", "The selected program is not accessible.");
        if ((request.Lens == HorizonLens.Contract || request.ContractId is not null) && !HasDeclaredPermission(principal, Permissions.ProgramsRead))
            Demand(principal, Permissions.ProgramsRead, new ResourceRef("Contract", request.ContractId?.ToString(CultureInfo.InvariantCulture),
                request.ProgramId is { } p ? new ProgramId(p) : null, ContractId: request.ContractId?.ToString(CultureInfo.InvariantCulture)));
        var workforceRequired = request.Lens == HorizonLens.Workforce || request.PersonId is not null;
        if (workforceRequired && !HasDeclaredPermission(principal, Permissions.WorkforceRead))
            Demand(principal, Permissions.WorkforceRead, requestedScope with { Type = "Workforce" });

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var version = await uow.Store<IPlanningStore>().ApprovedAsOfAsync(request.At, ct);
        bool Visible(Guid? program) => program is null || principal.Programs.Contains(new ProgramId(program.Value));
        var allOrders = (version?.Result?.Orders ?? []).Where(order => Visible(order.ProgramId)).ToDictionary(order => order.OrderNumber);
        var links = (await uow.Store<IOrderCommitmentLookup>().AsOfAsync(request.At, ct))
            .Where(link => allOrders.ContainsKey(link.OrderNumber) && link.ContractId is not null).ToDictionary(link => link.OrderNumber);
        var programs = (await uow.Store<IProgramsLookup>().ListProgramsAsync(ct)).Where(program => principal.Programs.Contains(new ProgramId(program.ProgramId))).ToDictionary(program => program.ProgramId);
        var contracts = (await uow.Store<IProgramsLookup>().ListContractsAsync(null, ct))
            .Where(contract => principal.Programs.Contains(new ProgramId(contract.ProgramId))
                && authorizer.Authorize(principal, Permissions.ProgramsRead,
                    new ResourceRef("Contract", contract.ContractId.ToString(CultureInfo.InvariantCulture), new ProgramId(contract.ProgramId), ContractId: contract.ContractId.ToString(CultureInfo.InvariantCulture))).IsPermitted)
            .ToDictionary(contract => contract.ContractId);
        if (request.ContractId is { } contractId && !contracts.ContainsKey(contractId))
            throw new CommandRejectedException("contract_not_found", "The selected contract is not accessible.");

        // A scoped read filters actual archived operation context; an omitted selector never widens it.
        var window = (version?.Result?.Operations ?? []).Where(operation => allOrders.ContainsKey(operation.OrderNumber)
            && (operation.Scheduled ? operation.Start < request.To && operation.End > request.From : version!.From < request.To && version.To > request.From)
            && authorizer.Authorize(principal, Permissions.ScheduleRead, new ResourceRef("ScheduleOperation", $"{operation.OrderNumber}/{operation.Sequence}",
                operation.ProgramId is { } programId ? new ProgramId(programId) : null, FacilityId: operation.SiteCode, WorkCenterId: operation.WorkCenterCode,
                ContractId: links.GetValueOrDefault(operation.OrderNumber)?.ContractId?.ToString(CultureInfo.InvariantCulture),
                CustomerId: links.TryGetValue(operation.OrderNumber, out var link) && link.ContractId is { } linkedContract && contracts.TryGetValue(linkedContract, out var contract) ? contract.Customer : null)).IsPermitted
            && (!workforceRequired || authorizer.Authorize(principal, Permissions.WorkforceRead,
                new ResourceRef("Workforce", request.PersonId?.ToString("D"), operation.ProgramId is { } workforceProgram ? new ProgramId(workforceProgram) : null,
                    FacilityId: operation.SiteCode, WorkCenterId: operation.WorkCenterCode, ContractId: links.GetValueOrDefault(operation.OrderNumber)?.ContractId?.ToString(CultureInfo.InvariantCulture))).IsPermitted))
            .ToList();
        var visibleOrderIds = window.Select(operation => operation.OrderNumber).ToHashSet();
        allOrders = allOrders.Where(order => visibleOrderIds.Contains(order.Key)).ToDictionary(order => order.Key, order => order.Value);
        links = links.Where(link => visibleOrderIds.Contains(link.Key)).ToDictionary(link => link.Key, link => link.Value);

        var people = new Dictionary<Guid, string>();
        foreach (var personId in window.SelectMany(operation => operation.Segments).SelectMany(segment => segment.Operators).Distinct())
        {
            var person = await uow.People.FindAsync(new PersonId(personId), ct);
            people[personId] = person?.DisplayName ?? personId.ToString("D");
        }
        var filtered = window.Where(operation => (request.ProgramId is null || operation.ProgramId == request.ProgramId)
            && (request.WorkCenter is null || operation.WorkCenterCode.Equals(request.WorkCenter, StringComparison.OrdinalIgnoreCase))
            && (request.Machine is null || string.Equals(operation.MachineCode, request.Machine, StringComparison.OrdinalIgnoreCase))
            && (request.OrderNumber is null || operation.OrderNumber == request.OrderNumber)
            && (request.PersonId is null || operation.Segments.Any(segment => segment.End > request.From && segment.Start < request.To && segment.Operators.Contains(request.PersonId.Value)))
            && (request.ContractId is null || links.TryGetValue(operation.OrderNumber, out var link) && link.ContractId == request.ContractId)).ToList();
        var excluded = 0;
        if (request.OnlyCommitted || request.Lens == HorizonLens.Contract)
        {
            excluded = filtered.Count(operation => !links.ContainsKey(operation.OrderNumber));
            filtered = filtered.Where(operation => links.TryGetValue(operation.OrderNumber, out var link)
                && (request.Lens != HorizonLens.Contract || link.ContractId is { } id && contracts.ContainsKey(id))).ToList();
        }
        var ids = filtered.Select(operation => operation.OrderNumber).Distinct().ToHashSet();
        var orders = allOrders.Values.Where(order => ids.Contains(order.OrderNumber)).OrderBy(order => order.OrderNumber).ToList();
        var grouped = filtered.SelectMany(operation => Keys(request.Lens, operation, links, request.From, request.To)).GroupBy(pair => pair.Key, pair => pair.Operation, StringComparer.Ordinal)
            .Select(group => new HorizonGroup(group.Key, Label(request.Lens, group.Key, programs, contracts, people), Counts(group.ToList(), allOrders), group.Select(operation => operation.OrderNumber).Distinct().Order().ToList()))
            .OrderBy(group => group.Label, StringComparer.Ordinal).ToList();
        var choices = new HorizonChoices(
            window.Where(operation => operation.ProgramId is not null).Select(operation => operation.ProgramId!.Value).Distinct().Order().Select(id => new HorizonChoice(id.ToString("D"), programs.TryGetValue(id, out var program) ? program.Code : id.ToString("D"))).ToList(),
            links.Values.Select(link => link.ContractId!.Value).Distinct().Where(contracts.ContainsKey).Order().Select(id => new HorizonChoice(id.ToString(CultureInfo.InvariantCulture), contracts[id].Number)).ToList(),
            window.Select(operation => operation.WorkCenterCode).Distinct().Order(StringComparer.Ordinal).Select(code => new HorizonChoice(code, code)).ToList(),
            window.Where(operation => operation.MachineCode is not null).Select(operation => operation.MachineCode!).Distinct().Order(StringComparer.Ordinal).Select(code => new HorizonChoice(code, code)).ToList(),
            allOrders.Keys.Order().Select(number => new HorizonChoice(number.ToString(CultureInfo.InvariantCulture), $"#{number} · {allOrders[number].PartNumber}")).ToList(),
            people.OrderBy(person => person.Value, StringComparer.Ordinal).Select(person => new HorizonChoice(person.Key.ToString("D"), person.Value)).ToList());
        return new(request.Lens, request.At, request.From, request.To, version?.VersionId, version?.ApprovedAt,
            Counts(filtered, allOrders), orders, filtered, grouped, choices, links.Values.Where(link => ids.Contains(link.OrderNumber)).ToList(), excluded,
            version is null ? "No approved schedule was in force at this instant. No current projection has been substituted."
                : $"Approved schedule {version.VersionId} in force at {request.At:O}; projections and assigned crews are the recorded schedule, not today's recalculation. Contract attribution comes only from explicit order commitments recorded by this instant.");
    }

    private static IEnumerable<(string Key, ScheduledOperation Operation)> Keys(HorizonLens lens, ScheduledOperation operation, IReadOnlyDictionary<long, OrderCommitment> links, DateTimeOffset from, DateTimeOffset to) => lens switch
    {
        HorizonLens.Factory => [("Factory", operation)],
        HorizonLens.Program => [(operation.ProgramId?.ToString("D") ?? "Unassigned", operation)],
        HorizonLens.Contract => links.TryGetValue(operation.OrderNumber, out var link) && link.ContractId is { } contract ? [(contract.ToString(CultureInfo.InvariantCulture), operation)] : [],
        HorizonLens.WorkCenter => [(operation.WorkCenterCode, operation)],
        HorizonLens.Machine => [(operation.MachineCode ?? "No machine", operation)],
        HorizonLens.Order => [(operation.OrderNumber.ToString(CultureInfo.InvariantCulture), operation)],
        HorizonLens.Workforce => operation.Segments.Any(segment => segment.End > from && segment.Start < to && segment.Operators.Count > 0)
            ? operation.Segments.Where(segment => segment.End > from && segment.Start < to).SelectMany(segment => segment.Operators).Distinct().Select(person => (person.ToString("D"), operation))
            : [("Unassigned", operation)],
        _ => [],
    };

    private static string Label(HorizonLens lens, string key, IReadOnlyDictionary<Guid, ProgramInfo> programs, IReadOnlyDictionary<long, ContractInfo> contracts, IReadOnlyDictionary<Guid, string> people) =>
        lens == HorizonLens.Program && Guid.TryParse(key, out var program) && programs.TryGetValue(program, out var p) ? p.Code
        : lens == HorizonLens.Contract && long.TryParse(key, out var contract) && contracts.TryGetValue(contract, out var c) ? c.Number
        : lens == HorizonLens.Workforce && Guid.TryParse(key, out var person) && people.TryGetValue(person, out var name) ? name : key;

    private static HorizonCounts Counts(IReadOnlyList<ScheduledOperation> operations, IReadOnlyDictionary<long, ScheduledOrder> allOrders)
    {
        var orders = operations.Select(operation => operation.OrderNumber).Distinct().Select(number => allOrders[number]).ToList();
        return new(orders.Count, operations.Count, operations.Count(operation => operation.Scheduled), operations.Count(operation => !operation.Scheduled),
            orders.Count(order => order.Status == DeliveryStatus.OnTime), orders.Count(order => order.Status == DeliveryStatus.AtRisk),
            orders.Count(order => order.Status == DeliveryStatus.Late), orders.Count(order => order.Status == DeliveryStatus.Unschedulable));
    }

    private void Demand(ForgePrincipal principal, string permission, ResourceRef resource)
    {
        var decision = authorizer.Authorize(principal, permission, resource);
        if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision);
    }

    private static bool HasDeclaredPermission(ForgePrincipal principal, string permission) =>
        (principal.EffectivePermissions is { } configured ? configured.Contains(permission)
            : principal.Roles.Any(role => Roles.PermissionsOf(role).Contains(permission, StringComparer.Ordinal)))
        || principal.ScopedPermissions?.Any(grant => grant.Permission == permission) == true
        || principal.DelegatedPermissions?.Any(grant => grant.Permission == permission) == true;
}
