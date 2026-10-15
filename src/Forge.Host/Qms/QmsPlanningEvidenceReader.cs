using System.Globalization;
using System.Text.Json;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Kernel.WorkAssignments;
using Forge.Planning;
using Forge.Procurement.Contracts;
using Forge.Quality.Contracts;
using Forge.Quality.Planning;
using Forge.Quality.Qms;
using Forge.Resources.Contracts;
using static Forge.Quality.Planning.QmsPlanningRules;

namespace Forge.Host.Qms;

/// <summary>Evidence is read from actual owning records in the command UOW. No domain service is called to manufacture evidence.</summary>
public sealed class QmsPlanningEvidenceReader(IForgeAuthorizer authorizer,IWorkSourceResolver work,IDocumentTargetResolver targets,QmsService qms,
    QmsOptions options,TimeProvider time,IEnumerable<IQmsPlanningOwnerEvidenceResolver>? ownerPorts=null) : IQmsPlanningEvidenceReader
{
    public Task<QmsPlanningSourceSnapshot> ResolveAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsPlanningEvidenceReference reference,bool lockSource,CancellationToken ct)=>
        ResolveAsync(uow,principal,reference,lockSource,new HashSet<string>(StringComparer.Ordinal),ct);
    private async Task<QmsPlanningSourceSnapshot> ResolveAsync(IKernelUnitOfWork uow,ForgePrincipal p,QmsPlanningEvidenceReference r,bool locked,HashSet<string> path,CancellationToken ct)
    {
        EvidenceReference(r);var identity=r.Context+"/"+r.Type+"/"+r.Key+"/"+r.Revision;
        Require(path.Add(identity),"planning_evidence_cycle","Owner evidence contains a dependency cycle.");
        try
        {
            var pg=uow as PostgresUnitOfWork??throw new CommandRejectedException("planning_evidence_unavailable","Owner evidence requires the configured PostgreSQL transaction.");
            var owners=(ownerPorts??[]).Where(port=>port.Owns(r.Context,r.Type)).ToArray();
            Require(owners.Length<=1,"planning_owner_ambiguous","This evidence type has more than one registered actual owner.");
            if(owners.Length==1)
            {
                var actual=await owners[0].ResolveAsync(uow,p,r,locked,(linked,lockLinked)=>ResolveAsync(uow,p,linked,lockLinked,path,ct),ct);
                Require(actual.Reference==r&&actual.OwnerContext==r.Context&&actual.OwnerType==r.Type&&!string.IsNullOrWhiteSpace(actual.OwnerId)&&!string.IsNullOrWhiteSpace(actual.Fingerprint)&&actual.Sources.Count>0,
                    "planning_evidence_unavailable","The registered owner did not supply complete actual source identity and authority.");
                foreach(var source in actual.Sources)Permit(p,source.Permission,source.Resource);
                if(r.EventId is{} actualEvent)await ValidateEventAsync(pg,actualEvent,r.Type,r.Revision is null?r.Key:r.Key+"/"+r.Revision,ct);
                if(r.AuditId is{} actualAudit)await ValidateAuditAsync(pg,actualAudit,r.Type,r.Revision is null?r.Key:r.Key+"/"+r.Revision,ct);
                return actual;
            }
            var sources=new List<QmsPlanningReadSource>();var authors=new List<string>();
            object content;DateTimeOffset? created=null,approved=null,closed=null;string? sha=null;bool licensed=false;string ownerId=r.Key;string? revision=r.Revision;
            if(r.Context=="Engineering"&&r.Type=="DocumentRevision")
            {
                if(locked)await LockAsync(pg,"SELECT d.number FROM engineering.document d JOIN engineering.document_revision v ON v.document_number=d.number WHERE d.number=$1 AND v.revision=$2 FOR SHARE OF d,v",r.Key,r.Revision??"",ct);
                var doc=await uow.Store<IDocumentLookup>().FindDocumentAsync(r.Key,ct)??throw Missing();var exact=doc.Revisions.FirstOrDefault(x=>x.Revision==r.Revision)??throw Missing();
                sources.Add(new(Permissions.EngineeringRead,new("DocumentRevision",doc.Number+"/"+exact.Revision,QmsPlanningService.Program(doc.ProgramId),doc.Markings)));
                authors.Add(doc.CreatedBy);authors.Add(exact.Author);created=exact.AuthoredAt;approved=exact.ApprovedAt;closed=exact.ObsoletedAt;sha=exact.FileSha256;
                // File identity/content is immutable; classification/state affect current access, not retained bytes.
                content=new{doc.Number,doc.Authority,exact.Revision,exact.FileSha256,exact.FileName,exact.FileSize,exact.ChangeNote,exact.Author,exact.AuthoredAt};
            }
            else if(r.Context=="Engineering"&&r.Type=="DocumentLink")
            {
                Require(long.TryParse(r.Key,NumberStyles.None,CultureInfo.InvariantCulture,out var linkId)&&linkId>0,"planning_evidence_unavailable","The actual attachment is unavailable.");
                if(locked)await LockAsync(pg,"SELECT link_id::text FROM engineering.document_link WHERE link_id::text=$1 AND $2::text<>'' FOR SHARE",r.Key,"attachment",ct);
                var link=await uow.Store<IDocumentLookup>().FindLinkAsync(linkId,ct)??throw Missing();
                var target=await targets.ResolveAsync(uow,link.TargetType,link.TargetId,ct)??throw Missing();
                var actualTarget=await work.ResolveAsync(uow,link.TargetType.ToString(),link.TargetId,ct);
                sources.AddRange((actualTarget?.Resources??[new ResourceRef(link.TargetType.ToString(),link.TargetId,QmsPlanningService.Program(target.ProgramId),target.Markings)]).Select(x=>new QmsPlanningReadSource(target.ReadPermission,x)));
                var document=await uow.Store<IDocumentLookup>().FindDocumentAsync(link.DocumentNumber,ct)??throw Missing();
                sources.Add(new(Permissions.EngineeringRead,new("Document",document.Number,QmsPlanningService.Program(document.ProgramId),document.Markings)));
                sources.Add(new(target.ReadPermission,new("DocumentLink",r.Key,QmsPlanningService.Program(link.ProgramId),link.TargetMarkings)));
                content=link;created=link.LinkedAt;closed=link.DetachedAt;authors.Add(link.LinkedBy);
            }
            else if(r.Context=="Engineering"&&r.Type=="EvidenceVerification")
            {
                Require(long.TryParse(r.Key,NumberStyles.None,CultureInfo.InvariantCulture,out var verificationId)&&verificationId>0,"planning_evidence_unavailable","The actual verification is unavailable.");string documentNumber,documentRevision;
                await using(var cmd=pg.Command("SELECT document_number,revision FROM engineering.evidence_verification WHERE verification_id=$1",verificationId))
                await using(var reader=await cmd.ExecuteReaderAsync(ct)){if(!await reader.ReadAsync(ct))throw Missing();documentNumber=reader.GetString(0);documentRevision=reader.GetString(1);}
                var verification=await uow.Store<IEvidenceLookup>().FindVerificationAsync(documentNumber,documentRevision,ct)??throw Missing();
                var actual=await ResolveAsync(uow,p,new("Engineering","DocumentRevision",documentNumber,documentRevision),locked,path,ct);sources.AddRange(actual.Sources);content=verification;created=verification.VerifiedAt;approved=verification.VerifiedAt;sha=verification.FileSha256;authors.Add(verification.VerifiedBy);
            }
            else if(r.Context=="Engineering"&&r.Type=="DocumentRequirement")
            {
                Require(long.TryParse(r.Key,NumberStyles.None,CultureInfo.InvariantCulture,out var requirementId)&&requirementId>0,"planning_evidence_unavailable","The actual requirement is unavailable.");string part,partRevision;
                await using(var cmd=pg.Command("SELECT part_number,revision FROM engineering.revision_document WHERE requirement_id=$1"+(locked?" FOR SHARE":""),requirementId))
                await using(var reader=await cmd.ExecuteReaderAsync(ct)){if(!await reader.ReadAsync(ct))throw Missing();part=reader.GetString(0);partRevision=reader.GetString(1);}
                var requirement=(await uow.Store<IDocumentLookup>().ListRequirementsAsync(part,partRevision,true,ct)).FirstOrDefault(x=>x.RequirementId==requirementId)??throw Missing();
                var doc=await uow.Store<IDocumentLookup>().FindDocumentAsync(requirement.DocumentNumber,ct)??throw Missing();
                sources.Add(new(Permissions.EngineeringRead,new("Document",doc.Number,QmsPlanningService.Program(doc.ProgramId),doc.Markings)));sources.Add(new(Permissions.EngineeringRead,new("ItemRevision",part+"/"+partRevision)));
                content=requirement;created=requirement.AddedAt;closed=requirement.RemovedAt;authors.Add(requirement.AddedBy);
            }
            else if(r.Context=="Procurement"&&r.Type=="TechnicalPackage")
            {
                var keys=r.Key.Split('/');Require(keys.Length==2&&long.TryParse(keys[0],NumberStyles.None,CultureInfo.InvariantCulture,out _)&&int.TryParse(keys[1],NumberStyles.None,CultureInfo.InvariantCulture,out _),"planning_evidence_unavailable","The actual technical package is unavailable.");
                var package=await uow.Store<ITechnicalPackageLookup>().FindPackageAsync(long.Parse(keys[0],CultureInfo.InvariantCulture),int.Parse(keys[1],CultureInfo.InvariantCulture),ct)??throw Missing();
                var owner=await work.ResolveAsync(uow,"PurchaseOrder",keys[0],ct)??throw Missing();sources.AddRange(owner.Resources.Select(x=>new QmsPlanningReadSource(owner.ReadPermission,x)));content=package;created=package.RecordedAt;approved=package.RecordedAt;authors.Add(package.RecordedBy);
                foreach(var document in package.Documents.Where(x=>x.Revision is not null)) {var actual=await ResolveAsync(uow,p,new("Engineering","DocumentRevision",document.DocumentNumber,document.Revision),locked,path,ct);sources.AddRange(actual.Sources);}
                // One package may retain several file hashes; the shared-file query selects its exact relation independently.
            }
            else if(r.Context=="Quality"&&r.Type=="QmsRevision")
            {
                var n=Number(r.Revision);if(locked)await LockAsync(pg,"SELECT d.code FROM quality.qms_record d JOIN quality.qms_revision v ON v.code=d.code WHERE d.code=$1 AND v.revision::text=$2 FOR SHARE OF d,v",r.Key,n.ToString(CultureInfo.InvariantCulture),ct);
                var record=await uow.Store<IQmsLookup>().FindAsync(r.Key,ct)??throw Missing();var exact=record.Revisions.FirstOrDefault(x=>x.Revision==n)??throw Missing();
                Require(record.OrganizationCode==options.OrganizationCode&&await qms.CanReadAsync(uow,p,record,ct,n),"planning_evidence_unavailable","The actual QMS revision is unavailable.");
                sources.Add(new(Permissions.QmsRead,QmsService.Resource(record)));content=new{record.Code,record.Kind,exact.Revision,exact.Content};created=record.CreatedAt;approved=exact.History.LastOrDefault(x=>x.Action=="Approve")?.At;closed=exact.EffectiveTo;licensed=exact.RequiresLicensedVerification;
                authors.AddRange(new[]{exact.CreatedBy,exact.LastEditedBy});authors.AddRange(exact.History.Where(x=>x.Action is "Create" or "Edit").Select(x=>x.Actor));
                foreach(var linked in exact.Sources)
                {
                    var reference=linked.Kind=="DocumentRevision"?new QmsPlanningEvidenceReference("Engineering","DocumentRevision",linked.Key,linked.Revision):new("Quality","QmsRevision",linked.Key,linked.Revision);
                    var source=await ResolveAsync(uow,p,reference,locked,path,ct);sources.AddRange(source.Sources);licensed|=source.RequiresLicensedVerification;
                }
            }
            else if(r.Context=="Quality"&&r.Type=="QmsPlanningRevision")
            {
                var n=Number(r.Revision);if(locked)await LockAsync(pg,"SELECT entity_id FROM quality.planning_head WHERE entity_type='Revision' AND entity_id=$1 AND $2::text<>'' FOR SHARE",r.Key+"/"+n,n.ToString(CultureInfo.InvariantCulture),ct);
                var record=await uow.Store<IQmsPlanningLookup>().FindAsync(r.Key,ct)??throw Missing();var exact=record.Revisions.FirstOrDefault(x=>x.Revision==n)??throw Missing();
                Require(record.OrganizationCode==options.OrganizationCode,"planning_evidence_unavailable","The governing organization is unavailable.");
                sources.Add(new(Permissions.QmsRead,QmsPlanningService.Resource(record)));content=new{record.Code,record.Kind,exact.Revision,exact.Content};created=record.CreatedAt;approved=exact.History.LastOrDefault(x=>x.Action=="Approve")?.At;closed=exact.EffectiveTo;licensed=exact.RequiresLicensedVerification;
                authors.AddRange(QmsPlanningService.Authors(exact));
                foreach(var saved in exact.Sources){sources.AddRange(saved.Sources);var current=await ResolveAsync(uow,p,saved.Reference,locked,path,ct);sources.AddRange(current.Sources);licensed|=current.RequiresLicensedVerification;}
            }
            else if(r.Context=="Execution"&&r.Type=="ProductionReport")
            {
                Require(long.TryParse(r.Key,NumberStyles.None,CultureInfo.InvariantCulture,out var id)&&id>0,"planning_evidence_unavailable","The actual report is unavailable.");
                long order;int sequence;
                await using(var command=pg.Command("SELECT order_number,sequence FROM execution.production_report WHERE report_id=$1"+(locked?" FOR SHARE":""),id))
                await using(var reader=await command.ExecuteReaderAsync(ct)){if(!await reader.ReadAsync(ct))throw Missing();order=reader.GetInt64(0);sequence=reader.GetInt32(1);}
                var report=(await uow.Store<IExecutionLookup>().ListReportsAsync(DateTimeOffset.MinValue,DateTimeOffset.MaxValue,order,ct)).FirstOrDefault(x=>x.ReportId==id)??throw Missing();
                var owner=await ResolveAsync(uow,p,new("Execution","OrderOperation",order+"/"+sequence),locked,path,ct);sources.AddRange(owner.Sources);authors.Add("person:"+report.PersonId);created=report.ReportedAt;
                sources.AddRange(owner.Sources.Where(x=>x.Permission==Permissions.OrdersRead).Select(x=>new QmsPlanningReadSource(Permissions.ShopFloorRead,x.Resource with{Type="ProductionReport",Id=r.Key})));
                var reportOrder=await uow.Store<IExecutionLookup>().FindOrderAsync(order,ct)??throw Missing();
                if(locked)await LockAsync(pg,"SELECT part_number FROM engineering.item WHERE part_number=$1 AND $2::text<>'' FOR SHARE",reportOrder.PartNumber,"unit",ct);
                var reportUnit=(await uow.Store<IEngineeringLookup>().FindItemAsync(reportOrder.PartNumber,ct))?.UnitOfMeasure??throw Missing();
                content=new{Report=report,CurrentItemUnit=reportUnit,HistoricUnitRecorded=false};
            }
            else if(r.Context=="Quality"&&r.Type is "QmsPlanningTask" or "QmsPlanningAssessment" or "QmsPlanningCase")
            {
                Require(Guid.TryParse(r.Key,out var id)&&id!=Guid.Empty,"planning_evidence_unavailable","The actual planning work ID is unavailable.");
                string definitionCode;int definitionRevision;
                if(r.Type=="QmsPlanningTask")
                {
                    var task=await uow.Store<IQmsPlanningLookup>().FindTaskAsync(id,ct)??throw Missing();content=task;definitionCode=task.Code;definitionRevision=task.DefinitionRevision;
                    authors.AddRange(new[]{task.CreatedBy,"person:"+task.OwnerPersonId,task.CompletedBy??""}.Where(x=>x.Length>0));created=task.CreatedAt;approved=task.VerifiedAt;closed=task.State==QmsTaskState.Verified?task.VerifiedAt:null;
                    foreach(var saved in task.Evidence){sources.AddRange(saved.Sources);var current=await ResolveAsync(uow,p,saved.Reference,locked,path,ct);sources.AddRange(current.Sources);}
                }
                else if(r.Type=="QmsPlanningAssessment")
                {
                    var assessment=await uow.Store<IQmsPlanningLookup>().FindAssessmentAsync(id,ct)??throw Missing();content=assessment;definitionCode=assessment.Code;definitionRevision=assessment.DefinitionRevision;
                    authors.AddRange(new[]{assessment.CreatedBy,assessment.LastEditedBy});created=assessment.CreatedAt;approved=assessment.History.LastOrDefault(x=>x.Action=="Approve")?.At;
                    foreach(var saved in assessment.Sources){sources.AddRange(saved.Sources);var current=await ResolveAsync(uow,p,saved.Reference,locked,path,ct);sources.AddRange(current.Sources);}
                }
                else
                {
                    var planningCase=await uow.Store<IQmsPlanningLookup>().FindCaseAsync(id,ct)??throw Missing();content=planningCase;definitionCode=planningCase.Code;definitionRevision=planningCase.DefinitionRevision;authors.Add(planningCase.CreatedBy);created=planningCase.CreatedAt;closed=planningCase.History.LastOrDefault(x=>x.Action=="Close")?.At;
                }
                if(locked)await LockAsync(pg,"SELECT entity_id FROM quality.planning_head WHERE entity_type=$1 AND entity_id=$2 FOR SHARE",r.Type.Replace("QmsPlanning",""),id.ToString(),ct);
                var definition=await ResolveAsync(uow,p,new("Quality","QmsPlanningRevision",definitionCode,definitionRevision.ToString(CultureInfo.InvariantCulture)),locked,path,ct);sources.AddRange(definition.Sources);licensed=definition.RequiresLicensedVerification;
            }
            else if(r.Context=="Quality"&&r.Type is "Ncr" or "NCR" or "Capa" or "CAPA")
            {
                Require(long.TryParse(r.Key,NumberStyles.None,CultureInfo.InvariantCulture,out var id)&&id>0,"planning_evidence_unavailable","The actual quality evidence is unavailable.");
                var isNcr=r.Type is "Ncr" or "NCR";
                if(locked)await LockAsync(pg,isNcr?"SELECT ncr_id::text FROM quality.ncr WHERE ncr_id::text=$1 AND $2::text<>'' FOR SHARE":"SELECT capa_id::text FROM quality.capa WHERE capa_id::text=$1 AND $2::text<>'' FOR SHARE",r.Key,"quality",ct);
                var owner=await work.ResolveAsync(uow,isNcr?"Ncr":"Capa",r.Key,ct)??throw Missing();sources.AddRange(owner.Resources.Select(x=>new QmsPlanningReadSource(owner.ReadPermission,x)));
                if(isNcr)
                {
                    var ncr=await uow.Store<IQualityLookup>().FindNcrAsync(id,ct)??throw Missing();content=ncr;created=ncr.RaisedAt;closed=ncr.ClosedAt;authors.AddRange(new[]{ncr.RaisedBy,ncr.DispositionedBy??"",ncr.ClosedBy??""}.Where(x=>x.Length>0));
                    if(ncr.OrderNumber is{} order){var actual=await ResolveAsync(uow,p,new("Execution",ncr.Sequence is null?"Order":"OrderOperation",order+(ncr.Sequence is{} seq?"/"+seq:"")),locked,path,ct);sources.AddRange(actual.Sources);}
                }
                else
                {
                    var capa=await uow.Store<Forge.Quality.IQualityStore>().FindCapaAsync(id,ct)??throw Missing();content=capa;created=capa.OpenedAt;closed=capa.VerifiedAt;authors.AddRange(new[]{capa.OpenedBy,capa.ImplementedBy??""}.Where(x=>x.Length>0));
                    if(capa.NcrId is{} ncr){var actual=await ResolveAsync(uow,p,new("Quality","Ncr",ncr.ToString(CultureInfo.InvariantCulture)),locked,path,ct);sources.AddRange(actual.Sources);}
                }
            }
            else if(r.Context=="Execution"&&r.Type is "Order" or "ProductionOrder" or "OrderOperation")
            {
                var parts=r.Key.Split('/');Require(long.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out var number)&&number>0,"planning_evidence_unavailable","The actual order is unavailable.");
                if(locked)await LockAsync(pg,"SELECT order_number::text FROM execution.production_order WHERE order_number::text=$1 AND $2::text<>'' FOR SHARE",parts[0],"owner",ct);
                var order=await uow.Store<IExecutionLookup>().FindOrderAsync(number,ct)??throw Missing();
                authors.AddRange(new[]{order.CreatedBy??"",order.ReleasedBy??""}.Where(x=>x.Length>0));approved=order.ReleasedAt;closed=order.Status==OrderStatus.Completed?order.Operations.Max(x=>x.CompletedAt):null;
                var centers=await uow.Store<IResourcesLookup>().ListWorkCentersAsync(ct);var areas=await uow.Store<IResourcesLookup>().ListSafetyAreasAsync(ct);
                foreach(var operation in order.Operations.Where(x=>r.Type!="OrderOperation"||parts.Length==2&&x.Sequence.ToString(CultureInfo.InvariantCulture)==parts[1]))
                {
                    // Retired resources remain actual historical scope; absence never turns them into an unscoped grant.
                    var center=centers.FirstOrDefault(x=>x.Code==operation.WorkCenterCode)??throw Missing();var area=areas.FirstOrDefault(x=>x.Code==center.SafetyAreaCode)??throw Missing();
                    sources.Add(new(Permissions.OrdersRead,new(r.Type,r.Key,QmsPlanningService.Program(order.ProgramId),FacilityId:area.SiteCode,WorkCenterId:center.Code,ContractId:order.ContractId?.ToString(CultureInfo.InvariantCulture))));
                }
                if(order.Operations.Count==0)sources.Add(new(Permissions.OrdersRead,new(r.Type,r.Key,QmsPlanningService.Program(order.ProgramId),ContractId:order.ContractId?.ToString(CultureInfo.InvariantCulture))));
                var documentation=await DocumentationService.ForOrderAsync(uow,order,time.GetUtcNow(),null,ct);
                foreach(var document in documentation.Documents.Where(x=>r.Type!="OrderOperation"||x.OperationSequence is null||parts.Length==2&&x.OperationSequence.Value.ToString(CultureInfo.InvariantCulture)==parts[1]))
                    foreach(var exactRevision in new[]{document.AtRelease,document.Applicable,document.PinnedRevision}.Where(x=>x is not null).Distinct())
                    {
                        var docSource=await ResolveAsync(uow,p,new("Engineering","DocumentRevision",document.DocumentNumber,exactRevision),locked,path,ct);
                        sources.AddRange(docSource.Sources);
                    }
                var selectedOperations=order.Operations.Where(x=>r.Type!="OrderOperation"||parts.Length==2&&x.Sequence.ToString(CultureInfo.InvariantCulture)==parts[1]).ToArray();
                Require(r.Type!="OrderOperation"||selectedOperations.Length==1,"planning_evidence_unavailable","The actual operation is unavailable.");
                content=new{order.OrderNumber,order.PartNumber,order.Revision,order.Quantity,order.ProgramId,order.ReleasedAt,Operations=selectedOperations.Select(x=>new{x.Sequence,x.Description,x.WorkCenterCode,x.ActivityCode,x.EligibleMachines,x.RequiredQualifications,x.PlannedMinutes,x.CrewSize}),order.Materials};
            }
            else
            {
                // Other established owner types are accepted only when the owning resolver identifies a real record and its successful audit supplies an actual recorded version.
                if(locked&&r.Type is "EngineeringChange" or "EngineeringReview")await LockAsync(pg,"SELECT change_number FROM engineering.change WHERE change_number=$1 AND $2::text<>'' FOR SHARE",r.Key.Split('/')[0],"owner",ct);
                var source=await work.ResolveAsync(uow,r.Type,r.Revision is null?r.Key:r.Key+"/"+r.Revision,ct)??throw Missing();
                Require(OwnerContext(r.Type)==r.Context,"planning_evidence_unavailable","The declared context does not match the actual owner.");
                sources.AddRange(source.Resources.Select(x=>new QmsPlanningReadSource(source.ReadPermission,x)));
                // The transaction-scoped immutable success record proves an owned decision, not physical occurrence.
                await using(var cmd=pg.Command("SELECT audit_id,recorded_at,actor,new_value,hash FROM kernel.audit_log WHERE entity_type=$1 AND entity_id=$2 AND outcome='Succeeded' ORDER BY sequence DESC LIMIT 1",r.Type,r.Revision is null?r.Key:r.Key+"/"+r.Revision))
                await using(var reader=await cmd.ExecuteReaderAsync(ct))
                {if(!await reader.ReadAsync(ct))throw Missing();content=new{AuditId=reader.GetGuid(0),RecordedAt=reader.GetFieldValue<DateTimeOffset>(1),Value=reader.IsDBNull(3)?null:reader.GetString(3),Hash=reader.GetString(4)};}
                await using(var cmd=pg.Command("SELECT DISTINCT actor FROM kernel.audit_log WHERE entity_type=$1 AND entity_id=$2 AND outcome='Succeeded' ORDER BY actor",r.Type,r.Revision is null?r.Key:r.Key+"/"+r.Revision))
                await using(var reader=await cmd.ExecuteReaderAsync(ct)){while(await reader.ReadAsync(ct))authors.Add(reader.GetString(0));}
            }
            foreach(var source in sources.Distinct())Permit(p,source.Permission,source.Resource);
            var fingerprint=CanonicalJson.Fingerprint(content);
            if(r.EventId is{} eventId)await ValidateEventAsync(pg,eventId,r.Type,revision is null?ownerId:ownerId+"/"+revision,ct);
            if(r.AuditId is{} auditId)await ValidateAuditAsync(pg,auditId,r.Type,revision is null?ownerId:ownerId+"/"+revision,ct);
            return new(r,r.Context,r.Type,ownerId,revision,fingerprint,time.GetUtcNow(),created,approved,closed,sha,authors.Distinct().Order().ToArray(),sources.Distinct().ToArray(),licensed,CanonicalJson.Serialize(content));
        }
        finally{path.Remove(identity);}
    }
    public async Task<IReadOnlyList<QmsPlanningSourceSnapshot>> SharedFileOwnersAsync(IKernelUnitOfWork uow,ForgePrincipal principal,string sha256,CancellationToken ct)
    {
        var pg=uow as PostgresUnitOfWork??throw Missing();var refs=new List<QmsPlanningEvidenceReference>();
        // No UI pagination or filtered subset: every immutable Engineering revision sharing these bytes is accounted for.
        await using(var cmd=pg.Command("SELECT document_number,revision FROM engineering.document_revision WHERE file_sha256=$1 ORDER BY document_number,revision",sha256))
        await using(var reader=await cmd.ExecuteReaderAsync(ct)){while(await reader.ReadAsync(ct))refs.Add(new("Engineering","DocumentRevision",reader.GetString(0),reader.GetString(1)));}
        await using(var cmd=pg.Command("SELECT DISTINCT po_number,line_number FROM procurement.line_package_document WHERE file_sha256=$1 ORDER BY po_number,line_number",sha256))
        await using(var reader=await cmd.ExecuteReaderAsync(ct)){while(await reader.ReadAsync(ct))refs.Add(new("Procurement","TechnicalPackage",reader.GetInt64(0)+"/"+reader.GetInt32(1)));}
        await using(var cmd=pg.Command("SELECT verification_id FROM engineering.evidence_verification WHERE file_sha256=$1 ORDER BY verification_id",sha256))
        await using(var reader=await cmd.ExecuteReaderAsync(ct)){while(await reader.ReadAsync(ct))refs.Add(new("Engineering","EvidenceVerification",reader.GetInt64(0).ToString(CultureInfo.InvariantCulture)));}
        await using(var cmd=pg.Command("SELECT DISTINCT l.link_id FROM engineering.document_link l JOIN engineering.document_revision r ON r.document_number=l.document_number AND (l.revision IS NULL OR l.revision=r.revision) WHERE r.file_sha256=$1 ORDER BY l.link_id",sha256))
        await using(var reader=await cmd.ExecuteReaderAsync(ct)){while(await reader.ReadAsync(ct))refs.Add(new("Engineering","DocumentLink",reader.GetInt64(0).ToString(CultureInfo.InvariantCulture)));}
        await using(var cmd=pg.Command("SELECT DISTINCT l.requirement_id FROM engineering.revision_document l JOIN engineering.document_revision r ON r.document_number=l.document_number AND (l.document_revision IS NULL OR l.document_revision=r.revision) WHERE r.file_sha256=$1 ORDER BY l.requirement_id",sha256))
        await using(var reader=await cmd.ExecuteReaderAsync(ct)){while(await reader.ReadAsync(ct))refs.Add(new("Engineering","DocumentRequirement",reader.GetInt64(0).ToString(CultureInfo.InvariantCulture)));}
        Require(refs.Count>0,"retention_shared_owners_unknown","The complete file-owner set is unavailable.");var result=new List<QmsPlanningSourceSnapshot>();
        foreach(var reference in refs)result.Add(await ResolveAsync(uow,principal,reference,true,ct));return result;
    }
    public async Task<QmsMetricMeasurement> MeasureAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsMetricDefinitionContent definition,DateTimeOffset from,DateTimeOffset to,CancellationToken ct)
        =>await MeasureCoreAsync(uow,principal,definition,from,to,true,ct);

    public Task<QmsMetricMeasurement> MeasureSnapshotAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsMetricDefinitionContent definition,DateTimeOffset from,DateTimeOffset to,CancellationToken ct)
        =>MeasureCoreAsync(uow,principal,definition,from,to,false,ct);

    private async Task<QmsMetricMeasurement> MeasureCoreAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsMetricDefinitionContent definition,DateTimeOffset from,DateTimeOffset to,bool lockSources,CancellationToken ct)
    {
        var p=principal;var d=definition;
        Metric(d);Range(from,to);Require(from>=d.PeriodFrom&&to<=d.PeriodTo,"metric_period","Use the exact approved definition period.");
        if(d.SourceKind!=QmsMetricSourceKind.RecordedProductionReports)return Measurement(QmsMetricAvailability.Unavailable,"This definition requires attributed human evidence.",null,null,null,[],d,from,to);
        if(to>time.GetUtcNow())return Measurement(QmsMetricAvailability.Partial,"The declared period is still open; no complete scalar is reported.",null,null,null,[],d,from,to);
        var rows=new List<QmsMetricEvidenceRow>();
        foreach(var orderId in d.OrderNumbers.Order())
        {
            try
            {
                var order=await uow.Store<IExecutionLookup>().FindOrderAsync(orderId,ct)??throw Missing();
                await ResolveAsync(uow,p,new("Execution","Order",orderId.ToString(CultureInfo.InvariantCulture)),lockSources,ct);
                var unit=(await uow.Store<IEngineeringLookup>().FindItemAsync(order.PartNumber,ct))?.UnitOfMeasure??throw Missing();
                // Production reports contain recorded quantities, not historic unit/machine snapshots. Unit must agree with the owner's present item and explicit definition.
                if(d.ProductionMeasure is not (QmsRecordedProductionMeasure.ReportCount or QmsRecordedProductionMeasure.ScrapFraction)&&unit!=d.Unit)return Measurement(QmsMetricAvailability.Unavailable,"A required owner's unit does not match the approved metric unit.",null,null,null,[],d,from,to);
                Require(d.IncludeAllOperations==true||d.OperationSequences.All(seq=>order.Operations.Any(x=>x.Sequence==seq)),"metric_cohort","A required operation is absent from an actual order.");
                // Order-filtered lookup currently ignores the time range, so the exact half-open filter is applied here explicitly.
                var reports=(await uow.Store<IExecutionLookup>().ListReportsAsync(from,to,orderId,ct)).Where(x=>x.ReportedAt>=from&&x.ReportedAt<to&&(d.IncludeAllOperations==true||d.OperationSequences.Contains(x.Sequence))).ToArray();
                foreach(var report in reports)
                {
                    if(rows.Count==d.MaximumRows)return Measurement(QmsMetricAvailability.Partial,"The complete declared cohort exceeds its approved evidence bound; no complete scalar is reported.",null,null,null,rows,d,from,to);
                    var source=await ResolveAsync(uow,p,new("Execution","ProductionReport",report.ReportId.ToString(CultureInfo.InvariantCulture)),lockSources,ct);
                    rows.Add(new(report.ReportId,orderId,report.Sequence,report.PersonId,report.ReportedAt,report.GoodQuantity,report.ScrapQuantity,unit,source));
                }
            }
            catch(ForgeAuthorizationException){return Measurement(QmsMetricAvailability.Unavailable,"At least one required cohort owner is inaccessible; a filtered subset is not the declared cohort.",null,null,null,[],d,from,to);}
            catch(CommandRejectedException){return Measurement(QmsMetricAvailability.Unavailable,"At least one required cohort owner or operation is unavailable; no complete scalar is reported.",null,null,null,[],d,from,to);}
        }
        var good=rows.Sum(x=>x.GoodQuantity);var scrap=rows.Sum(x=>x.ScrapQuantity);var total=good+scrap;
        if(d.ProductionMeasure==QmsRecordedProductionMeasure.ScrapFraction&&total==0)return Measurement(QmsMetricAvailability.Unavailable,"The actual denominator is zero; no ratio is defined.",null,scrap,total,rows,d,from,to);
        var actual=d.ProductionMeasure switch{QmsRecordedProductionMeasure.GoodQuantity=>good,QmsRecordedProductionMeasure.ScrapQuantity=>scrap,QmsRecordedProductionMeasure.TotalReportedQuantity=>total,QmsRecordedProductionMeasure.ReportCount=>rows.Count,QmsRecordedProductionMeasure.ScrapFraction=>scrap/total,_=>throw Missing()};
        return Measurement(QmsMetricAvailability.Complete,"Complete recorded-report evidence for the declared order/operation cohort and closed period. This is not a physical-conformity or first-pass-yield claim.",actual,d.ProductionMeasure==QmsRecordedProductionMeasure.ScrapFraction?scrap:actual,d.ProductionMeasure==QmsRecordedProductionMeasure.ScrapFraction?total:null,rows,d,from,to);
    }
    private QmsMetricMeasurement Measurement(QmsMetricAvailability availability,string basis,decimal? actual,decimal? numerator,decimal? denominator,IReadOnlyList<QmsMetricEvidenceRow> rows,QmsMetricDefinitionContent d,DateTimeOffset from,DateTimeOffset to)=>
        new(availability,basis,actual,numerator,denominator,d.Unit,from,to,d.OrderNumbers,rows,CanonicalJson.Fingerprint(new{availability,from,to,d.OrderNumbers,d.OperationSequences,d.IncludeAllOperations,Rows=rows.Select(x=>new{x.ReportId,x.Source.Fingerprint})}),time.GetUtcNow());
    private void Permit(ForgePrincipal p,string permission,ResourceRef resource){var decision=authorizer.Authorize(p,permission,resource);if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision);}
    private static async Task LockAsync(PostgresUnitOfWork uow,string sql,string key,string revision,CancellationToken ct){await using var cmd=uow.Command(sql,key,revision);await cmd.ExecuteScalarAsync(ct);}
    private static int Number(string? value)=>int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var n)&&n>0?n:throw Missing();
    private static CommandRejectedException Missing()=>new("planning_evidence_unavailable","The actual owner evidence is unavailable.");
    private static string? OwnerContext(string type)=>type switch{"Ncr" or "NCR" or "Capa" or "CAPA" or "Inspection"=>"Quality","RootCause"=>"Execution","EngineeringChange" or "EngineeringReview"=>"Engineering","Project" or "ProjectReview" or "ProjectChange"=>"Projects","PurchaseOrder" or "RFQ" or "SourceSelection"=>"Procurement","WorkOrder" or "Outage"=>"Maintenance","MaterialTask" or "MaterialRequest" or "Adjustment" or "ShelfLifeExtension"=>"Inventory","Hold"=>"Kernel",_=>null};
    private static async Task ValidateAuditAsync(PostgresUnitOfWork pg,Guid id,string type,string key,CancellationToken ct)
    {
        await using var cmd=pg.Command("SELECT entity_type,entity_id,new_value FROM kernel.audit_log WHERE audit_id=$1 AND outcome='Succeeded'",id);
        await using var reader=await cmd.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))throw Missing();
        var valid=type=="ProductionReport"?reader.GetString(0)=="OrderOperation"&&!reader.IsDBNull(2)&&ReportEventMatches(reader.GetString(2),key):reader.GetString(0)==type&&reader.GetString(1)==key;
        Require(valid,"planning_evidence_unavailable","The audit ID does not belong to this successful owner record.");
    }
    private static async Task ValidateEventAsync(PostgresUnitOfWork pg,Guid id,string type,string key,CancellationToken ct)
    {
        await using var cmd=pg.Command("SELECT envelope FROM kernel.event_log WHERE event_id=$1 UNION ALL SELECT envelope FROM kernel.outbox WHERE event_id=$1",id);
        var json=await cmd.ExecuteScalarAsync(ct) as string;Require(json is not null,"planning_evidence_unavailable","The actual event is unavailable.");
        using var document=JsonDocument.Parse(json!);var envelope=document.RootElement;
        var valid=type=="ProductionReport"?envelope.GetProperty("entityType").GetString()=="OrderOperation"&&ReportEventMatches(envelope.GetProperty("payload").GetString(),key):envelope.GetProperty("entityType").GetString()==type&&envelope.GetProperty("entityId").GetString()==key;
        Require(valid,"planning_evidence_unavailable","The event ID does not belong to this actual owner record.");
    }
    private static bool ReportEventMatches(string? payload,string key){if(payload is null)return false;using var document=JsonDocument.Parse(payload);return document.RootElement.TryGetProperty("reportId",out var id)&&id.ToString()==key;}
}
