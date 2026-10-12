using Forge.Kernel.Security;
using Forge.Kernel.Identity;
using Npgsql;

namespace Forge.Host.WorkInbox;

public sealed record ApprovalInboxItem(string WorkType, string WorkId, string Title, string Requester, string DeepLink, string Permission, DateTimeOffset CreatedAt);
public sealed record WorkAssignment(Guid AssignmentId, string WorkType, string WorkId, string Title, string DeepLink, string Status, string Assignee, string AssignedBy, DateTimeOffset AssignedAt, DateTimeOffset UpdatedAt, string Reason);
public sealed record AssignWorkRequest(string WorkType, string WorkId, string Title, string DeepLink, Guid AssigneePersonId, string Reason);
public sealed record AssignmentStatusRequest(string Status, string Reason);

public sealed class WorkInboxStore(NpgsqlDataSource dataSource, TimeProvider time)
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
              UNION ALL SELECT 'Alternate',alternate_id::text,'Alternate '||primary_part||' → '||alternate_part,proposed_by,'#/engineering','maintenance.alternates.approve',proposed_at FROM engineering.alternate WHERE status='Proposed' AND proposed_by<>$1
              UNION ALL SELECT 'EngineeringReview',change_number||'/'||round||'/'||review_kind,'Review '||review_kind||' for '||change_number,originator,'#/engineering','engineering.changes.review',created_at FROM engineering.change c JOIN engineering.change_review r USING(change_number) WHERE c.status='InReview' AND r.decision IS NULL AND r.required_by=ANY($2::text[]) AND c.originator<>$1
            ) approvals ORDER BY created_at LIMIT 500
            """;
        var candidates = new List<Candidate>();
        await using (var connection = await dataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand(sql, connection))
        {
            cmd.Parameters.AddWithValue("person:" + principal.PersonId.Value.ToString("D"));
            cmd.Parameters.AddWithValue(principal.Roles.ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) candidates.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6)));
        }
        return candidates.Where(c => principal.HasPermission(c.Permission, ResourceRef.Of(c.WorkType, c.WorkId)))
            .Select(c => new ApprovalInboxItem(c.WorkType, c.WorkId, c.Title, c.Requester, c.DeepLink, c.Permission, c.CreatedAt)).ToArray();
    }

    public async Task<IReadOnlyList<WorkAssignment>> AssignmentsAsync(ForgePrincipal principal, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT assignment_id,work_type,work_id,title,deep_link,status,assignee_person_id,assigned_by,assigned_at,updated_at,reason FROM kernel.work_assignment WHERE assignee_person_id=$1 ORDER BY CASE status WHEN 'Assigned' THEN 0 WHEN 'Acknowledged' THEN 1 WHEN 'InProgress' THEN 2 ELSE 3 END,updated_at DESC LIMIT 500", connection);
        cmd.Parameters.AddWithValue(principal.PersonId.Value);
        var rows = new List<WorkAssignment>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetGuid(6).ToString("D"), reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8), reader.GetFieldValue<DateTimeOffset>(9), reader.GetString(10)));
        return rows;
    }

    public async Task<Guid> AssignAsync(ForgePrincipal principal, AssignWorkRequest request, CancellationToken ct)
    {
        if (!principal.HasPermission("work.assignments.manage")) throw new UnauthorizedAccessException("Assignment management permission is required.");
        Validate(request.WorkType, request.WorkId, request.Title, request.DeepLink, request.Reason);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        if (!await SourceExistsAsync(connection, request.WorkType, request.WorkId, ct)) throw new KeyNotFoundException("The source work item does not exist or is no longer active.");
        await using var tx = await connection.BeginTransactionAsync(ct);
        Guid id;
        await using (var cmd = new NpgsqlCommand("INSERT INTO kernel.work_assignment(assignment_id,work_type,work_id,title,deep_link,assignee_person_id,assigned_by,reason) SELECT $1,$2,$3,$4,$5,$6,'person:'||$7,$8 WHERE EXISTS(SELECT 1 FROM kernel.person WHERE person_id=$6 AND is_active) ON CONFLICT(work_type,work_id,assignee_person_id) DO UPDATE SET title=EXCLUDED.title,deep_link=EXCLUDED.deep_link,assigned_by=EXCLUDED.assigned_by,status='Assigned',assigned_at=now(),updated_at=now(),completed_at=NULL,reason=EXCLUDED.reason", connection, tx))
        {
            cmd.CommandText += " RETURNING assignment_id";
            cmd.Parameters.AddWithValue(ForgeIds.New(time)); cmd.Parameters.AddWithValue(request.WorkType); cmd.Parameters.AddWithValue(request.WorkId); cmd.Parameters.AddWithValue(request.Title.Trim()); cmd.Parameters.AddWithValue(request.DeepLink); cmd.Parameters.AddWithValue(request.AssigneePersonId); cmd.Parameters.AddWithValue(principal.PersonId.Value.ToString("D")); cmd.Parameters.AddWithValue(request.Reason.Trim());
            id = (Guid?)await cmd.ExecuteScalarAsync(ct) ?? throw new KeyNotFoundException("The assignee is not an active Forge person.");
        }
        await using (var history = new NpgsqlCommand("INSERT INTO kernel.work_assignment_history(assignment_id,new_status,changed_by,reason) VALUES($1,'Assigned','person:'||$2,$3)", connection, tx))
        { history.Parameters.AddWithValue(id); history.Parameters.AddWithValue(principal.PersonId.Value.ToString("D")); history.Parameters.AddWithValue(request.Reason.Trim()); await history.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct); return id;
    }

    public async Task UpdateStatusAsync(Guid id, ForgePrincipal principal, AssignmentStatusRequest request, CancellationToken ct)
    {
        var allowed = request.Status switch { "Acknowledged" => new[] { "Assigned" }, "InProgress" => new[] { "Acknowledged" }, "Completed" => new[] { "InProgress" }, _ => Array.Empty<string>() };
        if (allowed.Length == 0 || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500) throw new ArgumentException("Use the next assignment state and provide a reason.");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        string? oldStatus;
        await using (var cmd = new NpgsqlCommand("SELECT status FROM kernel.work_assignment WHERE assignment_id=$1 AND assignee_person_id=$2 FOR UPDATE", connection, tx))
        { cmd.Parameters.AddWithValue(id); cmd.Parameters.AddWithValue(principal.PersonId.Value); oldStatus = (string?)await cmd.ExecuteScalarAsync(ct); }
        if (oldStatus is null) throw new KeyNotFoundException("Assignment was not found.");
        if (!allowed.Contains(oldStatus)) throw new InvalidOperationException($"Cannot move an assignment from {oldStatus} to {request.Status}.");
        await using (var cmd = new NpgsqlCommand("UPDATE kernel.work_assignment SET status=$2,updated_at=now(),completed_at=CASE WHEN $2='Completed' THEN now() ELSE NULL END WHERE assignment_id=$1", connection, tx))
        { cmd.Parameters.AddWithValue(id); cmd.Parameters.AddWithValue(request.Status); await cmd.ExecuteNonQueryAsync(ct); }
        await using (var cmd = new NpgsqlCommand("INSERT INTO kernel.work_assignment_history(assignment_id,old_status,new_status,changed_by,reason) VALUES($1,$2,$3,'person:'||$4,$5)", connection, tx))
        { cmd.Parameters.AddWithValue(id); cmd.Parameters.AddWithValue(oldStatus); cmd.Parameters.AddWithValue(request.Status); cmd.Parameters.AddWithValue(principal.PersonId.Value.ToString("D")); cmd.Parameters.AddWithValue(request.Reason.Trim()); await cmd.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
    }

    private static void Validate(string type, string id, string title, string link, string reason)
    {
        if (type is not ("NCR" or "Inspection" or "WorkOrder" or "RFQ" or "ProjectReview" or "MaterialRequest") || string.IsNullOrWhiteSpace(id) || id.Length > 100 || string.IsNullOrWhiteSpace(title) || title.Trim().Length > 240 || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
            throw new ArgumentException("A supported work type, identifier, title and reason are required.");
        if (link is not ("#/quality" or "#/procurement" or "#/maintenance" or "#/projects" or "#/inventory" or "#/engineering" or "#/shopfloor")) throw new ArgumentException("The work link must be a local Forge page.");
    }

    private static async Task<bool> SourceExistsAsync(NpgsqlConnection connection, string type, string id, CancellationToken ct)
    {
        var sql = type switch
        {
            "NCR" => "SELECT EXISTS(SELECT 1 FROM quality.ncr WHERE ncr_id::text=$1 AND status<>'Closed')",
            "Inspection" => "SELECT EXISTS(SELECT 1 FROM quality.inspection WHERE inspection_id::text=$1)",
            "WorkOrder" => "SELECT EXISTS(SELECT 1 FROM maintenance.work_order WHERE work_order_id::text=$1 AND status IN ('Open','Scheduled','InProgress','Completed'))",
            "RFQ" => "SELECT EXISTS(SELECT 1 FROM procurement.rfq WHERE rfq_id::text=$1 AND status IN ('Draft','Released'))",
            "ProjectReview" => "SELECT EXISTS(SELECT 1 FROM projects.project WHERE code=$1 AND status='Draft') OR EXISTS(SELECT 1 FROM projects.change WHERE change_id::text=$1 AND status='Pending')",
            "MaterialRequest" => "SELECT EXISTS(SELECT 1 FROM inventory.material_task WHERE task_id::text=$1 AND status IN ('Requested','Allocated','Picked'))",
            _ => "SELECT false",
        };
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue(id);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }
}
