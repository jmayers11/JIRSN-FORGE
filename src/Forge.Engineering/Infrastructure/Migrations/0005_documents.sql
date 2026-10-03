-- V2.2: controlled documents (V2 §2-3) and documents attached to records (owner request at V2 approval).
-- A document has an authority: Internal (we control it: draft, review, approval, release), External (a
-- customer, standards body or manufacturer controls it; Forge only records which revision applies) or
-- Reference (an uncontrolled attachment such as a customer PO, photo or certificate; never production
-- authority). Files are stored once by SHA-256 hash and never change.

CREATE TABLE engineering.document_file (
    sha256       text PRIMARY KEY CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    size_bytes   bigint NOT NULL CHECK (size_bytes > 0 AND size_bytes <= 52428800),
    content      bytea NOT NULL,
    stored_by    text NOT NULL,
    stored_at    timestamptz NOT NULL,
    CHECK (octet_length(content) = size_bytes)
);

CREATE FUNCTION engineering.reject_file_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'document files never change' USING ERRCODE = 'insufficient_privilege';
END;
$$;

CREATE TRIGGER document_file_immutable BEFORE UPDATE OR DELETE ON engineering.document_file
    FOR EACH ROW EXECUTE FUNCTION engineering.reject_file_change();

CREATE SEQUENCE engineering.reference_document_seq START 1;

CREATE TABLE engineering.document (
    number              text PRIMARY KEY CHECK (number ~ '^[A-Z0-9][A-Z0-9._-]{0,59}$'),
    title               text NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    doc_type            text NOT NULL CHECK (doc_type IN ('Drawing', 'Specification', 'Procedure', 'WorkInstruction', 'InspectionPlan', 'Standard', 'Datasheet', 'Certificate', 'PurchaseDocument', 'Contract', 'Photo', 'Other')),
    authority           text NOT NULL CHECK (authority IN ('Internal', 'External', 'Reference')),
    external_authority  text NULL,
    external_number     text NULL,
    program_id          uuid NULL,
    markings            integer NOT NULL DEFAULT 0 CHECK (markings BETWEEN 0 AND 7),
    created_by          text NOT NULL,
    created_at          timestamptz NOT NULL,
    changed_by          text NULL,
    changed_at          timestamptz NULL,
    CHECK (authority <> 'External' OR external_authority IS NOT NULL),
    CHECK (authority = 'External' OR (external_authority IS NULL AND external_number IS NULL))
);

-- Statuses by authority:
--   Internal:  Draft -> InReview -> Approved -> Released -> Superseded; Draft/InReview/Approved/Released -> Obsolete
--   External:  Draft -> Applicable -> Superseded; Draft/Applicable -> Obsolete
--   Reference: Uncontrolled -> Obsolete
CREATE TABLE engineering.document_revision (
    document_number  text NOT NULL REFERENCES engineering.document(number),
    revision         text NOT NULL CHECK (revision ~ '^[A-Z0-9][A-Z0-9._-]{0,19}$'),
    status           text NOT NULL CHECK (status IN ('Draft', 'InReview', 'Approved', 'Released', 'Applicable', 'Superseded', 'Obsolete', 'Uncontrolled')),
    file_sha256      text NULL REFERENCES engineering.document_file(sha256),
    file_name        text NULL CHECK (file_name IS NULL OR length(file_name) BETWEEN 1 AND 200),
    change_note      text NULL,
    review_by        date NULL,
    author           text NOT NULL,
    authored_at      timestamptz NOT NULL,
    submitted_by     text NULL,
    submitted_at     timestamptz NULL,
    approved_by      text NULL,
    approved_at      timestamptz NULL,
    released_by      text NULL,
    released_at      timestamptz NULL,
    effective_from   date NULL,
    effective_to     date NULL,
    obsoleted_by     text NULL,
    obsoleted_at     timestamptz NULL,
    PRIMARY KEY (document_number, revision),
    CHECK (approved_by IS NULL OR approved_by <> author),
    CHECK (released_by IS NULL OR released_by <> author),
    CHECK (status NOT IN ('Released', 'Applicable', 'Uncontrolled', 'Approved', 'InReview') OR file_sha256 IS NOT NULL),
    CHECK (status NOT IN ('Released', 'Applicable', 'Superseded') OR (released_by IS NOT NULL AND effective_from IS NOT NULL)),
    CHECK (effective_to IS NULL OR effective_to >= effective_from),
    CHECK ((file_sha256 IS NULL) = (file_name IS NULL))
);

-- At most one current (released or applicable) revision per document: effectivity never overlaps.
CREATE UNIQUE INDEX document_revision_current ON engineering.document_revision (document_number)
    WHERE status IN ('Released', 'Applicable');

