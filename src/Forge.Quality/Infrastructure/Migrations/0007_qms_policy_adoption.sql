-- Operational adoption is a governed binding, not an inferred policy default or a tenancy mechanism.
CREATE TABLE quality.qms_policy_adoption (
    code text PRIMARY KEY CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,39}$'),
    organization_code text NOT NULL,
    facility_code text NOT NULL REFERENCES resources.site(code),
    program_id uuid NULL REFERENCES programs.program(program_id),
    purpose text NOT NULL CHECK (purpose IN ('Execution','ProductRelease','LabelCopy','AttemptRetention')),
    markings integer NOT NULL CHECK (markings BETWEEN 0 AND 7),
    created_by text NOT NULL,
    created_at timestamptz NOT NULL
);
CREATE INDEX qms_policy_adoption_scope ON quality.qms_policy_adoption(organization_code,facility_code,purpose);
CREATE TABLE quality.qms_policy_adoption_revision (
    code text NOT NULL REFERENCES quality.qms_policy_adoption(code),
    revision integer NOT NULL CHECK (revision>0),
    version integer NOT NULL CHECK (version>0),
    state text NOT NULL CHECK (state IN ('Draft','Submitted','Reviewed','Approved','Effective','Superseded','Withdrawn')),
    content jsonb NOT NULL CHECK (jsonb_typeof(content)='object'),
    policy_code text NOT NULL,
    policy_revision integer NOT NULL,
    created_by text NOT NULL,
    last_edited_by text NOT NULL,
    reviewed_by text NULL,
    approved_by text NULL,
    effective_from timestamptz NULL,
    effective_to timestamptz NULL,
    PRIMARY KEY(code,revision),
    FOREIGN KEY(policy_code,policy_revision) REFERENCES quality.qms_revision(code,revision),
    CHECK (policy_code=upper(btrim(content->>'policyCode')) AND policy_revision=(content->>'policyRevision')::integer),
    CHECK (reviewed_by IS NULL OR reviewed_by NOT IN (created_by,last_edited_by)),
    CHECK (approved_by IS NULL OR approved_by NOT IN (created_by,last_edited_by)),
    CHECK (state NOT IN ('Reviewed','Approved','Effective','Superseded') OR reviewed_by IS NOT NULL),
    CHECK (state NOT IN ('Approved','Effective','Superseded') OR approved_by IS NOT NULL),
    CHECK (state NOT IN ('Effective','Superseded') OR effective_from IS NOT NULL),
    CHECK (effective_to IS NULL OR effective_from IS NOT NULL AND effective_to>=effective_from)
);
CREATE UNIQUE INDEX qms_adoption_one_effective ON quality.qms_policy_adoption_revision(code) WHERE state='Effective';
CREATE UNIQUE INDEX qms_adoption_one_open ON quality.qms_policy_adoption_revision(code) WHERE state IN ('Draft','Submitted','Reviewed','Approved');
CREATE TABLE quality.qms_policy_adoption_source (
    code text NOT NULL,
    revision integer NOT NULL,
    source_code text NOT NULL,
    source_revision integer NOT NULL,
    program_id uuid NULL,
    markings integer NOT NULL CHECK (markings BETWEEN 0 AND 7),
    facility_code text NOT NULL,
    PRIMARY KEY(code,revision,source_code,source_revision),
    FOREIGN KEY(code,revision) REFERENCES quality.qms_policy_adoption_revision(code,revision),
    FOREIGN KEY(source_code,source_revision) REFERENCES quality.qms_revision(code,revision)
);
CREATE TABLE quality.qms_policy_adoption_transition (
    code text NOT NULL,
    revision integer NOT NULL,
    version integer NOT NULL,
    action text NOT NULL,
    actor text NOT NULL,
    at timestamptz NOT NULL,
    note text NOT NULL,
    reason text NOT NULL CHECK(length(btrim(reason))>0),
    from_state text NULL,
    to_state text NOT NULL,
    PRIMARY KEY(code,revision,version),
    FOREIGN KEY(code,revision) REFERENCES quality.qms_policy_adoption_revision(code,revision)
);
CREATE TRIGGER qms_adoption_header_immutable BEFORE UPDATE OR DELETE ON quality.qms_policy_adoption FOR EACH ROW EXECUTE FUNCTION quality.reject_record_change();
CREATE TRIGGER qms_adoption_history_immutable BEFORE UPDATE OR DELETE ON quality.qms_policy_adoption_transition FOR EACH ROW EXECUTE FUNCTION quality.reject_record_change();
CREATE FUNCTION quality.guard_qms_policy_adoption() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE owning_purpose text;
BEGIN
    IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Governed policy adoption history is never deleted'; END IF;
    SELECT purpose INTO owning_purpose FROM quality.qms_policy_adoption WHERE code=NEW.code;
    IF NEW.content->>'purpose' IS DISTINCT FROM owning_purpose THEN RAISE EXCEPTION 'An adoption cannot change its governing purpose'; END IF;
    IF TG_OP='INSERT' THEN
        IF NEW.state<>'Draft' OR NEW.version<>1 OR NEW.reviewed_by IS NOT NULL OR NEW.approved_by IS NOT NULL OR NEW.effective_from IS NOT NULL OR NEW.effective_to IS NOT NULL THEN
            RAISE EXCEPTION 'A new adoption revision must begin as an unattributed-to-review draft';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.code<>OLD.code OR NEW.revision<>OLD.revision OR NEW.created_by<>OLD.created_by OR NEW.version<>OLD.version+1 THEN RAISE EXCEPTION 'Adoption identity and sequential versions are immutable'; END IF;
    IF OLD.state<>'Draft' AND (NEW.content<>OLD.content OR NEW.policy_code<>OLD.policy_code OR NEW.policy_revision<>OLD.policy_revision OR NEW.last_edited_by<>OLD.last_edited_by) THEN
        RAISE EXCEPTION 'Submitted adoption content is immutable';
    END IF;
    IF NOT ((OLD.state='Draft' AND NEW.state IN ('Draft','Submitted','Withdrawn'))
        OR (OLD.state='Submitted' AND NEW.state IN ('Reviewed','Withdrawn'))
        OR (OLD.state='Reviewed' AND NEW.state IN ('Approved','Withdrawn'))
        OR (OLD.state='Approved' AND NEW.state IN ('Effective','Withdrawn'))
        OR (OLD.state='Effective' AND NEW.state IN ('Superseded','Withdrawn'))) THEN RAISE EXCEPTION 'Invalid adoption lifecycle transition'; END IF;
    IF EXISTS(SELECT 1 FROM quality.qms_policy_adoption_transition h WHERE h.code=OLD.code AND h.revision=OLD.revision AND h.action IN ('Create','Edit') AND (h.actor=NEW.reviewed_by OR h.actor=NEW.approved_by)) THEN
        RAISE EXCEPTION 'An adoption author cannot review or approve their own binding';
    END IF;
    IF OLD.reviewed_by IS NOT NULL AND NEW.reviewed_by IS DISTINCT FROM OLD.reviewed_by OR OLD.approved_by IS NOT NULL AND NEW.approved_by IS DISTINCT FROM OLD.approved_by
        OR OLD.effective_from IS NOT NULL AND NEW.effective_from IS DISTINCT FROM OLD.effective_from THEN RAISE EXCEPTION 'Recorded adoption approval and effectivity provenance is immutable'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER qms_adoption_revision_guard BEFORE INSERT OR UPDATE OR DELETE ON quality.qms_policy_adoption_revision FOR EACH ROW EXECUTE FUNCTION quality.guard_qms_policy_adoption();
CREATE FUNCTION quality.guard_qms_adoption_source() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE current_state text;
BEGIN
    SELECT state INTO current_state FROM quality.qms_policy_adoption_revision WHERE code=COALESCE(NEW.code,OLD.code) AND revision=COALESCE(NEW.revision,OLD.revision);
    IF current_state<>'Draft' THEN RAISE EXCEPTION 'Submitted adoption sources are immutable'; END IF;
    IF TG_OP='DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER qms_adoption_source_guard BEFORE INSERT OR UPDATE OR DELETE ON quality.qms_policy_adoption_source FOR EACH ROW EXECUTE FUNCTION quality.guard_qms_adoption_source();
