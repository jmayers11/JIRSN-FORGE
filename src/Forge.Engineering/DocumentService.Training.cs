using Forge.Engineering.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Engineering;

/// <summary>A person was trained on one revision of a controlled document (V3.9).</summary>
public sealed record RecordDocumentTraining(PersonId PersonId, string DocumentNumber, string Revision, DateOnly? TrainedOn = null);

/// <summary>Whether readiness checks training on controlled documents, and for which kinds of document (V3.9).</summary>
public sealed record SetDocumentTrainingPolicy(TrainingPolicy Policy, IReadOnlyList<DocumentType>? DocumentTypes = null);

public sealed record DocumentTrainingRecorded(Guid PersonId, string DocumentNumber, string Revision) : IDomainEvent
{
    public static string EventType => nameof(DocumentTrainingRecorded);

    public static int SchemaVersion => 1;
}

public sealed record DocumentTrainingPolicyChanged(string Policy, IReadOnlyList<string> DocumentTypes) : IDomainEvent
{
    public static string EventType => nameof(DocumentTrainingPolicyChanged);

    public static int SchemaVersion => 1;
}

/// <summary>Training on controlled documents (V3.9, V3 brief B).</summary>
public sealed partial class DocumentService
{
    private const string TrainingEntity = "DocumentTraining";

    public Task<CommandResult> RecordTrainingAsync(RecordDocumentTraining c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var number = BusinessCode.Require(c.DocumentNumber, "Document");
        var spec = new CommandSpec(SourceContext, nameof(RecordDocumentTraining), Permissions.CompetencyManage, ResourceRef.Of(DocumentEntity, number));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (await uow.People.LoadPrincipalAsync(c.PersonId, ct) is null)
            {
                throw new CommandRejectedException("person_not_found", "This person is not registered.");
            }

            var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(number, ct)
                ?? throw new CommandRejectedException("document_not_found", $"Document {number} does not exist.");
            var revision = document.Revisions.FirstOrDefault(r => r.Revision == (c.Revision ?? string.Empty).Trim())
                ?? throw new CommandRejectedException("revision_not_found", $"{number} has no revision {c.Revision}.");
            if (revision.Status is not (DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable or DocumentRevisionStatus.Superseded))
            {
                throw new CommandRejectedException("revision_not_released", $"{number} rev {revision.Revision} is {revision.Status}; training is on a released revision.");
            }

            var today = siteClock.DateAt(now);
            var on = c.TrainedOn ?? today;
            if (on > today)
            {
                throw new CommandRejectedException("invalid_date", "Training is recorded once it has happened.");
            }

            var training = new DocumentTraining(0, c.PersonId.Value, number, revision.Revision, on, now, context.Actor);
            var key = $"{c.PersonId.Value}:{training.Key}";
            if (!await uow.Store<IDocumentTrainingStore>().InsertTrainingAsync(training, ct))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(key, $"Training on {number} rev {revision.Revision} is already recorded."));
            }

            return new CommandOutcome(
                CommandResult.Ok(key, $"Training on {number} rev {revision.Revision} recorded."),
                [pipeline.Event(new DocumentTrainingRecorded(c.PersonId.Value, number, revision.Revision), context, SourceContext, TrainingEntity, key)],
                new AuditChange(TrainingEntity, key, null, CanonicalJson.Serialize(training)));
        }, ct);
    }

    public Task<CommandResult> SetTrainingPolicyAsync(SetDocumentTrainingPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var spec = new CommandSpec(SourceContext, nameof(SetDocumentTrainingPolicy), Permissions.CompetencyManage, ResourceRef.Of("DocumentTrainingPolicy", "plant"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var before = await uow.Store<IDocumentTrainingLookup>().GetTrainingPolicyAsync(ct);
            var types = (c.DocumentTypes is { Count: > 0 } t ? t : before.DocumentTypes).Distinct().Order().ToList();
            if (c.Policy == before.Policy && types.SequenceEqual(before.DocumentTypes.Order()))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange("plant", $"The training policy is already {c.Policy}."));
            }

            var policy = new DocumentTrainingPolicy(c.Policy, types, now, context.Actor);
            await uow.Store<IDocumentTrainingStore>().SetTrainingPolicyAsync(policy, ct);
            var names = types.Select(x => x.ToString()).ToList();
            return new CommandOutcome(
                CommandResult.Ok("plant", $"Training on controlled documents ({string.Join(", ", names)}) is now {c.Policy.ToString().ToLowerInvariant()}."),
                [pipeline.Event(new DocumentTrainingPolicyChanged(c.Policy.ToString(), names), context, SourceContext, "DocumentTrainingPolicy", "plant")],
                new AuditChange("DocumentTrainingPolicy", "plant", CanonicalJson.Serialize(before), CanonicalJson.Serialize(policy)));
        }, ct);
    }
}
