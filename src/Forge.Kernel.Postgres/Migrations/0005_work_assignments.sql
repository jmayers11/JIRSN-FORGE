CREATE TABLE kernel.work_assignment (
    assignment_id uuid PRIMARY KEY,
    work_type text NOT NULL CHECK (work_type IN ('NCR','Inspection','WorkOrder','RFQ','ProjectReview','MaterialRequest')),
    work_id text NOT NULL,
    title text NOT NULL CHECK (length(trim(title)) BETWEEN 1 AND 240),
    deep_link text NOT NULL CHECK (deep_link LIKE '#/%' AND deep_link NOT LIKE '#/%/%/%/%/%'),
    assignee_person_id uuid NOT NULL REFERENCES kernel.person(person_id),
    assigned_by text NOT NULL,
    status text NOT NULL DEFAULT 'Assigned' CHECK (status IN ('Assigned','Acknowledged','InProgress','Completed')),
    assigned_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz NULL,
    reason text NOT NULL CHECK (length(trim(reason)) BETWEEN 1 AND 500),
    CHECK ((status = 'Completed') = (completed_at IS NOT NULL)),
    UNIQUE(work_type, work_id, assignee_person_id)
);
CREATE INDEX work_assignment_inbox ON kernel.work_assignment(assignee_person_id,status,updated_at DESC);
CREATE TABLE kernel.work_assignment_history (
    history_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    assignment_id uuid NOT NULL REFERENCES kernel.work_assignment(assignment_id),
    old_status text NULL,
    new_status text NOT NULL,
    changed_by text NOT NULL,
    reason text NOT NULL,
    changed_at timestamptz NOT NULL DEFAULT now()
);
CREATE FUNCTION kernel.reject_work_assignment_history_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'work assignment history is append only' USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER work_assignment_history_kept BEFORE UPDATE OR DELETE ON kernel.work_assignment_history
FOR EACH ROW EXECUTE FUNCTION kernel.reject_work_assignment_history_change();
INSERT INTO kernel.permission_catalog(permission,description) VALUES
 ('work.assignments.manage','Assign and reassign tracked work to people')
ON CONFLICT (permission) DO NOTHING;
INSERT INTO kernel.role_permission(role_name,permission)
SELECT role_name,'work.assignments.manage' FROM kernel.role_definition
WHERE role_name IN ('SecurityAdministrator','Supervisor') ON CONFLICT DO NOTHING;
