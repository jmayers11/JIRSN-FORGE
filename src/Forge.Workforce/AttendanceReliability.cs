using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Workforce;

public enum AttendanceOpportunityState { Scheduled, Cancelled }
public enum AttendanceEventKind { Arrival, CallOut, NoShow, ApprovedPto, ProtectedLeave, ExcusedAbsence, EarlyDeparture, ScheduleChange, ShiftCancelled }
public enum AttendanceEventReviewState { PendingReview, Verified, Disputed, Superseded }
public enum AttendanceEventSource { Manual, Nexus, Timeclock, Hris }

/// <summary>A scheduled arrival obligation, retained even if the schedule is later changed.</summary>
public sealed record ScheduledAttendanceOpportunity(
    Guid OpportunityId,
    Guid PersonId,
    string ShiftCode,
    DateTimeOffset ScheduledStart,
    DateTimeOffset ScheduledEnd,
    AttendanceOpportunityState State = AttendanceOpportunityState.Scheduled,
    Guid? SupersedesOpportunityId = null);

/// <summary>
/// A provenance-bearing attendance fact. Protected leave details belong in restricted source records;
/// this calculation only consumes the classification needed to exclude the opportunity.
/// Corrections append a new event with SupersedesEventId rather than overwriting history.
/// </summary>
public sealed record AttendanceIntelligenceEvent(
    Guid EventId,
    Guid PersonId,
    Guid OpportunityId,
    AttendanceEventKind Kind,
    DateTimeOffset? ObservedAt,
    AttendanceEventReviewState ReviewState,
    AttendanceEventSource Source,
    DateTimeOffset RecordedAt,
    Guid? SupersedesEventId = null,
    string? ReasonCode = null,
    string RecordedBy = "",
    string? SourceRecordId = null);

/// <summary>Effective-dated calculation policy. Each scheduled opportunity uses exactly one version.</summary>
public sealed record AttendancePolicyVersion(
    string PolicyCode,
    int Revision,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveThrough,
    int GraceMinutes,
    bool IncludeReportedCallOutsInArrivalDenominator = false)
{
    public bool AppliesAt(DateTimeOffset at) => EffectiveFrom <= at && (EffectiveThrough is null || at < EffectiveThrough);
}

public sealed record AttendanceReliabilitySummary(
    int ScheduledOpportunities,
    int EligibleArrivalOpportunities,
    int OnTimeArrivals,
    int VerifiedLateArrivals,
    int VerifiedCallOuts,
    int VerifiedNoShows,
    int ExcludedOpportunities,
    int PendingReviewOpportunities,
    int MissingPolicyOpportunities,
    decimal? OnTimeArrivalPercent,
    int CallOutDenominator,
    int NoShowDenominator,
    IReadOnlyList<(string PolicyCode, int Revision)> PolicyRevisions,
    string Explanation);

/// <summary>
/// Deterministic, non-disciplinary attendance calculations. Missing punches never create a no-show;
/// only a verified explicit event may do so. Policy changes apply by effective date and do not rewrite
/// the recorded source facts.
/// </summary>
public static class AttendanceReliabilityCalculator
{
    private static readonly HashSet<AttendanceEventKind> ExcludedKinds =
    [AttendanceEventKind.ApprovedPto, AttendanceEventKind.ProtectedLeave, AttendanceEventKind.ExcusedAbsence];

