using Forge.Kernel.Audit;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Kernel.Access;

public sealed record DelegationProposed(Guid DelegationId, Guid Grantor, Guid Recipient, IReadOnlyList<string> Permissions,
    IReadOnlyList<DelegationScope> Scopes, DateTimeOffset StartsAt, DateTimeOffset ExpiresAt) : IDomainEvent
{
    public static string EventType => nameof(DelegationProposed);
    public static int SchemaVersion => 1;
}
public sealed record DelegationApproved(Guid DelegationId, Guid Approver) : IDomainEvent
{
    public static string EventType => nameof(DelegationApproved);
    public static int SchemaVersion => 1;
}
public sealed record DelegationRevoked(Guid DelegationId) : IDomainEvent
{
    public static string EventType => nameof(DelegationRevoked);
    public static int SchemaVersion => 1;
}

/// <summary>Temporary permission lending uses the existing command, audit, event and authorization path.</summary>
public sealed class DelegationService(CommandPipeline pipeline, IKernelUnitOfWorkFactory unitOfWork, TimeProvider time)
{
    private const string Entity = "AuthorityDelegation";

    public async Task<IReadOnlyList<AuthorityDelegation>> ListAsync(ForgePrincipal principal, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return await uow.Store<IDelegationStore>().ListAsync(principal.HasPermission(Permissions.AccessManage) ? null : principal.PersonId, ct);
    }

    public Task<CommandResult> ProposeAsync(ProposeDelegation command, CommandContext context, CancellationToken ct)
    {
        var resource = DelegationAuthority.Resource(command.Scopes);
        return pipeline.ExecuteAsync(new CommandSpec("Kernel", nameof(ProposeDelegation), Permissions.DelegationPropose, resource, ReasonRequired: true),
            command, context, async (uow, now) =>
            {
                if (command.Recipient == context.Principal.PersonId)
                    throw new CommandRejectedException("self_delegation", "A delegation must name a different recipient.");
                if (command.StartsAt < now.AddMinutes(-5) || command.ExpiresAt <= command.StartsAt
                    || command.ExpiresAt <= now || command.ExpiresAt > now.AddYears(1))
                    throw new CommandRejectedException("invalid_delegation_interval", "The start may be at most five minutes in the past; expiry must follow start, be in the future and within one year.");
                var permissions = command.Permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                if (permissions.Count is 0 or > 50)
                    throw new CommandRejectedException("invalid_delegation_permissions", "Select between one and fifty business-action permissions.");
                var scopes = NormalizeScopes(command.Scopes);
                var recipient = await uow.People.FindAsync(command.Recipient, ct);
                if (recipient is not { IsActive: true })
                    throw new CommandRejectedException("inactive_recipient", "The recipient must be an active registered person.");
                var grantor = await uow.People.LoadPrincipalAsync(context.Principal.PersonId, ct);
                foreach (var action in permissions)
                {
                    if (!await uow.People.HasPermissionAsync(action, ct))
                        throw new CommandRejectedException("unknown_permission", $"Unknown permission '{action}'.");
                    if (grantor is null || !DelegationAuthority.CanLend(grantor, action, scopes))
                        throw new CommandRejectedException("delegation_escalation", $"The grantor's own current role authority does not cover '{action}' in every requested context. Access management and delegation administration cannot be delegated.");
                }
                var record = new AuthorityDelegation(ForgeIds.New(time), context.Principal.PersonId, command.Recipient,
                    permissions, scopes, command.StartsAt, command.ExpiresAt, DelegationStatus.PendingApproval, now, context.Reason!);
                await uow.Store<IDelegationStore>().InsertAsync(record, ct);
                return Outcome(record, null, new DelegationProposed(record.DelegationId, record.Grantor.Value,
                    record.Recipient.Value, permissions, scopes, record.StartsAt, record.ExpiresAt), context, "Delegation proposed; independent approval is required.");
            }, ct);
    }

