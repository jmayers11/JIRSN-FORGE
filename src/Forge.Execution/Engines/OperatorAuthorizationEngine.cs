using Forge.Competency.Contracts;
using Forge.Kernel.Engines;

namespace Forge.Execution.Engines;

/// <summary>A person as the shop floor sees them: present or not, and their qualification records.</summary>
public sealed record FloorPerson(Guid PersonId, string Name, bool Present, IReadOnlyList<PersonQualification> Qualifications);

public sealed record OperatorSnapshot(IReadOnlyList<QualificationTypeInfo> Types, IReadOnlyList<FloorPerson> People)
{
    public FloorPerson? Person(Guid id) => People.FirstOrDefault(p => p.PersonId == id);
}

/// <param name="RequiredQualifications">The routing's qualifications plus the machine activity's operator qualifications.</param>
/// <param name="TrainerId">The person who badged as trainer, if any.</param>
public sealed record OperatorRequest(Guid OperatorId, IReadOnlyList<string> RequiredQualifications, Guid? TrainerId);

public enum OperatorDecision
{
    /// <summary>Independently qualified: may start.</summary>
    Authorized,

    /// <summary>In training for what is missing and a valid trainer badged: may start, supervised.</summary>
    SupervisedTraining,

    /// <summary>In training for what is missing; a trainer must badge.</summary>
    TrainerRequired,

    /// <summary>Missing a qualification they are not in training for.</summary>
    NotEligible,

    /// <summary>No current presence evidence: clock in first.</summary>
    NotPresent,
}

public sealed record OperatorCheck(
    OperatorDecision Decision,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> AvailableTrainers,
    string Explanation);

/// <summary>
/// Decides whether a badged person may start an operation, and on what terms. Pure and
/// deterministic. It never grants anything: supervised work does not make a trainee qualified.
/// A trainer for qualification Q is someone who holds Q and a Trainer-kind qualification that has
/// Q as a prerequisite — being a qualified operator and being a qualified trainer are different.
/// </summary>
public sealed class OperatorAuthorizationEngine : IEngine<OperatorSnapshot, OperatorRequest, OperatorCheck>
{
    public string Name => "operator-authorization";

    public string Version => "1";

    public EngineEvaluation<OperatorCheck> Evaluate(OperatorSnapshot snapshot, OperatorRequest parameters, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(parameters);
        var required = parameters.RequiredQualifications.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var op = snapshot.Person(parameters.OperatorId);
        if (op is null)
        {
            return Result(OperatorDecision.NotEligible, required, [], "This person is not a registered worker.", []);
        }

        if (!op.Present)
        {
            return Result(OperatorDecision.NotPresent, [], [], $"{op.Name} has no current clock-in or supervisor confirmation. Clock in first.", []);
        }

        var checks = required.Select(code => Check(op, code, asOf)).ToList();
        var missing = required.Where(code => !Holds(op, code, asOf)).ToList();
        if (missing.Count == 0)
        {
            return Result(OperatorDecision.Authorized, [], [], required.Count == 0
                ? $"{op.Name} may start: the operation requires no qualification."
                : $"{op.Name} holds every required qualification ({string.Join(", ", required)}).", checks);
        }

        var notInTraining = missing.Where(code => !op.Qualifications.Any(q => q.Code == code && q.Status == QualificationStatus.InTraining)).ToList();
        if (notInTraining.Count > 0)
        {
            return Result(OperatorDecision.NotEligible, missing, [],
                $"{op.Name} is not qualified for {string.Join(", ", notInTraining)} and is not in training for it. Contact a trainer, a supervisor or the training coordinator.", checks);
        }

        var trainers = snapshot.People
            .Where(p => p.PersonId != op.PersonId && p.Present && CanTrain(snapshot, p, required, missing, asOf))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        var names = trainers.Select(t => t.Name).ToList();

        if (parameters.TrainerId is not { } trainerId)
        {
            return Result(OperatorDecision.TrainerRequired, missing, names, names.Count > 0
                ? $"{op.Name} is in training for {string.Join(", ", missing)}. An authorized trainer must badge: {string.Join(", ", names)}."
                : $"{op.Name} is in training for {string.Join(", ", missing)}, but no authorized trainer is present. Contact a trainer, a supervisor or the training coordinator.", checks);
        }

        var trainer = snapshot.Person(trainerId);
        var reason = trainer is null ? "The trainer badge does not belong to a registered worker."
            : trainer.PersonId == op.PersonId ? "A trainee cannot supervise themselves."
            : !trainer.Present ? $"{trainer.Name} has no current clock-in or supervisor confirmation."
            : !CanTrain(snapshot, trainer, required, missing, asOf) ? $"{trainer.Name} is not an authorized trainer for {string.Join(", ", missing)}: a trainer must hold the qualification and a current trainer qualification for it."
            : null;
        if (reason is not null)
        {
            return Result(OperatorDecision.TrainerRequired, missing, names, reason, checks);
        }

        checks.Add(new EvidenceNode("Trainer", EvidenceOutcome.Pass, $"{trainer!.Name} holds {string.Join(", ", required)} and is a current trainer for {string.Join(", ", missing)}."));
        return Result(OperatorDecision.SupervisedTraining, missing, names,
            $"{op.Name} may start under {trainer.Name}'s supervision. Supervised work does not grant {string.Join(", ", missing)}.", checks);
    }

