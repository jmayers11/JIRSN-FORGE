using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Workforce.Contracts;

namespace Forge.Workforce;

public enum DevelopmentNominationKind { AdvancedTraining, LeadReadiness, PromotionReadiness, CompensationConsideration }
public enum DevelopmentNominationStatus { Draft, Submitted, InReview, Approved, Declined, Deferred, TrainingScheduled, Completed, PracticalAssessment, Authorized }

/// <summary>A documented development proposal; its status is changed only by a human workflow command.</summary>
public sealed record DevelopmentNomination(
    Guid NominationId,
    Guid PersonId,
    DevelopmentNominationKind Kind,
    string Pathway,
    string Rationale,
    string ExpectedPlantBenefit,
    IReadOnlyList<string> EvidenceReferences,
    decimal? EstimatedTrainingHours,
    decimal? EstimatedCost,
    string? Currency,
    DevelopmentNominationStatus Status,
    string CreatedBy,
    Guid? CreatedByPersonId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version);

public sealed record DevelopmentNominationEvent(Guid EventId, Guid NominationId, DevelopmentNominationStatus? FromStatus,
    DevelopmentNominationStatus ToStatus, string Note, string RecordedBy, Guid? RecordedByPersonId, DateTimeOffset RecordedAt);

public sealed record CreateDevelopmentNomination(Guid PersonId, DevelopmentNominationKind Kind, string Pathway, string Rationale,
    string ExpectedPlantBenefit, IReadOnlyList<string> EvidenceReferences, decimal? EstimatedTrainingHours, decimal? EstimatedCost, string? Currency);

public sealed record AdvanceDevelopmentNomination(Guid NominationId, DevelopmentNominationStatus Status, string Note);

public sealed record DevelopmentNominationChanged(Guid NominationId, Guid PersonId, string Kind, string Status) : IDomainEvent
{
    public static string EventType => nameof(DevelopmentNominationChanged);
    public static int SchemaVersion => 1;
}

public interface IWorkforceDevelopmentStore
{
    Task<IReadOnlyList<DevelopmentNomination>> ListNominationsAsync(Guid? personId, CancellationToken ct);
    Task<DevelopmentNomination?> FindNominationAsync(Guid nominationId, CancellationToken ct);
    Task InsertNominationAsync(DevelopmentNomination nomination, CancellationToken ct);
    Task<bool> AdvanceNominationAsync(DevelopmentNomination nomination, DevelopmentNominationStatus expectedStatus, int expectedVersion, CancellationToken ct);
    Task InsertNominationEventAsync(DevelopmentNominationEvent nominationEvent, CancellationToken ct);
    Task<IReadOnlyList<DevelopmentNominationEvent>> ListNominationEventsAsync(Guid nominationId, CancellationToken ct);
}

public sealed class DevelopmentNominationService(CommandPipeline pipeline)
{
    private const string SourceContext = "Workforce";

