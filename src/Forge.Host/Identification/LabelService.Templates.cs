using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using static Forge.Host.Identification.PresentationAccess;

namespace Forge.Host.Identification;

public sealed partial class LabelService
{
    private async Task<LabelTemplateView> TemplateAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string code, int revision, CancellationToken ct)
    {
        _ = access.Namespace;
        principal=await uow.People.LoadPrincipalAsync(principal.PersonId,ct) ?? throw Missing();
        var record = (await Store(uow).FindAsync<LabelTemplateView>("Template", TemplateKey(code, revision), ct))?.Record ?? throw Missing();
        access.Permit(principal, Permissions.IdentificationRead, new("LabelTemplate", TemplateKey(code, revision), FacilityId: record.FacilityCode));
        var recorded = (await Store(uow).HistoryAsync<LabelTemplateView>("Template", TemplateKey(code, revision), ct)).SelectMany(row => row.Sources).Distinct().ToArray();
        await access.CurrentSourcesAsync(uow, principal, recorded, ct);
        return record;
    }

    public async Task<PresentationEnvelope<LabelTemplateView>> ReadTemplateAsync(ForgePrincipal principal, string code, int revision, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        await TemplateAsync(uow, principal, code, revision, ct);
        return await Store(uow).FindAsync<LabelTemplateView>("Template", TemplateKey(code, revision), ct) ?? throw Missing();
    }

    public async Task<IdentificationPage<LabelTemplateView>> TemplatesAsync(ForgePrincipal principal, int take, string? after, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var allowed = new List<LabelTemplateView>();
        string? cursor = after;
        var size = Math.Clamp(take, 1, 200);
        while (allowed.Count <= size)
        {
            var raw = await Store(uow).ListAsync<LabelTemplateView>("Template", null, 200, cursor, ct);
            if (raw.Count == 0) break;
            foreach (var template in raw)
            {
                cursor = TemplateKey(template.Code, template.Revision);
                try { await TemplateAsync(uow, principal, template.Code, template.Revision, ct); allowed.Add(template); }
                catch (ForgeAuthorizationException) { }
                catch (CommandRejectedException) { }
                if (allowed.Count > size) break;
            }
            if (raw.Count < 200 || allowed.Count > size) break;
        }
        var items = allowed.Take(size).ToArray();
        return new(items, allowed.Count > size ? TemplateKey(items[^1].Code, items[^1].Revision) : null);
    }

    private static void TemplateContent(int width, int height, IReadOnlyList<string> kinds, IReadOnlyList<LabelSymbology> symbologies)
    {
        if (width is < 96 or > 2048 || height is < 64 or > 2048 || kinds is null || symbologies is null || kinds.Count is < 1 or > 30 || symbologies.Count is < 1 or > 3
            || kinds.Any(kind => !CanonicalIdentification.SupportedKinds.Contains(kind, StringComparer.Ordinal) || kind is "label" or "alias")
            || symbologies.Any(symbology => !Enum.IsDefined(symbology)))
            throw new CommandRejectedException("label_template_content", "Supply bounded pixels, supported primitive kinds and configured symbologies.");
    }

    public Task<CommandResult> CreateTemplateAsync(CreateLabelTemplate command, CommandContext context, CancellationToken ct)
    {
        var code = BusinessCode.Require(command.Code, "Template");
        var facility = BusinessCode.Require(command.FacilityCode, "Facility");
        var resource = new ResourceRef("LabelTemplate", TemplateKey(code, 1), FacilityId: facility);
        return pipeline.ExecuteAsync(new("Identification", nameof(CreateLabelTemplate), Permissions.IdentificationLabelsManage, resource, ReasonRequired: true), command, context,
            async (uow, now) =>
            {
                await Store(uow).LockAsync("template:" + code, ct);
                if ((await Store(uow).SubjectRecordsAsync<LabelTemplateView>("Template", code, ct)).Count != 0)
                    throw new CommandRejectedException("label_template_exists", "This template code already has governed history.");
                TemplateContent(command.Width, command.Height, command.AllowedKinds, command.AllowedSymbologies);
                var sources = await access.DocumentsAsync(uow, context.Principal, command.Documents, now, false, ct);
                var record = new LabelTemplateView(code, 1, 1, LabelTemplateState.Draft, command.Width, command.Height, command.AllowedKinds,
                    command.AllowedSymbologies, facility, command.Documents, context.Actor, context.Actor, null, now, null, sources);
                await Store(uow).AppendAsync("Template", TemplateKey(code, 1), access.Namespace, code, 1, record.State.ToString(), record, Transition("Create", 1, null, record.State.ToString(), context, now), ct);
                return Outcome<LabelTemplateView>("LabelTemplate", TemplateKey(code, 1), "Create", 1, null, record, context, resource);
            }, ct, async (uow, now) =>
            {
                _ = access.Namespace;
                if (!(await uow.Store<Forge.Resources.Contracts.IResourcesLookup>().ListSitesAsync(ct)).Any(site => site.Code == facility)) throw Missing();
                await access.DocumentsAsync(uow, context.Principal, command.Documents, now, false, ct); return new(resource);
            });
    }

    public Task<CommandResult> ReviseTemplateAsync(ReviseLabelTemplate command, CommandContext context, CancellationToken ct)
    {
        var code = BusinessCode.Require(command.Code, "Template");
        async Task<LabelTemplateView> Latest(IKernelUnitOfWork uow)
        {
            var latest = (await Store(uow).SubjectRecordsAsync<LabelTemplateView>("Template", code, ct)).MaxBy(record => record.Revision) ?? throw Missing();
            return await TemplateAsync(uow, context.Principal, code, latest.Revision, ct);
        }
        return pipeline.ExecuteAsync(new("Identification", nameof(ReviseLabelTemplate), Permissions.IdentificationLabelsManage, ResourceRef.Of("LabelTemplate", code), ReasonRequired: true), command, context,
            async (uow, now) =>
            {
                await Store(uow).LockAsync("template:" + code, ct);
                var old = await Latest(uow); Version(old.Version, command.ExpectedVersion);
                if (old.State == LabelTemplateState.Draft) throw new CommandRejectedException("label_template_open_draft", "Approve or withdraw the current draft before creating another immutable revision.");
                TemplateContent(command.Width, command.Height, command.AllowedKinds, command.AllowedSymbologies);
                var sources = await access.DocumentsAsync(uow, context.Principal, command.Documents, now, false, ct);
                var record = old with { Revision = checked(old.Revision + 1), Version = 1, State = LabelTemplateState.Draft, Width = command.Width,
                    Height = command.Height, AllowedKinds = command.AllowedKinds, AllowedSymbologies = command.AllowedSymbologies, Documents = command.Documents,
                    CreatedBy = context.Actor, LastEditedBy = context.Actor, ApprovedBy = null, ApprovedAt = null, CreatedAt = now, Sources = sources };
                await Store(uow).AppendAsync("Template", TemplateKey(code, record.Revision), access.Namespace, code, 1, record.State.ToString(), record, Transition("Revise", 1, null, record.State.ToString(), context, now), ct);
                return Outcome<LabelTemplateView>("LabelTemplate", TemplateKey(code, record.Revision), "Revise", 1, null, record, context, new("LabelTemplate", code, FacilityId: old.FacilityCode));
            }, ct, async (uow, now) => { var old = await Latest(uow); await access.DocumentsAsync(uow, context.Principal, command.Documents, now, false, ct); return new(new("LabelTemplate", code, FacilityId: old.FacilityCode)); });
    }

    public Task<CommandResult> TransitionTemplateAsync(TransitionLabelTemplate command, CommandContext context, CancellationToken ct)
    {
        var permission = command.Withdraw ? Permissions.IdentificationLabelsManage : Permissions.IdentificationLabelsApprove;
        return pipeline.ExecuteAsync(new("Identification", command.Withdraw ? "WithdrawLabelTemplate" : "ApproveLabelTemplate", permission, ResourceRef.Of("LabelTemplate", TemplateKey(command.Code, command.Revision)), ReasonRequired: true), command, context,
            async (uow, now) =>
            {
                await Store(uow).LockAsync("template:" + command.Code, ct);
                var old = await TemplateAsync(uow, context.Principal, command.Code, command.Revision, ct); Version(old.Version, command.ExpectedVersion);
                if (old.State == LabelTemplateState.Withdrawn || !command.Withdraw && old.State != LabelTemplateState.Draft)
                    throw new CommandRejectedException("label_template_state", "This immutable template revision cannot make that transition.");
                if (!command.Withdraw && (old.CreatedBy == context.Actor || old.LastEditedBy == context.Actor))
                    throw new CommandRejectedException("separation_of_duties", "A different authorized person must approve the template.");
                var sources = await access.DocumentsAsync(uow, context.Principal, old.Documents, now, !command.Withdraw, ct);
                var next = old with { Version = checked(old.Version + 1), State = command.Withdraw ? LabelTemplateState.Withdrawn : LabelTemplateState.Approved,
                    ApprovedBy = command.Withdraw ? old.ApprovedBy : context.Actor, ApprovedAt = command.Withdraw ? old.ApprovedAt : now, Sources = sources };
                await Store(uow).AppendAsync("Template", TemplateKey(old.Code, old.Revision), access.Namespace, old.Code, next.Version, next.State.ToString(), next, Transition(command.Withdraw ? "Withdraw" : "Approve", next.Version, old.State.ToString(), next.State.ToString(), context, now), ct);
                return Outcome("LabelTemplate", TemplateKey(old.Code, old.Revision), command.Withdraw ? "Withdraw" : "Approve", next.Version, old, next, context, new("LabelTemplate", old.Code, FacilityId: old.FacilityCode));
            }, ct, async (uow, now) => { var old = await TemplateAsync(uow, context.Principal, command.Code, command.Revision, ct); await access.DocumentsAsync(uow, context.Principal, old.Documents, now, !command.Withdraw, ct); return new(new("LabelTemplate", old.Code, FacilityId: old.FacilityCode)); });
    }
}
