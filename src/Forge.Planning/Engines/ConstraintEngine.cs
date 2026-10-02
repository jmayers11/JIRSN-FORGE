using Forge.Kernel.Engines;

namespace Forge.Planning.Engines;

/// <summary>
/// A constraint found in a schedule run: what limited the work, where, when, which orders it
/// affects, the evidence, and people who could help. The key identifies "the same problem" across
/// runs so its lifecycle (open, investigating, resolved, verified) can be tracked.
/// </summary>
public sealed record ConstraintFinding(
    string Key,
    string Category,
    string Subject,
    DateTimeOffset From,
    DateTimeOffset Until,
    string Finding,
    string Evidence,
    IReadOnlyList<long> AffectedOrders,
    IReadOnlyList<string> AffectedOperations,
    IReadOnlyList<string> Alternatives);

public sealed record ConstraintRequest(ScheduleResult Schedule);

/// <summary>
/// Turns a schedule's recorded binding constraints and interruptions into findings. Capacity
/// limitations (maintenance, safety staffing, qualified labor) are reported whenever they delay or
/// interrupt work. Ordinary queuing (machine or people busy) is reported only when it makes an order
/// late or at risk, to avoid alerts about normal sequencing. Causes come only from what the
/// scheduler recorded; nothing is inferred.
/// </summary>
public sealed class ConstraintEngine : IEngine<SiteSnapshot, ConstraintRequest, IReadOnlyList<ConstraintFinding>>
{
    public string Name => "constraints";

    public string Version => "2";

    public EngineEvaluation<IReadOnlyList<ConstraintFinding>> Evaluate(SiteSnapshot snapshot, ConstraintRequest parameters, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(parameters);
        var schedule = parameters.Schedule;
        var troubled = schedule.Orders.Where(o => o.Status != DeliveryStatus.OnTime).Select(o => o.OrderNumber).ToHashSet();
        var raw = new List<(string Category, string Subject, DateTimeOffset From, DateTimeOffset Until, string Detail, ScheduledOperation Op)>();

        foreach (var op in schedule.Operations)
        {
            var subject = Subject(snapshot, op);
            if (!op.Scheduled)
            {
                raw.Add((Category(op.BindingConstraint), subject, schedule.From, schedule.To, op.BindingDetail, op));
                continue;
            }

            if (op.BindingConstraint is not ("Now" or "Predecessor" or "Calendar"))
            {
                raw.Add((Category(op.BindingConstraint), subject, op.Start!.Value, op.Start.Value, op.BindingDetail, op));
            }

            foreach (var interruption in op.Interruptions)
            {
                raw.Add((Category(interruption.Limiting), subject, interruption.From, interruption.Until, interruption.Detail, op));
            }
        }

        var timeline = new CapacityTimeline(snapshot, new Span(schedule.From, schedule.To));
        var findings = raw
            .Where(r => r.Category is "Maintenance" or "SafetyStaffing" or "QualifiedLabor" or "RequirementProfile" or "Horizon" or "Material" || troubled.Contains(r.Op.OrderNumber))
            .Where(r => r.Category != "LatePredecessor")
            .GroupBy(r => (r.Category, r.Subject, Day: WorkCalendar.LocalDate(snapshot, WorkCalendar.SiteOfWorkCenter(snapshot, r.Op.WorkCenterCode), r.From)))
            .OrderBy(g => g.Key.Day)
            .ThenBy(g => g.Key.Category, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Subject, StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.OrderBy(x => x.From).First();
                var orders = g.Select(x => x.Op.OrderNumber).Distinct().Order().ToList();
                var operations = g.Select(x => $"{x.Op.OrderNumber}/{x.Op.Sequence}").Distinct().Order(StringComparer.Ordinal).ToList();
                var from = g.Min(x => x.From);
                var until = g.Max(x => x.Until);
                return new ConstraintFinding(
                    $"{g.Key.Category}|{g.Key.Subject}|{g.Key.Day:yyyy-MM-dd}",
                    g.Key.Category,
                    g.Key.Subject,
                    from,
                    until,
                    $"{Describe(g.Key.Category)} at {g.Key.Subject} on {g.Key.Day:yyyy-MM-dd} affects {orders.Count} order(s).",
                    first.Detail,
                    orders,
                    operations,
                    Alternatives(timeline, first.Op, g.Key.Category, from));
            })
            .ToList();

