using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Security;

namespace Forge.Host.Identification;

public sealed partial class LabelService
{
    /// <summary>Host current-source/audit/history hook; performs all mixed read checks in the caller's UOW without reading artifact bytes.</summary>
    public async Task<IReadOnlyList<ResourceRef>?> RequireSourceReadAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string type, string id, CancellationToken ct)
    {
        principal = await uow.People.LoadPrincipalAsync(principal.PersonId, ct) ?? throw PresentationAccess.Missing();
        if (type == "LabelTemplate")
        {
            var key = id.Split('/',2); if (key.Length != 2 || !int.TryParse(key[1],NumberStyles.None,CultureInfo.InvariantCulture,out var revision)) return null;
            var template = await TemplateAsync(uow,principal,key[0],revision,ct);
            return template.Sources.Select(source=>source.Resource).Append(new("LabelTemplate",id,FacilityId:template.FacilityCode)).Distinct().ToArray();
        }
        if (!Guid.TryParseExact(id,"D",out var parsed)) return null;
        var store = Store(uow); var resources = new List<ResourceRef>();
        if (type == "IdentificationAlias")
        {
            var alias = (await store.FindAsync<IdentificationAliasView>("Alias",id,ct))?.Record;
            if(alias is null) return null;
            var subject=await ReadAliasAsync(uow,principal,alias,ct);
            return subject.Resources.Concat(alias.Snapshot.Resources).Concat(alias.Sources.Select(source=>source.Resource)).Distinct().ToArray();
        }
        LabelCopyView[] copies;
        if(type == "LabelIssuance")
        {
            var issue=(await store.FindAsync<LabelIssuanceView>("Issuance",id,ct))?.Record;if(issue is null) return null;
            copies=(await store.SubjectRecordsAsync<LabelCopyView>("Copy",issue.SubjectCode,ct)).Where(copy=>copy.IssuanceId==parsed).ToArray();
            if(copies.Length!=issue.Copies) throw PresentationAccess.Missing();
        }
        else
        {
            var copyId=parsed;
            if(type=="LabelPrintAttempt")
            {
                var attempt=(await store.FindAsync<LabelPrintAttemptView>("PrintAttempt",id,ct))?.Record;if(attempt is null)return null;
                await ReadAttemptAsync(uow,principal,attempt,ct);copyId=attempt.LabelId;
                resources.AddRange(attempt.Snapshot?.Resources ?? []);resources.AddRange((attempt.Sources ?? []).Select(source=>source.Resource));
            }
            else if(type!="LabelCopy") return null;
            var copy=(await store.FindAsync<LabelCopyView>("Copy",PresentationAccess.Key(copyId),ct))?.Record;if(copy is null)return null;copies=[copy];
        }
        foreach(var copy in copies)
        {
            var subject=await ReadCopyAsync(uow,principal,copy,ct);await RequireArtifactTemplateAsync(uow,principal,copy,ct);
            resources.AddRange(subject.Resources);resources.AddRange(copy.Snapshot.Resources);resources.AddRange(copy.Sources.Select(source=>source.Resource));
        }
        return resources.Distinct().ToArray();
    }
}
