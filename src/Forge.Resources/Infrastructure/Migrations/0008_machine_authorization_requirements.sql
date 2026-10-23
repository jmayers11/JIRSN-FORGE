ALTER TABLE resources.machine_activity
    ADD COLUMN requires_machine_authorization boolean NOT NULL DEFAULT false;