    public static AttendanceReliabilitySummary Calculate(
        IReadOnlyCollection<ScheduledAttendanceOpportunity> opportunities,
        IReadOnlyCollection<AttendanceIntelligenceEvent> events,
        IReadOnlyCollection<AttendancePolicyVersion> policies,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(opportunities);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(policies);
        if (periodEnd <= periodStart) throw new ArgumentException("Attendance period must end after it starts.", nameof(periodEnd));
        if (policies.Any(policy => policy.Revision < 1 || policy.GraceMinutes is < 0 or > 240))
            throw new ArgumentException("Each attendance policy needs a positive revision and a grace window from zero to 240 minutes.", nameof(policies));

        var effectivePolicies = policies.GroupBy(policy => policy.PolicyCode, StringComparer.Ordinal)
            .SelectMany(group =>
            {
                var ordered = group.OrderBy(policy => policy.EffectiveFrom).ThenBy(policy => policy.Revision).ToArray();
                var revisionChronologyValid = ordered.Zip(ordered.Skip(1), (current, next) => next.Revision > current.Revision).All(valid => valid);
                return ordered.Select((policy, index) =>
                {
                    var through = policy.EffectiveThrough;
                    // An open-ended historical version ends when its next effective version begins.
                    // An explicitly overlapping effectiveThrough is retained so that the calculator
                    // reports an ambiguous policy instead of silently rewriting authored policy history.
                    // If revisions themselves are out of effective-date order, the source history is
                    // inconsistent; retain the overlap so the affected opportunity remains unavailable.
                    if (revisionChronologyValid && index + 1 < ordered.Length && through is null)
                        through = ordered[index + 1].EffectiveFrom;
                    return policy with { EffectiveThrough = through };
                });
            }).ToArray();
        var supersededOpportunities = opportunities.Where(opportunity => opportunity.SupersedesOpportunityId is not null)
            .Select(opportunity => opportunity.SupersedesOpportunityId!.Value).ToHashSet();
        var inPeriod = opportunities.Where(opportunity => !supersededOpportunities.Contains(opportunity.OpportunityId)
            && opportunity.ScheduledStart >= periodStart
            && opportunity.ScheduledStart < periodEnd && opportunity.ScheduledStart <= asOf).ToArray();
        var byOpportunity = events.Where(item => item.ReviewState != AttendanceEventReviewState.Superseded)
            .GroupBy(item => item.OpportunityId).ToDictionary(group => group.Key, group => CurrentEvents(group.ToArray()));

        var scheduled = 0;
        var eligible = 0;
        var onTime = 0;
        var late = 0;
        var callOuts = 0;
        var noShows = 0;
        var excluded = 0;
        var classifiedForEventRates = 0;
        var pending = 0;
        var missingPolicy = 0;
        var usedPolicies = new HashSet<(string PolicyCode, int Revision)>();

        foreach (var opportunity in inPeriod)
        {
            if (opportunity.State == AttendanceOpportunityState.Cancelled) continue;
            scheduled++;
            var applicable = effectivePolicies.Where(policy => policy.AppliesAt(opportunity.ScheduledStart)).ToArray();
            if (applicable.Length != 1)
            {
                missingPolicy++;
                pending++;
                continue;
            }

            var policy = applicable[0];
            usedPolicies.Add((policy.PolicyCode, policy.Revision));
            var current = byOpportunity.GetValueOrDefault(opportunity.OpportunityId) ?? [];
            if (current.Any(item => item.ReviewState is AttendanceEventReviewState.PendingReview or AttendanceEventReviewState.Disputed))
            {
                pending++;
                continue;
            }

            var verified = current.Where(item => item.ReviewState == AttendanceEventReviewState.Verified
                && item.PersonId == opportunity.PersonId).ToArray();
            var absences = verified.Where(item => ExcludedKinds.Contains(item.Kind) || item.Kind is AttendanceEventKind.CallOut or AttendanceEventKind.NoShow).ToArray();
            if (absences.Length > 1 || (absences.Length == 1 && verified.Any(item => item.Kind is AttendanceEventKind.Arrival or AttendanceEventKind.EarlyDeparture)))
            {
                pending++;
                continue;
            }

            if (absences.Length == 1)
            {
                var absence = absences[0];
                if (ExcludedKinds.Contains(absence.Kind))
                {
                    excluded++;
                    continue;
                }

                if (absence.Kind == AttendanceEventKind.CallOut)
                {
                    callOuts++;
                    if (policy.IncludeReportedCallOutsInArrivalDenominator) eligible++;
                    else excluded++;
                    classifiedForEventRates++;
                    continue;
                }

                noShows++;
                eligible++;
                classifiedForEventRates++;
                continue;
            }

            if (verified.Any(item => item.Kind is AttendanceEventKind.ScheduleChange or AttendanceEventKind.ShiftCancelled))
            {
                excluded++;
                continue;
            }

            var arrival = verified.Where(item => item.Kind == AttendanceEventKind.Arrival).OrderBy(item => item.RecordedAt).LastOrDefault();
            if (arrival?.ObservedAt is not { } arrivedAt)
            {
                // No punch is unknown until a person with authority verifies an explicit classification.
                pending++;
                continue;
            }

            eligible++;
            classifiedForEventRates++;
            if (arrivedAt <= opportunity.ScheduledStart.AddMinutes(policy.GraceMinutes)) onTime++;
            else late++;
        }

        var complete = pending == 0 && missingPolicy == 0;
        decimal? percent = complete && eligible > 0
            ? decimal.Round(100m * onTime / eligible, 2, MidpointRounding.AwayFromZero)
            : null;
        return new AttendanceReliabilitySummary(scheduled, eligible, onTime, late, callOuts, noShows, excluded, pending,
            missingPolicy, percent, classifiedForEventRates, classifiedForEventRates, usedPolicies.OrderBy(policy => policy.PolicyCode, StringComparer.Ordinal)
                .ThenBy(policy => policy.Revision).ToArray(),
            percent is { } value
                ? $"On-time arrivals are {onTime} of {eligible} eligible scheduled arrival opportunities ({value:0.##}%). {excluded} opportunities were excluded by verified absence classification."
                : $"On-time arrival percentage is unavailable: {pending} opportunity(ies) need review and {missingPolicy} opportunity(ies) have no unique effective policy.");
    }

