using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Kernel.WorkAssignments;
using Npgsql;

namespace Forge.Host.WorkInbox;

public sealed record ApprovalInboxItem(string WorkType, string WorkId, string Title, string Requester, string DeepLink, string Permission, DateTimeOffset CreatedAt);
public sealed record WorkAssignment(Guid AssignmentId, string WorkType, string WorkId, string Title, string DeepLink, string Status, string Assignee, string AssignedBy, DateTimeOffset AssignedAt, DateTimeOffset UpdatedAt, string Reason);
public sealed record AssignWorkRequest(string WorkType, string WorkId, string Title, string DeepLink, Guid AssigneePersonId, string Reason);
public sealed record AssignmentStatusRequest(string Status, string Reason);

/// <summary>Only authorized current source records become inbox items; the inbox owns no domain decision.</summary>
public sealed class WorkInboxStore(NpgsqlDataSource dataSource, IKernelUnitOfWorkFactory unitOfWork, IWorkSourceResolver sources, IForgeAuthorizer authorizer, TimeProvider time)
{
    private sealed record Candidate(string WorkType, string WorkId, string Title, string Requester, string DeepLink, string Permission, DateTimeOffset CreatedAt);

    public async Task<IReadOnlyList<ApprovalInboxItem>> ApprovalsAsync(ForgePrincipal principal, CancellationToken ct)
    {
        const string sql = """
            SELECT * FROM (
              SELECT 'NCR' work_type,ncr_id::text work_id,'NCR '||ncr_id||' · '||defect title,dispositioned_by requester,'#/quality' deep_link,'quality.approve' permission,dispositioned_at created_at FROM quality.ncr WHERE status='AwaitingApproval' AND dispositioned_by IS NOT NULL AND dispositioned_by<>$1
              UNION ALL SELECT 'Schedule',version_id::text,'Schedule version '||version_id,proposed_by,'#/scheduling','planning.schedule.approve',proposed_at FROM planning.schedule_version WHERE status='Proposed' AND proposed_by<>$1
              UNION ALL SELECT 'PurchaseOrder',po_number::text,'Purchase order '||po_number||' · '||supplier,created_by,'#/procurement','procurement.approve',created_at FROM procurement.purchase_order WHERE status='Draft' AND created_by<>$1
              UNION ALL SELECT 'Project',code,'Project '||code||' · '||title,created_by,'#/projects','projects.approve',created_at FROM projects.project WHERE status='Draft' AND created_by<>$1
              UNION ALL SELECT 'ProjectChange',change_id::text,'Project change '||change_id||' · '||reason,requested_by,'#/projects','projects.approve',requested_at FROM projects.change WHERE status='Pending' AND requested_by<>$1
              UNION ALL SELECT 'Outage',outage_id::text,'Outage '||outage_id||' · '||reason,proposed_by,'#/maintenance','maintenance.outage.approve',proposed_at FROM maintenance.outage WHERE status='Proposed' AND proposed_by<>$1
              UNION ALL SELECT 'DocumentRevision',document_number||' rev '||revision,'Document '||document_number||' revision '||revision,author,'#/engineering','engineering.documents.approve',submitted_at FROM engineering.document_revision WHERE status='InReview' AND author<>$1 AND submitted_by<>$1
              UNION ALL SELECT 'ManufacturerPart',part_number||'/'||manufacturer||'/'||mpn,'Manufacturer part '||mpn||' for '||part_number,proposed_by,'#/engineering','engineering.cots.manage',proposed_at FROM engineering.manufacturer_part WHERE status='Proposed' AND proposed_by<>$1
              UNION ALL SELECT 'Alternate',alternate_id::text,'Alternate '||primary_part||' → '||alternate_part,proposed_by,'#/engineering',CASE WHEN kind='MaintenanceOnly' THEN 'maintenance.alternates.approve' ELSE 'engineering.alternates.approve' END,proposed_at FROM engineering.alternate WHERE status='Proposed' AND proposed_by<>$1
              UNION ALL SELECT 'EngineeringReview',change_number||'/'||round||'/'||review_kind,'Review '||review_kind||' for '||change_number,originator,'#/engineering','engineering.changes.review',created_at FROM engineering.change c JOIN engineering.change_review r USING(change_number) WHERE c.status='InReview' AND r.decision IS NULL AND r.required_by=ANY($2::text[]) AND c.originator<>$1
              UNION ALL SELECT 'EngineeringChange',change_number,'Engineering change '||change_number||' · '||title,originator,'#/engineering','engineering.changes.approve',created_at FROM engineering.change c WHERE status='InReview' AND originator<>$1 AND NOT EXISTS(SELECT 1 FROM engineering.change_review r WHERE r.change_number=c.change_number AND r.round=c.review_round AND r.decision IS NULL)
              UNION ALL SELECT 'SafetyRule',code||'/'||version,'Safety rule '||code||' version '||version,authored_by,'#/work-centers','safety.rules.approve',authored_at FROM safety.rule WHERE status='Draft' AND authored_by<>$1
              UNION ALL SELECT 'ShelfLifeExtension',extension_id::text,'Shelf-life extension '||part_number||' lot '||lot_number,requested_by,'#/inventory','quality.approve',requested_at FROM inventory.shelf_life_extension WHERE approved IS NULL AND requested_by<>$1
              UNION ALL SELECT 'SupplierUseAsIs',rejection_id::text,'Use as is · rejection '||rejection_id||' · '||defect,dispositioned_by,'#/quality','quality.approve',dispositioned_at FROM inventory.supplier_rejection WHERE disposition='UseAsIs' AND approved_by IS NULL AND dispositioned_by IS NOT NULL AND dispositioned_by<>$1
              UNION ALL SELECT 'Adjustment',request_id::text,kind||' '||request_id||' · '||part_number,requested_by,'#/inventory','inventory.approve',requested_at FROM inventory.adjustment_request WHERE state='Pending' AND requested_by<>$1
              UNION ALL SELECT 'RFQRelease',rfq_id::text,'Release RFQ '||rfq_id||' · '||title,created_by,'#/procurement','procurement.approve',created_at FROM procurement.rfq WHERE status='Draft' AND created_by<>$1
              UNION ALL SELECT 'SourceSelection',r.rfq_id||'/'||q.quote_id,'Select quote '||q.quote_id||' for RFQ '||r.rfq_id,q.recorded_by,'#/procurement','procurement.approve',q.recorded_at FROM procurement.rfq r JOIN procurement.supplier_quote q USING(rfq_id) WHERE r.status='Released' AND q.recorded_by<>$1
              UNION ALL SELECT 'RootCause',root_cause_id::text,'Confirm root cause '||root_cause_id||' · '||cause,proposed_by,'#/shopfloor','execution.rootcause.confirm',proposed_at FROM execution.root_cause WHERE confirmed_at IS NULL AND proposed_by<>$1
              UNION ALL SELECT 'Delegation',delegation_id::text,'Temporary delegation · '||reason,'person:'||grantor_id,'#/settings','kernel.access.manage',proposed_at FROM kernel.authority_delegation WHERE status='PendingApproval' AND grantor_id<>$3 AND recipient_id<>$3 AND expires_at>$4
            ) approvals ORDER BY created_at,work_type,work_id
            """;
        var candidates = new List<Candidate>();
        await using (var connection = await dataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand(sql, connection))
        {
            cmd.Parameters.AddWithValue("person:" + principal.PersonId.Value.ToString("D"));
            cmd.Parameters.AddWithValue(principal.Roles.ToArray());
            cmd.Parameters.AddWithValue(principal.PersonId.Value);
            cmd.Parameters.AddWithValue(time.GetUtcNow());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) candidates.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6)));
        }
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var visible = new List<ApprovalInboxItem>();
        foreach (var candidate in candidates)
        {
            if (OwnedSourceFamilies.Contains(candidate.WorkType)
                && !await OwnedSourceFamilies.CanReadAsync(uow, sources, principal, candidate.WorkType, candidate.WorkId, ct)) continue;
            var source = await sources.ResolveAsync(uow, candidate.WorkType, candidate.WorkId, ct);
            if (source is not { IsActive: true, DecisionAvailable: true } || source.Resources.Count == 0 || source.DecisionExcludedActors?.Contains("person:" + principal.PersonId.Value.ToString("D"), StringComparer.Ordinal) == true || source.Resources.Any(resource =>
                !authorizer.Authorize(principal, source.ReadPermission, resource).IsPermitted || !authorizer.Authorize(principal, candidate.Permission, resource).IsPermitted)) continue;
            visible.Add(new(candidate.WorkType, candidate.WorkId, candidate.Title, candidate.Requester, candidate.DeepLink, candidate.Permission, candidate.CreatedAt));
            if (visible.Count == 500) break;
        }
        return visible;
    }

    public async Task<IReadOnlyList<WorkAssignment>> AssignmentsAsync(ForgePrincipal principal, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var visible = new List<WorkAssignment>();
        foreach (var assignment in await uow.Store<IWorkAssignmentStore>().ListAsync(principal.PersonId, ct))
        {
            if (OwnedSourceFamilies.Contains(assignment.WorkType)
                && !await OwnedSourceFamilies.CanReadAsync(uow, sources, principal, assignment.WorkType, assignment.WorkId, ct)) continue;
            var source = await sources.ResolveAsync(uow, assignment.WorkType, assignment.WorkId, ct);
            if (source is null || source.Resources.Count == 0 || source.Resources.Any(resource => !authorizer.Authorize(principal, source.ReadPermission, resource).IsPermitted)) continue;
            visible.Add(new(assignment.AssignmentId, assignment.WorkType, assignment.WorkId, assignment.Title, assignment.DeepLink, assignment.Status,
                assignment.Assignee.Value.ToString("D"), assignment.AssignedBy, assignment.AssignedAt, assignment.UpdatedAt, assignment.Reason));
        }
        return visible.OrderBy(assignment => assignment.Status switch { "Assigned" => 0, "Acknowledged" => 1, "InProgress" => 2, _ => 3 }).Take(500).ToList();
    }
}
