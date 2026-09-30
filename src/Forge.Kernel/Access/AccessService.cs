using Forge.Kernel.Audit;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Kernel.Access;

/// <summary>
/// Manages who can do what in Forge: people known to Forge, their functional roles and their
/// program access. Every change goes through the <see cref="CommandPipeline"/>: authorized,
/// reasoned, idempotent, audited and emitted as an event in one transaction.
/// </summary>
public sealed class AccessService(CommandPipeline pipeline, IKernelUnitOfWorkFactory unitOfWork, TimeProvider time)
{
    public const string SourceContext = "Kernel";
    private const string PersonEntity = "Person";

    public Task<CommandResult> RegisterPersonAsync(RegisterPerson command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return pipeline.ExecuteAsync(Spec(nameof(RegisterPerson), null), command, context, async (uow, now) =>
        {
            if (string.IsNullOrWhiteSpace(command.IdpSubject) || string.IsNullOrWhiteSpace(command.DisplayName))
            {
                throw new CommandRejectedException("invalid", "Identity subject and display name are required.");
            }

            var subject = command.IdpSubject.Trim();
            if (await uow.People.FindBySubjectAsync(subject, ct) is not null)
            {
                throw new CommandRejectedException("subject_exists", "A person with this identity subject is already registered.");
            }

            var person = new PersonRecord(new PersonId(ForgeIds.New(time)), subject, command.DisplayName.Trim(), true, command.Markings);
            await uow.People.InsertAsync(person, now, context.Actor, ct);

            var id = person.Id.ToString();
            return new CommandOutcome(
                CommandResult.Ok(id, "Person registered."),
                [Event(new PersonRegistered(person.Id.Value, person.IdpSubject, person.DisplayName, person.Markings.ToString()), context, id)],
                new AuditChange(PersonEntity, id, null, CanonicalJson.Serialize(new { person.IdpSubject, person.DisplayName, markings = person.Markings.ToString() })));
        }, ct);
    }

