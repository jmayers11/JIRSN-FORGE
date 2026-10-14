using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using static Forge.Host.Identification.PresentationAccess;

namespace Forge.Host.Identification;

public sealed partial class LabelService
{
    private static (string Domain, string Alias) AliasKey(string domain, string alias)
    {
        if (!CanonicalIdentification.IsValidNamespace(domain) || string.IsNullOrEmpty(alias) || alias.Length > 1024 || alias.Any(char.IsControl))
            throw new CommandRejectedException("alias_key", "Supply an explicit bounded domain and exact alias text.");
        return (domain, alias); // Exact ordinal text, never trimming, URL execution, or a global bare-lot interpretation.
    }

    private async Task<IdentificationSubject> ReadAliasAsync(IKernelUnitOfWork uow, ForgePrincipal principal, IdentificationAliasView alias, CancellationToken ct)
    {
        principal=await uow.People.LoadPrincipalAsync(principal.PersonId,ct) ?? throw Missing();
        var source = await access.CapturedSubjectAsync(uow, principal, alias.Snapshot, alias.Sources, ct);
        foreach (var resource in source.Resources) access.Permit(principal, Permissions.IdentificationRead, OwnResource(resource,alias.FacilityCode,"IdentificationAlias",Key(alias.AliasId)));
        return source;
    }

    public async Task<PresentationEnvelope<IdentificationAliasView>> AliasAsync(ForgePrincipal principal, Guid id, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var envelope = await Store(uow).FindAsync<IdentificationAliasView>("Alias", Key(id), ct) ?? throw Missing();
        await ReadAliasAsync(uow, principal, envelope.Record, ct);
        return envelope;
    }

    public async Task<IdentificationPage<IdentificationAliasView>> AliasesAsync(ForgePrincipal principal, string subjectCode, int take, string? after, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var source = await access.SubjectAsync(uow, principal, subjectCode, ct);
        var visible = new List<IdentificationAliasView>();
        foreach (var alias in await Store(uow).SubjectRecordsAsync<IdentificationAliasView>("Alias", source.CanonicalSubjectCode, ct))
        {
            try { await ReadAliasAsync(uow, principal, alias, ct); visible.Add(alias); }
            catch (ForgeAuthorizationException) { }
            catch (CommandRejectedException) { }
        }
        var size = Math.Clamp(take, 1, 200);
        var page = visible.OrderBy(row => Key(row.AliasId), StringComparer.Ordinal).Where(row => after is null || string.CompareOrdinal(Key(row.AliasId), after) > 0).Take(size + 1).ToArray();
        var items = page.Take(size).ToArray();
        return new(items, page.Length > size ? Key(items[^1].AliasId) : null);
    }