        var root = new EvidenceNode("Constraints", findings.Count == 0 ? EvidenceOutcome.Pass : EvidenceOutcome.Fail, $"{findings.Count} constraint(s) found in the schedule.")
        {
            Children = findings.Select(f => new EvidenceNode(f.Category, EvidenceOutcome.Fail, $"{f.Finding} {f.Evidence}")).ToList(),
        };
        return new EngineEvaluation<IReadOnlyList<ConstraintFinding>>(findings, root);
    }

    private static string Category(string limiting) => limiting switch
    {
        "Equipment" => "Maintenance",
        "Safety" => "SafetyStaffing",
        "Workforce" => "QualifiedLabor",
        "OperatorsBusy" => "LaborShortage",
        "MachineBusy" => "MachineCapacity",
        "Predecessor" => "LatePredecessor",
        _ => limiting,
    };

    private static string Describe(string category) => category switch
    {
        "Maintenance" => "Machine downtime",
        "SafetyStaffing" => "Safety staffing shortfall",
        "QualifiedLabor" => "Qualified labor shortfall",
        "LaborShortage" => "Qualified people fully booked",
        "MachineCapacity" => "Machine fully booked",
        "Calendar" => "No working time",
        "RequirementProfile" => "Missing machine requirement",
        "Horizon" => "Not enough capacity in the horizon",
        "Material" => "Material not available",
        _ => category,
    };

    private static string Subject(SiteSnapshot snapshot, ScheduledOperation op)
    {
        var area = snapshot.WorkCenters.FirstOrDefault(w => w.Code == op.WorkCenterCode)?.SafetyAreaCode ?? op.WorkCenterCode;
        return op.BindingConstraint is "Equipment" or "MachineBusy" || op.Interruptions.Any(i => i.Limiting is "Equipment" or "MachineBusy")
            ? op.MachineCode ?? op.WorkCenterCode
            : op.BindingConstraint is "Safety" || op.Interruptions.Any(i => i.Limiting == "Safety") ? area : op.WorkCenterCode;
    }

    /// <summary>People projected at work elsewhere at that time who hold a relevant qualification.</summary>
    private static IReadOnlyList<string> Alternatives(CapacityTimeline timeline, ScheduledOperation op, string category, DateTimeOffset at)
    {
        if (category is not ("SafetyStaffing" or "QualifiedLabor" or "LaborShortage") || !timeline.Horizon.Contains(at))
        {
            return [];
        }

        var snapshot = timeline.Snapshot;
        var area = snapshot.WorkCenters.FirstOrDefault(w => w.Code == op.WorkCenterCode)?.SafetyAreaCode;
        var relevant = snapshot.MachineActivities.Where(m => m.MachineCode == op.MachineCode && m.ActivityCode == op.ActivityCode).SelectMany(m => m.OperatorQualifications)
            .Concat(PersonnelCompositionEngine.ApplicableRules(snapshot, new WorkItem(area ?? string.Empty, op.WorkCenterCode, op.MachineCode, op.ActivityCode), at).SelectMany(r => r.Slots).SelectMany(s => s.SatisfiedBy))
            .ToHashSet(StringComparer.Ordinal);
        var projected = timeline.ProjectedAt(at);
        return projected.People
            .Where(p => p.CountsAsPresent && p.Assignment?.SafetyAreaCode != area)
            .Select(p => (p.Name, Quals: p.ValidQualifications.Where(relevant.Contains).ToList(), Area: p.Assignment?.SafetyAreaCode))
            .Where(x => x.Quals.Count > 0)
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => $"{x.Name} (projected in {x.Area}, holds {string.Join(", ", x.Quals)})")
            .ToList();
    }
}