    public Task<CommandResult> AssignRoleAsync(AssignRole command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return pipeline.ExecuteAsync(Spec(nameof(AssignRole), command.PersonId), command, context, async (uow, now) =>
        {
            RequireKnownRole(command.Role);
            RequireNotSelf(context, command.PersonId);
            await RequireActivePersonAsync(uow, command.PersonId, ct);

            var id = command.PersonId.ToString();
            if (!await uow.People.AddRoleAsync(command.PersonId, command.Role, now, context.Actor, ct))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Role already assigned."));
            }

            return new CommandOutcome(
                CommandResult.Ok(id, $"Role {command.Role} assigned."),
                [Event(new RoleAssigned(command.PersonId.Value, command.Role), context, id)],
                new AuditChange(PersonEntity, id, null, CanonicalJson.Serialize(new { role = command.Role })));
        }, ct);
    }

    public Task<CommandResult> RevokeRoleAsync(RevokeRole command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return pipeline.ExecuteAsync(Spec(nameof(RevokeRole), command.PersonId), command, context, async (uow, _) =>
        {
            RequireKnownRole(command.Role);

            // Only an active administrator can revoke, and never from themselves, so at least one
            // administrator always remains.
            RequireNotSelf(context, command.PersonId);

            var id = command.PersonId.ToString();
            if (!await uow.People.RemoveRoleAsync(command.PersonId, command.Role, ct))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Role was not assigned."));
            }

            return new CommandOutcome(
                CommandResult.Ok(id, $"Role {command.Role} revoked."),
                [Event(new RoleRevoked(command.PersonId.Value, command.Role), context, id)],
                new AuditChange(PersonEntity, id, CanonicalJson.Serialize(new { role = command.Role }), null));
        }, ct);
    }

    public Task<CommandResult> GrantProgramAccessAsync(GrantProgramAccess command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return pipeline.ExecuteAsync(Spec(nameof(GrantProgramAccess), command.PersonId), command, context, async (uow, now) =>
        {
            RequireNotSelf(context, command.PersonId);
            await RequireActivePersonAsync(uow, command.PersonId, ct);

            var id = command.PersonId.ToString();
            if (!await uow.People.AddProgramAccessAsync(command.PersonId, command.ProgramId, now, context.Actor, ct))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Program access already granted."));
            }

            // Access-management audit is not program-segregated: administrators must see every grant they make.
            return new CommandOutcome(
                CommandResult.Ok(id, "Program access granted."),
                [Event(new ProgramAccessGranted(command.PersonId.Value, command.ProgramId.Value), context, id, command.ProgramId)],
                new AuditChange(PersonEntity, id, null, CanonicalJson.Serialize(new { programId = command.ProgramId.Value })));
        }, ct);
    }

    public Task<CommandResult> RevokeProgramAccessAsync(RevokeProgramAccess command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return pipeline.ExecuteAsync(Spec(nameof(RevokeProgramAccess), command.PersonId), command, context, async (uow, _) =>
        {
            RequireNotSelf(context, command.PersonId);

            var id = command.PersonId.ToString();
            if (!await uow.People.RemoveProgramAccessAsync(command.PersonId, command.ProgramId, ct))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Program access was not granted."));
            }

            return new CommandOutcome(
                CommandResult.Ok(id, "Program access revoked."),
                [Event(new ProgramAccessRevoked(command.PersonId.Value, command.ProgramId.Value), context, id, command.ProgramId)],
                new AuditChange(PersonEntity, id, CanonicalJson.Serialize(new { programId = command.ProgramId.Value }), null));
        }, ct);
    }

    /// <summary>
    /// Creates the first security administrator from deployment configuration. Does nothing once any
    /// active administrator exists, so it cannot be used to add administrators later.
    /// </summary>
    public async Task<bool> BootstrapAdministratorAsync(string idpSubject, string displayName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idpSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        await using var uow = await unitOfWork.BeginAsync(ct);
        if (await uow.People.CountActiveWithRoleAsync(Roles.SecurityAdministrator, ct) > 0)
        {
            return false;
        }

        var now = time.GetUtcNow();
        var correlation = ForgeIds.New(time);
        const string reason = "Initial administrator from deployment configuration";
        var person = await uow.People.FindBySubjectAsync(idpSubject, ct);
        if (person is null)
        {
            person = new PersonRecord(new PersonId(ForgeIds.New(time)), idpSubject, displayName, true, DataMarking.None);
            await uow.People.InsertAsync(person, now, SystemActors.Bootstrap, ct);
            await uow.Outbox.EnqueueAsync(
                EventFactory.Create(new PersonRegistered(person.Id.Value, person.IdpSubject, person.DisplayName, person.Markings.ToString()), SystemActors.Bootstrap, correlation, reason, time, SourceContext, PersonEntity, person.Id.ToString()),
                ct);
        }

        await uow.People.AddRoleAsync(person.Id, Roles.SecurityAdministrator, now, SystemActors.Bootstrap, ct);
        await uow.Outbox.EnqueueAsync(
            EventFactory.Create(new RoleAssigned(person.Id.Value, Roles.SecurityAdministrator), SystemActors.Bootstrap, correlation, reason, time, SourceContext, PersonEntity, person.Id.ToString()),
            ct);
        await uow.Audit.AppendAsync(
            new AuditEntry
            {
                AuditId = ForgeIds.New(time),
                RecordedAt = now,
                Actor = SystemActors.Bootstrap,
                Action = "BootstrapAdministrator",
                Outcome = AuditOutcomes.Succeeded,
                EntityType = PersonEntity,
                EntityId = person.Id.ToString(),
                NewValue = CanonicalJson.Serialize(new { role = Roles.SecurityAdministrator, person.IdpSubject }),
                Reason = reason,
                Source = nameof(CommandSource.System),
                CorrelationId = correlation,
            },
            ct);
        await uow.CommitAsync(ct);
        return true;
    }

    private static CommandSpec Spec(string commandType, PersonId? target) =>
        new(SourceContext, commandType, Permissions.AccessManage, ResourceRef.Of(PersonEntity, target), ReasonRequired: true);

    private EventEnvelope Event<TEvent>(TEvent payload, CommandContext context, string entityId, ProgramId? program = null)
        where TEvent : IDomainEvent =>
        pipeline.Event(payload, context, SourceContext, PersonEntity, entityId, program);

    private static void RequireKnownRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role) || !Roles.IsKnown(role))
        {
            throw new CommandRejectedException("unknown_role", $"Unknown role '{role}'. Known roles: {string.Join(", ", Roles.All)}.");
        }
    }

    private static void RequireNotSelf(CommandContext context, PersonId target)
    {
        if (context.Principal.PersonId == target)
        {
            throw new CommandRejectedException("separation_of_duties", "You cannot change your own roles or program access.");
        }
    }

    private static async Task RequireActivePersonAsync(IKernelUnitOfWork uow, PersonId id, CancellationToken ct)
    {
        var person = await uow.People.FindAsync(id, ct)
            ?? throw new CommandRejectedException("person_not_found", "Person not found.");
        if (!person.IsActive)
        {
            throw new CommandRejectedException("person_inactive", "Person is not active.");
        }
    }
}
