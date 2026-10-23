-- Official employee title shown in Workforce Kanban and editable through an audited command.
ALTER TABLE workforce.worker
    ADD COLUMN job_title text NULL CHECK (job_title IS NULL OR length(job_title) <= 100),
    ADD COLUMN updated_at timestamptz NULL,
    ADD COLUMN updated_by text NULL;
