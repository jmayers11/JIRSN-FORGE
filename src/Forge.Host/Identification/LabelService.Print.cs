using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using static Forge.Host.Identification.PresentationAccess;

namespace Forge.Host.Identification;

public sealed partial class LabelService
{
    public async Task RequireArtifactTemplateAsync(IKernelUnitOfWork uow, ForgePrincipal principal, LabelCopyView copy, CancellationToken ct) =>
        _ = await TemplateAsync(uow, principal, copy.TemplateCode, copy.TemplateVersion, ct);

    private async Task<(LabelCopyView Copy, IdentificationSubject Source, LabelTemplateView Template, QmsAdoptedPolicy Policy)> PrintFactsAsync(
        IKernelUnitOfWork uow, ForgePrincipal principal, Guid labelId, DateTimeOffset now, CancellationToken ct)
    {
        var copy = (await Store(uow).FindAsync<LabelCopyView>("Copy", Key(labelId), ct))?.Record ?? throw Missing();
        var source = await access.CapturedSubjectAsync(uow, principal, copy.Snapshot, copy.Sources, ct);
        await access.ActiveSubjectAsync(uow,source,ct);
        PresentationReadRules.RequireCurrentIdentity(copy, source);
        await Store(uow).LockAsync("template:" + copy.TemplateCode, ct);
        var template = await TemplateAsync(uow, principal, copy.TemplateCode, copy.TemplateVersion, ct);
        await access.DocumentsAsync(uow, principal, template.Documents, now, true, ct);
        var policy = await access.PolicyAsync(uow, principal, source, copy.FacilityCode, Permissions.IdentificationLabelsPrint, now, ct,"LabelCopy",Key(copy.LabelId));
        ValidateIssue(new(copy.SubjectCode, copy.TemplateCode, copy.TemplateVersion, copy.Symbology, 1, copy.FacilityCode), source, template, policy);
        return (copy, source, template, policy);
    }

    public Task<CommandResult> RequestPrintAsync(RequestLabelPrint command, CommandContext context, CancellationToken ct) =>
        pipeline.ExecuteAsync(new("Identification", nameof(RequestLabelPrint), Permissions.IdentificationLabelsPrint,
            ResourceRef.Of("LabelCopy", Key(command.LabelId)), ReasonRequired: true), command, context,
            async (uow, now) =>
            {
                await Store(uow).LockAsync("copy:" + Key(command.LabelId), ct);
                var facts = await PrintFactsAsync(uow, context.Principal, command.LabelId, now, ct);
                await Store(uow).LockAsync("subject:" + facts.Copy.SubjectCode, ct);
                facts = await PrintFactsAsync(uow, context.Principal, command.LabelId, now, ct);
                Version(facts.Copy.Version, command.ExpectedVersion);
                if (facts.Copy.State != LabelCopyState.Active) throw new CommandRejectedException("label_state", "Only an active authorized copy may be printed.");
                var previous=(await Store(uow).SubjectRecordsAsync<LabelPrintAttemptView>("PrintAttempt",facts.Copy.SubjectCode,ct)).Where(attempt=>attempt.LabelId==facts.Copy.LabelId).ToArray();
                if(previous.Any(attempt=>attempt.State is LabelPrintState.Requested or LabelPrintState.Sent or LabelPrintState.SpoolAccepted or LabelPrintState.Uncertain))
                    throw new CommandRejectedException("print_reconciliation_required","Reconcile the existing physical attempt before another request.");
                if(previous.Any(attempt=>attempt.ObservedPrinted || attempt.State==LabelPrintState.Confirmed))
                    throw new CommandRejectedException("label_reprint_required","Use a governed reprint or replacement decision for another physical copy.");
                // A browser request records a request, never spool acceptance or physical printing.
                var printer = command.PrinterCode is null ? null : Forge.Kernel.Identity.BusinessCode.Require(command.PrinterCode, "Declared printer");
                var artifact = rendering.Render(facts.Copy, facts.Template);
                var file = await uow.Store<IGeneratedDocumentContentStore>().StoreAsync(artifact.Bytes, context.Actor, now, ct);
                if (file.Sha256 != artifact.Sha256) throw new InvalidOperationException("Retained generated artifact hash mismatch.");
                var id = Forge.Kernel.Identity.ForgeIds.New(pipeline.Time);
                var attempt = new LabelPrintAttemptView(id, facts.Copy.LabelId, 1, LabelPrintState.Requested, printer, context.Actor, now,
                    null, null, null, file.Sha256, false, false, CanonicalJson.Fingerprint(facts.Template), facts.Policy.Fingerprint,Snapshot(facts.Source),Capture(facts.Policy,facts.Template.Sources));
                await Store(uow).AppendAsync("PrintAttempt", Key(id), access.Namespace, facts.Copy.SubjectCode, 1,
                    attempt.State.ToString(), attempt, Transition("RequestPrint", 1, null, attempt.State.ToString(), context, now), ct);
                return Outcome<LabelPrintAttemptView>("LabelPrintAttempt", Key(id), "RequestPrint", 1, null, attempt, context, facts.Source.Resources[0]);
            }, ct, async (uow, now) => { var facts = await PrintFactsAsync(uow, context.Principal, command.LabelId, now, ct); return Authority(facts.Source, facts.Copy.FacilityCode, "LabelCopy", Key(command.LabelId)); });

