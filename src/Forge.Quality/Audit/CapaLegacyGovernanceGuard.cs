using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Planning;
using Forge.Quality.Qms;
using static Forge.Quality.Planning.QmsPlanningRules;

namespace Forge.Quality.Audit;

/// <summary>No pipeline or domain service dependency. The guard runs inside the original command transaction before replay.</summary>
public sealed class CapaLegacyGovernanceGuard(IForgeAuthorizer authorizer,IQmsPlanningEvidenceReader evidence,QmsOptions options):ICapaLegacyGovernanceGuard
{
    public async Task<bool> CanReadAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long capaId,CancellationToken ct)
    {
        try{await evidence.ResolveAsync(uow,principal,new("Quality","Capa",capaId.ToString(System.Globalization.CultureInfo.InvariantCulture)),false,ct);return true;}
        catch(ForgeAuthorizationException){return false;}catch(CommandRejectedException){return false;}
    }
    public async Task AssertLegacyAsync(IKernelUnitOfWork uow,CommandContext context,long capaId,CancellationToken ct)
    {
        await uow.Store<IAuditCapaStore>().LockCapaAsync(capaId,ct);
        var core=await uow.Store<IQualityStore>().FindCapaAsync(capaId,ct)??throw QmsAuditService.Missing();
        var governed=await uow.Store<IAuditCapaLookup>().FindCapaAsync(capaId,ct);
        if(governed is null){Require(!core.Governed,"capa_governance_unavailable","The governed CAPA source is unavailable.");return;}
        Require(options.Configured&&governed.OrganizationCode==options.OrganizationCode,"audit_not_found","The CAPA source is unavailable.");
        var decision=authorizer.Authorize(context.Principal,Permissions.QualityRead,QmsAuditService.Resource(governed));if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision);
        await evidence.ResolveAsync(uow,context.Principal,new("Quality","Capa",capaId.ToString(System.Globalization.CultureInfo.InvariantCulture)),true,ct);
        throw new CommandRejectedException("capa_governed","Use the governed investigation, action and effectiveness workflow for this existing CAPA ID.");
    }
}
