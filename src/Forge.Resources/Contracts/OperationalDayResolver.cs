using NodaTime;

namespace Forge.Resources.Contracts;

/// <summary>Which occurrence of a recurring shift a user is currently planning against.</summary>
public sealed record OperationalShiftOccurrence(
    string SiteCode,
    string ShiftCode,
    DateOnly OperationalDate,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Relation);

/// <summary>
/// Resolves an operational date from the assigned shift, its site's time zone and closure calendar.
/// Overnight shifts keep the local date on which the shift started. Outside a shift, the nearest
/// scheduled occurrence is used (ties prefer the upcoming shift), so a pre-shift login can prepare
/// for the upcoming workday without resetting at midnight.
/// </summary>
public static class OperationalDayResolver
{
    private const int SearchDays = 370;

    public static OperationalShiftOccurrence? Resolve(
        ShiftInfo shift,
        string? timeZone,
        DateTimeOffset at,
        IEnumerable<SiteClosureInfo>? closures = null)
    {
        ArgumentNullException.ThrowIfNull(shift);
        if (shift.DurationMinutes is < 1 or > 1440 || shift.Days.Count == 0 || shift.Days.Any(day => day is < 1 or > 7))
            return null;

        var zone = SiteTime.Zone(timeZone);
        var instant = Instant.FromDateTimeOffset(at);
        var localToday = instant.InZone(zone).Date;
        var closedDates = (closures ?? [])
            .Where(closure => string.Equals(closure.SiteCode, shift.SiteCode, StringComparison.OrdinalIgnoreCase))
            .Select(closure => closure.Date)
            .ToHashSet();
        var candidates = new List<(LocalDate Date, Instant Start, Instant End)>();

        for (var offset = -SearchDays; offset <= SearchDays; offset++)
        {
            var date = localToday.PlusDays(offset);
            if (!shift.Days.Contains((int)date.DayOfWeek) || closedDates.Contains(new DateOnly(date.Year, date.Month, date.Day)))
                continue;

            // Minute precision, exactly as the planning WorkCalendar and the attendance schedule build shift occurrences, so a
            // shift stored with seconds still lines up with its generated attendance opportunities.
            var start = date.At(new LocalTime(shift.Start.Hour, shift.Start.Minute))
                .InZoneLeniently(zone).ToInstant();
            candidates.Add((date, start, start + Duration.FromMinutes(shift.DurationMinutes)));
        }

        if (candidates.Count == 0)
            return null;

        var active = candidates
            .Where(candidate => candidate.Start <= instant && instant < candidate.End)
            .OrderByDescending(candidate => candidate.Start)
            .FirstOrDefault();
        if (active != default)
            return ToOccurrence(active, shift, "Current");

        var nearest = candidates
            .Select(candidate => (Candidate: candidate, Distance: candidate.Start > instant ? candidate.Start - instant : instant - candidate.End))
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Candidate.Start < instant ? 1 : 0)
            .First();
        return ToOccurrence(nearest.Candidate, shift, nearest.Candidate.Start > instant ? "Upcoming" : "Previous");
    }

    private static OperationalShiftOccurrence ToOccurrence(
        (LocalDate Date, Instant Start, Instant End) candidate,
        ShiftInfo shift,
        string relation) => new(
            shift.SiteCode,
            shift.Code,
            new DateOnly(candidate.Date.Year, candidate.Date.Month, candidate.Date.Day),
            candidate.Start.ToDateTimeOffset(),
            candidate.End.ToDateTimeOffset(),
            relation);
}
