using Forge.Competency.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Competency;

/// <summary>
/// Qualification types, prerequisites, training and certification. A person becomes qualified only
/// through <see cref="CertifyAsync"/> by someone holding certification authority who is not that
/// person. Training records never grant a qualification on their own.
/// </summary>
public sealed class CompetencyService(CommandPipeline pipeline)
{
    public const string SourceContext = "Competency";
    private const string TypeEntity = "QualificationType";
    private const string GrantEntity = "PersonQualification";

    public Task<CommandResult> DefineTypeAsync(DefineQualificationType command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var code = BusinessCode.Require(command.Code, "Qualification");
        var spec = new CommandSpec(SourceContext, nameof(DefineQualificationType), Permissions.CompetencyManage, ResourceRef.Of(TypeEntity, code));
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var name = BusinessCode.RequireName(command.Name, "Qualification");
            if (command.ValidityDays is <= 0 or > 3650)
            {
                throw new CommandRejectedException("invalid_validity", "Validity must be between 1 and 3650 days, or empty for no expiry.");
            }

            var store = uow.Store<ICompetencyStore>();
            if (await store.FindTypeAsync(code, ct) is not null)
            {
                throw new CommandRejectedException("code_exists", $"Qualification type {code} already exists.");
            }

            var type = new QualificationTypeInfo(code, name, command.Kind, command.ValidityDays, []);
            await store.InsertTypeAsync(type, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(code, $"Qualification type {code} defined."),
                [pipeline.Event(new QualificationTypeDefined(code, name, command.Kind.ToString(), command.ValidityDays), context, SourceContext, TypeEntity, code)],
                new AuditChange(TypeEntity, code, null, CanonicalJson.Serialize(type)));
        }, ct);
    }

    public Task<CommandResult> AddPrerequisiteAsync(AddPrerequisite command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var code = BusinessCode.Require(command.Code, "Qualification");
        var spec = new CommandSpec(SourceContext, nameof(AddPrerequisite), Permissions.CompetencyManage, ResourceRef.Of(TypeEntity, code));
        return pipeline.ExecuteAsync(spec, command, context, async (uow, _) =>
        {
            var prerequisite = BusinessCode.Require(command.PrerequisiteCode, "Prerequisite");
            var store = uow.Store<ICompetencyStore>();
            if (await store.FindTypeAsync(code, ct) is null || await store.FindTypeAsync(prerequisite, ct) is null)
            {
                throw new CommandRejectedException("unknown_qualification", "Both qualification types must exist.");
            }

            var edges = await store.ListPrerequisitesAsync(ct);
            if (code == prerequisite || Reaches(edges, prerequisite, code))
            {
                throw new CommandRejectedException("prerequisite_cycle", $"{prerequisite} already depends on {code}; adding this would create a cycle.");
            }

            if (!await store.AddPrerequisiteAsync(code, prerequisite, ct))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(code, "Prerequisite already defined."));
            }

            return new CommandOutcome(
                CommandResult.Ok(code, $"{prerequisite} is now required before {code}."),
                [pipeline.Event(new QualificationPrerequisiteAdded(code, prerequisite), context, SourceContext, TypeEntity, code)],
                new AuditChange(TypeEntity, code, null, CanonicalJson.Serialize(new { prerequisite })));
        }, ct);
    }

    public Task<CommandResult> StartTrainingAsync(StartTraining command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var code = BusinessCode.Require(command.Code, "Qualification");
        var spec = new CommandSpec(SourceContext, nameof(StartTraining), Permissions.CompetencyManage, ResourceRef.Of(GrantEntity, $"{command.PersonId}:{code}"));
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var store = uow.Store<ICompetencyStore>();
            await RequireTypeAsync(store, code, ct);
            await RequireActivePersonAsync(uow, command.PersonId, ct);

            var existing = await store.FindAsync(command.PersonId, code, ct);
            if (existing is { Status: QualificationStatus.InTraining })
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(Id(command.PersonId, code), "Already in training."));
            }

            if (existing is { Status: QualificationStatus.Qualified } && existing.IsValidAt(now))
            {
                throw new CommandRejectedException("already_qualified", "The person already holds this qualification.");
            }

            if (existing is { Status: QualificationStatus.Revoked })
            {
                throw new CommandRejectedException("revoked", "This qualification was revoked. A certifier must review before training restarts.");
            }

            var record = new PersonQualification(command.PersonId.Value, code, QualificationStatus.InTraining, null, null);
            await store.UpsertAsync(record, context.Actor, null, now, ct);
            var id = Id(command.PersonId, code);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Training for {code} started."),
                [pipeline.Event(new TrainingStarted(command.PersonId.Value, code), context, SourceContext, GrantEntity, id)],
                new AuditChange(GrantEntity, id, existing is null ? null : CanonicalJson.Serialize(existing), CanonicalJson.Serialize(record)));
        }, ct);
    }

    public Task<CommandResult> CertifyAsync(CertifyQualification command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var code = BusinessCode.Require(command.Code, "Qualification");
        var spec = new CommandSpec(SourceContext, nameof(CertifyQualification), Permissions.CompetencyCertify, ResourceRef.Of(GrantEntity, $"{command.PersonId}:{code}"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            if (context.Principal.PersonId == command.PersonId)
            {
                throw new CommandRejectedException("separation_of_duties", "You cannot certify your own qualification.");
            }

            if (string.IsNullOrWhiteSpace(command.Evidence))
            {
                throw new CommandRejectedException("evidence_required", "Certification requires evidence (assessment record, test result or document reference).");
            }

            var store = uow.Store<ICompetencyStore>();
            var type = await RequireTypeAsync(store, code, ct);
            await RequireActivePersonAsync(uow, command.PersonId, ct);

            var existing = await store.FindAsync(command.PersonId, code, ct);
            if (existing is { Status: QualificationStatus.Revoked })
            {
                throw new CommandRejectedException("revoked", "This qualification was revoked; start training again before certifying.");
            }

            var missing = new List<string>();
            foreach (var prerequisite in type.Prerequisites)
            {
                var held = await store.FindAsync(command.PersonId, prerequisite, ct);
                if (held is null || !held.IsValidAt(now))
                {
                    missing.Add(prerequisite);
                }
            }

            if (missing.Count > 0)
            {
                throw new CommandRejectedException("prerequisites_missing", $"Missing valid prerequisites: {string.Join(", ", missing)}.");
            }

            var expires = type.ValidityDays is { } days ? now.AddDays(days) : (DateTimeOffset?)null;
            var record = new PersonQualification(command.PersonId.Value, code, QualificationStatus.Qualified, now, expires);
            await store.UpsertAsync(record, context.Actor, command.Evidence.Trim(), now, ct);
            var id = Id(command.PersonId, code);
            return new CommandOutcome(
                CommandResult.Ok(id, $"{code} certified" + (expires is null ? "." : $" until {expires:yyyy-MM-dd}.")),
                [pipeline.Event(new QualificationGranted(command.PersonId.Value, code, expires, context.Actor), context, SourceContext, GrantEntity, id)],
                new AuditChange(GrantEntity, id, existing is null ? null : CanonicalJson.Serialize(existing), CanonicalJson.Serialize(new { record, evidence = command.Evidence.Trim() })));
        }, ct);
    }

    public Task<CommandResult> SuspendAsync(SuspendQualification command, CommandContext context, CancellationToken ct) =>
        ChangeStatusAsync(nameof(SuspendQualification), command.PersonId, command.Code, QualificationStatus.Suspended, command, context, ct);

    public Task<CommandResult> RevokeAsync(RevokeQualification command, CommandContext context, CancellationToken ct) =>
        ChangeStatusAsync(nameof(RevokeQualification), command.PersonId, command.Code, QualificationStatus.Revoked, command, context, ct);

    private Task<CommandResult> ChangeStatusAsync<TCommand>(string commandType, PersonId person, string rawCode, QualificationStatus status, TCommand command, CommandContext context, CancellationToken ct)
    {
        var code = BusinessCode.Require(rawCode, "Qualification");
        var spec = new CommandSpec(SourceContext, commandType, Permissions.CompetencyCertify, ResourceRef.Of(GrantEntity, $"{person}:{code}"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var store = uow.Store<ICompetencyStore>();
            var existing = await store.FindAsync(person, code, ct)
                ?? throw new CommandRejectedException("not_found", "The person has no record for this qualification.");
            var id = Id(person, code);
            if (existing.Status == status)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, $"Already {status}."));
            }

            var record = existing with { Status = status };
            await store.UpsertAsync(record, context.Actor, null, now, ct);
            Kernel.Events.EventEnvelope evt = status == QualificationStatus.Revoked
                ? pipeline.Event(new QualificationRevoked(person.Value, code), context, SourceContext, GrantEntity, id)
                : pipeline.Event(new QualificationSuspended(person.Value, code), context, SourceContext, GrantEntity, id);
            return new CommandOutcome(
                CommandResult.Ok(id, $"{code} {status.ToString().ToLowerInvariant()}."),
                [evt],
                new AuditChange(GrantEntity, id, CanonicalJson.Serialize(existing), CanonicalJson.Serialize(record)));
        }, ct);
    }

    private static string Id(PersonId person, string code) => $"{person}:{code}";

    private static async Task<QualificationTypeInfo> RequireTypeAsync(ICompetencyStore store, string code, CancellationToken ct) =>
        await store.FindTypeAsync(code, ct) ?? throw new CommandRejectedException("unknown_qualification", $"Qualification type {code} does not exist.");

    private static async Task RequireActivePersonAsync(IKernelUnitOfWork uow, PersonId person, CancellationToken ct)
    {
        var record = await uow.People.FindAsync(person, ct) ?? throw new CommandRejectedException("person_not_found", "Person not found.");
        if (!record.IsActive)
        {
            throw new CommandRejectedException("person_inactive", "Person is not active.");
        }
    }

    // True if 'from' depends (directly or indirectly) on 'target'.
    private static bool Reaches(IReadOnlyList<(string Code, string Prerequisite)> edges, string from, string target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([from]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == target)
            {
                return true;
            }

            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var edge in edges.Where(e => e.Code == current))
            {
                stack.Push(edge.Prerequisite);
            }
        }

        return false;
    }
}
