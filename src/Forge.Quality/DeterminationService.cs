using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Quality;

/// <summary>Decide whether first article inspection applies to a part revision from a supplier. The reason is the basis.</summary>
public sealed record DetermineFai(string PartNumber, string Revision, string SupplierCode, DeterminationState State);

/// <summary>Record a first article inspection with its results and the verified signed reference report it is backed by.</summary>
public sealed record RecordFai(string PartNumber, string Revision, string SupplierCode, bool Passed, IReadOnlyList<ReceivingCharacteristicResult> Results, string DocumentNumber, string DocumentRevision);

public sealed record DeterminationMade(string Question, string Subject, string State) : IDomainEvent
{
    public static string EventType => nameof(DeterminationMade);

    public static int SchemaVersion => 1;
}

public sealed record FaiRecorded(long FaiId, string Subject, bool Passed) : IDomainEvent
{
    public static string EventType => nameof(FaiRecorded);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Determinations (V3.11, V3 brief R): whether first article inspection is required for a part revision from a
/// supplier, decided with a basis and kept for good; and FAI records with structured results and a signed report.
/// </summary>
public sealed class DeterminationService(CommandPipeline pipeline)
{
    public const string SourceContext = "Quality";

    public Task<CommandResult> DetermineFaiAsync(DetermineFai c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var spec = new CommandSpec(SourceContext, nameof(DetermineFai), Permissions.QualityPlan, ResourceRef.Of("Determination", c.PartNumber ?? string.Empty), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var subject = await SubjectAsync(uow, c.PartNumber, c.Revision, c.SupplierCode, ct);
            var determinations = await uow.Store<IDeterminationLookup>().ListDeterminationsAsync(DeterminationQuestion.Fai, subject, ct);
            var current = determinations.Count == 0 ? null : determinations[0];
            if (current?.State == c.State)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(subject, $"FAI for {subject} is already {c.State}."));
            }

            var determination = new Determination(0, DeterminationQuestion.Fai, subject, c.State, context.Reason!.Trim(), context.Actor, now);
            var id = await uow.Store<IDeterminationStore>().InsertDeterminationAsync(determination, ct);
            determination = determination with { DeterminationId = id };
            return new CommandOutcome(
                CommandResult.Ok(subject, $"FAI for {subject}: {c.State}."),
                [pipeline.Event(new DeterminationMade("FAI", subject, c.State.ToString()), context, SourceContext, "Determination", subject)],
                new AuditChange("Determination", Key(id), current is null ? null : CanonicalJson.Serialize(current), CanonicalJson.Serialize(determination)));
        }, ct);
    }

    public Task<CommandResult> RecordFaiAsync(RecordFai c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var spec = new CommandSpec(SourceContext, nameof(RecordFai), Permissions.QualityInspect, ResourceRef.Of("Fai", c.PartNumber ?? string.Empty));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var subject = await SubjectAsync(uow, c.PartNumber, c.Revision, c.SupplierCode, ct);
            var results = (c.Results ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Characteristic)).ToList();
            if (results.Count == 0)
            {
                throw new CommandRejectedException("results_required", "Record the result of each characteristic checked.");
            }

            if (c.Passed && results.Any(r => !r.Pass))
            {
                throw new CommandRejectedException("results_disagree", "An FAI with a failed characteristic has not passed.");
            }

            // Business evidence stays a reference attachment; verification binds the original signed file.
            var number = BusinessCode.Require(c.DocumentNumber, "Document");
            var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(number, ct)
                ?? throw new CommandRejectedException("document_not_found", $"Document {number} does not exist.");
            if (!DocumentAccess.CanSee(context.Principal, document))
            {
                throw new CommandRejectedException("document_not_found", $"Document {number} does not exist.");
            }

            var revision = document.Revisions.FirstOrDefault(r => r.Revision == (c.DocumentRevision ?? string.Empty).Trim());
            if (document.Authority != DocumentAuthority.Reference || revision?.Status != DocumentRevisionStatus.Uncontrolled || revision.FileSha256 is null || await uow.Store<IEvidenceLookup>().FindVerificationAsync(number, revision.Revision, ct) is null)
            {
                throw new CommandRejectedException("report_not_signed", $"The FAI report {number} rev {c.DocumentRevision} has no verified signed reference file.");
            }

            var parts = subject.Split(':', '@');
            var fai = new FaiRecord(0, subject, parts[0], parts[1], parts[2], c.Passed, results, number, revision.Revision, context.Actor, now);
            var id = await uow.Store<IDeterminationStore>().InsertFaiAsync(fai, ct);
            fai = fai with { FaiId = id };
            return new CommandOutcome(
                CommandResult.Ok(Key(id), $"FAI {id} for {subject} {(c.Passed ? "passed" : "failed")}, backed by {number} rev {revision.Revision}."),
                [pipeline.Event(new FaiRecorded(id, subject, c.Passed), context, SourceContext, "Fai", Key(id))],
                new AuditChange("Fai", Key(id), null, CanonicalJson.Serialize(fai)));
        }, ct);
    }

    private static async Task<string> SubjectAsync(Kernel.Access.IKernelUnitOfWork uow, string? rawPart, string? rawRevision, string? rawSupplier, CancellationToken ct)
    {
        var part = BusinessCode.Require(rawPart ?? string.Empty, "Part");
        var revision = (rawRevision ?? string.Empty).Trim().ToUpperInvariant();
        if (!(await uow.Store<IEngineeringLookup>().ListRevisionsAsync(part, ct)).Any(r => r.Revision == revision))
        {
            throw new CommandRejectedException("unknown_revision", $"{part} has no revision {revision}.");
        }

        return Fai.Subject(part, revision, BusinessCode.Require(rawSupplier ?? string.Empty, "Supplier"));
    }

    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);
}