    private static AttendanceIntelligenceEvent[] CurrentEvents(AttendanceIntelligenceEvent[] items)
    {
        var superseded = items.Where(item => item.SupersedesEventId is not null).Select(item => item.SupersedesEventId!.Value).ToHashSet();
        return items.Where(item => !superseded.Contains(item.EventId)).OrderBy(item => item.RecordedAt).ThenBy(item => item.EventId).ToArray();
    }
}

public interface IAttendanceReliabilityStore
{
    Task<IReadOnlyList<ScheduledAttendanceOpportunity>> ListAttendanceOpportunitiesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<ScheduledAttendanceOpportunity?> FindAttendanceOpportunityAsync(Guid personId, DateTimeOffset at, CancellationToken ct);
    Task<bool> InsertAttendanceOpportunityAsync(ScheduledAttendanceOpportunity opportunity, string source, string sourceKey, DateTimeOffset recordedAt, string actor, CancellationToken ct);
    Task<IReadOnlyList<AttendanceIntelligenceEvent>> ListAttendanceIntelligenceEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<AttendanceIntelligenceEvent?> FindAttendanceIntelligenceEventAsync(Guid eventId, CancellationToken ct);
    Task InsertAttendanceIntelligenceEventAsync(AttendanceIntelligenceEvent item, CancellationToken ct);
    Task<bool> InsertImportedAttendanceIntelligenceEventAsync(AttendanceIntelligenceEvent item, string sourceRecordId, CancellationToken ct);
    Task<IReadOnlyList<AttendancePolicyVersion>> ListAttendancePoliciesAsync(CancellationToken ct);
    Task<bool> InsertAttendancePolicyAsync(AttendancePolicyVersion policy, DateTimeOffset recordedAt, string actor, CancellationToken ct);
}

public sealed record SetAttendancePolicy(AttendancePolicyVersion Policy);
public sealed record RecordAttendanceClassification(Guid OpportunityId, AttendanceEventKind Kind, DateTimeOffset? ObservedAt, string? ReasonCode);
public sealed record ReviewAttendanceIntelligenceEvent(Guid EventId, AttendanceEventReviewState Decision, string? ReasonCode);
public sealed record CorrectAttendanceIntelligenceEvent(Guid EventId, AttendanceEventKind Kind, DateTimeOffset? ObservedAt, string? ReasonCode);
public sealed record RequestOwnAttendanceCorrection(Guid EventId, AttendanceEventKind Kind, DateTimeOffset? ObservedAt, string? ReasonCode);
public sealed record ImportAttendanceSourceEvent(Guid OpportunityId, AttendanceEventKind Kind, DateTimeOffset? ObservedAt,
    AttendanceEventSource Source, string SourceRecordId);
public sealed record AttendanceIntelligenceEventRecorded(Guid EventId, Guid OpportunityId, Guid PersonId, string Kind, string ReviewState, string Source) : IDomainEvent
{
    public static string EventType => nameof(AttendanceIntelligenceEventRecorded);
    public static int SchemaVersion => 1;
}

