using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Microsoft.Extensions.Time.Testing;

namespace Forge.Kernel.Tests;

public class AccessServiceTests
{
    private static readonly ProgramId Falcon = new(Guid.Parse("018f0000-0000-7000-8000-000000000001"));
    private readonly InMemoryKernel kernel = new();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly AccessService service;

    public AccessServiceTests()
    {
        service = new AccessService(new CommandPipeline(kernel, new ForgeAuthorizer(), time), kernel, time);
    }

    private async Task<ForgePrincipal> AdminAsync()
    {
        await service.BootstrapAdministratorAsync("admin-sub", "Admin", CancellationToken.None);
        var admin = kernel.People.Values.Single(p => p.IdpSubject == "admin-sub");
        await using var uow = await kernel.BeginAsync(CancellationToken.None);
        return (await uow.People.LoadPrincipalAsync(admin.Id, CancellationToken.None))!;
    }

    private static CommandContext Ctx(ForgePrincipal p, string key, string? reason = "onboarding") =>
        new(p, Guid.Parse("018f0000-0000-7000-8000-00000000c0de"), key, CommandSource.Api, reason);

    private async Task<PersonId> RegisterAsync(ForgePrincipal admin, string subject)
    {
        var result = await service.RegisterPersonAsync(new RegisterPerson(subject, subject, DataMarking.None), Ctx(admin, "reg-" + subject), CancellationToken.None);
        return new PersonId(Guid.Parse(result.EntityId!));
    }

    [Fact]
    public async Task Bootstrap_runs_once_only()
    {
        Assert.True(await service.BootstrapAdministratorAsync("admin-sub", "Admin", CancellationToken.None));
        Assert.False(await service.BootstrapAdministratorAsync("someone-else", "Other", CancellationToken.None));
        Assert.Single(kernel.People);
    }

    [Fact]
    public async Task Granting_access_writes_state_event_and_audit_together()
    {
        var admin = await AdminAsync();
        var sarah = await RegisterAsync(admin, "sarah");
        var before = kernel.Audit.Count;

        var result = await service.GrantProgramAccessAsync(new GrantProgramAccess(sarah, Falcon), Ctx(admin, "grant-1"), CancellationToken.None);

        Assert.Equal("ok", result.Code);
        Assert.Contains((sarah, Falcon), kernel.ProgramAccess);
        Assert.Equal(nameof(ProgramAccessGranted), kernel.Outbox[^1].EventType);
        Assert.Equal(Falcon, kernel.Outbox[^1].ProgramId);
        var audit = kernel.Audit[^1].Entry;
        Assert.Equal(before + 1, kernel.Audit.Count);
        Assert.Equal("GrantProgramAccess", audit.Action);
        Assert.Equal("onboarding", audit.Reason);
        Assert.Null(audit.ProgramId);
    }

