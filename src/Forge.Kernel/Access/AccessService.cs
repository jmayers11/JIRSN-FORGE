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

    public Task<CommandResult> DeactivateIdentityAsync(DeactivateIdentity command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return pipeline.ExecuteAsync(Spec(nameof(DeactivateIdentity), null), command, context, async (uow, now) =>
        {
            var subject = command.IdpSubject.Trim();
            if (subject.Length == 0 || command.Provider != "Nexus")
            {
                throw new CommandRejectedException("invalid_provisioning_request", "A provider subject and the configured identity provider are required.");
            }

            var person = await uow.People.FindBySubjectAsync(subject, ct);
            if (person is null || !person.IsActive)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(subject, "Identity is not registered or is already inactive."));
            }

            if (!await uow.People.SetActiveAsync(person.Id, false, ct))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(person.Id.ToString(), "Identity is already inactive."));
            }
            var id = person.Id.ToString();
            return new CommandOutcome(
                CommandResult.Ok(id, "Identity deactivated in Forge."),
                [Event(new IdentityDeactivated(person.Id.Value, person.IdpSubject, command.Provider), context, id)],
                new AuditChange(PersonEntity, id, "active", "inactive"));
        }, ct);
    }

    public Task<CommandResult> AssignRoleAsync(AssignRole command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return pipeline.ExecuteAsync(Spec(nameof(AssignRole), command.PersonId), command, context, async (uow, now) =>
        {
            if (!await uow.People.HasRoleAsync(command.Role, ct))
            {
                throw new CommandRejectedException("unknown_role", $"Unknown or inactive role '{command.Role}'.");
            }

            if (command.ExpiresAt is { } expiresAt && (expiresAt <= now || expiresAt > now.AddYears(1)))
            {
                throw new CommandRejectedException("invalid_expiry", "Delegated role authority must expire in the future and within one year.");
            }
            await RequireNotSelfAsync(uow, context, command.PersonId, "access.self_role_change", ct);
            await RequireActivePersonAsync(uow, command.PersonId, ct);

            var id = command.PersonId.ToString();
            if (!await uow.People.AddRoleAsync(command.PersonId, command.Role, now, context.Actor, command.ExpiresAt, ct))
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
            if (!await uow.People.HasRoleAsync(command.Role, ct))
            {
                throw new CommandRejectedException("unknown_role", $"Unknown or inactive role '{command.Role}'.");
            }

            // Only an active administrator can revoke, and never from themselves, so at least one
            // administrator always remains.
            await RequireNotSelfAsync(uow, context, command.PersonId, "access.self_role_change", ct);

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

    public Task<CommandResult> ChangeRolePermissionAsync(ChangeRolePermission command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return pipeline.ExecuteAsync(Spec(nameof(ChangeRolePermission), null), command, context, async (uow, now) =>
        {
            if (string.Equals(command.Role, Roles.SecurityAdministrator, StringComparison.Ordinal))
            {
                throw new CommandRejectedException("protected_role", "The SecurityAdministrator role is protected from permission changes.");
            }

            if (!await uow.People.HasRoleAsync(command.Role, ct))
            {
                throw new CommandRejectedException("unknown_role", $"Unknown or inactive role '{command.Role}'.");
            }

            if (!await uow.People.HasPermissionAsync(command.Permission, ct))
            {
                throw new CommandRejectedException("unknown_permission", $"Unknown permission '{command.Permission}'.");
            }

            if (!command.Granted && command.Permission == Permissions.AccessManage)
            {
                throw new CommandRejectedException("protected_permission", "Access management cannot be removed through role policy.");
            }

            var changed = command.Granted
                ? await uow.People.AddRolePermissionAsync(command.Role, command.Permission, now, context.Actor, ct)
                : await uow.People.RemoveRolePermissionAsync(command.Role, command.Permission, ct);
            if (!changed)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(command.Role, "Role permission already has that value."));
            }

            var id = command.Role + ":" + command.Permission;
            return new CommandOutcome(
                CommandResult.Ok(id, command.Granted ? "Role permission granted." : "Role permission revoked."),
                [Event(new RolePermissionChanged(command.Role, command.Permission, command.Granted), context, id)],
                new AuditChange("RolePermission", id, command.Granted ? null : command.Permission, command.Granted ? command.Permission : null));
        }, ct);
    }

    public Task<CommandResult> ChangeRolePermissionScopeAsync(ChangeRolePermissionScope command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return pipeline.ExecuteAsync(Spec(nameof(ChangeRolePermissionScope), null), command, context, async (uow, now) =>
        {
            if (command.Role == Roles.SecurityAdministrator)
            {
                throw new CommandRejectedException("protected_role", "The SecurityAdministrator role cannot be scoped.");
            }

            if (!await uow.People.HasRoleAsync(command.Role, ct) || !await uow.People.HasPermissionAsync(command.Permission, ct))
            {
                throw new CommandRejectedException("unknown_policy", "The role or permission is unknown or inactive.");
            }

            var normalizedId = command.ScopeId.Trim();
            var validScope = command.ScopeType switch
            {
                "program" => Guid.TryParse(normalizedId, out _),
                "facility" or "work_center" or "contract" or "customer" or "shift" => normalizedId.Length is > 0 and <= 120,
                _ => false,
            };
            if (!validScope)
            {
                throw new CommandRejectedException("invalid_scope", "Scope must be a known dimension with a valid identifier.");
            }

            if (command.ScopeType == "program")
            {
                normalizedId = Guid.Parse(normalizedId).ToString("D");
            }

            var changed = false;
            if (command.Granted)
            {
                _ = await uow.People.AddRolePermissionAsync(command.Role, command.Permission, now, context.Actor, ct);
                changed = await uow.People.AddRolePermissionScopeAsync(command.Role, command.Permission, command.ScopeType, normalizedId, ct);
            }
            else
            {
                changed = await uow.People.RemoveRolePermissionScopeAsync(command.Role, command.Permission, command.ScopeType, normalizedId, ct);
                if (changed && await uow.People.CountRolePermissionScopesAsync(command.Role, command.Permission, ct) == 0)
                {
                    _ = await uow.People.RemoveRolePermissionAsync(command.Role, command.Permission, ct);
                }
            }

            if (!changed)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(command.Role, "Role permission scope already has that value."));
            }

            var id = $"{command.Role}:{command.Permission}:{command.ScopeType}:{normalizedId}";
            return new CommandOutcome(
                CommandResult.Ok(id, command.Granted ? "Scoped authority granted." : "Scoped authority revoked."),
                [Event(new RolePermissionScopeChanged(command.Role, command.Permission, command.ScopeType, normalizedId, command.Granted), context, id)],
                new AuditChange("RolePermissionScope", id, command.Granted ? null : normalizedId, command.Granted ? normalizedId : null));
        }, ct);
    }

    public Task<CommandResult> CreateRoleAsync(CreateRole command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return pipeline.ExecuteAsync(Spec(nameof(CreateRole), null), command, context, async (uow, now) =>
        {
            var role = command.Role.Trim();
            if (role.Length is < 2 or > 80 || role.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            {
                throw new CommandRejectedException("invalid_role", "Role names must be 2–80 ASCII letters, digits, underscores or hyphens.");
            }

            var displayName = command.DisplayName.Trim();
            if (displayName.Length is < 2 or > 120)
            {
                throw new CommandRejectedException("invalid_role", "A role display name of 2–120 characters is required.");
            }

            var permissions = command.Permissions.Distinct(StringComparer.Ordinal).ToArray();
            if (permissions.Length == 0)
            {
                throw new CommandRejectedException("invalid_role", "A role must grant at least one permission.");
            }

            foreach (var permission in permissions)
            {
                if (!await uow.People.HasPermissionAsync(permission, ct))
                {
                    throw new CommandRejectedException("unknown_permission", $"Unknown permission '{permission}'.");
                }
            }

            if (!await uow.People.CreateRoleAsync(role, displayName, permissions, now, context.Actor, ct))
            {
                throw new CommandRejectedException("role_exists", $"Role '{role}' already exists.");
            }

            return new CommandOutcome(
                CommandResult.Ok(role, "Role created."),
                [Event(new RoleCreated(role, displayName, permissions), context, role)],
                new AuditChange("Role", role, null, CanonicalJson.Serialize(new { role, displayName, permissions })));
        }, ct);
    }

    public Task<CommandResult> ChangeSeparationPolicyAsync(ChangeSeparationPolicy command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return pipeline.ExecuteAsync(Spec(nameof(ChangeSeparationPolicy), null), command, context, async (uow, _) =>
        {
            if (command.Policy is not ("access.self_role_change" or "access.self_program_change"))
            {
                throw new CommandRejectedException("unknown_policy", $"Unknown separation policy '{command.Policy}'.");
            }

            if (command.Policy == "access.self_role_change" && !command.Enabled)
            {
                throw new CommandRejectedException("protected_policy", "Self role changes remain prohibited to protect administrative access.");
            }

            if (!await uow.People.SetSeparationPolicyAsync(command.Policy, command.Enabled, ct))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(command.Policy, "Policy already has that value."));
            }

            return new CommandOutcome(
                CommandResult.Ok(command.Policy, "Separation policy updated."),
                [Event(new SeparationPolicyChanged(command.Policy, command.Enabled), context, command.Policy)],
                new AuditChange("SeparationPolicy", command.Policy, (!command.Enabled).ToString(), command.Enabled.ToString()));
        }, ct);
    }

    public Task<CommandResult> ChangeApprovalThresholdAsync(ChangeApprovalThreshold command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return pipeline.ExecuteAsync(Spec(nameof(ChangeApprovalThreshold), null), command, context, async (uow, now) =>
        {
            if (!await uow.People.HasPermissionAsync(command.Permission, ct))
            {
                throw new CommandRejectedException("unknown_permission", $"Unknown permission '{command.Permission}'.");
            }

            var currency = command.Currency?.Trim().ToUpperInvariant();
            if (command.MaximumAmount is { } amount && (amount < 0 || amount > 1_000_000_000_000_000m || currency is null || currency.Length != 3 || currency.Any(c => !char.IsAsciiLetter(c))))
            {
                throw new CommandRejectedException("invalid_threshold", "Approval thresholds require a non-negative amount and a three-letter currency code.");
            }

            if (!await uow.People.SetApprovalThresholdAsync(command.Permission, command.MaximumAmount, command.MaximumAmount is null ? null : currency, now, context.Actor, ct))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(command.Permission, "Approval threshold already has that value."));
            }

            return new CommandOutcome(
                CommandResult.Ok(command.Permission, command.MaximumAmount is null ? "Approval threshold removed." : "Approval threshold updated."),
                [Event(new ApprovalThresholdChanged(command.Permission, command.MaximumAmount, currency), context, command.Permission)],
                new AuditChange("ApprovalThreshold", command.Permission, null, CanonicalJson.Serialize(new { command.MaximumAmount, currency })));
        }, ct);
    }

    public Task<CommandResult> GrantProgramAccessAsync(GrantProgramAccess command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return pipeline.ExecuteAsync(Spec(nameof(GrantProgramAccess), command.PersonId), command, context, async (uow, now) =>
        {
            await RequireNotSelfAsync(uow, context, command.PersonId, "access.self_program_change", ct);
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
            await RequireNotSelfAsync(uow, context, command.PersonId, "access.self_program_change", ct);

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

        await uow.People.AddRoleAsync(person.Id, Roles.SecurityAdministrator, now, SystemActors.Bootstrap, null, ct);
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

    private static async Task RequireNotSelfAsync(IKernelUnitOfWork uow, CommandContext context, PersonId target, string policy, CancellationToken ct)
    {
        if (context.Principal.PersonId == target && await uow.People.IsSeparationPolicyEnabledAsync(policy, ct))
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
