using System.Collections.Concurrent;
using NodaTime;

namespace Forge.Resources.Contracts;

/// <summary>
/// Calendar arithmetic in a site's time zone (ADR 0057 §1). A date rule (calibration due, effective dates, shelf life,
/// due and expiry dates, report periods) is judged on the local date of the site the record belongs to, never on the
/// UTC date: at a New York site the UTC date changes at 20:00 (19:00 in winter). Zones are IANA ids; the time-zone
/// database is NodaTime's, as the work calendar uses.
/// </summary>
public static class SiteTime
{
    /// <summary>The zone a site gets when none is given (resources migration 0002).</summary>
    public const string DefaultTimeZone = "America/New_York";

    public static DateTimeZone Zone(string? timeZone) =>
        (string.IsNullOrWhiteSpace(timeZone) ? null : DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone.Trim()))
        ?? DateTimeZoneProviders.Tzdb[DefaultTimeZone];

    /// <summary>The local calendar date of an instant in the given zone.</summary>
    public static DateOnly DateAt(DateTimeOffset at, string? timeZone)
    {
        var d = Instant.FromDateTimeOffset(at).InZone(Zone(timeZone)).Date;
        return new DateOnly(d.Year, d.Month, d.Day);
    }

    /// <summary>
    /// For pure engines whose callers pass the site's zone: the local date in that zone, or the UTC date when no zone is
    /// given (engine unit tests that state their instants in UTC). Production callers always pass a zone.
    /// </summary>
    public static DateOnly DateAtOrUtc(DateTimeOffset at, string? timeZone)
    {
        if (timeZone is null)
        {
            var u = Instant.FromDateTimeOffset(at).InUtc().Date;
            return new DateOnly(u.Year, u.Month, u.Day);
        }

        return DateAt(at, timeZone);
    }

    /// <summary>The instant a local date begins in the zone, or at UTC midnight when no zone is given (see <see cref="DateAtOrUtc"/>).</summary>
    public static DateTimeOffset StartOfOrUtc(DateOnly day, string? timeZone) =>
        timeZone is null ? new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, TimeSpan.Zero) : StartOf(day, timeZone);

    /// <summary>The instant (UTC) a local calendar date begins in the given zone.</summary>
    public static DateTimeOffset StartOf(DateOnly day, string? timeZone) =>
        new LocalDate(day.Year, day.Month, day.Day).AtStartOfDayInZone(Zone(timeZone)).ToDateTimeOffset().ToUniversalTime();
}

/// <summary>
/// "What day is it at this site?" — the one place Forge answers it (ADR 0057 §1). Records that belong to no site use the
/// installation's home site: the first site created. The zones are read once at startup and kept current by the site
/// commands, so the answer needs no database round trip; with no site yet, the default zone applies.
/// </summary>
public interface ISiteClock
{
    /// <summary>The home site's zone (the first site created), or the default.</summary>
    string HomeTimeZone { get; }

    /// <summary>The site's zone; the home site's for null or an unknown code.</summary>
    string TimeZoneOf(string? siteCode);

    /// <summary>The local date at the site (the home site for null) of an instant.</summary>
    DateOnly DateAt(DateTimeOffset at, string? siteCode = null);

    /// <summary>The instant the site's local date begins.</summary>
    DateTimeOffset StartOf(DateOnly day, string? siteCode = null);
}

/// <summary>The cached site clock. Loaded after migrations; updated by <see cref="ResourcesService"/> when a site is created or its zone changes.</summary>
public sealed class SiteClock : ISiteClock
{
    private readonly ConcurrentDictionary<string, string> zones = new(StringComparer.Ordinal);
    private volatile string? home;

    public string HomeTimeZone => home is { } h && zones.TryGetValue(h, out var z) ? z : SiteTime.DefaultTimeZone;

    public string TimeZoneOf(string? siteCode) =>
        siteCode is not null && zones.TryGetValue(siteCode, out var z) ? z : HomeTimeZone;

    public DateOnly DateAt(DateTimeOffset at, string? siteCode = null) => SiteTime.DateAt(at, TimeZoneOf(siteCode));

    public DateTimeOffset StartOf(DateOnly day, string? siteCode = null) => SiteTime.StartOf(day, TimeZoneOf(siteCode));

    /// <summary>Replaces the cache: sites in creation order, the first being home.</summary>
    public void Load(IEnumerable<(string Code, string TimeZone)> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);
        zones.Clear();
        home = null;
        foreach (var (code, zone) in sites)
        {
            Remember(code, zone);
        }
    }

    /// <summary>Records a site's zone (a new site becomes home if there is none yet).</summary>
    public void Remember(string siteCode, string timeZone)
    {
        zones[siteCode] = timeZone;
        home ??= siteCode;
    }
}

/// <summary>Site dates inside a unit of work, for code that has no injected <see cref="ISiteClock"/> (static queries and reports).</summary>
public static class SiteDates
{
    /// <summary>The local date at the site (the home site when null) of an instant, reading the zone in this transaction.</summary>
    public static async Task<DateOnly> SiteDateAsync(this Forge.Kernel.Access.IKernelUnitOfWork uow, DateTimeOffset at, CancellationToken ct, string? siteCode = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return SiteTime.DateAt(at, await uow.SiteTimeZoneAsync(ct, siteCode));
    }

    /// <summary>The site's zone (the home site's when null or unknown), read in this transaction.</summary>
    public static Task<string> SiteTimeZoneAsync(this Forge.Kernel.Access.IKernelUnitOfWork uow, CancellationToken ct, string? siteCode = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.Store<IResourcesLookup>().SiteTimeZoneAsync(siteCode, ct);
    }
}
