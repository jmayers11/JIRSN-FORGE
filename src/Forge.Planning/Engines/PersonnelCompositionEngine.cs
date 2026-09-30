using System.Globalization;
using Forge.Kernel.Engines;
using Forge.SafetyRules.Contracts;

namespace Forge.Planning.Engines;

public enum CompositionStatus
{
    Satisfied,
    NotSatisfied,
    NoRuleApplies,
}

public sealed record SlotFill(string Slot, Guid PersonId, string PersonName, string Qualification);

public sealed record RuleEvaluation(
    string RuleRef,
    string Title,
    bool Satisfied,
    int RequiredPeople,
    int PresentPeople,
    IReadOnlyList<SlotFill> Fills,
    IReadOnlyList<string> Shortfalls);

public sealed record CompositionResult(CompositionStatus Status, IReadOnlyList<RuleEvaluation> Rules);

/// <summary>
/// Decides whether the people physically present in a safety area satisfy every safety rule that
/// applies to an activity. Each role slot needs a distinct person (maximum bipartite matching), so
/// one person may hold many qualifications but never counts as two people. Rules are evaluated,
/// never invented or relaxed: an activity with no applicable rule is reported as such.
/// </summary>
public sealed class PersonnelCompositionEngine : IEngine<SiteSnapshot, WorkItem, CompositionResult>
{
    public string Name => "personnel-composition";

    public string Version => "1";

    public EngineEvaluation<CompositionResult> Evaluate(SiteSnapshot snapshot, WorkItem parameters, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(parameters);

        var rules = ApplicableRules(snapshot, parameters, asOf);
        var present = snapshot.PresentInArea(parameters.SafetyAreaCode).ToList();
        var presentNode = new EvidenceNode(
            "PeoplePresentInSafetyArea",
            EvidenceOutcome.Info,
            present.Count == 0 ? $"Nobody has current presence evidence in {parameters.SafetyAreaCode}." : $"{present.Count} present in {parameters.SafetyAreaCode}: {string.Join(", ", present.Select(p => p.Name))}.")
        {
            Inputs = new(StringComparer.Ordinal)
            {
                ["safetyArea"] = parameters.SafetyAreaCode,
                ["people"] = string.Join(";", present.Select(p => $"{p.Name}|{p.Presence}|until {p.PresentUntil:u}|{p.Assignment?.ActivityCode}{(p.Assignment?.AsTrainee == true ? "|trainee" : string.Empty)}")),
            },
        };

        if (rules.Count == 0)
        {
            var none = new EvidenceNode(
                "SafetyComposition",
                EvidenceOutcome.Pass,
                $"No approved safety rule applies to {parameters.Describe()}.")
            { Children = [presentNode] };
            return new EngineEvaluation<CompositionResult>(new CompositionResult(CompositionStatus.NoRuleApplies, []), none);
        }

        var evaluations = new List<RuleEvaluation>();
        var ruleNodes = new List<EvidenceNode>();
        foreach (var rule in rules)
        {
            var (evaluation, node) = EvaluateRule(rule, present);
            evaluations.Add(evaluation);
            ruleNodes.Add(node);
        }

        var outcome = EvidenceNode.Combine(ruleNodes);
        var root = new EvidenceNode(
            "SafetyComposition",
            outcome,
            outcome == EvidenceOutcome.Pass
                ? $"All {rules.Count} applicable safety rule(s) satisfied for {parameters.Describe()}."
                : $"{evaluations.Count(e => !e.Satisfied)} of {rules.Count} applicable safety rule(s) not satisfied for {parameters.Describe()}.")
        {
            Children = [presentNode, .. ruleNodes],
        };

        var status = evaluations.All(e => e.Satisfied) ? CompositionStatus.Satisfied : CompositionStatus.NotSatisfied;
        return new EngineEvaluation<CompositionResult>(new CompositionResult(status, evaluations), root);
    }

    internal static IReadOnlyList<SafetyRuleDefinition> ApplicableRules(SiteSnapshot snapshot, WorkItem work, DateTimeOffset asOf) => snapshot.Rules
        .Where(r => r.IsEffectiveAt(asOf))
        .Where(r => r.ScopeKind switch
        {
            RuleScopeKind.SafetyArea => r.ScopeCode == work.SafetyAreaCode,
            RuleScopeKind.WorkCenter => r.ScopeCode == work.WorkCenterCode,
            RuleScopeKind.Machine => r.ScopeCode == work.MachineCode,
            _ => false,
        })
        .Where(r => r.ActivityCodes.Count == 0 || r.ActivityCodes.Contains(work.ActivityCode, StringComparer.Ordinal))
        .OrderBy(r => r.Code, StringComparer.Ordinal)
        .ToList();

