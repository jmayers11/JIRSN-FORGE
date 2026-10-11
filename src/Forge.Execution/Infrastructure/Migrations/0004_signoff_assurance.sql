ALTER TABLE execution.training_session
    ADD COLUMN version integer NOT NULL DEFAULT 1 CHECK (version > 0);

ALTER TABLE execution.electronic_signoff
    ADD COLUMN signer_role text NOT NULL DEFAULT 'Legacy',
    ADD COLUMN assurance text NOT NULL DEFAULT 'Legacy',
    ADD COLUMN record_version integer NOT NULL DEFAULT 1 CHECK (record_version > 0);
