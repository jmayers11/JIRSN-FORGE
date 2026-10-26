using Forge.Engineering.Contracts;
using Forge.Kernel.Postgres;

namespace Forge.Engineering.Infrastructure;

internal sealed class PostgresLaborStandardReviewStore(PostgresUnitOfWork uow) : ILaborStandardReviewStore
{
    public async Task<IReadOnlyList<LaborStandardReview>> ListReviewsAsync(CancellationToken ct)
    {
        var list = new List<LaborStandardReview>();
        await using var cmd = uow.Command(
            "SELECT review_id, part_number, revision, sequence, status, reason, raised_by, raised_at, decided_by, decided_at, resolution FROM engineering.labor_standard_review ORDER BY review_id");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new LaborStandardReview(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), Enum.Parse<LaborStandardReviewStatus>(r.GetString(4)), r.GetString(5),
                r.GetString(6), r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
                r.IsDBNull(10) ? null : r.GetString(10)));
        return list;
    }

    public async Task<long?> InsertReviewAsync(LaborStandardReview review, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(review);
        await using var cmd = uow.Command(
            """
            INSERT INTO engineering.labor_standard_review (part_number, revision, sequence, status, reason, raised_by, raised_at)
            VALUES ($1, $2, $3, 'Open', $4, $5, $6) ON CONFLICT DO NOTHING RETURNING review_id
            """,
            review.PartNumber, review.Revision, review.Sequence, review.Reason, review.RaisedBy, review.RaisedAt);
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    public async Task<bool> DecideReviewAsync(LaborStandardReview review, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(review);
        await using var cmd = uow.Command(
            "UPDATE engineering.labor_standard_review SET status = $2, decided_by = $3, decided_at = $4, resolution = $5 WHERE review_id = $1 AND status = 'Open'",
            review.ReviewId, review.Status.ToString(), review.DecidedBy, review.DecidedAt, review.Resolution);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
