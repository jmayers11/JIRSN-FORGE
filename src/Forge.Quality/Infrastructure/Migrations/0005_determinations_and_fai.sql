-- V3.11: determinations and first article inspection (V3 brief R; determinations moved here from V3.1).
--
-- A determination records whether something applies to a subject and why: for first article inspection (FAI), a
-- part revision from a supplier is Required, NotRequired, NotApplicable or PendingDetermination. Until someone
-- decides, it is pending and nothing that depends on it is shown as complete. Every determination is kept; the
-- latest one for a question and subject is in force.
--
-- An FAI records structured results against the part revision from that supplier, with the signed (released) FAI
-- report document it is backed by.

CREATE TABLE quality.determination (
    determination_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    question          text NOT NULL CHECK (question IN ('FAI')),
    subject           text NOT NULL,
    state             text NOT NULL CHECK (state IN ('Required', 'NotRequired', 'NotApplicable', 'PendingDetermination')),
    basis             text NOT NULL,
    decided_by        text NOT NULL,
    decided_at        timestamptz NOT NULL
);
CREATE INDEX determination_subject ON quality.determination (question, subject, determination_id DESC);

CREATE TABLE quality.fai (
    fai_id             bigint GENERATED ALWAYS AS IDENTITY (START WITH 5001) PRIMARY KEY,
    subject            text NOT NULL,
    part_number        text NOT NULL,
    part_revision      text NOT NULL,
    supplier_code      text NOT NULL,
    passed             boolean NOT NULL,
    results            jsonb NOT NULL,
    document_number    text NOT NULL,
    document_revision  text NOT NULL,
    recorded_by        text NOT NULL,
    recorded_at        timestamptz NOT NULL,
    FOREIGN KEY (document_number, document_revision) REFERENCES engineering.document_revision(document_number, revision)
);
CREATE INDEX fai_subject ON quality.fai (subject, fai_id DESC);

CREATE FUNCTION quality.reject_record_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% rows are records and are never changed', TG_TABLE_NAME;
END $$;
CREATE TRIGGER determination_append_only BEFORE UPDATE OR DELETE ON quality.determination
    FOR EACH ROW EXECUTE FUNCTION quality.reject_record_change();
CREATE TRIGGER fai_append_only BEFORE UPDATE OR DELETE ON quality.fai
    FOR EACH ROW EXECUTE FUNCTION quality.reject_record_change();
