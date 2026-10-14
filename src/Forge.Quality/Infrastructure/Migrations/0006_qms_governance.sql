-- V4.3 foundation: one explicitly configured organization per deployment. This is not a tenant boundary.
-- Engineering owns document content; QMS records reference exact revisions and preserve human applicability.
CREATE TABLE quality.qms_record (
    code text PRIMARY KEY CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,39}$'),
    kind text NOT NULL CHECK (kind IN ('Profile','Process','Policy','Requirement')),
    organization_code text NOT NULL,
    facility_code text NOT NULL REFERENCES resources.site(code),
    program_id uuid NULL REFERENCES programs.program(program_id),
    markings integer NOT NULL CHECK (markings BETWEEN 0 AND 7),
    created_by text NOT NULL,
    created_at timestamptz NOT NULL
);
CREATE TABLE quality.qms_revision (
    code text NOT NULL REFERENCES quality.qms_record(code),
    revision integer NOT NULL CHECK (revision > 0),
    version integer NOT NULL CHECK (version > 0),
    state text NOT NULL CHECK (state IN ('Draft','Submitted','Reviewed','Approved','Effective','Superseded','Withdrawn')),
    content jsonb NOT NULL CHECK (jsonb_typeof(content) = 'object'),
    created_by text NOT NULL,
    last_edited_by text NOT NULL,
    reviewed_by text NULL,
    approved_by text NULL,
    effective_from timestamptz NULL,
    effective_to timestamptz NULL,
    requires_licensed_verification boolean NOT NULL,
    PRIMARY KEY (code,revision),
    CHECK (effective_to IS NULL OR effective_from IS NOT NULL AND effective_to >= effective_from),
    CHECK (reviewed_by IS NULL OR reviewed_by NOT IN (created_by,last_edited_by)),
    CHECK (approved_by IS NULL OR approved_by NOT IN (created_by,last_edited_by)),
    CHECK (state NOT IN ('Reviewed','Approved','Effective','Superseded') OR reviewed_by IS NOT NULL),
    CHECK (state NOT IN ('Approved','Effective','Superseded') OR approved_by IS NOT NULL),
    CHECK (state NOT IN ('Effective','Superseded') OR effective_from IS NOT NULL)
);
CREATE UNIQUE INDEX qms_one_effective ON quality.qms_revision(code) WHERE state='Effective';
CREATE UNIQUE INDEX qms_one_open_revision ON quality.qms_revision(code) WHERE state IN ('Draft','Submitted','Reviewed','Approved');
CREATE TABLE quality.qms_revision_source (
    code text NOT NULL,
    revision integer NOT NULL,
    kind text NOT NULL CHECK (kind IN ('DocumentRevision','QmsRevision')),
    source_key text NOT NULL,
    source_revision text NOT NULL,
    program_id uuid NULL,
    markings integer NOT NULL CHECK (markings BETWEEN 0 AND 7),
    facility_code text NULL,
    document_number text NULL,
    document_revision text NULL,
    qms_code text NULL,
    qms_revision integer NULL,
    PRIMARY KEY(code,revision,kind,source_key,source_revision),
    FOREIGN KEY(code,revision) REFERENCES quality.qms_revision(code,revision),
    FOREIGN KEY(document_number,document_revision) REFERENCES engineering.document_revision(document_number,revision),
    FOREIGN KEY(qms_code,qms_revision) REFERENCES quality.qms_revision(code,revision),
    CHECK ((kind='DocumentRevision' AND document_number=source_key AND document_revision=source_revision AND qms_code IS NULL AND qms_revision IS NULL)
        OR (kind='QmsRevision' AND qms_code=source_key AND qms_revision::text=source_revision AND document_number IS NULL AND document_revision IS NULL))
);
CREATE TABLE quality.qms_transition (
    code text NOT NULL,
    revision integer NOT NULL,
    version integer NOT NULL,
    action text NOT NULL,
    actor text NOT NULL,
    at timestamptz NOT NULL,
    note text NOT NULL,
    reason text NOT NULL CHECK (length(btrim(reason)) > 0),
    from_state text NULL,
    to_state text NOT NULL,
    PRIMARY KEY(code,revision,version),
    FOREIGN KEY(code,revision) REFERENCES quality.qms_revision(code,revision)
);
CREATE TRIGGER qms_record_immutable BEFORE UPDATE OR DELETE ON quality.qms_record
    FOR EACH ROW EXECUTE FUNCTION quality.reject_record_change();
CREATE TRIGGER qms_transition_immutable BEFORE UPDATE OR DELETE ON quality.qms_transition
    FOR EACH ROW EXECUTE FUNCTION quality.reject_record_change();
CREATE FUNCTION quality.guard_qms_revision() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP='DELETE' THEN RAISE EXCEPTION 'QMS revision history is never deleted'; END IF;
    IF NEW.code<>OLD.code OR NEW.revision<>OLD.revision OR NEW.created_by<>OLD.created_by OR NEW.version<>OLD.version+1 THEN
        RAISE EXCEPTION 'QMS identity and sequential version history are immutable';
    END IF;
    IF OLD.state<>'Draft' AND (NEW.content<>OLD.content OR NEW.last_edited_by<>OLD.last_edited_by OR NEW.requires_licensed_verification<>OLD.requires_licensed_verification) THEN
        RAISE EXCEPTION 'Submitted QMS content is immutable; create a new governed revision';
    END IF;
    IF NOT ((OLD.state='Draft' AND NEW.state IN ('Draft','Submitted','Withdrawn'))
        OR (OLD.state='Submitted' AND NEW.state IN ('Reviewed','Withdrawn'))
        OR (OLD.state='Reviewed' AND NEW.state IN ('Approved','Withdrawn'))
        OR (OLD.state='Approved' AND NEW.state IN ('Effective','Withdrawn'))
        OR (OLD.state='Effective' AND NEW.state IN ('Superseded','Withdrawn'))) THEN
        RAISE EXCEPTION 'Invalid governed QMS lifecycle transition';
    END IF;
    IF EXISTS (SELECT 1 FROM quality.qms_transition h WHERE h.code=OLD.code AND h.revision=OLD.revision
        AND h.action IN ('Create','Edit') AND (h.actor=NEW.reviewed_by OR h.actor=NEW.approved_by)) THEN
        RAISE EXCEPTION 'A recorded content author cannot independently review or approve that revision';
    END IF;
    IF OLD.reviewed_by IS NOT NULL AND NEW.reviewed_by IS DISTINCT FROM OLD.reviewed_by
        OR OLD.approved_by IS NOT NULL AND NEW.approved_by IS DISTINCT FROM OLD.approved_by
        OR OLD.effective_from IS NOT NULL AND NEW.effective_from IS DISTINCT FROM OLD.effective_from THEN
        RAISE EXCEPTION 'Recorded QMS review, approval and effectivity provenance is immutable';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER qms_revision_guard BEFORE UPDATE OR DELETE ON quality.qms_revision FOR EACH ROW EXECUTE FUNCTION quality.guard_qms_revision();
CREATE FUNCTION quality.guard_qms_source() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE current_state text;
BEGIN
    SELECT state INTO current_state FROM quality.qms_revision WHERE code=COALESCE(NEW.code,OLD.code) AND revision=COALESCE(NEW.revision,OLD.revision);
    IF current_state<>'Draft' THEN RAISE EXCEPTION 'Submitted QMS source references are immutable'; END IF;
    IF TG_OP='DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER qms_source_guard BEFORE INSERT OR UPDATE OR DELETE ON quality.qms_revision_source FOR EACH ROW EXECUTE FUNCTION quality.guard_qms_source();
