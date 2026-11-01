using Forge.Kernel.Postgres;
using Forge.Procurement.Contracts;

namespace Forge.Procurement.Infrastructure;

internal sealed class PostgresScorecardPolicyStore(PostgresUnitOfWork uow) : IScorecardPolicyStore, IScorecardPolicyLookup
{
    public async Task InsertScorecardPolicyAsync(ScorecardPolicy p, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO procurement.scorecard_policy (late_tolerance_days, early_limit_days, grade_a_on_time, grade_a_quality, grade_b_on_time, grade_b_quality, period_days, effective_from, set_by, reason)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            """,
            p.LateToleranceDays, p.EarlyLimitDays, p.GradeAOnTimePercent, p.GradeAQualityPercent, p.GradeBOnTimePercent, p.GradeBQualityPercent, p.PeriodDays, p.EffectiveFrom, p.SetBy, p.Reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ScorecardPolicy> ScorecardPolicyAsync(CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            SELECT late_tolerance_days, early_limit_days, grade_a_on_time, grade_a_quality, grade_b_on_time, grade_b_quality, period_days, effective_from, set_by, reason
            FROM procurement.scorecard_policy ORDER BY effective_from DESC, policy_id DESC LIMIT 1
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new ScorecardPolicy(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.GetDecimal(2) / 1.00m, r.GetDecimal(3) / 1.00m, r.GetDecimal(4) / 1.00m, r.GetDecimal(5) / 1.00m,
                r.GetInt32(6), r.GetFieldValue<DateTimeOffset>(7), r.GetString(8), r.GetString(9))
            : ScorecardPolicy.Default;
    }
}