    private static (RuleEvaluation, EvidenceNode) EvaluateRule(SafetyRuleDefinition rule, IReadOnlyList<PersonSnapshot> present)
    {
        var required = Math.Max(rule.MinimumPhysicalPeople, rule.ProhibitLoneWork ? 2 : 1);
        var shortfalls = new List<string>();
        var children = new List<EvidenceNode>();

        var headcountOk = present.Count >= required;
        if (!headcountOk)
        {
            shortfalls.Add($"Needs {required} physical people present; {present.Count} present.");
        }

        children.Add(new EvidenceNode(
            "MinimumPhysicalPeople",
            headcountOk ? EvidenceOutcome.Pass : EvidenceOutcome.Fail,
            $"{present.Count} of {required} required people present{(rule.ProhibitLoneWork ? " (lone work prohibited)" : string.Empty)}.")
        {
            RuleRef = rule.Reference,
            Inputs = new(StringComparer.Ordinal) { ["required"] = required.ToString(CultureInfo.InvariantCulture), ["present"] = present.Count.ToString(CultureInfo.InvariantCulture) },
        });

        // Expand slots into instances, then match distinct people to instances.
        var instances = rule.Slots.SelectMany(s => Enumerable.Range(1, s.Count).Select(i => (Slot: s, Index: i))).ToList();
        var eligible = instances.Select(inst => present.Where(p => CanFill(p, inst.Slot)).ToList()).ToList();
        var assignment = Match(eligible, present);

        var fills = new List<SlotFill>();
        foreach (var slot in rule.Slots)
        {
            var slotInstances = instances.Select((inst, i) => (inst, i)).Where(x => x.inst.Slot == slot).ToList();
            var filled = slotInstances.Where(x => assignment[x.i] is not null).Select(x => assignment[x.i]!).ToList();
            foreach (var person in filled)
            {
                fills.Add(new SlotFill(slot.Name, person.PersonId, person.Name, slot.SatisfiedBy.First(person.Holds)));
            }

            var missing = slot.Count - filled.Count;
            string detail;
            if (missing == 0)
            {
                detail = $"{slot.Name}: filled by {string.Join(", ", filled.Select(p => p.Name))}.";
            }
            else
            {
                var qualifiedTrainees = present.Where(p => p.Assignment?.AsTrainee == true && !slot.TraineeMayFill && slot.SatisfiedBy.Any(p.Holds)).Select(p => p.Name).ToList();
                var note = qualifiedTrainees.Count > 0
                    ? $" {string.Join(", ", qualifiedTrainees)} hold(s) the qualification but are assigned as trainees, which this rule does not allow for this slot."
                    : string.Empty;
                detail = $"{slot.Name}: needs {missing} more distinct person(s) holding {string.Join(" or ", slot.SatisfiedBy)}.{note}";
                shortfalls.Add(detail);
            }

            children.Add(new EvidenceNode("RoleSlot", missing == 0 ? EvidenceOutcome.Pass : EvidenceOutcome.Fail, detail)
            {
                RuleRef = rule.Reference,
                Inputs = new(StringComparer.Ordinal)
                {
                    ["slot"] = slot.Name,
                    ["required"] = slot.Count.ToString(CultureInfo.InvariantCulture),
                    ["satisfiedBy"] = string.Join(",", slot.SatisfiedBy),
                    ["traineeMayFill"] = slot.TraineeMayFill ? "yes" : "no",
                },
            });
        }

        var satisfied = shortfalls.Count == 0;
        var node = new EvidenceNode(
            "SafetyRule",
            satisfied ? EvidenceOutcome.Pass : EvidenceOutcome.Fail,
            $"{rule.Title}: {(satisfied ? "satisfied" : "not satisfied")}.")
        {
            RuleRef = rule.Reference,
            Children = children,
        };
        return (new RuleEvaluation(rule.Reference, rule.Title, satisfied, required, present.Count, fills, shortfalls), node);
    }

    private static bool CanFill(PersonSnapshot person, RoleSlot slot) =>
        slot.SatisfiedBy.Any(person.Holds) && (person.Assignment?.AsTrainee != true || slot.TraineeMayFill);

    /// <summary>
    /// Maximum bipartite matching (augmenting paths). Instances are tried in rule order and people in
    /// person-id order, so the same inputs always produce the same assignment.
    /// </summary>
    private static PersonSnapshot?[] Match(IReadOnlyList<List<PersonSnapshot>> eligible, IReadOnlyList<PersonSnapshot> people)
    {
        var personToInstance = new Dictionary<Guid, int>();
        for (var instance = 0; instance < eligible.Count; instance++)
        {
            TryAugment(instance, eligible, personToInstance, new HashSet<Guid>());
        }

        var result = new PersonSnapshot?[eligible.Count];
        foreach (var (personId, instance) in personToInstance)
        {
            result[instance] = people.First(p => p.PersonId == personId);
        }

        return result;
    }

    private static bool TryAugment(int instance, IReadOnlyList<List<PersonSnapshot>> eligible, Dictionary<Guid, int> personToInstance, HashSet<Guid> visited)
    {
        foreach (var person in eligible[instance])
        {
            if (!visited.Add(person.PersonId))
            {
                continue;
            }

            if (!personToInstance.TryGetValue(person.PersonId, out var current) || TryAugment(current, eligible, personToInstance, visited))
            {
                personToInstance[person.PersonId] = instance;
                return true;
            }
        }

        return false;
    }
}
