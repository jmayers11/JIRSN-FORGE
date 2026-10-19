using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;
using NodaTime;

namespace Forge.Planning.Engines;

/// <summary>A half-open time span [Start, End).</summary>
public readonly record struct Span(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Length => End - Start;

    public bool Contains(DateTimeOffset at) => Start <= at && at < End;

    public bool Overlaps(Span other) => Start < other.End && other.Start < End;
}

/// <summary>
/// Turns recurring shifts into concrete time spans using the bundled IANA time-zone database
/// (NodaTime), so results are the same on every server and across daylight-saving changes.
/// </summary>
public static class WorkCalendar
{
    /// <summary>Concrete spans of a shift overlapping [from, to), skipping site closure dates.</summary>
    public static IReadOnlyList<Span> Instances(ShiftInfo shift, SiteSnapshot snapshot, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(shift);
        ArgumentNullException.ThrowIfNull(snapshot);
        var zone = Zone(snapshot, shift.SiteCode);
        var closed = snapshot.Closures.Where(c => c.SiteCode == shift.SiteCode).Select(c => c.Date).ToHashSet();
        var firstDay = Instant.FromDateTimeOffset(from).InZone(zone).Date.PlusDays(-2);
        var lastDay = Instant.FromDateTimeOffset(to).InZone(zone).Date;
        var spans = new List<Span>();
        for (var day = firstDay; day <= lastDay; day = day.PlusDays(1))
        {
            if (!shift.Days.Contains((int)day.DayOfWeek) || closed.Contains(new DateOnly(day.Year, day.Month, day.Day)))
            {
                continue;
            }

            var start = (day + new LocalTime(shift.Start.Hour, shift.Start.Minute)).InZoneLeniently(zone).ToInstant();
            var end = start.Plus(Duration.FromMinutes(shift.DurationMinutes));
            var span = new Span(start.ToDateTimeOffset(), end.ToDateTimeOffset());
            if (span.Overlaps(new Span(from, to)))
            {
                spans.Add(new Span(Max(span.Start, from), Min(span.End, to)));
            }
        }

        return spans;
    }

    /// <summary>When a work center is working: the union of its shifts.</summary>
    public static IReadOnlyList<Span> WorkCenterOpen(SiteSnapshot snapshot, string workCenterCode, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var spans = snapshot.WorkCenterShifts
            .Where(w => w.WorkCenterCode == workCenterCode)
            .SelectMany(w => snapshot.Shifts.Where(s => s.Code == w.ShiftCode))
            .SelectMany(s => Instances(s, snapshot, from, to));
        return Merge(spans);
    }

    /// <summary>When a person is expected at work: their shift, minus recorded unavailability.</summary>
    public static IReadOnlyList<Span> PersonScheduled(SiteSnapshot snapshot, PersonSnapshot person, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(person);
        var shift = snapshot.Shifts.FirstOrDefault(s => s.Code == person.ShiftCode);
        if (!person.Active || shift is null)
        {
            return [];
        }

        var away = snapshot.Unavailability.Where(u => u.PersonId == person.PersonId).Select(u => new Span(u.StartsAt, u.EndsAt)).ToList();
        return Subtract(Instances(shift, snapshot, from, to), away);
    }

    /// <summary>Local calendar date of an instant at a site.</summary>
    public static DateOnly LocalDate(SiteSnapshot snapshot, string siteCode, DateTimeOffset at)
    {
        var date = Instant.FromDateTimeOffset(at).InZone(Zone(snapshot, siteCode)).Date;
        return new DateOnly(date.Year, date.Month, date.Day);
    }

    /// <summary>Start of the local day after <paramref name="date"/>, i.e. the end of that day, at a site.</summary>
    public static DateTimeOffset EndOfDay(SiteSnapshot snapshot, string siteCode, DateOnly date)
    {
        var next = new LocalDate(date.Year, date.Month, date.Day).PlusDays(1);
        return next.AtStartOfDayInZone(Zone(snapshot, siteCode)).ToDateTimeOffset();
    }

    public static string SiteOfWorkCenter(SiteSnapshot snapshot, string workCenterCode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var area = snapshot.WorkCenters.FirstOrDefault(w => w.Code == workCenterCode)?.SafetyAreaCode;
        return snapshot.SafetyAreas.FirstOrDefault(a => a.Code == area)?.SiteCode
            ?? (snapshot.Sites.Count > 0 ? snapshot.Sites[0].Code : null)
            ?? string.Empty;
    }

    public static IReadOnlyList<Span> Merge(IEnumerable<Span> spans)
    {
        var merged = new List<Span>();
        foreach (var s in spans.Where(s => s.End > s.Start).OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && s.Start <= merged[^1].End)
            {
                merged[^1] = new Span(merged[^1].Start, Max(merged[^1].End, s.End));
            }
            else
            {
                merged.Add(s);
            }
        }

        return merged;
    }

    public static IReadOnlyList<Span> Subtract(IEnumerable<Span> spans, IReadOnlyList<Span> remove)
    {
        var result = new List<Span>();
        foreach (var span in spans)
        {
            var pieces = new List<Span> { span };
            foreach (var r in remove)
            {
                pieces = pieces.SelectMany(p => !p.Overlaps(r)
                    ? new[] { p }
                    : new[] { new Span(p.Start, r.Start), new Span(r.End, p.End) }.Where(x => x.End > x.Start).ToArray()).ToList();
            }

            result.AddRange(pieces);
        }

        return Merge(result);
    }

    public static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    public static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeZone Zone(SiteSnapshot snapshot, string siteCode)
    {
        var id = snapshot.Sites.FirstOrDefault(s => s.Code == siteCode)?.TimeZone ?? snapshot.HomeTimeZone ?? "America/New_York";
        return DateTimeZoneProviders.Tzdb.GetZoneOrNull(id) ?? DateTimeZoneProviders.Tzdb["America/New_York"];
    }
}

/// <summary>
/// The forecast view of the workforce at a future instant: people are where their schedule and home
/// work center say they will be, unless recorded unavailability says otherwise. Projected presence is
/// labelled as such and never used for current safety decisions.
/// </summary>
public static class Projection
{
    public static SiteSnapshot At(SiteSnapshot snapshot, DateTimeOffset at, IReadOnlyDictionary<Guid, IReadOnlyList<Span>> scheduled)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scheduled);
        var people = snapshot.People.Select(p =>
        {
            var working = scheduled.TryGetValue(p.PersonId, out var spans) && spans.Any(s => s.Contains(at));
            // A temporary move or a transfer in force that day decides where the person counts (ADR 0057 §5, amends ADR 0056 §1).
            var center = p.WorkCenterPeriods.Count == 0 ? p.HomeWorkCenterCode : p.WorkCenterOn(snapshot.LocalDate(at));
            var home = snapshot.WorkCenters.FirstOrDefault(w => w.Code == center);
            var valid = p.ValidQualifications.Where(q => p.HoldsAt(q, at)).ToList();
            return p with
            {
                Presence = working && home is not null ? PresenceKind.Projected : null,
                PresentUntil = null,
                Unavailability = null,
                Assignment = home is null ? null : new WorkAssignment(home.SafetyAreaCode, home.Code, null, "PROJECTED", false),
                ValidQualifications = valid,
            };
        }).ToList();
        return snapshot with { People = people };
    }

    public static IReadOnlyDictionary<Guid, IReadOnlyList<Span>> Schedules(SiteSnapshot snapshot, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.People.ToDictionary(p => p.PersonId, p => WorkCalendar.PersonScheduled(snapshot, p, from, to));
    }
}
