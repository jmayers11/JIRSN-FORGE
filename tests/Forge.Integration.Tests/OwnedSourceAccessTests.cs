using Forge.Host.WorkInbox;
using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Kernel.WorkAssignments;

namespace Forge.Integration.Tests;

public sealed class OwnedSourceAccessTests
{
    [Fact]
    public async Task A_resolved_identity_does_not_substitute_for_the_registered_owner_read_check()
    {
        // A permissive resource lookup is insufficient for owners with mixed source permissions.
        var lookup = new LookupOnly();
        foreach (var family in new[] { "QmsPolicyAdoptionRevision", "LabelCopy", "LabelPrintAttempt", "IdentificationAlias", "ScanSession", "ScanAcceptance" })
            Assert.False(await OwnedSourceFamilies.CanReadAsync(null!, lookup, null!, family, "known-id", CancellationToken.None));
    }

    [Fact]
    public async Task Read_projection_preserves_the_owning_helpers_decision()
    {
        var owner = new OwnerLookup(false);
        Assert.False(await OwnedSourceFamilies.CanReadAsync(null!, owner, null!, "ScanAcceptance", "actual-acceptance", CancellationToken.None));
        Assert.Equal(("ScanAcceptance", "actual-acceptance", 1), (owner.Type, owner.Id, owner.Calls));
        owner.Permitted = true;
        Assert.True(await OwnedSourceFamilies.CanReadAsync(null!, owner, null!, "LabelCopy", "old-voided-copy", CancellationToken.None));
        Assert.Equal(("LabelCopy", "old-voided-copy", 2), (owner.Type, owner.Id, owner.Calls));
    }

    private class LookupOnly : IWorkSourceResolver
    {
        public Task<WorkSource?> ResolveAsync(IKernelUnitOfWork uow, string type, string id, CancellationToken ct) =>
            Task.FromResult<WorkSource?>(new(Permissions.OrdersRead, [new(type, id)]));
    }

    private sealed class OwnerLookup(bool permitted) : LookupOnly, IOwnedSourceResolver
    {
        public bool Permitted { get; set; } = permitted;
        public string? Type { get; private set; }
        public string? Id { get; private set; }
        public int Calls { get; private set; }
        public Task<bool> CanReadOwnedSourceAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string type, string id, CancellationToken ct)
        {
            Type = type; Id = id; Calls++;
            return Task.FromResult(Permitted);
        }
    }
}