    [Fact]
    public async Task Repeating_a_command_with_the_same_key_replays_the_result_without_new_records()
    {
        var admin = await AdminAsync();
        var sarah = await RegisterAsync(admin, "sarah");
        var first = await service.AssignRoleAsync(new AssignRole(sarah, Roles.Auditor), Ctx(admin, "k1"), CancellationToken.None);
        var audits = kernel.Audit.Count;
        var events = kernel.Outbox.Count;

        var second = await service.AssignRoleAsync(new AssignRole(sarah, Roles.Auditor), Ctx(admin, "k1"), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(audits, kernel.Audit.Count);
        Assert.Equal(events, kernel.Outbox.Count);
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_request_is_a_conflict()
    {
        var admin = await AdminAsync();
        var sarah = await RegisterAsync(admin, "sarah");
        await service.AssignRoleAsync(new AssignRole(sarah, Roles.Auditor), Ctx(admin, "k1"), CancellationToken.None);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            service.GrantProgramAccessAsync(new GrantProgramAccess(sarah, Falcon), Ctx(admin, "k1"), CancellationToken.None));
    }

    [Fact]
    public async Task Nobody_can_change_their_own_access_and_the_attempt_is_audited()
    {
        var admin = await AdminAsync();

        var ex = await Assert.ThrowsAsync<CommandRejectedException>(() =>
            service.GrantProgramAccessAsync(new GrantProgramAccess(admin.PersonId, Falcon), Ctx(admin, "self"), CancellationToken.None));

        Assert.Equal("separation_of_duties", ex.Code);
        Assert.DoesNotContain((admin.PersonId, Falcon), kernel.ProgramAccess);
        Assert.Equal(AuditOutcomes.Rejected, kernel.Audit[^1].Entry.Outcome);
    }

    [Fact]
    public async Task Unauthorized_caller_is_denied_audited_and_signalled()
    {
        var admin = await AdminAsync();
        var sarahId = await RegisterAsync(admin, "sarah");
        await using var uow = await kernel.BeginAsync(CancellationToken.None);
        var sarah = (await uow.People.LoadPrincipalAsync(sarahId, CancellationToken.None))!;

        var ex = await Assert.ThrowsAsync<ForgeAuthorizationException>(() =>
            service.AssignRoleAsync(new AssignRole(admin.PersonId, Roles.Auditor), Ctx(sarah, "x"), CancellationToken.None));

        Assert.False(ex.Decision.IsPermitted);
        Assert.Equal(AuditOutcomes.Denied, kernel.Audit[^1].Entry.Outcome);
        Assert.Equal(nameof(AuthorizationDenied), kernel.Outbox[^1].EventType);
    }

    [Fact]
    public async Task Access_changes_require_a_reason()
    {
        var admin = await AdminAsync();
        var sarah = await RegisterAsync(admin, "sarah");

        var ex = await Assert.ThrowsAsync<CommandRejectedException>(() =>
            service.AssignRoleAsync(new AssignRole(sarah, Roles.Auditor), Ctx(admin, "r", reason: " "), CancellationToken.None));
        Assert.Equal("reason_required", ex.Code);
        Assert.DoesNotContain((sarah, Roles.Auditor), kernel.RoleAssignments);
    }

    [Fact]
    public async Task Unknown_roles_are_rejected()
    {
        var admin = await AdminAsync();
        var sarah = await RegisterAsync(admin, "sarah");
        var ex = await Assert.ThrowsAsync<CommandRejectedException>(() =>
            service.AssignRoleAsync(new AssignRole(sarah, "SuperUser"), Ctx(admin, "u"), CancellationToken.None));
        Assert.Equal("unknown_role", ex.Code);
    }

    [Fact]
    public async Task An_administrator_can_remove_another_administrator_but_not_themselves()
    {
        var admin = await AdminAsync();
        var bob = await RegisterAsync(admin, "bob");
        await service.AssignRoleAsync(new AssignRole(bob, Roles.SecurityAdministrator), Ctx(admin, "a1"), CancellationToken.None);
        await using var uow = await kernel.BeginAsync(CancellationToken.None);
        var bobPrincipal = (await uow.People.LoadPrincipalAsync(bob, CancellationToken.None))!;

        // Bob removes the original admin: allowed, one admin remains.
        await service.RevokeRoleAsync(new RevokeRole(admin.PersonId, Roles.SecurityAdministrator), Ctx(bobPrincipal, "r1"), CancellationToken.None);
        Assert.DoesNotContain((admin.PersonId, Roles.SecurityAdministrator), kernel.RoleAssignments);
        Assert.Contains((bob, Roles.SecurityAdministrator), kernel.RoleAssignments);

        var ex = await Assert.ThrowsAsync<CommandRejectedException>(() =>
            service.RevokeRoleAsync(new RevokeRole(bob, Roles.SecurityAdministrator), Ctx(bobPrincipal, "r2"), CancellationToken.None));
        Assert.Equal("separation_of_duties", ex.Code);
    }

    [Fact]
    public async Task Audit_chain_stays_intact_across_commands()
    {
        var admin = await AdminAsync();
        var sarah = await RegisterAsync(admin, "sarah");
        await service.AssignRoleAsync(new AssignRole(sarah, Roles.Auditor), Ctx(admin, "c1"), CancellationToken.None);
        await Assert.ThrowsAsync<CommandRejectedException>(() =>
            service.AssignRoleAsync(new AssignRole(admin.PersonId, Roles.Auditor), Ctx(admin, "c2"), CancellationToken.None));

        Assert.Null(Forge.Kernel.Audit.AuditChain.FindFirstBreak(kernel.Audit));
        Assert.True(kernel.Audit.Count >= 4);
    }
}