    public Task<CommandResult> ObservePrintAsync(ObserveLabelPrint command, CommandContext context, CancellationToken ct)
    {
        async Task<(LabelPrintAttemptView Attempt, IdentificationSubject Source, LabelCopyView Copy)> Facts(IKernelUnitOfWork uow, DateTimeOffset now)
        {
            var attempt = (await Store(uow).FindAsync<LabelPrintAttemptView>("PrintAttempt", Key(command.AttemptId), ct))?.Record ?? throw Missing();
            await ReadAttemptAsync(uow,context.Principal,attempt,ct);
            var facts = await PrintFactsAsync(uow, context.Principal, attempt.LabelId, now, ct);
            return (attempt, facts.Source, facts.Copy);
        }
        return pipeline.ExecuteAsync(new("Identification", nameof(ObserveLabelPrint), Permissions.IdentificationLabelsPrint,
            ResourceRef.Of("LabelPrintAttempt", Key(command.AttemptId)), ReasonRequired: true), command, context,
            async (uow, now) =>
            {
                await Store(uow).LockAsync("attempt:" + Key(command.AttemptId), ct);
                var facts = await Facts(uow, now); var old = facts.Attempt;
                Version(old.Version, command.ExpectedVersion);
                ValidateObservation(command);
                var attachmentObservation = old.State == LabelPrintState.Confirmed && old.ObservedPrinted && !old.ObservedAttached
                    && command.Outcome == LabelPrintState.Confirmed && command.ObservedPrinted && command.ObservedAttached;
                if (old.State == LabelPrintState.Failed || old.State == LabelPrintState.Confirmed && !attachmentObservation)
                    throw new CommandRejectedException("print_attempt_final", "Append an attachment observation to confirmed printing, or record a separate print attempt. Previous observations are immutable.");
                var next = old with { Version = checked(old.Version + 1), State = command.Outcome, Observation = command.Observation,
                    OutcomeBy = context.Actor, OutcomeAt = now, ObservedPrinted = command.ObservedPrinted, ObservedAttached = command.ObservedAttached };
                await Store(uow).AppendAsync("PrintAttempt", Key(old.AttemptId), access.Namespace, facts.Copy.SubjectCode, next.Version,
                    next.State.ToString(), next, Transition("ObservePrint", next.Version, old.State.ToString(), next.State.ToString(), context, now, command.Observation), ct);
                return Outcome("LabelPrintAttempt", Key(old.AttemptId), "ObservePrint", next.Version, old, next, context, facts.Source.Resources[0]);
            }, ct, async (uow, now) => { var facts = await Facts(uow, now); ValidateObservation(command); return Authority(facts.Source, facts.Copy.FacilityCode, "LabelPrintAttempt", Key(command.AttemptId)); });
    }

