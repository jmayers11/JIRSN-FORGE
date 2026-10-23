using Forge.Kernel.Engines;

namespace Forge.Planning.Engines;

public sealed record CapacityRequest(DateTimeOffset From, DateTimeOffset To, string? WorkCenterCode = null);

public sealed record MachineCapacity(
    string MachineCode,
    string WorkCenterCode,
    string ActivityCode,
    decimal WorkingHours,
    decimal ExecutableHours,
    IReadOnlyList<CapacityInterval> Intervals);

/// <summary>Projected hours of people holding a qualification on one local day (Skills MRP supply).</summary>
public sealed record QualifiedLaborDay(DateOnly Day, string Qualification, decimal Hours, IReadOnlyList<string> People);

public sealed record CapacityResult(DateTimeOffset From, DateTimeOffset To, string Confidence, IReadOnlyList<MachineCapacity> Machines, IReadOnlyList<QualifiedLaborDay> QualifiedLabor);

/// <summary>
/// Executable capacity over time: for every machine activity, the working spans when it could
/// actually run (equipment up, safety staffing satisfied, a qualified operator projected), and the
/// first limiting dimension when it could not. Machine windows are each feasible on their own; they
/// share people, so they are not additive. The scheduler allocates people once.
/// </summary>
public sealed class ExecutableCapacityEngine : IEngine<SiteSnapshot, CapacityRequest, CapacityResult>
{
    public const string ProjectedConfidence = "Projected from shift schedules, recorded absences and recorded downtime; not observed presence.";

    public string Name => "executable-capacity";

    public string Version => "1";

    public EngineEvaluation<CapacityResult> Evaluate(SiteSnapshot snapshot, CapacityRequest parameters, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(parameters);
        var horizon = new Span(WorkCalendar.Max(parameters.From, asOf), parameters.To);
        if (horizon.End <= horizon.Start || horizon.Length > TimeSpan.FromDays(62))
        {
            throw new ArgumentException("The horizon must end after it starts (and after now) and span at most 62 days.", nameof(parameters));
        }

        var timeline = new CapacityTimeline(snapshot, horizon);
        var machines = new List<MachineCapacity>();
        var nodes = new List<EvidenceNode>();
        foreach (var profile in snapshot.MachineActivities.OrderBy(m => m.MachineCode, StringComparer.Ordinal).ThenBy(m => m.ActivityCode, StringComparer.Ordinal))
        {
            var machine = snapshot.Machines.FirstOrDefault(m => m.Code == profile.MachineCode);
            if (machine is null || (parameters.WorkCenterCode is { } wc && machine.WorkCenterCode != wc))
            {
                continue;
            }

            var intervals = timeline.For(new WorkRequirement(machine.WorkCenterCode, machine.Code, profile.ActivityCode, profile.OperatorQualifications, [], 1, profile.RequiresMachineAuthorization));
            var working = Hours(intervals.Sum(i => i.Span.Length.TotalHours));
            var executable = Hours(intervals.Where(i => i.Executable).Sum(i => i.Span.Length.TotalHours));
            var merged = CapacityTimeline.Merged(intervals);
            machines.Add(new MachineCapacity(machine.Code, machine.WorkCenterCode, profile.ActivityCode, working, executable, merged));
            nodes.Add(new EvidenceNode(
                "MachineCapacity",
                EvidenceOutcome.Info,
                $"{profile.ActivityCode} on {machine.Code}: {executable} of {working} working hours executable.")
            {
                Children = merged.Where(i => !i.Executable).Select(i => new EvidenceNode(i.Limiting ?? "Unknown", EvidenceOutcome.Fail, $"{i.Start:u} to {i.End:u}: {i.Detail}")).ToList(),
            });
        }

        var labor = QualifiedLabor(snapshot, timeline);
        var root = new EvidenceNode("ExecutableCapacity", EvidenceOutcome.Info, $"Executable capacity {horizon.Start:u} to {horizon.End:u}. {ProjectedConfidence}") { Children = nodes };
        return new EngineEvaluation<CapacityResult>(new CapacityResult(horizon.Start, horizon.End, ProjectedConfidence, machines, labor), root);
    }

    private static List<QualifiedLaborDay> QualifiedLabor(SiteSnapshot snapshot, CapacityTimeline timeline)
    {
        var siteCode = snapshot.Sites.Count > 0 ? snapshot.Sites[0].Code : string.Empty;
        var hours = new SortedDictionary<(DateOnly Day, string Qualification), (double Hours, SortedSet<string> People)>();
        foreach (var person in snapshot.People.OrderBy(p => p.PersonId))
        {
            foreach (var span in timeline.Schedules[person.PersonId])
            {
                foreach (var qualification in person.ValidQualifications)
                {
                    var end = person.QualificationExpiry.TryGetValue(qualification, out var expiry) && expiry is { } e ? WorkCalendar.Min(span.End, e) : span.End;
                    // Split at local midnights so hours land on the right day.
                    var cursor = span.Start;
                    while (cursor < end)
                    {
                        var day = WorkCalendar.LocalDate(snapshot, siteCode, cursor);
                        var dayEnd = WorkCalendar.Min(WorkCalendar.EndOfDay(snapshot, siteCode, day), end);
                        var key = (day, qualification);
                        var current = hours.TryGetValue(key, out var v) ? v : (0d, new SortedSet<string>(StringComparer.Ordinal));
                        current.Item2.Add(person.Name);
                        hours[key] = (current.Item1 + (dayEnd - cursor).TotalHours, current.Item2);
                        cursor = dayEnd;
                    }
                }
            }
        }

        return hours.Select(h => new QualifiedLaborDay(h.Key.Day, h.Key.Qualification, Hours(h.Value.Hours), h.Value.People.ToList())).ToList();
    }

    internal static decimal Hours(double value) => decimal.Round((decimal)value, 2, MidpointRounding.AwayFromZero);
}
