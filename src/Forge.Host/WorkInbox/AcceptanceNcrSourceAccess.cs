using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Kernel.WorkAssignments;
using Forge.Quality;
using Forge.Quality.Contracts;

namespace Forge.Host.WorkInbox;

/// <summary>Existing child NCR/CAPA ownership stays with Quality; acceptance adds exact source bindings only.</summary>
public sealed class AcceptanceNcrSourceAccess(IServiceProvider services)
{
    public static bool Contains(string type) => type is "Ncr" or "NCR" or "Capa" or "CAPA";

    public async Task<WorkSource?> ResolveAsync(IKernelUnitOfWork uow, string type, string id, CancellationToken ct)
    {
        var facts = await FindAsync(uow, type, id, ct);
        if (facts is null) return null;
        var scopes = facts.NcrId is { } ncrId
            ? await services.GetRequiredService<IAcceptanceSourceAccess>().ResourcesForNcrAsync(uow, ncrId, type, facts.Id, ct) : null;
        scopes ??= facts.Ncr is { } ncr ? await QualityService.ResourcesForNcrAsync(uow, ncr, ct)
            : await QualityService.ResourcesForCapaAsync(uow, facts.Capa!, ct);
        if (scopes.Count == 0) return null;
        return new(Permissions.QualityRead, scopes.Select(resource => resource with { Type = type, Id = id }).Distinct().ToArray(),
            facts.Ncr is { } record ? record.Status != NcrStatus.Closed : facts.Capa!.Status != CapaStatus.Verified,
            DecisionAvailable: facts.Ncr is { } decision ? decision.Status == NcrStatus.AwaitingApproval : true);
    }

    public async Task<bool> CanReadAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string type, string id, CancellationToken ct)
    {
        try
        {
            principal = await uow.People.LoadPrincipalAsync(principal.PersonId, ct) is { IsActive: true } current ? current : principal with { IsActive = false };
            if (!principal.IsActive) return false;
            var facts = await FindAsync(uow, type, id, ct);
            if (facts is null) return false;
            var own = await ResolveAsync(uow, type, id, ct);
            if (own is null || own.Resources.Count == 0) return false;
            var authorizer = services.GetRequiredService<IForgeAuthorizer>();
            if (own.Resources.Any(resource => !authorizer.Authorize(principal, Permissions.QualityRead, resource).IsPermitted)) return false;
            return facts.NcrId is { } ncrId
                ? await services.GetRequiredService<IAcceptanceSourceAccess>().CanReadNcrSourceAsync(uow, principal, ncrId, ct) : true;
        }
        catch (ForgeAuthorizationException) { return false; }
        catch (CommandRejectedException) { return false; }
    }

    private sealed record Facts(long Id, long? NcrId, NcrInfo? Ncr, CapaInfo? Capa);

    private static async Task<Facts?> FindAsync(IKernelUnitOfWork uow, string type, string id, CancellationToken ct)
    {
        if (!Contains(type) || !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0) return null;
        if (type is "Ncr" or "NCR")
        {
            var ncr = await uow.Store<IQualityLookup>().FindNcrAsync(number, ct);
            return ncr is null ? null : new(number, number, ncr, null);
        }
        var capa = await uow.Store<IQualityStore>().FindCapaAsync(number, ct);
        return capa is null ? null : new(number, capa.NcrId, null, capa);
    }
}
