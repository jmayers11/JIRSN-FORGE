using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using static Forge.Host.Identification.PresentationAccess;

namespace Forge.Host.Identification;

public sealed partial class LabelService(CommandPipeline pipeline, IKernelUnitOfWorkFactory unitOfWork,
    PresentationAccess access, LabelRendering rendering)
{
    private static IIdentificationPresentationStore Store(IKernelUnitOfWork uow) => uow.Store<IIdentificationPresentationStore>();
    private static LabelTransition Transition(string action, int version, string? from, string to, CommandContext context, DateTimeOffset now, string? observation = null) =>
        new(action, version, from, to, context.Actor, now, context.Reason!, observation);
    private static QmsLabelCopyRules Rules(QmsAdoptedPolicy policy) => policy.Rules.LabelCopy ?? throw new CommandRejectedException("label_policy_incomplete", "Adopt complete label rules before consequential presentation actions.");
    private static QmsLabelLifecycleRules Lifecycle(QmsAdoptedPolicy policy) => Rules(policy).Lifecycle ?? throw new CommandRejectedException("label_policy_incomplete", "Adopt explicit label lifecycle rules.");
    private static void Version(int actual, int expected) { if (actual != expected) throw new CommandRejectedException("presentation_version", "The presentation changed. Refresh it before deciding."); }
    private static void Allow(bool? decision, string code) { if (decision != true) throw new CommandRejectedException(code, "The current adopted policy does not permit this action."); }

    private CommandOutcome Outcome<T>(string kind, string key, string action, int version, T? prior, T record,
        CommandContext context, ResourceRef resource) => new(CommandResult.Ok(key, "Presentation decision recorded."),
        [pipeline.Event(new IdentificationPresentationChanged(kind, key, action, version), context, "Identification", kind, key, resource.Program, resource.Markings)],
        new AuditChange(kind, key, prior is null ? null : CanonicalJson.Serialize(prior), CanonicalJson.Serialize(record), resource.Program, resource.Markings));

    public async Task<(LabelIssuanceView Issuance, IReadOnlyList<LabelCopyView> Copies)> IssuanceAsync(ForgePrincipal principal, Guid id, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var issue = (await Store(uow).FindAsync<LabelIssuanceView>("Issuance", Key(id), ct))?.Record ?? throw Missing();
        var copies = (await Store(uow).SubjectRecordsAsync<LabelCopyView>("Copy", issue.SubjectCode, ct)).Where(copy => copy.IssuanceId == id).ToArray();
        foreach (var copy in copies) await ReadCopyAsync(uow, principal, copy, ct);
        if (copies.Length != issue.Copies) throw Missing();
        return (issue, copies);
    }

    public async Task<IdentificationPage<LabelCopyView>> ListAsync(ForgePrincipal principal, string subjectCode, int take, string? after, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var source = await access.SubjectAsync(uow, principal, subjectCode, ct);
        var allowed = new List<LabelCopyView>();
        foreach (var copy in await Store(uow).SubjectRecordsAsync<LabelCopyView>("Copy", source.CanonicalSubjectCode, ct))
        {
            try { await ReadCopyAsync(uow, principal, copy, ct); allowed.Add(copy); }
            catch (ForgeAuthorizationException) { }
            catch (CommandRejectedException) { }
        }
        var page = allowed.OrderBy(copy => Key(copy.LabelId), StringComparer.Ordinal).Where(copy => after is null || string.CompareOrdinal(Key(copy.LabelId), after) > 0).Take(Math.Clamp(take, 1, 200) + 1).ToArray();
        var items = page.Take(Math.Clamp(take, 1, 200)).ToArray();
        return new(items, page.Length > items.Length ? Key(items[^1].LabelId) : null);
    }

    public async Task<LabelDetail> DetailAsync(ForgePrincipal principal, Guid id, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var envelope = await Store(uow).FindAsync<LabelCopyView>("Copy", Key(id), ct) ?? throw Missing();
        var copy = envelope.Record;
        var source = await ReadCopyAsync(uow, principal, copy, ct);
        var attempts = (await Store(uow).SubjectRecordsAsync<LabelPrintAttemptView>("PrintAttempt", copy.SubjectCode, ct)).Where(attempt => attempt.LabelId == id).ToArray();
        foreach(var attempt in attempts)await ReadAttemptAsync(uow,principal,attempt,ct);
        var actions = new List<string>();
        var capabilities = new LabelPolicyCapabilities(false);
        try
        {
            var policy = await access.PolicyAsync(uow, principal, source, copy.FacilityCode, Permissions.IdentificationRead, pipeline.Time.GetUtcNow(), ct);
            var rules = Rules(policy); var life = Lifecycle(policy);
            capabilities = new(true, rules.RequireCopyAuthorization, life.RequireIndependentCopyAuthorization, life.RequiredConfirmation?.ToString(), life.AllowReprint, life.AllowReplacement, life.AllowVoid);
            var currentConfiguration = true;
            try
            {
                await access.ActiveSubjectAsync(uow,source,ct);
                var template = await TemplateAsync(uow, principal, copy.TemplateCode, copy.TemplateVersion, ct);
                await access.DocumentsAsync(uow, principal, template.Documents, pipeline.Time.GetUtcNow(), true, ct);
                ValidateIssue(new(copy.SubjectCode, copy.TemplateCode, copy.TemplateVersion, copy.Symbology, 1, copy.FacilityCode), source, template, policy);
            }
            catch (CommandRejectedException) { currentConfiguration = false; }
            if (currentConfiguration && copy.State == LabelCopyState.PendingApproval && (life.RequireIndependentCopyAuthorization == false || copy.CreatedBy != "person:" + principal.PersonId)) actions.Add("Approve");
            if (currentConfiguration && copy.State == LabelCopyState.Active)
            {
                if (life.AllowReprint == true) actions.Add("Reprint");
                if (life.AllowReplacement == true) actions.Add("Replace");
                actions.Add("RequestPrint");
            }
            if ((copy.State is LabelCopyState.Active or LabelCopyState.PendingApproval) && life.AllowVoid == true) actions.Add("Void");
            actions.RemoveAll(action => !May(principal, action == "Approve" ? Permissions.IdentificationLabelsApprove : action == "RequestPrint" ? Permissions.IdentificationLabelsPrint : Permissions.IdentificationLabelsManage, source, copy.FacilityCode));
        }
        catch (ForgeAuthorizationException) { }
        catch (CommandRejectedException) { }
        try { PresentationReadRules.RequireCurrentIdentity(copy, source); }
        catch (CommandRejectedException) { actions.RemoveAll(action => action is "RequestPrint" or "Reprint" or "Approve"); }
        return new(copy, envelope.Transitions, attempts, actions, capabilities, source.Display);
    }

    private bool May(ForgePrincipal principal, string permission, IdentificationSubject source, string facility)
    {
        try { foreach (var resource in source.Resources) access.Permit(principal, permission, OwnResource(resource,facility,"LabelCopy",null)); return true; }
        catch (ForgeAuthorizationException) { return false; }
    }

    public async Task<IdentificationSubject> ReadCopyAsync(IKernelUnitOfWork uow, ForgePrincipal principal, LabelCopyView copy, CancellationToken ct)
    {
        principal=await uow.People.LoadPrincipalAsync(principal.PersonId,ct) ?? throw Missing();
        var source = await access.CapturedSubjectAsync(uow, principal, copy.Snapshot, copy.Sources, ct);
        _ = await TemplateAsync(uow, principal, copy.TemplateCode, copy.TemplateVersion, ct);
        foreach (var resource in source.Resources) access.Permit(principal, Permissions.IdentificationRead, OwnResource(resource,copy.FacilityCode,"LabelCopy",Key(copy.LabelId)));
        return source;
    }

    public Task<CommandResult> IssueAsync(IssueLabels command, CommandContext context, CancellationToken ct) =>
        pipeline.ExecuteAsync(new("Identification", nameof(IssueLabels), Permissions.IdentificationLabelsManage, ResourceRef.Of("LabelCopy"), ReasonRequired: true), command, context,
            async (uow, now) =>
            {
                var (source, template, policy) = await IssueFactsAsync(uow, context.Principal, command, now, ct);
                await Store(uow).LockAsync("subject:" + source.CanonicalSubjectCode, ct);
                (source, template, policy) = await IssueFactsAsync(uow, context.Principal, command, now, ct);
                return await CreateCopiesAsync(uow, command, source, template, policy, null, LabelRelationship.Original, context, now, ct);
            }, ct, async (uow, now) => { var facts = await IssueFactsAsync(uow, context.Principal, command, now, ct); return Authority(facts.Source, command.FacilityCode); });

    private async Task<(IdentificationSubject Source, LabelTemplateView Template, QmsAdoptedPolicy Policy)> IssueFactsAsync(IKernelUnitOfWork uow, ForgePrincipal principal, IssueLabels command, DateTimeOffset now, CancellationToken ct)
    {
        var source = await access.SubjectAsync(uow, principal, command.SubjectCode, ct);
        await access.ActiveSubjectAsync(uow,source,ct);
        await Store(uow).LockAsync("template:" + command.TemplateCode, ct);
        var template = await TemplateAsync(uow, principal, command.TemplateCode, command.TemplateVersion, ct);
        await access.DocumentsAsync(uow, principal, template.Documents, now, true, ct);
        var policy = await access.PolicyAsync(uow, principal, source, command.FacilityCode, Permissions.IdentificationLabelsManage, now, ct);
        ValidateIssue(command, source, template, policy);
        return (source, template, policy);
    }

    private static void ValidateIssue(IssueLabels command, IdentificationSubject source, LabelTemplateView template, QmsAdoptedPolicy policy)
    {
        var life = Lifecycle(policy); var rules = Rules(policy);
        if (command.Copies <= 0 || rules.MaximumCopiesPerRequest is not { } maximum || command.Copies > maximum)
            throw new CommandRejectedException("label_copy_count", "The copy count exceeds the adopted per-request limit.");
        if (template.State != LabelTemplateState.Approved || template.FacilityCode != command.FacilityCode
            || !template.AllowedKinds.Contains(source.PresentedKind ?? source.Display.Kind, StringComparer.Ordinal) || !template.AllowedSymbologies.Contains(command.Symbology)
            || life.AllowedSubjectKinds?.Contains(source.PresentedKind ?? source.Display.Kind, StringComparer.Ordinal) != true
            || life.AllowedTemplates?.Any(reference => reference.Code == template.Code && reference.Version == template.Revision) != true
            || life.AllowedSymbologies?.Any(symbology => symbology.ToString() == command.Symbology.ToString()) != true)
            throw new CommandRejectedException("label_configuration", "The exact approved template, subject and symbology are not permitted by the adopted policy.");
    }

    private async Task<CommandOutcome> CreateCopiesAsync(IKernelUnitOfWork uow, IssueLabels command, IdentificationSubject source,
        LabelTemplateView template, QmsAdoptedPolicy policy, Guid? priorId, LabelRelationship relationship,
        CommandContext context, DateTimeOffset now, CancellationToken ct)
    {
        var rules = Rules(policy); var life = Lifecycle(policy);
        if (rules.RequireCopyAuthorization is null || life.RequireIndependentCopyAuthorization is null || life.MaximumActiveCopies is null || life.ActiveCopyMode is null)
            throw new CommandRejectedException("label_policy_incomplete", "Adopt explicit copy authority and active-copy limits.");
        var pending = rules.RequireCopyAuthorization == true;
        if (!pending) await ActiveLimitAsync(uow, source.CanonicalSubjectCode, command.Copies, priorId, relationship, life, ct);
        var issueId = ForgeIds.New(pipeline.Time);
        var issuance = new LabelIssuanceView(issueId, source.CanonicalSubjectCode, template.Code, template.Revision, command.Symbology, command.FacilityCode,
            command.Copies, context.Actor, now, policy.PolicyCode, policy.PolicyRevision, policy.Fingerprint,priorId,relationship,Snapshot(source),Capture(policy,template.Sources));
        await Store(uow).AppendAsync("Issuance", Key(issueId), access.Namespace, source.CanonicalSubjectCode, 1, "Issued", issuance, Transition("Issue", 1, null, "Issued", context, now), ct);
        for (var index = 0; index < command.Copies; index++)
        {
            var id = ForgeIds.New(pipeline.Time);
            var copy = new LabelCopyView(id, issueId, "forge:v1:" + access.Namespace + ":label:" + Key(id), source.CanonicalSubjectCode, source.Display,
                template.Code, template.Revision, command.Symbology, command.FacilityCode, pending ? LabelCopyState.PendingApproval : LabelCopyState.Active, 1,
                context.Actor, now, pending ? null : context.Actor, pending ? null : now, priorId, relationship,
                policy.PolicyCode, policy.PolicyRevision, policy.Fingerprint, Snapshot(source), Capture(policy, template.Sources));
            await Store(uow).AppendAsync("Copy", Key(id), access.Namespace, copy.SubjectCode, 1, copy.State.ToString(), copy, Transition(relationship.ToString(), 1, null, copy.State.ToString(), context, now), ct);
        }
        if (!pending && relationship == LabelRelationship.Replacement && life.ReplacementVoidsPriorCopy == true && priorId is { } oldId)
            await EndCopyAsync(uow, oldId, LabelCopyState.Replaced, context, now, ct);
        return Outcome<LabelIssuanceView>("LabelIssuance", Key(issueId), "Issue", 1, null, issuance, context, source.Resources[0]);
    }

    private static async Task ActiveLimitAsync(IKernelUnitOfWork uow, string subject, int count, Guid? previous, LabelRelationship relationship, QmsLabelLifecycleRules life, CancellationToken ct)
    {
        var copies = await Store(uow).SubjectRecordsAsync<LabelCopyView>("Copy", subject, ct);
        var active = copies.Count(copy => copy.State == LabelCopyState.Active && !(relationship == LabelRelationship.Replacement && life.ReplacementVoidsPriorCopy == true && copy.LabelId == previous));
        var maximum = life.ActiveCopyMode == QmsActiveCopyMode.SingleActive ? 1 : life.MaximumActiveCopies ?? 0;
        if (active + count > maximum) throw new CommandRejectedException("label_active_copy_limit", "The adopted active-copy limit requires an explicit replacement or reconciliation.");
    }

    private static async Task EndCopyAsync(IKernelUnitOfWork uow, Guid id, LabelCopyState state, CommandContext context, DateTimeOffset now, CancellationToken ct)
    {
        var old = (await Store(uow).FindAsync<LabelCopyView>("Copy", Key(id), ct))?.Record ?? throw Missing();
        if (old.State != LabelCopyState.Active) throw new CommandRejectedException("label_replacement_source", "The original copy changed before replacement authorization.");
        var next = old with { State = state, Version = checked(old.Version + 1) };
        await Store(uow).AppendAsync("Copy", Key(id), CanonicalIdentification.Parse(old.Code, old.Code.Split(':')[2]).Namespace, old.SubjectCode, next.Version, state.ToString(), next, Transition(state.ToString(), next.Version, old.State.ToString(), state.ToString(), context, now), ct);
    }

    public Task<CommandResult> ChangeAsync(ChangeLabelCopy command, CommandContext context, CancellationToken ct)
    {
        var permission = command.Action == "Approve" ? Permissions.IdentificationLabelsApprove : Permissions.IdentificationLabelsManage;
        async Task<(LabelCopyView Copy, IdentificationSubject Source, QmsAdoptedPolicy Policy)> Facts(IKernelUnitOfWork uow, DateTimeOffset now)
        {
            var copy = (await Store(uow).FindAsync<LabelCopyView>("Copy", Key(command.LabelId), ct))?.Record ?? throw Missing();
            var source = await access.CapturedSubjectAsync(uow, context.Principal, copy.Snapshot, copy.Sources, ct);
            var policy = await access.PolicyAsync(uow, context.Principal, source, copy.FacilityCode, permission, now, ct,"LabelCopy",Key(copy.LabelId));
            var life = Lifecycle(policy);
            if (command.Action is "Reprint" or "Replace") Allow(command.Action == "Reprint" ? life.AllowReprint : life.AllowReplacement, "label_copy_not_permitted");
            else if (command.Action == "Void") Allow(life.AllowVoid, "label_void_not_permitted");
            else if (command.Action == "Approve")
            {
                if (life.RequireIndependentCopyAuthorization is null) throw new CommandRejectedException("label_policy_incomplete", "Adopt explicit copy authorization rules.");
                if (life.RequireIndependentCopyAuthorization == true && copy.CreatedBy == context.Actor)
                    throw new CommandRejectedException("separation_of_duties", "A different authorized person must approve this label copy.");
            }
            else throw new CommandRejectedException("label_action", "Select an explicit label lifecycle action.");
            if (command.Action != "Void")
            {
                await access.ActiveSubjectAsync(uow,source,ct);
                if (command.Action != "Replace") PresentationReadRules.RequireCurrentIdentity(copy, source);
                await Store(uow).LockAsync("template:" + copy.TemplateCode, ct);
                var template = await TemplateAsync(uow, context.Principal, copy.TemplateCode, copy.TemplateVersion, ct);
                await access.DocumentsAsync(uow, context.Principal, template.Documents, now, true, ct);
                ValidateIssue(new(copy.SubjectCode, copy.TemplateCode, copy.TemplateVersion, copy.Symbology, 1, copy.FacilityCode), source, template, policy);
            }
            return (copy, source, policy);
        }
        return pipeline.ExecuteAsync(new("Identification", "Label" + command.Action, permission, ResourceRef.Of("LabelCopy", Key(command.LabelId)), ReasonRequired: true), command, context,
            async (uow, now) =>
            {
                var facts = await Facts(uow, now);
                await Store(uow).LockAsync("subject:" + facts.Copy.SubjectCode, ct);
                facts = await Facts(uow, now);
                var old = facts.Copy; Version(old.Version, command.ExpectedVersion); var life = Lifecycle(facts.Policy);
                if (command.Action is "Reprint" or "Replace")
                {
                    if (old.State != LabelCopyState.Active) throw new CommandRejectedException("label_state", "Only an active label may be reprinted or replaced.");
                    Allow(command.Action == "Reprint" ? life.AllowReprint : life.AllowReplacement, "label_copy_not_permitted");
                    var template = await TemplateAsync(uow, context.Principal, old.TemplateCode, old.TemplateVersion, ct);
                    var issue = new IssueLabels(old.SubjectCode, old.TemplateCode, old.TemplateVersion, old.Symbology, 1, old.FacilityCode);
                    ValidateIssue(issue, facts.Source, template, facts.Policy);
                    return await CreateCopiesAsync(uow, issue, facts.Source, template, facts.Policy, old.LabelId,
                        command.Action == "Reprint" ? LabelRelationship.Reprint : LabelRelationship.Replacement, context, now, ct);
                }
                LabelCopyView next;
                if (command.Action == "Approve")
                {
                    if (old.State != LabelCopyState.PendingApproval) throw new CommandRejectedException("label_state", "This copy does not await authorization.");
                    if (life.RequireIndependentCopyAuthorization == true && old.CreatedBy == context.Actor)
                        throw new CommandRejectedException("separation_of_duties", "A different authorized person must approve this label copy.");
                    await ActiveLimitAsync(uow, old.SubjectCode, 1, old.PreviousLabelId, old.Relationship, life, ct);
                    next = old with { Version = checked(old.Version + 1), State = LabelCopyState.Active, ApprovedBy = context.Actor, ApprovedAt = now };
                    if (old.Relationship == LabelRelationship.Replacement && life.ReplacementVoidsPriorCopy == true && old.PreviousLabelId is { } previous)
                        await EndCopyAsync(uow, previous, LabelCopyState.Replaced, context, now, ct);
                }
                else if (command.Action == "Void")
                {
                    Allow(life.AllowVoid, "label_void_not_permitted");
                    if (old.State is LabelCopyState.Replaced or LabelCopyState.Voided) throw new CommandRejectedException("label_state", "The label is already inactive.");
                    next = old with { Version = checked(old.Version + 1), State = LabelCopyState.Voided };
                }
                else throw new CommandRejectedException("label_action", "Select an explicit label lifecycle action.");
                await Store(uow).AppendAsync("Copy", Key(old.LabelId), access.Namespace, old.SubjectCode, next.Version, next.State.ToString(), next, Transition(command.Action, next.Version, old.State.ToString(), next.State.ToString(), context, now), ct);
                return Outcome("LabelCopy", Key(old.LabelId), command.Action, next.Version, old, next, context, facts.Source.Resources[0]);
            }, ct, async (uow, now) => { var facts = await Facts(uow, now); return Authority(facts.Source, facts.Copy.FacilityCode, "LabelCopy", Key(command.LabelId)); });
    }
}
