-- V3.9: training on controlled documents as a readiness dimension (V3 brief B).
--
-- A person is trained on one revision of a controlled document; a new revision needs new training. The plant's
-- policy says whether readiness ignores training (Off), names untrained operators without stopping work (Warn) or
-- treats an operation as not executable until enough trained operators are present (Enforce), and for which kinds
-- of document. Training records are kept for good.

CREATE TABLE engineering.document_training (
    training_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    person_id        uuid NOT NULL REFERENCES kernel.person(person_id),
    document_number  text NOT NULL,
    revision         text NOT NULL,
    trained_on       date NOT NULL,
    recorded_at      timestamptz NOT NULL,
    recorded_by      text NOT NULL,
    FOREIGN KEY (document_number, revision) REFERENCES engineering.document_revision(document_number, revision),
    UNIQUE (person_id, document_number, revision)
);

CREATE FUNCTION engineering.reject_training_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'document training records are history and are never changed';
END $$;
CREATE TRIGGER document_training_append_only BEFORE UPDATE OR DELETE ON engineering.document_training
    FOR EACH ROW EXECUTE FUNCTION engineering.reject_training_change();

CREATE TABLE engineering.document_training_policy (
    policy_id       integer PRIMARY KEY CHECK (policy_id = 1),
    policy          text NOT NULL CHECK (policy IN ('Off', 'Warn', 'Enforce')),
    document_types  text[] NOT NULL,
    changed_at      timestamptz NOT NULL,
    changed_by      text NOT NULL
);
INSERT INTO engineering.document_training_policy (policy_id, policy, document_types, changed_at, changed_by)
VALUES (1, 'Off', '{WorkInstruction,Procedure}', now(), 'system');
