-- Department is operational profile metadata; existing workers remain blank until managed explicitly.
ALTER TABLE workforce.worker
    ADD COLUMN department text NULL CHECK (department IS NULL OR length(department) <= 100);
