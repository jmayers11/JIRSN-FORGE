using Forge.Workforce;

namespace Forge.Integration.Tests;

public sealed class AttendanceReliabilityCalculatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AsOf = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Person = Guid.Parse("a7b4db4f-2c70-4a04-9640-70a9668f7f6d");
    private static readonly AttendancePolicyVersion Policy = new("DEFAULT", 1, Start, null, 5);

    [Fact]
    public void Quarter_excludes_verified_pto_and_protected_leave_and_explains_the_denominator()
    {
        var opportunities = Enumerable.Range(0, 60).Select(index => Opportunity(index)).ToArray();
        var events = new List<AttendanceIntelligenceEvent>();
        for (var index = 0; index < 4; index++) events.Add(Event(index, AttendanceEventKind.ApprovedPto));
        events.Add(Event(4, AttendanceEventKind.ProtectedLeave));
        for (var index = 5; index < 58; index++) events.Add(Event(index, AttendanceEventKind.Arrival, ActualAt(index, 4)));
        events.Add(Event(58, AttendanceEventKind.Arrival, ActualAt(58, 12)));
        events.Add(Event(59, AttendanceEventKind.Arrival, ActualAt(59, 12)));

        var summary = AttendanceReliabilityCalculator.Calculate(opportunities, events, [Policy], Start, AsOf, AsOf);

        Assert.Equal(60, summary.ScheduledOpportunities);
        Assert.Equal(55, summary.EligibleArrivalOpportunities);
        Assert.Equal(53, summary.OnTimeArrivals);
        Assert.Equal(2, summary.VerifiedLateArrivals);
        Assert.Equal(5, summary.ExcludedOpportunities);
        Assert.Equal(96.36m, summary.OnTimeArrivalPercent);
        Assert.Contains("53 of 55", summary.Explanation);
    }

    [Fact]
    public void Missing_punch_is_pending_review_and_never_inferred_as_a_no_show()
    {
        var summary = AttendanceReliabilityCalculator.Calculate(
            [Opportunity(1), Opportunity(2)], [Event(1, AttendanceEventKind.Arrival, ActualAt(1, 0))],
            [Policy], Start, AsOf, AsOf);

        Assert.Equal(0, summary.VerifiedNoShows);
        Assert.Equal(1, summary.PendingReviewOpportunities);
        Assert.Null(summary.OnTimeArrivalPercent);
    }

    [Fact]
    public void Effective_policy_versions_apply_to_their_own_scheduled_dates()
    {
        var later = Start.AddDays(1);
        var policies = new[] { Policy with { EffectiveThrough = later }, new AttendancePolicyVersion("DEFAULT", 2, later, null, 0) };
        var opportunities = new[] { Opportunity(1, Start.AddHours(9)), Opportunity(2, Start.AddDays(1).AddHours(9)) };
        var events = new[] { Event(1, AttendanceEventKind.Arrival, Start.AddHours(9).AddMinutes(4)), Event(2, AttendanceEventKind.Arrival, later.AddHours(9).AddMinutes(4)) };

        var summary = AttendanceReliabilityCalculator.Calculate(opportunities, events, policies, Start, AsOf, AsOf);

        Assert.Equal(1, summary.OnTimeArrivals);
        Assert.Equal(1, summary.VerifiedLateArrivals);
        Assert.Equal(new[] { ("DEFAULT", 1), ("DEFAULT", 2) }, summary.PolicyRevisions);
    }

    [Fact]
    public void Callouts_and_no_shows_are_separate_and_only_explicit_verified_no_shows_enter_the_denominator()
    {
        var opportunities = new[] { Opportunity(1), Opportunity(2), Opportunity(3) };
        var events = new[]
        {
            Event(1, AttendanceEventKind.CallOut),
            Event(2, AttendanceEventKind.NoShow),
            Event(3, AttendanceEventKind.Arrival, ActualAt(3, 0), AttendanceEventReviewState.PendingReview),
        };

        var summary = AttendanceReliabilityCalculator.Calculate(opportunities, events, [Policy], Start, AsOf, AsOf);

        Assert.Equal(1, summary.VerifiedCallOuts);
        Assert.Equal(1, summary.VerifiedNoShows);
        Assert.Equal(1, summary.EligibleArrivalOpportunities);
        Assert.Equal(1, summary.PendingReviewOpportunities);
        Assert.Null(summary.OnTimeArrivalPercent);
    }

    [Fact]
    public void Overlapping_policy_versions_make_the_result_unavailable()
    {
        var second = Policy with { Revision = 2, EffectiveFrom = Start.AddHours(-1) };
        var summary = AttendanceReliabilityCalculator.Calculate(
            [Opportunity(1)], [Event(1, AttendanceEventKind.Arrival, ActualAt(1, 0))], [Policy, second], Start, AsOf, AsOf);

        Assert.Equal(1, summary.MissingPolicyOpportunities);
        Assert.Null(summary.OnTimeArrivalPercent);
    }

    [Fact]
    public void Approved_correction_supersedes_prior_event_without_deleting_it()
    {
        var prior = Event(1, AttendanceEventKind.NoShow);
        var correction = prior with
        {
            EventId = Guid.Parse("10000000-0000-0000-0000-000000000003"),
            Kind = AttendanceEventKind.ScheduleChange,
            ReviewState = AttendanceEventReviewState.PendingReview,
            Source = AttendanceEventSource.Manual,
            SupersedesEventId = prior.EventId,
            RecordedAt = Start.AddHours(1),
        };
        var summary = AttendanceReliabilityCalculator.Calculate(
            [Opportunity(1)], [prior, correction], [Policy], Start, AsOf, AsOf);

        Assert.Equal(0, summary.VerifiedNoShows);
        Assert.Equal(1, summary.PendingReviewOpportunities);
        Assert.Null(summary.OnTimeArrivalPercent);
    }

    [Fact]
    public void Approved_pto_count_requires_one_verified_current_classification()
    {
        var pto = Opportunity(90, AsOf.AddDays(1));
        var pendingPto = Opportunity(91, AsOf.AddDays(1));
        var protectedLeave = Opportunity(92, AsOf.AddDays(1));
        var conflicting = Opportunity(93, AsOf.AddDays(1));
        var corrected = Opportunity(94, AsOf.AddDays(1));
        var priorPto = Event(94, AttendanceEventKind.ApprovedPto);
        var correction = priorPto with
        {
            EventId = Guid.NewGuid(),
            Kind = AttendanceEventKind.Arrival,
            ReviewState = AttendanceEventReviewState.PendingReview,
            SupersedesEventId = priorPto.EventId,
        };
        var events = new[]
        {
            Event(90, AttendanceEventKind.ApprovedPto),
            Event(91, AttendanceEventKind.ApprovedPto, state: AttendanceEventReviewState.PendingReview),
            Event(92, AttendanceEventKind.ProtectedLeave),
            Event(93, AttendanceEventKind.ApprovedPto),
            Event(93, AttendanceEventKind.Arrival, AsOf.AddDays(1).AddHours(9)),
            priorPto,
            correction,
        };

        Assert.Equal(1, AttendanceReliabilityCalculator.VerifiedApprovedPtoCount(
            [pto, pendingPto, protectedLeave, conflicting, corrected], events, AsOf, AsOf.AddDays(2)));
    }

    private static ScheduledAttendanceOpportunity Opportunity(int day, DateTimeOffset? at = null)
    {
        var start = at ?? Start.AddDays(day).AddHours(9);
        return new ScheduledAttendanceOpportunity(Guid.Parse($"00000000-0000-0000-0000-{day + 1:000000000000}"), Person, "DAY", start, start.AddHours(8));
    }

    private static AttendanceIntelligenceEvent Event(int day, AttendanceEventKind kind, DateTimeOffset? observedAt = null,
        AttendanceEventReviewState state = AttendanceEventReviewState.Verified) => new(
        Guid.Parse($"10000000-0000-0000-0000-{day + 1:000000000000}"), Person,
        Guid.Parse($"00000000-0000-0000-0000-{day + 1:000000000000}"), kind, observedAt, state,
        AttendanceEventSource.Timeclock, Start.AddDays(day));

    private static DateTimeOffset ActualAt(int day, int minutesLate) => Start.AddDays(day).AddHours(9).AddMinutes(minutesLate);
}