    public Task<CommandResult> ApproveAsync(ApproveDelegation command, CommandContext context, CancellationToken ct) =>
        pipeline.ExecuteAsync(Spec(nameof(ApproveDelegation), command.DelegationId), command, context, async (uow, now) =>
        {
            var store = uow.Store<IDelegationStore>();
            var record = await RequireAsync(store, command.DelegationId, ct);
            if (context.Principal.PersonId == record.Grantor || context.Principal.PersonId == record.Recipient)
                throw new CommandRejectedException("delegation_approval_separation", "Neither grantor nor recipient may approve this delegation.");
            if (record.Status != DelegationStatus.PendingApproval || record.ExpiresAt <= now)
                throw new CommandRejectedException("delegation_not_pending", "Only a pending, unexpired delegation can be approved.");
            var grantor = await uow.People.LoadPrincipalAsync(record.Grantor, ct);
            var recipient = await uow.People.FindAsync(record.Recipient, ct);
            if (recipient is not { IsActive: true } || grantor is null
                || record.Permissions.Any(permission => !DelegationAuthority.CanLend(grantor, permission, record.Scopes)))
                throw new CommandRejectedException("delegation_authority_changed", "The recipient or grantor is inactive, or the grantor's current authority no longer covers the request.");
            if (!await store.ApproveAsync(record.DelegationId, context.Principal.PersonId, now, context.Reason!, ct))
                throw new CommandRejectedException("delegation_not_pending", "The delegation has already been decided.");
            var approved = record with { Status = DelegationStatus.Approved, Approver = context.Principal.PersonId, ApprovedAt = now, ApprovalReason = context.Reason };
            return Outcome(approved, record, new DelegationApproved(record.DelegationId, context.Principal.PersonId.Value), context, "Temporary delegation approved.");
        }, ct);

    public Task<CommandResult> RevokeAsync(RevokeDelegation command, CommandContext context, CancellationToken ct) =>
        pipeline.ExecuteAsync(Spec(nameof(RevokeDelegation), command.DelegationId), command, context, async (uow, now) =>
        {
            var store = uow.Store<IDelegationStore>();
            var record = await RequireAsync(store, command.DelegationId, ct);
            if (!await store.RevokeAsync(record.DelegationId, now, context.Reason!, ct))
                return CommandOutcome.NoChange(CommandResult.NoChange(record.DelegationId.ToString("D"), "Delegation was already revoked."));
            return Outcome(record with { Status = DelegationStatus.Revoked, RevokedAt = now, RevocationReason = context.Reason },
                record, new DelegationRevoked(record.DelegationId), context, "Delegation revoked.");
        }, ct);

    private static CommandSpec Spec(string name, Guid id) => new("Kernel", name, Permissions.AccessManage, ResourceRef.Of(Entity, id), ReasonRequired: true);
    private CommandOutcome Outcome<T>(AuthorityDelegation record, AuthorityDelegation? old, T evt, CommandContext context, string message) where T : IDomainEvent =>
        new(CommandResult.Ok(record.DelegationId.ToString("D"), message),
            [pipeline.Event(evt, context, "Kernel", Entity, record.DelegationId.ToString("D"))],
            new AuditChange(Entity, record.DelegationId.ToString("D"), old is null ? null : CanonicalJson.Serialize(old), CanonicalJson.Serialize(record)));
    private static async Task<AuthorityDelegation> RequireAsync(IDelegationStore store, Guid id, CancellationToken ct) =>
        await store.FindAsync(id, ct) ?? throw new CommandRejectedException("delegation_not_found", "Delegation does not exist.");

    private static IReadOnlyList<DelegationScope> NormalizeScopes(IReadOnlyList<DelegationScope> scopes)
    {
        if (scopes.Count > 6 || scopes.Select(scope => scope.ScopeType).Distinct(StringComparer.Ordinal).Count() != scopes.Count)
            throw new CommandRejectedException("invalid_delegation_scope", "Each scope dimension may appear once; dimensions combine to restrict the same action.");
        var result = new List<DelegationScope>();
        foreach (var scope in scopes)
        {
            var id = scope.ScopeId?.Trim() ?? string.Empty;
            var valid = scope.ScopeType switch
            {
                "program" => Guid.TryParse(id, out _),
                "facility" or "work_center" or "contract" or "customer" or "shift" => id.Length is > 0 and <= 120,
                _ => false,
            };
            if (!valid) throw new CommandRejectedException("invalid_delegation_scope", "Use a known scope dimension and a valid identifier, or no scopes for global authority.");
            result.Add(new(scope.ScopeType, scope.ScopeType == "program" ? Guid.Parse(id).ToString("D") : id));
        }
        return result;
    }
}
