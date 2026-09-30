using Forge.Kernel.Identity;
using Forge.Kernel.Security;

namespace Forge.Kernel.Tests;

public class AuthorizerTests
{
    private static readonly ProgramId Falcon = new(Guid.Parse("018f0000-0000-7000-8000-000000000001"));
    private static readonly ProgramId Osprey = new(Guid.Parse("018f0000-0000-7000-8000-000000000002"));
    private readonly ForgeAuthorizer authorizer = new();

    private static ForgePrincipal Principal(bool active = true, string[]? roles = null, ProgramId[]? programs = null, DataMarking markings = DataMarking.None) =>
        new(new PersonId(Guid.Parse("018f0000-0000-7000-8000-0000000000aa")), "Pat", active,
            new HashSet<string>(roles ?? [Roles.Auditor]), new HashSet<ProgramId>(programs ?? [Falcon]), markings);

    [Fact]
    public void Permits_when_role_program_and_markings_all_match()
    {
        var decision = authorizer.Authorize(Principal(markings: DataMarking.ControlledUnclassified), Permissions.AuditRead,
            new ResourceRef("Audit", null, Falcon, DataMarking.ControlledUnclassified));
        Assert.True(decision.IsPermitted);
    }

    [Fact]
    public void Role_alone_does_not_grant_another_programs_data()
    {
        var decision = authorizer.Authorize(Principal(), Permissions.AuditRead, new ResourceRef("Audit", null, Osprey));
        Assert.False(decision.IsPermitted);
        Assert.Contains(decision.Reasons, r => r.Contains("program", StringComparison.Ordinal));
    }

    [Fact]
    public void Program_access_alone_does_not_grant_an_action()
    {
        var decision = authorizer.Authorize(Principal(), Permissions.AccessManage, new ResourceRef("Person", null, Falcon));
        Assert.False(decision.IsPermitted);
    }

    [Fact]
    public void Markings_are_a_set_not_a_ladder()
    {
        var decision = authorizer.Authorize(Principal(markings: DataMarking.ControlledUnclassified), Permissions.AuditRead,
            new ResourceRef("Audit", Markings: DataMarking.ExportControlled));
        Assert.False(decision.IsPermitted);
        Assert.Contains(decision.Reasons, r => r.Contains("ExportControlled", StringComparison.Ordinal));
    }

    [Fact]
    public void Inactive_identity_is_denied_even_with_roles()
    {
        var decision = authorizer.Authorize(Principal(active: false, roles: [Roles.SecurityAdministrator]), Permissions.AccessManage, ResourceRef.Of("Person"));
        Assert.False(decision.IsPermitted);
    }

    [Fact]
    public void Every_failed_check_is_reported()
    {
        var decision = authorizer.Authorize(Principal(active: false), Permissions.AccessManage,
            new ResourceRef("Person", null, Osprey, DataMarking.Proprietary));
        Assert.Equal(4, decision.Reasons.Count);
    }
}
