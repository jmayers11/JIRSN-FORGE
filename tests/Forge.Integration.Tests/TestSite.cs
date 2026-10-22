using System.Globalization;

namespace Forge.Integration.Tests;

/// <summary>
/// The time zone the test plants' sites are created in. Forge judges date rules on the site's date (ADR 0057 §1), and
/// most tests state "today" as the UTC date, so their sites are in UTC: then the two agree at any hour, including the
/// evening hours when the New York date is a day behind UTC. Tests about site time itself create a New York site on
/// purpose (<see cref="NewYork"/>). Records with no site at all still use Forge's default zone (America/New_York).
/// </summary>
internal static class TestSite
{
    public const string TimeZone = "UTC";

    public const string NewYork = "America/New_York";

    /// <summary>A date at a test site, as the API writes dates.</summary>
    public static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