    private async Task<(IdentificationSubject Source, QmsAdoptedPolicy Policy)> AliasFactsAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        string subjectCode, string facility, string action, DateTimeOffset now, CancellationToken ct, Guid? owningId = null)
    {
        var source = await access.SubjectAsync(uow, principal, subjectCode, ct);
        var policy = await access.PolicyAsync(uow, principal, source, facility, Permissions.IdentificationAliasesManage, now, ct,"IdentificationAlias",owningId is{} id?Key(id):null);
        var life = Lifecycle(policy);
        Allow(action == "Replace" ? life.AllowAliasReplacement : action == "Void" ? life.AllowAliasVoid : life.AllowAliasCreate, "alias_not_permitted");
        if (life.AllowedSubjectKinds?.Contains(source.PresentedKind ?? source.Display.Kind, StringComparer.Ordinal) != true)
            throw new CommandRejectedException("alias_subject", "The adopted policy does not permit this primitive kind.");
        return (source, policy);
    }

    private async Task<IdentificationAliasView> AppendAliasAsync(IKernelUnitOfWork uow, string domain, string value, IdentificationSubject source,
        string facility, QmsAdoptedPolicy policy, Guid? previous, CommandContext context, DateTimeOffset now, CancellationToken ct)
    {
        var id = ForgeIds.New(pipeline.Time);
        var record = new IdentificationAliasView(id, domain, value, "forge:v1:" + access.Namespace + ":alias:" + Key(id), source.CanonicalSubjectCode,
            source.Display, facility, IdentificationAliasState.Active, 1, context.Actor, now, previous, null, null, Snapshot(source),
            Capture(policy, []), policy.PolicyCode, policy.PolicyRevision, policy.Fingerprint);
        await Store(uow).AppendAsync("Alias", Key(id), access.Namespace, source.CanonicalSubjectCode, 1, record.State.ToString(), record,
            Transition(previous is null ? "CreateAlias" : "ReplaceAlias", 1, null, record.State.ToString(), context, now), ct, domain, value);
        return record;
    }

    public Task<CommandResult> CreateAliasAsync(CreateIdentificationAlias command, CommandContext context, CancellationToken ct)
    {
        var (domain, value) = AliasKey(command.Domain, command.Alias);
        return pipeline.ExecuteAsync(new("Identification", nameof(CreateIdentificationAlias), Permissions.IdentificationAliasesManage,
            ResourceRef.Of("IdentificationAlias"), ReasonRequired: true), command, context,
            async (uow, now) =>
            {
                await Store(uow).LockAsync("alias:" + access.Namespace + ":" + domain + ":" + value, ct);
                var facts = await AliasFactsAsync(uow, context.Principal, command.SubjectCode, command.FacilityCode, "Create", now, ct);
                if (await Store(uow).FindActiveAliasAsync(access.Namespace, domain, value, ct) is not null)
                    throw new CommandRejectedException("alias_already_assigned", "This exact configured alias already has an active immutable mapping.");
                var record = await AppendAliasAsync(uow, domain, value, facts.Source, facts.Policy.FacilityCode, facts.Policy, null, context, now, ct);
                return Outcome<IdentificationAliasView>("IdentificationAlias", Key(record.AliasId), "CreateAlias", 1, null, record, context, facts.Source.Resources[0]);
            }, ct, async (uow, now) => { var facts = await AliasFactsAsync(uow, context.Principal, command.SubjectCode, command.FacilityCode, "Create", now, ct); return Authority(facts.Source, facts.Policy.FacilityCode,"IdentificationAlias"); });
    }

    public Task<CommandResult> ReplaceAliasAsync(ReplaceIdentificationAlias command, CommandContext context, CancellationToken ct)
    {
        async Task<(IdentificationAliasView Old, IdentificationSubject OldSource, IdentificationSubject NewSource, QmsAdoptedPolicy Policy)> Facts(IKernelUnitOfWork uow, DateTimeOffset now)
        {
            var old = (await Store(uow).FindAsync<IdentificationAliasView>("Alias", Key(command.AliasId), ct))?.Record ?? throw Missing();
            await access.CapturedSubjectAsync(uow, context.Principal, old.Snapshot, old.Sources, ct);
            var oldFacts = await AliasFactsAsync(uow, context.Principal, old.Snapshot.PresentationCode ?? old.SubjectCode, old.FacilityCode, "Replace", now, ct,old.AliasId);
            var next = await AliasFactsAsync(uow, context.Principal, command.SubjectCode, command.FacilityCode, "Replace", now, ct);
            return (old, oldFacts.Source, next.Source, next.Policy);
        }
        return pipeline.ExecuteAsync(new("Identification", nameof(ReplaceIdentificationAlias), Permissions.IdentificationAliasesManage,
            ResourceRef.Of("IdentificationAlias", Key(command.AliasId)), ReasonRequired: true), command, context,
            async (uow, now) =>
            {
                var facts = await Facts(uow, now);
                await Store(uow).LockAsync("alias:" + access.Namespace + ":" + facts.Old.Domain + ":" + facts.Old.Alias, ct);
                facts = await Facts(uow, now); var old = facts.Old; Version(old.Version, command.ExpectedVersion);
                if (old.State != IdentificationAliasState.Active) throw new CommandRejectedException("alias_inactive", "Only an active alias can be replaced.");
                var ended = old with { State = IdentificationAliasState.Voided, Version = checked(old.Version + 1), VoidedBy = context.Actor, VoidedAt = now };
                await Store(uow).AppendAsync("Alias", Key(old.AliasId), access.Namespace, old.SubjectCode, ended.Version, ended.State.ToString(), ended,
                    Transition("ReplaceAlias", ended.Version, old.State.ToString(), ended.State.ToString(), context, now), ct, old.Domain, old.Alias);
                var next = await AppendAliasAsync(uow, old.Domain, old.Alias, facts.NewSource, facts.Policy.FacilityCode, facts.Policy, old.AliasId, context, now, ct);
                return Outcome("IdentificationAlias", Key(next.AliasId), "ReplaceAlias", 1, old, next, context, facts.NewSource.Resources[0]);
            }, ct, async (uow, now) =>
            {
                var facts = await Facts(uow, now);
                var authority = Authority(facts.OldSource, facts.Old.FacilityCode, "IdentificationAlias", Key(command.AliasId));
                return authority with { AdditionalResources = (authority.AdditionalResources ?? []).Concat(facts.NewSource.Resources.Select(resource=>OwnResource(resource,facts.Policy.FacilityCode,"IdentificationAlias",null))).Distinct().ToArray() };
            });
    }

    public Task<CommandResult> VoidAliasAsync(VoidIdentificationAlias command, CommandContext context, CancellationToken ct)
    {
        async Task<(IdentificationAliasView Old, IdentificationSubject Source)> Facts(IKernelUnitOfWork uow, DateTimeOffset now)
        {
            var old = (await Store(uow).FindAsync<IdentificationAliasView>("Alias", Key(command.AliasId), ct))?.Record ?? throw Missing();
            await access.CapturedSubjectAsync(uow, context.Principal, old.Snapshot, old.Sources, ct);
            var facts = await AliasFactsAsync(uow, context.Principal, old.Snapshot.PresentationCode ?? old.SubjectCode, old.FacilityCode, "Void", now, ct,old.AliasId);
            return (old, facts.Source);
        }
        return pipeline.ExecuteAsync(new("Identification", nameof(VoidIdentificationAlias), Permissions.IdentificationAliasesManage,
            ResourceRef.Of("IdentificationAlias", Key(command.AliasId)), ReasonRequired: true), command, context,
            async (uow, now) =>
            {
                var facts = await Facts(uow, now);
                await Store(uow).LockAsync("alias:" + access.Namespace + ":" + facts.Old.Domain + ":" + facts.Old.Alias, ct);
                facts = await Facts(uow, now); var old = facts.Old; Version(old.Version, command.ExpectedVersion);
                if (old.State != IdentificationAliasState.Active) throw new CommandRejectedException("alias_inactive", "This alias is already inactive.");
                var next = old with { State = IdentificationAliasState.Voided, Version = checked(old.Version + 1), VoidedBy = context.Actor, VoidedAt = now };
                await Store(uow).AppendAsync("Alias", Key(old.AliasId), access.Namespace, old.SubjectCode, next.Version, next.State.ToString(), next,
                    Transition("VoidAlias", next.Version, old.State.ToString(), next.State.ToString(), context, now), ct, old.Domain, old.Alias);
                return Outcome("IdentificationAlias", Key(old.AliasId), "VoidAlias", next.Version, old, next, context, facts.Source.Resources[0]);
            }, ct, async (uow, now) => { var facts = await Facts(uow, now); return Authority(facts.Source, facts.Old.FacilityCode, "IdentificationAlias", Key(command.AliasId)); });
    }
}
