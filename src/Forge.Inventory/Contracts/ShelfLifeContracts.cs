using Forge.Resources.Contracts;

namespace Forge.Inventory.Contracts;

/// <summary>What a part's shelf life counts from (V3.6).</summary>
public enum ShelfLifeBasis
{
    Manufacture,
    Receipt,
}

/// <summary>Which usable lot is offered first (V3.6). Only ever applied after the hard rules.</summary>
public enum SelectionRule
{
    /// <summary>First in, first out: the oldest receipt first.</summary>
    Fifo,

    /// <summary>First expired, first out: the earliest expiry first.</summary>
    Fefo,
}

/// <summary>A part's shelf-life rule (V3.6), set by quality; the latest setting applies and every setting is kept.</summary>
public sealed record ShelfLifeRule(
    string PartNumber,
    int? ShelfLifeDays,
    ShelfLifeBasis Basis,
    int? RetestDays,
    int? OpenLifeHours,
    decimal? StorageMinC,
    decimal? StorageMaxC,
    string? StorageNote,
    SelectionRule Selection,
    string Reason,
    string SetBy,
    DateTimeOffset SetAt);

/// <summary>A request to move a lot's expiry later (V3.6). Decided by someone else; the lot keeps its original date.</summary>
public sealed record ShelfLifeExtension(
    long ExtensionId,
    string PartNumber,
    string LotNumber,
    DateOnly PreviousExpires,
    DateOnly NewExpires,
    string Evidence,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    bool? Approved = null,
    string? DecidedBy = null,
    DateTimeOffset? DecidedAt = null);

/// <summary>A retest of a lot (V3.6): passed with the next retest date, or failed.</summary>
public sealed record RetestInfo(long RetestId, string PartNumber, string LotNumber, bool Passed, DateOnly? NextDue, string Evidence, string TestedBy, DateTimeOffset TestedAt);

/// <summary>A period a location was outside its storage conditions (V3.6), recorded after the fact.</summary>
public sealed record StorageExcursion(long ExcursionId, string LocationCode, string Condition, decimal? MeasuredC, DateTimeOffset StartedAt, DateTimeOffset EndedAt, string? Note, string RecordedBy, DateTimeOffset RecordedAt);

public enum ShelfLifeStatus
{
    /// <summary>No shelf life, retest or open life applies.</summary>
    NotTracked,

    Ok,

    /// <summary>Usable, but expires within <see cref="LotShelfLife.ExpiringDays"/> days.</summary>
    Expiring,

    Expired,

    /// <summary>Opened, and its open life has run out.</summary>
    OpenLifeExpired,

    /// <summary>The retest date has passed without a passing retest.</summary>
    RetestOverdue,

    /// <summary>The last retest failed.</summary>
    RetestFailed,

    /// <summary>It sat in a location during a storage excursion and has not been retested since.</summary>
    Excursion,
}