    public Task<CommandResult> CreateAsync(CreateDevelopmentNomination c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = ForgeIds.New(pipeline.Time);
        var spec = new CommandSpec(SourceContext, nameof(CreateDevelopmentNomination), Permissions.WorkforceDevelopmentNominate,
            ResourceRef.Of("DevelopmentNomination", id.ToString("D")), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var person = await uow.People.FindAsync(new PersonId(c.PersonId), ct);
            if (person is not { IsActive: true } || !await uow.Store<IWorkforceLookup>().WorkerExistsAsync(c.PersonId, ct))
                throw new CommandRejectedException("worker_not_found", "Nomination must reference an active Forge worker.");
            if (!Enum.IsDefined(c.Kind)) throw new CommandRejectedException("invalid_nomination_kind", "Choose a supported development pathway.");
            var requestedEvidence = c.EvidenceReferences ?? [];
            if (requestedEvidence.Count > 12) throw new CommandRejectedException("too_many_evidence_refs", "A packet can link at most 12 source records.");
            var evidence = requestedEvidence.Select(ValidateEvidenceReference).Distinct(StringComparer.Ordinal).ToArray();
            if (c.EstimatedTrainingHours is < 0 or > 10000 || c.EstimatedCost is < 0 or > 10_000_000)
                throw new CommandRejectedException("invalid_estimate", "Training time and estimated cost must be non-negative and within supported limits.");
            var nomination = new DevelopmentNomination(id, c.PersonId, c.Kind,
                BusinessCode.RequireName(c.Pathway, "Pathway"), BusinessCode.RequireName(c.Rationale, "Rationale"),
                BusinessCode.RequireName(c.ExpectedPlantBenefit, "Expected plant benefit"), evidence,
                c.EstimatedTrainingHours, c.EstimatedCost, OptionalCode(c.Currency), DevelopmentNominationStatus.Draft,
                context.Actor, context.Principal.PersonId.Value, now, now, 1);
            var store = uow.Store<IWorkforceDevelopmentStore>();
            await store.InsertNominationAsync(nomination, ct);
            var entry = new DevelopmentNominationEvent(ForgeIds.New(pipeline.Time), id, null, DevelopmentNominationStatus.Draft,
                context.Reason!, context.Actor, context.Principal.PersonId.Value, now);
            await store.InsertNominationEventAsync(entry, ct);
            var key = id.ToString("D");
            return new CommandOutcome(
                CommandResult.Ok(key, $"Development packet {key} saved as Draft for {person.DisplayName}. No training, promotion or compensation decision was made."),
                [pipeline.Event(new DevelopmentNominationChanged(id, c.PersonId, c.Kind.ToString(), nameof(DevelopmentNominationStatus.Draft)), context, SourceContext, "DevelopmentNomination", key)],
                new AuditChange("DevelopmentNomination", key, null, CanonicalJson.Serialize(nomination)));
        }, ct);
    }

    public Task<CommandResult> AdvanceAsync(AdvanceDevelopmentNomination c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.NominationId.ToString("D");
        var allowed = c.Status is DevelopmentNominationStatus.Submitted
            ? Permissions.WorkforceDevelopmentNominate
            : Permissions.WorkforceNominationReview;
        var spec = new CommandSpec(SourceContext, nameof(AdvanceDevelopmentNomination), allowed,
            ResourceRef.Of("DevelopmentNomination", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IWorkforceDevelopmentStore>();
            var prior = await store.FindNominationAsync(c.NominationId, ct)
                ?? throw new CommandRejectedException("nomination_not_found", "The development packet does not exist.");
            if (!AllowedTransition(prior.Status, c.Status))
                throw new CommandRejectedException("invalid_nomination_transition", $"A packet cannot move from {prior.Status} to {c.Status}.");
            if (c.Status is DevelopmentNominationStatus.Approved or DevelopmentNominationStatus.Declined or DevelopmentNominationStatus.Deferred
                && context.Principal.PersonId.Value == prior.CreatedByPersonId)
                throw new CommandRejectedException("independent_review_required", "The person who created the packet cannot decide it.");
            if (string.IsNullOrWhiteSpace(c.Note) || c.Note.Trim().Length > 800)
                throw new CommandRejectedException("review_note_required", "Record a human review note of at most 800 characters.");
            var next = prior with { Status = c.Status, UpdatedAt = now, Version = prior.Version + 1 };
            if (!await store.AdvanceNominationAsync(next, prior.Status, prior.Version, ct))
                throw new CommandRejectedException("nomination_changed", "Another reviewer updated this packet. Refresh the page before continuing.");
            var entry = new DevelopmentNominationEvent(ForgeIds.New(pipeline.Time), c.NominationId, prior.Status, c.Status,
                BusinessCode.RequireName(c.Note, "Review note"), context.Actor, context.Principal.PersonId.Value, now);
            await store.InsertNominationEventAsync(entry, ct);
            // An approved packet needs its training scheduled: technical trainers get a request.
            var training = c.Status == DevelopmentNominationStatus.Approved
                ? await TrainingRequestService.OpenDevelopmentAsync(pipeline, uow, prior, entry.Note, now, context, ct)
                : null;
            var events = new List<EventEnvelope> { pipeline.Event(new DevelopmentNominationChanged(c.NominationId, prior.PersonId, prior.Kind.ToString(), c.Status.ToString()), context, SourceContext, "DevelopmentNomination", key) };
            if (training is not null) events.Add(training);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Development packet {key} moved to {c.Status}; the decision remains a human-reviewed recommendation.{(training is null ? "" : " Technical trainers were asked to schedule the training.")}"),
                events,
                new AuditChange("DevelopmentNomination", key, CanonicalJson.Serialize(prior), CanonicalJson.Serialize(next)));
        }, ct);
    }

    private static bool AllowedTransition(DevelopmentNominationStatus from, DevelopmentNominationStatus to) => (from, to) switch
    {
        (DevelopmentNominationStatus.Draft, DevelopmentNominationStatus.Submitted) => true,
        (DevelopmentNominationStatus.Submitted, DevelopmentNominationStatus.InReview) => true,
        (DevelopmentNominationStatus.InReview, DevelopmentNominationStatus.Approved or DevelopmentNominationStatus.Declined or DevelopmentNominationStatus.Deferred) => true,
        (DevelopmentNominationStatus.Deferred, DevelopmentNominationStatus.InReview) => true,
        (DevelopmentNominationStatus.Approved, DevelopmentNominationStatus.TrainingScheduled) => true,
        (DevelopmentNominationStatus.TrainingScheduled, DevelopmentNominationStatus.Completed) => true,
        (DevelopmentNominationStatus.Completed, DevelopmentNominationStatus.PracticalAssessment) => true,
        (DevelopmentNominationStatus.PracticalAssessment, DevelopmentNominationStatus.Authorized) => true,
        _ => false,
    };

    private static string ValidateEvidenceReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 160 || reference.Contains("..", StringComparison.Ordinal)
            || reference.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '/' or ':')))
            throw new CommandRejectedException("invalid_evidence_reference", "Evidence references use a short internal record type and identifier.");
        return reference.Trim();
    }

    private static string? OptionalCode(string? value) => string.IsNullOrWhiteSpace(value) ? null : BusinessCode.Require(value, "Currency");
}