    public static void ValidateObservation(ObserveLabelPrint command)
    {
        if (command.Outcome is not (LabelPrintState.Confirmed or LabelPrintState.Failed or LabelPrintState.Uncertain)
            || string.IsNullOrWhiteSpace(command.Observation) || command.Observation.Length > 2000 || command.Observation.Any(char.IsControl)
            || command.ObservedAttached && !command.ObservedPrinted || command.Outcome == LabelPrintState.Confirmed && !command.ObservedPrinted)
            throw new CommandRejectedException("print_observation", "Record an explicit bounded human observation; attachment requires observed printing.");
    }

    public async Task<LabelArtifact> ArtifactAsync(ForgePrincipal principal, Guid attemptId, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var attempt = (await Store(uow).FindAsync<LabelPrintAttemptView>("PrintAttempt", Key(attemptId), ct))?.Record ?? throw Missing();
        var copy = (await Store(uow).FindAsync<LabelCopyView>("Copy", Key(attempt.LabelId), ct))?.Record ?? throw Missing();
        await ReadAttemptAsync(uow, principal, attempt, ct);
        await RequireArtifactTemplateAsync(uow, principal, copy, ct);
        var bytes = await uow.Store<IGeneratedDocumentContentStore>().ReadAsync(attempt.ArtifactSha256, ct) ?? throw Missing();
        if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), attempt.ArtifactSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Retained artifact integrity mismatch.");
        return new(bytes, "image/svg+xml", "label-" + Key(copy.LabelId) + ".svg", attempt.ArtifactSha256);
    }

    public async Task<IdentificationSubject> ReadAttemptAsync(IKernelUnitOfWork uow, ForgePrincipal principal, LabelPrintAttemptView attempt, CancellationToken ct)
    {
        var copy=(await Store(uow).FindAsync<LabelCopyView>("Copy",Key(attempt.LabelId),ct))?.Record ?? throw Missing();
        var source=await ReadCopyAsync(uow,principal,copy,ct);
        if(attempt.Snapshot is{} snapshot)await access.CapturedSubjectAsync(uow,principal,snapshot,attempt.Sources ?? [],ct);
        else if(attempt.Sources is{} sources)await access.CurrentSourcesAsync(uow,principal,sources,ct);
        return source;
    }

    public async Task RequireUsableLabelAsync(IKernelUnitOfWork uow, ForgePrincipal principal, LabelCopyView copy, DateTimeOffset now, CancellationToken ct)
    {
        var source = await ReadCopyAsync(uow, principal, copy, ct);
        await access.ActiveSubjectAsync(uow, source, ct);
        PresentationReadRules.RequireCurrentIdentity(copy, source);
        var template = await TemplateAsync(uow, principal, copy.TemplateCode, copy.TemplateVersion, ct);
        await access.DocumentsAsync(uow, principal, template.Documents, now, true, ct);
        var policy = await access.PolicyAsync(uow, principal, source, copy.FacilityCode, Permissions.IdentificationRead, now, ct);
        ValidateIssue(new(copy.SubjectCode, copy.TemplateCode, copy.TemplateVersion, copy.Symbology, 1, copy.FacilityCode), source, template, policy);
        if (copy.State != LabelCopyState.Active) throw new CommandRejectedException("label_inactive", "The label copy is not active.");
        var attempts = await Store(uow).SubjectRecordsAsync<LabelPrintAttemptView>("PrintAttempt", copy.SubjectCode, ct);
        foreach (var attempt in attempts.Where(attempt => attempt.LabelId == copy.LabelId)) await ReadAttemptAsync(uow, principal, attempt, ct);
        PresentationReadRules.RequireUsable(copy, template, policy, attempts);
    }
}
