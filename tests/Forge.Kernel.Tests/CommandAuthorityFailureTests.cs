using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;

namespace Forge.Kernel.Tests;

public sealed class CommandAuthorityFailureTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_linked_source_failure_retains_actual_owner_scope_without_reserving_or_running_the_command(bool denied)
    {
        var kernel = new InMemoryKernel();
        var program = new ProgramId(Guid.Parse("018f0000-0000-7000-8000-000000000a11"));
        var markings = (DataMarking)1;
        var owner = new ResourceRef("ProtectedSource", "ACTUAL", program, markings, FacilityId: "A");
        var resolver = new OwnerScopeResolver(owner);
        var time = TimeProvider.System;
        var pipeline = new CommandPipeline(kernel, new ForgeAuthorizer(time), time, resolver);
        var principal = new ForgePrincipal(new PersonId(Guid.Parse("018f0000-0000-7000-8000-000000000b11")),
            "Authorized source actor", true, new HashSet<string>(StringComparer.Ordinal) { Roles.SecurityAdministrator },
            new HashSet<ProgramId> { program }, markings);
        var context = new CommandContext(principal, Guid.Parse("018f0000-0000-7000-8000-000000000c11"),
            "linked-source-failure", CommandSource.Api, "Record the actual owning source");
        var spec = new CommandSpec("Test", "LinkedSourceAction", Permissions.AccessManage,
            ResourceRef.Of("ProtectedSource", "ACTUAL"), ReasonRequired: true);
        var ran = false;
        Task<CommandResult> Execute() => pipeline.ExecuteAsync(spec, new { value = "unchanged" }, context,
            (_, _) => { ran = true; throw new InvalidOperationException("A failed authority callback cannot run the body."); },
            CancellationToken.None, (_, _) => Task.FromException<CommandAuthority>(denied
                ? new ForgeAuthorizationException(new(false, ["Linked source access was withdrawn."]))
                : new CommandRejectedException("linked_source_missing", "The linked source is unavailable.")));

        if (denied) await Assert.ThrowsAsync<ForgeAuthorizationException>(Execute);
        else await Assert.ThrowsAsync<CommandRejectedException>(Execute);

        Assert.False(ran);
        Assert.Empty(kernel.Idempotency);
        Assert.Empty(kernel.People);
        Assert.Equal(1, resolver.Calls);
        var audit = Assert.Single(kernel.Audit).Entry;
        Assert.Equal((owner.Type, owner.Id, program, markings, context.Actor, spec.CommandType),
            (audit.EntityType, audit.EntityId, audit.ProgramId, audit.Markings, audit.Actor, audit.Action));
        Assert.Equal(denied ? AuditOutcomes.Denied : AuditOutcomes.Rejected, audit.Outcome);
        if (denied)
        {
            var notification = Assert.Single(kernel.Outbox);
            Assert.Equal((program, markings), (notification.ProgramId, notification.Markings));
        }
        else Assert.Empty(kernel.Outbox);
        Assert.Null(Forge.Kernel.Audit.AuditChain.FindFirstBreak(kernel.Audit));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failure_after_authority_resolution_keeps_its_selected_scope_without_replacing_it(bool additionalSourceDenied)
    {
        var kernel = new InMemoryKernel();
        var program = new ProgramId(Guid.Parse("018f0000-0000-7000-8000-000000000d11"));
        var owning = new ResourceRef("ActualOwner", "OWNER", program, DataMarking.None);
        var additional = new ResourceRef("LinkedSource", "PRIVATE", program, (DataMarking)1);
        var resolver = new OwnerScopeResolver(ResourceRef.Of("UnexpectedFallback", "WRONG"));
        var pipeline = new CommandPipeline(kernel, new ForgeAuthorizer(), TimeProvider.System, resolver);
        var principal = new ForgePrincipal(new PersonId(Guid.Parse("018f0000-0000-7000-8000-000000000e11")),
            "Current actor", true, new HashSet<string>(StringComparer.Ordinal) { Roles.SecurityAdministrator },
            new HashSet<ProgramId> { program }, DataMarking.None);
        var context = new CommandContext(principal, Guid.Parse("018f0000-0000-7000-8000-000000000f11"),
            "resolved-failure", CommandSource.Api, "Preserve the actual source decision");
        var spec = new CommandSpec("Test", "ResolvedAction", Permissions.AccessManage, ResourceRef.Of("Requested", "INPUT"));
        var bodyRan = false;
        Task<CommandResult> Execute() => pipeline.ExecuteAsync(spec, new { value = 1 }, context,
            (_, _) => { bodyRan = true; throw new CommandRejectedException("invalid_state", "The owning state changed."); },
            CancellationToken.None, (_, _) => Task.FromResult(new CommandAuthority(owning,
                AdditionalResources: additionalSourceDenied ? [additional] : [])));

        if (additionalSourceDenied) await Assert.ThrowsAsync<ForgeAuthorizationException>(Execute);
        else await Assert.ThrowsAsync<CommandRejectedException>(Execute);

        Assert.Equal(!additionalSourceDenied, bodyRan);
        Assert.Equal(0, resolver.Calls);
        Assert.Empty(kernel.Idempotency);
        var selected = additionalSourceDenied ? additional : owning;
        var audit = Assert.Single(kernel.Audit).Entry;
        Assert.Equal((selected.Type, selected.Id, selected.Program, selected.Markings),
            (audit.EntityType, audit.EntityId, audit.ProgramId, audit.Markings));
        Assert.Null(Forge.Kernel.Audit.AuditChain.FindFirstBreak(kernel.Audit));
    }

    private sealed class OwnerScopeResolver(ResourceRef owner) : ICommandSourceResolver
    {
        public int Calls { get; private set; }

        public Task<CommandAuthority> ResolveAsync(IKernelUnitOfWork uow, CommandSpec spec, CommandContext context,
            DateTimeOffset now, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new CommandAuthority(owner with { Markings = owner.Markings | spec.Resource.Markings }));
        }
    }
}
