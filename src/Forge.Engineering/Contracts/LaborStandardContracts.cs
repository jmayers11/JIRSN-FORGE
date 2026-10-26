namespace Forge.Engineering.Contracts;

/// <summary>
/// Open: the labor standard is suspected wrong and is not used to judge anyone's efficiency. Confirmed: an engineer checked it and it
/// counts again, even where it looked implausible. Corrected: it was wrong; work done against it stays out of efficiency, and the fix
/// goes into a new routing revision.
/// </summary>
public enum LaborStandardReviewStatus { Open, Confirmed, Corrected }

/// <summary>A review of the setup and run standard of one routing operation of one revision (Sprint 10 WW-S10-10).</summary>
public sealed record LaborStandardReview(
    long ReviewId,
    string PartNumber,
    string Revision,
    int Sequence,
    LaborStandardReviewStatus Status,
    string Reason,
    string RaisedBy,
    DateTimeOffset RaisedAt,
    string? DecidedBy = null,
    DateTimeOffset? DecidedAt = null,
    string? Resolution = null);

public interface ILaborStandardReviewLookup
{
    /// <summary>Every review, oldest first; the latest one for an operation decides how its standard is used.</summary>
    Task<IReadOnlyList<LaborStandardReview>> ListReviewsAsync(CancellationToken ct);
}

public interface ILaborStandardReviewStore : ILaborStandardReviewLookup
{
    Task<long?> InsertReviewAsync(LaborStandardReview review, CancellationToken ct);

    Task<bool> DecideReviewAsync(LaborStandardReview review, CancellationToken ct);
}

/// <summary>How efficiency may use a standard, given its reviews.</summary>
public static class LaborStandardReviews
{
    public static LaborStandardReview? Latest(IEnumerable<LaborStandardReview> reviews, string partNumber, string revision, int sequence)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        return reviews.Where(r => r.PartNumber == partNumber && r.Revision == revision && r.Sequence == sequence).MaxBy(r => r.ReviewId);
    }

    /// <summary>
    /// A standard implying less than a quarter, or more than four times, the labor actually recorded is implausible: it is kept out of
    /// efficiency and suggested for review until an engineer confirms it. Below an hour of labor nothing is judged implausible.
    /// </summary>
    public static bool Implausible(decimal earnedHours, decimal laborHours) =>
        laborHours >= 1m && (earnedHours < laborHours / 4m || earnedHours > laborHours * 4m);
}