/// <summary>
/// One lot's age, shelf life, open life, retest and storage history kept apart (V3.6), with the status the hard
/// rules follow: only <see cref="ShelfLifeStatus.NotTracked"/>, <see cref="ShelfLifeStatus.Ok"/> and
/// <see cref="ShelfLifeStatus.Expiring"/> lots are used, issued or shipped.
/// </summary>
public sealed record LotShelfLife(
    string PartNumber,
    string LotNumber,
    int AgeDays,
    DateOnly? ManufacturedOn,
    DateOnly? OriginalExpires,
    DateOnly? Expires,
    DateOnly? RetestDue,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? OpenExpiresAt,
    IReadOnlyList<long> Excursions,
    ShelfLifeStatus Status,
    string Explanation)
{
    public const int ExpiringDays = 30;

    public bool Usable => Status is ShelfLifeStatus.NotTracked or ShelfLifeStatus.Ok or ShelfLifeStatus.Expiring;

    /// <param name="today">The site's date.</param>
    /// <param name="timeZone">The site's zone, for the date a lot without a manufacture date was first received.</param>
    /// <param name="locationsHeld">The locations the lot has had stock in, with when it arrived and left (left null while there).</param>
    public static LotShelfLife For(
        LotInfo lot,
        ShelfLifeRule? rule,
        IEnumerable<ShelfLifeExtension> extensions,
        IEnumerable<RetestInfo> retests,
        IEnumerable<StorageExcursion> excursions,
        IEnumerable<(string Location, DateTimeOffset From, DateTimeOffset? Until)> locationsHeld,
        DateOnly today,
        DateTimeOffset now,
        string? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(lot);
        var mine = extensions.Where(e => e.PartNumber == lot.PartNumber && e.LotNumber == lot.LotNumber && e.Approved == true).OrderBy(e => e.DecidedAt).ToList();
        var tests = retests.Where(r => r.PartNumber == lot.PartNumber && r.LotNumber == lot.LotNumber).OrderBy(r => r.TestedAt).ToList();
        var lastTest = tests.LastOrDefault();
        var expires = mine.Count > 0 ? mine[^1].NewExpires : lot.ExpiresOn;
        var retestDue = lastTest is { Passed: true } ? lastTest.NextDue : lot.RetestDue;
        var openExpires = lot.OpenedAt is { } opened && rule?.OpenLifeHours is { } hours ? opened.AddHours(hours) : (DateTimeOffset?)null;
        var start = lot.ManufacturedOn ?? SiteTime.DateAtOrUtc(lot.FirstReceivedAt, timeZone);
        var age = Math.Max(0, today.DayNumber - start.DayNumber);

        // Storage excursions while the lot was in the location, outside the part's storage conditions, and not retested since.
        var held = locationsHeld.ToList();
        var hits = excursions
            .Where(x => rule is not null && (rule.StorageMinC is not null || rule.StorageMaxC is not null)
                && (x.MeasuredC is not { } c || (rule.StorageMinC is { } min && c < min) || (rule.StorageMaxC is { } max && c > max))
                && held.Any(h => h.Location == x.LocationCode && h.From < x.EndedAt && (h.Until is not { } until || until > x.StartedAt)))
            .Where(x => !tests.Any(t => t.Passed && t.TestedAt > x.EndedAt))
            .ToList();

        var tracked = expires is not null || retestDue is not null || openExpires is not null || hits.Count > 0 || lastTest is not null;
        var (status, why) =
            expires is { } e && e < today ? (ShelfLifeStatus.Expired, $"Expired {e:yyyy-MM-dd}{(mine.Count > 0 ? " (after an extension)" : string.Empty)}.")
            : openExpires is { } o && o <= now ? (ShelfLifeStatus.OpenLifeExpired, $"Opened {lot.OpenedAt:u}; open life ran out {o:u}.")
            : lastTest is { Passed: false } ? (ShelfLifeStatus.RetestFailed, $"Failed retest {lastTest.TestedAt:yyyy-MM-dd}: {lastTest.Evidence}")
            : retestDue is { } r && r < today ? (ShelfLifeStatus.RetestOverdue, $"Retest was due {r:yyyy-MM-dd}.")
            : hits.Count > 0 ? (ShelfLifeStatus.Excursion, $"In {string.Join(", ", hits.Select(h => h.LocationCode).Distinct(StringComparer.Ordinal))} during a storage excursion ({string.Join("; ", hits.Select(h => h.Condition))}); retest it before use.")
            : expires is { } soon && soon.DayNumber - today.DayNumber <= ExpiringDays ? (ShelfLifeStatus.Expiring, $"Expires {soon:yyyy-MM-dd}, in {soon.DayNumber - today.DayNumber} day(s).")
            : tracked ? (ShelfLifeStatus.Ok, expires is { } later ? $"Expires {later:yyyy-MM-dd}." : retestDue is { } next ? $"Retest due {next:yyyy-MM-dd}." : "Within its limits.")
            : (ShelfLifeStatus.NotTracked, "No shelf life, retest or open life applies.");
        return new LotShelfLife(lot.PartNumber, lot.LotNumber, age, lot.ManufacturedOn, lot.ExpiresOn, expires, retestDue, lot.OpenedAt, openExpires, hits.Select(h => h.ExcursionId).ToList(), status, why);
    }
}

/// <summary>A lot offered for use (V3.6), or why it is not.</summary>
public sealed record SelectionCandidate(string LotNumber, string LocationCode, decimal Quantity, DateTimeOffset Received, DateOnly? Expires, string? PartRevision, int Rank, string? ExcludedBecause);

