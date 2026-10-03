-- V2.3: which documents an item revision is built to, the rules that decide which revision of a document
-- applies, and the site policy that decides whether documentation blocks readiness (V2 §7, review Q).
-- Date effectivity stays the default rule; rules are exceptions written as data. History is kept: rows
-- are never deleted, they are removed or withdrawn with who, when and why.

-- An item revision is built to these documents. A NULL document_revision means "the revision that
-- applies"; a revision pins it. A NULL operation_sequence means the whole item.
CREATE TABLE engineering.revision_document (
    requirement_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    part_number         text NOT NULL,
    revision            text NOT NULL,
    document_number     text NOT NULL REFERENCES engineering.document(number),
    document_revision   text NULL,
    operation_sequence  integer NULL CHECK (operation_sequence IS NULL OR operation_sequence BETWEEN 1 AND 9999),
    note                text NULL,
    added_by            text NOT NULL,
    added_at            timestamptz NOT NULL,
    removed_by          text NULL,
    removed_at          timestamptz NULL,
    removed_reason      text NULL,
    FOREIGN KEY (part_number, revision) REFERENCES engineering.revision(part_number, revision),
    FOREIGN KEY (document_number, document_revision) REFERENCES engineering.document_revision(document_number, revision),
    CHECK ((removed_at IS NULL) = (removed_by IS NULL) AND (removed_at IS NULL) = (removed_reason IS NULL))
);

CREATE UNIQUE INDEX revision_document_active ON engineering.revision_document (part_number, revision, document_number, COALESCE(operation_sequence, 0))
    WHERE removed_at IS NULL;

-- A rule: this revision of the document applies when the condition holds. Order rules are deviations for
-- one order and need a reference (deviation or concession number).
CREATE TABLE engineering.applicability_rule (
    rule_id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_number  text NOT NULL,
    revision         text NOT NULL,
    condition_kind   text NOT NULL CHECK (condition_kind IN ('Program', 'Customer', 'DueDate', 'Order')),
    program_id       uuid NULL,
    customer         text NULL,
    due_from         date NULL,
    due_to           date NULL,
    order_number     bigint NULL,
    reference        text NULL,
    note             text NULL,
    created_by       text NOT NULL,
    created_at       timestamptz NOT NULL,
    withdrawn_by     text NULL,
    withdrawn_at     timestamptz NULL,
    withdrawn_reason text NULL,
    FOREIGN KEY (document_number, revision) REFERENCES engineering.document_revision(document_number, revision),
    CHECK (condition_kind <> 'Program' OR program_id IS NOT NULL),
    CHECK (condition_kind <> 'Customer' OR customer IS NOT NULL),
    CHECK (condition_kind <> 'DueDate' OR (due_from IS NOT NULL OR due_to IS NOT NULL)),
    CHECK (due_to IS NULL OR due_from IS NULL OR due_to >= due_from),
    CHECK (condition_kind <> 'Order' OR (order_number IS NOT NULL AND reference IS NOT NULL)),
    CHECK ((withdrawn_at IS NULL) = (withdrawn_by IS NULL) AND (withdrawn_at IS NULL) = (withdrawn_reason IS NULL))
);

CREATE INDEX applicability_rule_document ON engineering.applicability_rule (document_number);

-- Whether the documentation readiness dimension is enforced for an item class. Absent = not enforced.
CREATE TABLE engineering.documentation_policy (
    item_class  text PRIMARY KEY CHECK (item_class IN ('Unclassified', 'Make', 'BuildToPrint', 'Cots', 'RawMaterial', 'StandardHardware', 'Consumable', 'Mro', 'Tooling', 'Service', 'OtherControlled')),
    enforced    boolean NOT NULL,
    changed_by  text NOT NULL,
    changed_at  timestamptz NOT NULL
);

CREATE FUNCTION engineering.keep_requirement_history() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'document requirements are never deleted; remove them' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF OLD.removed_at IS NOT NULL OR NEW.part_number <> OLD.part_number OR NEW.revision <> OLD.revision
        OR NEW.document_number <> OLD.document_number OR NEW.document_revision IS DISTINCT FROM OLD.document_revision
        OR NEW.operation_sequence IS DISTINCT FROM OLD.operation_sequence OR NEW.added_by <> OLD.added_by OR NEW.added_at <> OLD.added_at THEN
        RAISE EXCEPTION 'a document requirement only changes by being removed, once' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE FUNCTION engineering.keep_rule_history() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'applicability rules are never deleted; withdraw them' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF OLD.withdrawn_at IS NOT NULL OR NEW.document_number <> OLD.document_number OR NEW.revision <> OLD.revision
        OR NEW.condition_kind <> OLD.condition_kind OR NEW.program_id IS DISTINCT FROM OLD.program_id OR NEW.customer IS DISTINCT FROM OLD.customer
        OR NEW.due_from IS DISTINCT FROM OLD.due_from OR NEW.due_to IS DISTINCT FROM OLD.due_to OR NEW.order_number IS DISTINCT FROM OLD.order_number
        OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by THEN
        RAISE EXCEPTION 'an applicability rule only changes by being withdrawn, once' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER revision_document_history BEFORE UPDATE OR DELETE ON engineering.revision_document
    FOR EACH ROW EXECUTE FUNCTION engineering.keep_requirement_history();
CREATE TRIGGER applicability_rule_history BEFORE UPDATE OR DELETE ON engineering.applicability_rule
    FOR EACH ROW EXECUTE FUNCTION engineering.keep_rule_history();

-- An order's documentation reissued (traveler reprinted) after release: from then on the order references
-- the revisions that applied at the reissue. History only, never changed.
CREATE TABLE engineering.order_documentation_issue (
    issue_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_number  bigint NOT NULL,
    issued_at     timestamptz NOT NULL,
    issued_by     text NOT NULL,
    reason        text NOT NULL
);

CREATE INDEX order_documentation_issue_order ON engineering.order_documentation_issue (order_number);

CREATE FUNCTION engineering.reject_issue_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'documentation issues are history and never change' USING ERRCODE = 'insufficient_privilege';
END;
$$;

CREATE TRIGGER order_documentation_issue_history BEFORE UPDATE OR DELETE ON engineering.order_documentation_issue
    FOR EACH ROW EXECUTE FUNCTION engineering.reject_issue_change();
