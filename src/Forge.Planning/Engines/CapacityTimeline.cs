using Forge.Kernel.Engines;

namespace Forge.Planning.Engines;

/// <summary>What a piece of work needs: a place, an optional machine, an activity, qualified people.</summary>
public sealed record WorkRequirement(
    string WorkCenterCode,
    string? MachineCode,
    string ActivityCode,
    IReadOnlyList<string> OperatorQualifications,
    IReadOnlyList<string> RequiredQualifications,
    int Crew)
{
    public string Key =>
        $"{WorkCenterCode}|{MachineCode}|{ActivityCode}|{string.Join(",", OperatorQualifications)}|{string.Join(",", RequiredQualifications)}|{Crew}";

    public bool Qualifies(PersonSnapshot person) =>
        (OperatorQualifications.Count == 0 || OperatorQualifications.Any(person.Holds)) && RequiredQualifications.All(person.Holds);
}

/// <summary>
/// One span of working time for a requirement, with whether the work could run and, if not, the
/// first limiting dimension (Equipment, then Safety, then Workforce). <see cref="Candidates"/> are the
/// projected qualified people, in person-id order.
/// </summary>
public sealed record CapacityInterval(DateTimeOffset Start, DateTimeOffset End, bool Executable, string? Limiting, string Detail, IReadOnlyList<Guid> Candidates)
{
    public Span Span => new(Start, End);
}

/// <summary>
/// Builds executable-capacity timelines. Working time comes from shift calendars; inside it, the
/// state can only change at known instants (shift edges, downtime edges, absence edges,
/// qualification expiries), so each requirement is evaluated once per span between those instants.
/// </summary>
internal sealed class CapacityTimeline
{
    private readonly SiteSnapshot snapshot;
    private readonly Span horizon;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<Span>> schedules;
    private readonly SortedSet<DateTimeOffset> changePoints;
    private readonly Dictionary<DateTimeOffset, SiteSnapshot> projections = [];
    private readonly Dictionary<string, IReadOnlyList<CapacityInterval>> cache = new(StringComparer.Ordinal);
    private readonly PersonnelCompositionEngine composition = new();

    public CapacityTimeline(SiteSnapshot snapshot, Span horizon)
    {
        this.snapshot = snapshot;
        this.horizon = horizon;
        schedules = Projection.Schedules(snapshot, horizon.Start, horizon.End);
        changePoints = new SortedSet<DateTimeOffset>(
            schedules.Values.SelectMany(spans => spans.SelectMany(s => new[] { s.Start, s.End }))
                .Concat(snapshot.Downtime.SelectMany(d => new[] { d.StartsAt, d.EndsAt ?? horizon.End }))
                .Concat(snapshot.People.SelectMany(p => p.QualificationExpiry.Values.OfType<DateTimeOffset>()))
                .Where(horizon.Contains));
    }

    public SiteSnapshot Snapshot => snapshot;

    public Span Horizon => horizon;

    public IReadOnlyDictionary<Guid, IReadOnlyList<Span>> Schedules => schedules;

    public SiteSnapshot ProjectedAt(DateTimeOffset at)
    {
        if (!projections.TryGetValue(at, out var projected))
        {
            projected = Projection.At(snapshot, at, schedules);
            projections[at] = projected;
        }

        return projected;
    }

    public IReadOnlyList<CapacityInterval> For(WorkRequirement requirement)
    {
        if (cache.TryGetValue(requirement.Key, out var cached))
        {
            return cached;
        }

        var area = snapshot.WorkCenters.FirstOrDefault(w => w.Code == requirement.WorkCenterCode)?.SafetyAreaCode;
        var intervals = new List<CapacityInterval>();
        if (area is not null)
        {
            foreach (var open in WorkCalendar.WorkCenterOpen(snapshot, requirement.WorkCenterCode, horizon.Start, horizon.End))
            {
                var cuts = changePoints.GetViewBetween(open.Start, open.End).Where(p => p > open.Start && p < open.End).Append(open.End);
                var start = open.Start;
                foreach (var cut in cuts)
                {
                    intervals.Add(Evaluate(requirement, area, new Span(start, cut)));
                    start = cut;
                }
            }
        }

        cache[requirement.Key] = intervals;
        return intervals;
    }

    /// <summary>Adjacent intervals with the same outcome joined, for display.</summary>
    public static IReadOnlyList<CapacityInterval> Merged(IReadOnlyList<CapacityInterval> intervals)
    {
        var merged = new List<CapacityInterval>();
        foreach (var i in intervals)
        {
            if (merged.Count > 0 && merged[^1].End == i.Start && merged[^1].Executable == i.Executable && merged[^1].Limiting == i.Limiting && merged[^1].Detail == i.Detail)
            {
                merged[^1] = merged[^1] with { End = i.End };
            }
            else
            {
                merged.Add(i);
            }
        }

        return merged;
    }

    private CapacityInterval Evaluate(WorkRequirement requirement, string area, Span span)
    {
        if (requirement.MachineCode is { } machine)
        {
            var equipment = OperationReadinessEngine.EquipmentCheck(snapshot, machine, span.Start);
            if (equipment.Outcome == EvidenceOutcome.Fail)
            {
                return new CapacityInterval(span.Start, span.End, false, "Equipment", equipment.Detail, []);
            }
        }

        var projected = ProjectedAt(span.Start);
        var safety = composition.Evaluate(projected, new WorkItem(area, requirement.WorkCenterCode, requirement.MachineCode, requirement.ActivityCode), span.Start).Output;
        var candidates = projected.PresentInArea(area).Where(requirement.Qualifies).ToList();

        if (safety.Status == CompositionStatus.NotSatisfied)
        {
            var shortfall = string.Join(" ", safety.Rules.SelectMany(r => r.Shortfalls).Distinct());
            return new CapacityInterval(span.Start, span.End, false, "Safety", $"Projected staffing in {area} does not satisfy safety rules: {shortfall}", candidates.Select(p => p.PersonId).ToList());
        }

        if (candidates.Count < requirement.Crew)
        {
            var needs = string.Join(" and ", new[]
            {
                requirement.OperatorQualifications.Count == 0 ? null : "one of " + string.Join(", ", requirement.OperatorQualifications),
                requirement.RequiredQualifications.Count == 0 ? null : string.Join(", ", requirement.RequiredQualifications),
            }.Where(x => x is not null));
            return new CapacityInterval(
                span.Start,
                span.End,
                false,
                "Workforce",
                $"{candidates.Count} of {requirement.Crew} qualified people projected in {area}{(needs.Length == 0 ? string.Empty : " (needs " + needs + ")")}.",
                candidates.Select(p => p.PersonId).ToList());
        }

        return new CapacityInterval(
            span.Start,
            span.End,
            true,
            null,
            $"Executable: {string.Join(", ", candidates.Select(p => p.Name))} projected qualified in {area}.",
            candidates.Select(p => p.PersonId).ToList());
    }
}