CREATE FUNCTION engineering.check_document_revision() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    doc_authority text;
BEGIN
    SELECT authority INTO doc_authority FROM engineering.document WHERE number = NEW.document_number;
    IF (doc_authority = 'Internal' AND NEW.status NOT IN ('Draft', 'InReview', 'Approved', 'Released', 'Superseded', 'Obsolete'))
        OR (doc_authority = 'External' AND NEW.status NOT IN ('Draft', 'Applicable', 'Superseded', 'Obsolete'))
        OR (doc_authority = 'Reference' AND NEW.status NOT IN ('Uncontrolled', 'Obsolete')) THEN
        RAISE EXCEPTION 'status % is not allowed for a % document', NEW.status, doc_authority USING ERRCODE = 'check_violation';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        IF OLD.status IN ('Released', 'Applicable', 'Superseded', 'Obsolete', 'Uncontrolled') AND (
            NEW.file_sha256 IS DISTINCT FROM OLD.file_sha256 OR NEW.file_name IS DISTINCT FROM OLD.file_name
            OR NEW.change_note IS DISTINCT FROM OLD.change_note OR NEW.author IS DISTINCT FROM OLD.author
            OR NEW.approved_by IS DISTINCT FROM OLD.approved_by OR NEW.released_by IS DISTINCT FROM OLD.released_by
            OR NEW.effective_from IS DISTINCT FROM OLD.effective_from) THEN
            RAISE EXCEPTION 'revision %/% is % and frozen', OLD.document_number, OLD.revision, OLD.status USING ERRCODE = 'insufficient_privilege';
        END IF;
        IF OLD.status IN ('Superseded', 'Obsolete') AND NEW.status IS DISTINCT FROM OLD.status THEN
            RAISE EXCEPTION 'revision %/% is % for good', OLD.document_number, OLD.revision, OLD.status USING ERRCODE = 'insufficient_privilege';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER document_revision_rules BEFORE INSERT OR UPDATE ON engineering.document_revision
    FOR EACH ROW EXECUTE FUNCTION engineering.check_document_revision();

CREATE FUNCTION engineering.reject_revision_delete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'document revisions are never deleted' USING ERRCODE = 'insufficient_privilege';
END;
$$;

CREATE TRIGGER document_revision_kept BEFORE DELETE ON engineering.document_revision
    FOR EACH ROW EXECUTE FUNCTION engineering.reject_revision_delete();

-- A document attached to a record. A link names exactly one target. Purpose Follow means the record must
-- also follow this document; Information is for reference only. Detaching keeps the row (history).
CREATE TABLE engineering.document_link (
    link_id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_number  text NOT NULL REFERENCES engineering.document(number),
    revision         text NULL,
    target_type      text NOT NULL CHECK (target_type IN ('Item', 'ItemRevision', 'Supplier', 'Program', 'ProductionOrder', 'PurchaseOrder', 'WorkOrder', 'Ncr', 'WorkCenter')),
    target_id        text NOT NULL CHECK (length(target_id) BETWEEN 1 AND 100),
    target_label     text NOT NULL,
    program_id       uuid NULL,
    purpose          text NOT NULL CHECK (purpose IN ('Follow', 'Information')),
    note             text NULL,
    linked_by        text NOT NULL,
    linked_at        timestamptz NOT NULL,
    detached_by      text NULL,
    detached_at      timestamptz NULL,
    detach_reason    text NULL,
    FOREIGN KEY (document_number, revision) REFERENCES engineering.document_revision(document_number, revision),
    CHECK ((detached_at IS NULL) = (detached_by IS NULL) AND (detached_at IS NULL) = (detach_reason IS NULL))
);

CREATE UNIQUE INDEX document_link_active ON engineering.document_link (document_number, COALESCE(revision, ''), target_type, target_id)
    WHERE detached_at IS NULL;
CREATE INDEX document_link_target ON engineering.document_link (target_type, target_id);

CREATE FUNCTION engineering.check_document_link() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    doc_authority text;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'document links are never deleted; detach them' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF TG_OP = 'UPDATE' AND (OLD.detached_at IS NOT NULL OR NEW.document_number <> OLD.document_number OR NEW.revision IS DISTINCT FROM OLD.revision
        OR NEW.target_type <> OLD.target_type OR NEW.target_id <> OLD.target_id OR NEW.purpose <> OLD.purpose OR NEW.linked_by <> OLD.linked_by) THEN
        RAISE EXCEPTION 'a document link only changes by being detached, once' USING ERRCODE = 'insufficient_privilege';
    END IF;
    SELECT authority INTO doc_authority FROM engineering.document WHERE number = NEW.document_number;
    IF doc_authority = 'Reference' AND NEW.purpose = 'Follow' THEN
        RAISE EXCEPTION 'a reference document is never production authority' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER document_link_rules BEFORE INSERT OR UPDATE OR DELETE ON engineering.document_link
    FOR EACH ROW EXECUTE FUNCTION engineering.check_document_link();
