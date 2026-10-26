using Forge.Competency.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Host.Workforce;

public sealed record PrerequisiteCheck(string Code, string Name, bool Held, string? Group = null);

/// <summary>
/// What a nominator sees before nominating someone toward a qualification (Sprint 10 WW-S10-12): the prerequisites the person holds
/// and lacks, and the bottleneck the qualification is today, from recorded qualifications and open work only. Nothing is ranked or
/// decided here.
/// </summary>
public sealed record DevelopmentReadiness(
    Guid PersonId,
    string DisplayName,
    string QualificationCode,
    string QualificationName,
    bool AlreadyHeld,
    IReadOnlyList<PrerequisiteCheck> Prerequisites,
    bool PrerequisitesMet,
    int CurrentHolders,
    int HoldersInHomeWorkCenter,
    int OpenOperationsNeedingIt,
    decimal OpenPlannedHours,
    string Benefit);

public sealed class DevelopmentReadinessService(IKernelUnitOfWorkFactory unitOfWork, TimeProvider time)
{
    public async Task<DevelopmentReadiness> BuildAsync(ForgePrincipal principal, Guid personId, string qualificationCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var code = BusinessCode.Require(qualificationCode, "Qualification");
        var now = time.GetUtcNow();
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var competency = uow.Store<ICompetencyLookup>();
        var types = (await competency.ListTypesAsync(ct)).ToDictionary(t => t.Code, StringComparer.Ordinal);
        var type = types.GetValueOrDefault(code) ?? throw new CommandRejectedException("unknown_qualification", $"Qualification type {code} does not exist.");
        var workers = (await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(now, ct)).Where(w => w.IsActive).ToList();
        var person = workers.FirstOrDefault(w => w.PersonId == personId) ?? throw new CommandRejectedException("worker_not_found", "Choose an active Forge worker.");
        var valid = (await competency.GetQualificationsAsync(workers.Select(w => new PersonId(w.PersonId)).ToList(), ct)).Where(q => q.IsValidAt(now)).ToList();
        var mine = valid.Where(q => q.PersonId == personId).Select(q => q.Code).ToHashSet(StringComparer.Ordinal);
        string Name(string c) => types.TryGetValue(c, out var t) ? t.Name : c;

        var checks = type.Prerequisites.Select(p => new PrerequisiteCheck(p, Name(p), mine.Contains(p))).ToList();
        foreach (var group in type.PrerequisiteAlternatives ?? [])
            checks.AddRange(group.QualificationCodes.Select(p => new PrerequisiteCheck(p, Name(p), mine.Contains(p), group.Name)));
        var met = type.Prerequisites.All(mine.Contains) && (type.PrerequisiteAlternatives ?? []).All(g => g.QualificationCodes.Any(mine.Contains));

        var holders = valid.Where(q => q.Code == code).Select(q => q.PersonId).ToHashSet();
        var homeHolders = workers.Count(w => holders.Contains(w.PersonId) && w.HomeWorkCenterCode == person.HomeWorkCenterCode);

        // Open work that needs it: required on the operation, or an operator qualification of a machine activity it may run on.
        var resources = uow.Store<IResourcesLookup>();
        var visible = (await uow.Store<IExecutionLookup>().ListOpenOperationsAsync(ct))
            .Where(o => o.ProgramId is not { } p || principal.Programs.Contains(new ProgramId(p))).ToList();
        var needing = new List<OpenOperation>();
        foreach (var open in visible)
        {
            var op = open.Operation;
            var needs = op.RequiredQualifications.Contains(code, StringComparer.Ordinal);
            foreach (var machine in op.EligibleMachines)
            {
                if (needs) break;
                needs = await resources.FindMachineActivityAsync(machine, op.ActivityCode, ct) is { } activity && activity.OperatorQualifications.Contains(code, StringComparer.Ordinal);
            }

            if (needs) needing.Add(open);
        }

        var hours = decimal.Round(needing.Sum(o => o.Operation.PlannedMinutes) / 60m, 1);
        var holdsIt = holders.Contains(personId);
        var benefit = holdsIt
            ? $"{person.DisplayName} already holds {type.Name}."
            : holders.Count == 0
                ? $"No active worker holds {type.Name}; {needing.Count} open operation(s) ({hours:0.#} planned hours) need it. Qualifying {person.DisplayName} removes a single point of failure."
                : $"{holders.Count} active worker(s) hold {type.Name} ({homeHolders} in {person.HomeWorkCenterCode}); {needing.Count} open operation(s) ({hours:0.#} planned hours) need it. Qualifying {person.DisplayName} would make {holders.Count + 1}.";
        if (!holdsIt && !met) benefit += " Prerequisites are missing; they come first.";
        return new DevelopmentReadiness(personId, person.DisplayName, code, type.Name, holdsIt, checks, met, holders.Count, homeHolders, needing.Count, hours, benefit);
    }
}