/// <summary>
/// Which lots to use first (V3.6). The hard rules come first: released, in the company's custody and counted for
/// planning, the revision the order needs, and usable by shelf life. Only then does the part's FIFO or FEFO rule
/// order what is left; it never brings back a lot the rules left out.
/// </summary>
public static class LotSelection
{
    public static IReadOnlyList<SelectionCandidate> Rank(
        IEnumerable<StockPosition> positions,
        IReadOnlyDictionary<string, LotInfo> lots,
        IReadOnlyDictionary<string, LotShelfLife> shelfLife,
        SelectionRule rule,
        string? requiredRevision)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(lots);
        ArgumentNullException.ThrowIfNull(shelfLife);
        var all = new List<SelectionCandidate>();
        foreach (var p in positions.Where(p => p.LotNumber is not null && p.Quantity > 0))
        {
            var lot = lots.GetValueOrDefault(p.LotNumber!);
            var life = shelfLife.GetValueOrDefault(p.LotNumber!);
            var why = p.QualityStatus != StockStatus.Released ? $"{p.QualityStatus}, not released"
                : p.Custody != Custody.Company ? "not in the company's custody"
                : !p.Nettable ? $"{p.LocationCode} does not count for use"
                : requiredRevision is not null && lot?.PartRevision != requiredRevision ? $"revision {lot?.PartRevision ?? "not recorded"}, the order needs {requiredRevision}"
                : life is { Usable: false } ? life.Explanation
                : null;
            all.Add(new SelectionCandidate(p.LotNumber!, p.LocationCode, p.Quantity, lot?.FirstReceivedAt ?? DateTimeOffset.MaxValue, life?.Expires, lot?.PartRevision, 0, why));
        }

        var eligible = (rule == SelectionRule.Fefo
                ? all.Where(c => c.ExcludedBecause is null).OrderBy(c => c.Expires ?? DateOnly.MaxValue).ThenBy(c => c.Received)
                : all.Where(c => c.ExcludedBecause is null).OrderBy(c => c.Received).ThenBy(c => c.Expires ?? DateOnly.MaxValue))
            .ThenBy(c => c.LotNumber, StringComparer.Ordinal).ThenBy(c => c.LocationCode, StringComparer.Ordinal)
            .Select((c, i) => c with { Rank = i + 1 });
        return eligible.Concat(all.Where(c => c.ExcludedBecause is not null).OrderBy(c => c.LotNumber, StringComparer.Ordinal)).ToList();
    }
}

public interface IShelfLifeLookup
{
    Task<ShelfLifeRule?> FindRuleAsync(string partNumber, CancellationToken ct);

    /// <summary>The rule in force for every part that has one.</summary>
    Task<IReadOnlyList<ShelfLifeRule>> ListRulesAsync(CancellationToken ct);

    Task<IReadOnlyList<ShelfLifeExtension>> ListExtensionsAsync(string? partNumber, CancellationToken ct);

    Task<IReadOnlyList<RetestInfo>> ListRetestsAsync(string? partNumber, CancellationToken ct);

    Task<IReadOnlyList<StorageExcursion>> ListExcursionsAsync(CancellationToken ct);

    /// <summary>For each lot of the part (all parts when null), the locations it has had stock in: arrived, and left (null while there).</summary>
    Task<IReadOnlyList<(string PartNumber, string LotNumber, string Location, DateTimeOffset From, DateTimeOffset? Until)>> ListLotResidenceAsync(string? partNumber, CancellationToken ct);
}

public interface IShelfLifeStore
{
    Task InsertRuleAsync(ShelfLifeRule rule, CancellationToken ct);

    /// <summary>Gives a lot the dates it does not have yet; dates it already has never change.</summary>
    Task SetLotDatesAsync(string partNumber, string lotNumber, DateOnly? manufacturedOn, DateOnly? expiresOn, DateOnly? retestDue, CancellationToken ct);

    /// <summary>A new instance takes the earliest dates of the instances it came from, and is open if any of them was.</summary>
    Task InheritLotDatesAsync(string partNumber, IReadOnlyList<string> sourceLots, string lotNumber, CancellationToken ct);

    Task OpenLotAsync(string partNumber, string lotNumber, DateTimeOffset at, string actor, CancellationToken ct);

    Task<long> InsertExtensionAsync(ShelfLifeExtension extension, CancellationToken ct);

    Task DecideExtensionAsync(long extensionId, bool approved, DateTimeOffset at, string actor, CancellationToken ct);

    Task<long> InsertRetestAsync(RetestInfo retest, CancellationToken ct);

    Task<long> InsertExcursionAsync(StorageExcursion excursion, CancellationToken ct);
}