/// <summary>Versioned attendance policy and append-only classification workflow.</summary>
public sealed class AttendanceIntelligenceService(CommandPipeline pipeline)
{
    private const string SourceContext = "Workforce";

    public Task<CommandResult> ImportSourceEventAsync(ImportAttendanceSourceEvent c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.Source is not (AttendanceEventSource.Hris or AttendanceEventSource.Nexus))
            throw new CommandRejectedException("invalid_attendance_source", "Only HRIS or Nexus may submit external attendance facts.");
        if (c.Kind is AttendanceEventKind.ScheduleChange or AttendanceEventKind.ShiftCancelled)
            throw new CommandRejectedException("invalid_attendance_import_kind", "Schedule changes and cancellations must use the attendance schedule workflow.");
        var sourceRecordId = RequireSourceRecordId(c.SourceRecordId);
        var spec = new CommandSpec(SourceContext, nameof(ImportAttendanceSourceEvent), Permissions.AttendanceImport,
            ResourceRef.Of("AttendanceOpportunity", c.OpportunityId), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c with { SourceRecordId = sourceRecordId }, context, async (uow, now) =>
        {
            var store = uow.Store<IAttendanceReliabilityStore>();
            var opportunity = (await store.ListAttendanceOpportunitiesAsync(now.AddDays(-90), now.AddDays(90), ct))
                .SingleOrDefault(item => item.OpportunityId == c.OpportunityId)
                ?? throw new CommandRejectedException("attendance_opportunity_not_found", "The scheduled attendance opportunity was not found.");
            if (opportunity.State != AttendanceOpportunityState.Scheduled)
                throw new CommandRejectedException("attendance_opportunity_cancelled", "External attendance cannot be imported for a cancelled schedule opportunity.");
            if (c.Kind is AttendanceEventKind.Arrival or AttendanceEventKind.EarlyDeparture)
            {
                if (c.ObservedAt is not { } observedAt || observedAt < opportunity.ScheduledStart.AddHours(-4) || observedAt > opportunity.ScheduledEnd.AddHours(4))
                    throw new CommandRejectedException("attendance_observation_outside_shift", "Arrival or departure time must be within four hours of the scheduled shift.");
            }
            if (c.Kind == AttendanceEventKind.NoShow
                && (c.ObservedAt is not { } noShowAt || noShowAt < opportunity.ScheduledEnd || now < opportunity.ScheduledEnd))
                throw new CommandRejectedException("shift_not_finished", "A no-show can be imported only after the scheduled shift has ended.");

            var item = new AttendanceIntelligenceEvent(ForgeIds.New(pipeline.Time), opportunity.PersonId, opportunity.OpportunityId,
                c.Kind, c.ObservedAt, AttendanceEventReviewState.PendingReview, c.Source, now,
                ReasonCode: null, RecordedBy: context.Actor, SourceRecordId: sourceRecordId);
            if (!await store.InsertImportedAttendanceIntelligenceEventAsync(item, sourceRecordId, ct))
                throw new CommandRejectedException("attendance_source_record_duplicate", "This external attendance source record was already imported.");
            return new CommandOutcome(CommandResult.Ok(item.EventId.ToString("D"), "External attendance fact imported and queued for independent review."),
                [pipeline.Event(new AttendanceIntelligenceEventRecorded(item.EventId, item.OpportunityId, item.PersonId,
                    item.Kind.ToString(), item.ReviewState.ToString(), item.Source.ToString()), context, SourceContext, "AttendanceEvent", item.EventId.ToString("D"))],
                new AuditChange("AttendanceEvent", item.EventId.ToString("D"), null,
                    CanonicalJson.Serialize(new { item.OpportunityId, item.Kind, item.ReviewState, item.Source })));
        }, ct);
    }

    public Task<CommandResult> SetPolicyAsync(SetAttendancePolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var policy = c.Policy;
        if (string.IsNullOrWhiteSpace(policy.PolicyCode) || policy.PolicyCode.Length > 48
            || policy.PolicyCode.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_')
            || policy.Revision < 1 || policy.GraceMinutes is < 0 or > 240
            || (policy.EffectiveThrough is { } through && through <= policy.EffectiveFrom))
            throw new CommandRejectedException("invalid_attendance_policy", "Provide a policy code, positive revision, valid effective range, and grace window from zero to 240 minutes.");
        var spec = new CommandSpec(SourceContext, nameof(SetAttendancePolicy), Permissions.AttendancePolicyManage,
            new ResourceRef("AttendancePolicy", $"{policy.PolicyCode}:{policy.Revision}"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IAttendanceReliabilityStore>();
            var policies = await store.ListAttendancePoliciesAsync(ct);
            var nextRevision = policies.Where(p => p.PolicyCode == policy.PolicyCode).Select(p => p.Revision).DefaultIfEmpty(0).Max() + 1;
            if (policy.Revision != nextRevision)
                throw new CommandRejectedException("invalid_policy_revision", "Create the next sequential revision for this attendance policy.");
            var latest = policies.Where(p => p.PolicyCode == policy.PolicyCode).OrderByDescending(p => p.Revision).FirstOrDefault();
            if (latest is not null && policy.EffectiveFrom <= latest.EffectiveFrom)
                throw new CommandRejectedException("policy_effectivity_order", "A new policy revision must take effect after the previous version began; version boundaries are derived from this date.");
            if (!await store.InsertAttendancePolicyAsync(policy, now, context.Actor, ct))
                throw new CommandRejectedException("duplicate_policy", "That attendance policy revision already exists.");
            var id = $"{policy.PolicyCode}:{policy.Revision}";
            return new CommandOutcome(CommandResult.Ok(id, "Attendance policy version recorded."), [],
                new AuditChange("AttendancePolicy", id, null, CanonicalJson.Serialize(new { policy.PolicyCode, policy.Revision, policy.EffectiveFrom, policy.EffectiveThrough, policy.GraceMinutes, policy.IncludeReportedCallOutsInArrivalDenominator })));
        }, ct);
    }

    public Task<CommandResult> RecordClassificationAsync(RecordAttendanceClassification c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.Kind is AttendanceEventKind.ScheduleChange or AttendanceEventKind.ShiftCancelled)
            throw new CommandRejectedException("invalid_attendance_classification", "Schedule changes use the schedule workflow.");
        if (c.Kind == AttendanceEventKind.NoShow && c.ObservedAt is null)
            throw new CommandRejectedException("no_show_time_required", "A no-show classification requires the time it was reviewed.");
        var spec = new CommandSpec(SourceContext, nameof(RecordAttendanceClassification), Permissions.AttendanceClassificationRecord,
            ResourceRef.Of("AttendanceOpportunity", c.OpportunityId), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IAttendanceReliabilityStore>();
            var opportunity = (await store.ListAttendanceOpportunitiesAsync(now.AddDays(-90), now.AddDays(90), ct))
                .SingleOrDefault(o => o.OpportunityId == c.OpportunityId)
                ?? throw new CommandRejectedException("attendance_opportunity_not_found", "The scheduled attendance opportunity was not found.");
            if (c.Kind == AttendanceEventKind.NoShow && c.ObservedAt < opportunity.ScheduledEnd)
                throw new CommandRejectedException("shift_not_finished", "A no-show cannot be recorded until the scheduled opportunity has ended.");
            var item = new AttendanceIntelligenceEvent(ForgeIds.New(pipeline.Time), opportunity.PersonId, opportunity.OpportunityId, c.Kind, c.ObservedAt,
                AttendanceEventReviewState.PendingReview, AttendanceEventSource.Manual, now, ReasonCode: SafeCode(c.ReasonCode), RecordedBy: context.Actor);
            await store.InsertAttendanceIntelligenceEventAsync(item, ct);
            return new CommandOutcome(CommandResult.Ok(item.EventId.ToString("D"), "Classification submitted for independent review."),
                [pipeline.Event(new AttendanceIntelligenceEventRecorded(item.EventId, item.OpportunityId, item.PersonId, item.Kind.ToString(), item.ReviewState.ToString(), item.Source.ToString()), context, SourceContext, "AttendanceEvent", item.EventId.ToString("D"))],
                new AuditChange("AttendanceEvent", item.EventId.ToString("D"), null, CanonicalJson.Serialize(new { item.OpportunityId, item.Kind, item.ReviewState, item.Source, item.ReasonCode })));
        }, ct);
    }

    public Task<CommandResult> ReviewEventAsync(ReviewAttendanceIntelligenceEvent c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.Decision is not (AttendanceEventReviewState.Verified or AttendanceEventReviewState.Disputed))
            throw new CommandRejectedException("invalid_review_decision", "Choose Verified or Disputed.");
        var spec = new CommandSpec(SourceContext, nameof(ReviewAttendanceIntelligenceEvent), Permissions.AttendanceClassificationReview,
            ResourceRef.Of("AttendanceEvent", c.EventId), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IAttendanceReliabilityStore>();
            var original = await store.FindAttendanceIntelligenceEventAsync(c.EventId, ct)
                ?? throw new CommandRejectedException("attendance_event_not_found", "Attendance event was not found.");
            if (original.ReviewState is not (AttendanceEventReviewState.PendingReview or AttendanceEventReviewState.Disputed))
                throw new CommandRejectedException("attendance_event_already_reviewed", "Only a pending or disputed attendance event can be reviewed.");
            if (string.Equals(original.RecordedBy, context.Actor, StringComparison.Ordinal))
                throw new CommandRejectedException("independent_review_required", "A different person must review this attendance event.");
            var reviewed = original with { EventId = ForgeIds.New(pipeline.Time), ReviewState = c.Decision, RecordedAt = now,
                SupersedesEventId = original.EventId, ReasonCode = SafeCode(c.ReasonCode), RecordedBy = context.Actor };
            await store.InsertAttendanceIntelligenceEventAsync(reviewed, ct);
            return new CommandOutcome(CommandResult.Ok(reviewed.EventId.ToString("D"), $"Attendance event {c.Decision.ToString().ToLowerInvariant()}."),
                [pipeline.Event(new AttendanceIntelligenceEventRecorded(reviewed.EventId, reviewed.OpportunityId, reviewed.PersonId, reviewed.Kind.ToString(), reviewed.ReviewState.ToString(), reviewed.Source.ToString()), context, SourceContext, "AttendanceEvent", reviewed.EventId.ToString("D"))],
                new AuditChange("AttendanceEvent", reviewed.EventId.ToString("D"), original.ReviewState.ToString(), reviewed.ReviewState.ToString()));
        }, ct);
    }

    public Task<CommandResult> CorrectEventAsync(CorrectAttendanceIntelligenceEvent c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(CorrectAttendanceIntelligenceEvent), Permissions.AttendanceClassificationRecord,
            ResourceRef.Of("AttendanceEvent", c.EventId), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IAttendanceReliabilityStore>();
            var original = await store.FindAttendanceIntelligenceEventAsync(c.EventId, ct)
                ?? throw new CommandRejectedException("attendance_event_not_found", "Attendance event was not found.");
            if (c.Kind == AttendanceEventKind.ShiftCancelled)
                throw new CommandRejectedException("invalid_attendance_classification", "A shift cancellation must be recorded through the schedule workflow.");
            var correction = original with { EventId = ForgeIds.New(pipeline.Time), Kind = c.Kind, ObservedAt = c.ObservedAt,
                ReviewState = AttendanceEventReviewState.PendingReview, Source = AttendanceEventSource.Manual, RecordedAt = now,
                SupersedesEventId = original.EventId, ReasonCode = SafeCode(c.ReasonCode), RecordedBy = context.Actor };
            await store.InsertAttendanceIntelligenceEventAsync(correction, ct);
            return new CommandOutcome(CommandResult.Ok(correction.EventId.ToString("D"), "Correction submitted for independent review."),
                [pipeline.Event(new AttendanceIntelligenceEventRecorded(correction.EventId, correction.OpportunityId, correction.PersonId, correction.Kind.ToString(), correction.ReviewState.ToString(), correction.Source.ToString()), context, SourceContext, "AttendanceEvent", correction.EventId.ToString("D"))],
                new AuditChange("AttendanceEvent", correction.EventId.ToString("D"), original.Kind.ToString(), correction.Kind.ToString()));
        }, ct);
    }

    public Task<CommandResult> RequestOwnCorrectionAsync(RequestOwnAttendanceCorrection c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(RequestOwnAttendanceCorrection), Permissions.AttendanceCorrectionRequest,
            ResourceRef.Of("AttendanceEvent", c.EventId), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IAttendanceReliabilityStore>();
            var original = await store.FindAttendanceIntelligenceEventAsync(c.EventId, ct)
                ?? throw new CommandRejectedException("attendance_event_not_found", "Attendance event was not found.");
            if (original.PersonId != context.Principal.PersonId.Value)
                throw new CommandRejectedException("attendance_event_not_owned", "Employees can request a correction only for their own attendance records.");
            if (original.RecordedAt < now.AddDays(-366) || original.RecordedAt > now.AddDays(1))
                throw new CommandRejectedException("attendance_event_outside_correction_window", "Only attendance records from the past year can be corrected here.");
            if (c.Kind is not (AttendanceEventKind.Arrival or AttendanceEventKind.CallOut or AttendanceEventKind.NoShow
                or AttendanceEventKind.ApprovedPto or AttendanceEventKind.ProtectedLeave or AttendanceEventKind.ExcusedAbsence
                or AttendanceEventKind.EarlyDeparture or AttendanceEventKind.ScheduleChange))
                throw new CommandRejectedException("invalid_attendance_classification", "Choose a valid attendance classification; shift cancellations must use the schedule workflow.");

            var from = now.AddDays(-366);
            var through = now.AddDays(1);
            var events = await store.ListAttendanceIntelligenceEventsAsync(from, through, ct);
            if (events.Any(item => item.SupersedesEventId == original.EventId))
                throw new CommandRejectedException("attendance_event_already_corrected", "This attendance record already has a correction request or a reviewed replacement.");

            var opportunities = await store.ListAttendanceOpportunitiesAsync(from, through, ct);
            var opportunity = opportunities.SingleOrDefault(item => item.OpportunityId == original.OpportunityId)
                ?? throw new CommandRejectedException("attendance_opportunity_not_found", "The scheduled shift for this attendance record was not found in the correction period.");
            if (c.Kind is AttendanceEventKind.Arrival or AttendanceEventKind.EarlyDeparture)
            {
                if (c.ObservedAt is not { } observedAt || observedAt < opportunity.ScheduledStart.AddHours(-4) || observedAt > opportunity.ScheduledEnd.AddHours(4))
                    throw new CommandRejectedException("attendance_observation_outside_shift", "Arrival or departure time must be within four hours of the scheduled shift.");
            }
            if (c.Kind == AttendanceEventKind.NoShow
                && (c.ObservedAt is not { } noShowAt || noShowAt < opportunity.ScheduledEnd || now < opportunity.ScheduledEnd))
                throw new CommandRejectedException("shift_not_finished", "A no-show correction can be submitted only after the scheduled shift has ended.");

            var correction = original with { EventId = ForgeIds.New(pipeline.Time), Kind = c.Kind, ObservedAt = c.ObservedAt,
                ReviewState = AttendanceEventReviewState.PendingReview, Source = AttendanceEventSource.Manual, RecordedAt = now,
                SupersedesEventId = original.EventId, ReasonCode = SafeCode(c.ReasonCode), RecordedBy = context.Actor };
            await store.InsertAttendanceIntelligenceEventAsync(correction, ct);
            return new CommandOutcome(CommandResult.Ok(correction.EventId.ToString("D"), "Correction request submitted for independent review."),
                [pipeline.Event(new AttendanceIntelligenceEventRecorded(correction.EventId, correction.OpportunityId, correction.PersonId, correction.Kind.ToString(), correction.ReviewState.ToString(), correction.Source.ToString()), context, SourceContext, "AttendanceEvent", correction.EventId.ToString("D"))],
                new AuditChange("AttendanceEvent", correction.EventId.ToString("D"), original.Kind.ToString(), correction.Kind.ToString()));
        }, ct);
    }

    private static string? SafeCode(string? value) => string.IsNullOrWhiteSpace(value) ? null
        : new string(value.Trim().Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').Take(64).ToArray());

    private static string RequireSourceRecordId(string? value)
    {
        var key = value?.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 160 || key.Any(char.IsControl))
            throw new CommandRejectedException("invalid_attendance_source_record", "Provide an opaque source record identifier of at most 160 characters.");
        return key;
    }
}