    /// <summary>Codes of Trainer-kind qualifications that authorize training for <paramref name="code"/>.</summary>
    public static IReadOnlyList<string> TrainerQualificationsFor(IReadOnlyList<QualificationTypeInfo> types, string code) =>
        types.Where(t => t.Kind == QualificationKind.Trainer && t.Prerequisites.Contains(code, StringComparer.Ordinal)).Select(t => t.Code).Order(StringComparer.Ordinal).ToList();

    private static bool CanTrain(OperatorSnapshot snapshot, FloorPerson trainer, IReadOnlyList<string> required, IReadOnlyList<string> missing, DateTimeOffset at) =>
        required.All(code => Holds(trainer, code, at))
        && missing.All(code => TrainerQualificationsFor(snapshot.Types, code).Any(t => Holds(trainer, t, at)));

    private static bool Holds(FloorPerson person, string code, DateTimeOffset at) =>
        person.Qualifications.Any(q => q.Code == code && q.IsValidAt(at));

    private static EvidenceNode Check(FloorPerson person, string code, DateTimeOffset at)
    {
        var record = person.Qualifications.FirstOrDefault(q => q.Code == code);
        return record switch
        {
            null => new EvidenceNode(code, EvidenceOutcome.Fail, $"{person.Name} has no record for {code}."),
            _ when record.IsValidAt(at) => new EvidenceNode(code, EvidenceOutcome.Pass, record.ExpiresAt is { } e ? $"Qualified until {e:u}." : "Qualified, no expiry."),
            { Status: QualificationStatus.InTraining } => new EvidenceNode(code, EvidenceOutcome.Fail, $"{person.Name} is in training for {code}."),
            { Status: QualificationStatus.Qualified } => new EvidenceNode(code, EvidenceOutcome.Fail, $"{code} expired {record.ExpiresAt:u}."),
            _ => new EvidenceNode(code, EvidenceOutcome.Fail, $"{code} is {record.Status}."),
        };
    }

    private static EngineEvaluation<OperatorCheck> Result(OperatorDecision decision, IReadOnlyList<string> missing, IReadOnlyList<string> trainers, string explanation, IReadOnlyList<EvidenceNode> checks) =>
        new(
            new OperatorCheck(decision, missing, trainers, explanation),
            new EvidenceNode("Operator", decision is OperatorDecision.Authorized or OperatorDecision.SupervisedTraining ? EvidenceOutcome.Pass : EvidenceOutcome.Fail, explanation)
            {
                Children = checks,
            });
}
