-- V2.4: engineering change control (V2 §4-6). A change lists the document and item revisions it moves
-- from -> to; reviews required by policy are decided by people other than the originator; a change board
-- approves; release (separate from approval unless a policy combines them) releases every listed
-- revision in one transaction. History is kept: nothing here is ever deleted.

CREATE SEQUENCE engineering.change_number_seq START 1;

CREATE TABLE engineering.change (
    change_number        text PRIMARY KEY CHECK (change_number ~ '^ECN-[0-9]{4,}$'),
    title                text NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    change_class         text NOT NULL CHECK (change_class IN ('Major', 'Minor', 'Administrative')),
    change_type          text NOT NULL CHECK (change_type IN ('Design', 'Documentation', 'Process', 'Supplier', 'Correction')),
    priority             text NOT NULL CHECK (priority IN ('Routine', 'Urgent', 'Emergency')),
    reason               text NOT NULL,
    description          text NULL,
    program_id           uuid NULL,
    customer             text NULL,
    requested_effective  date NULL,
    status               text NOT NULL CHECK (status IN ('Draft', 'InReview', 'Approved', 'Released', 'Rejected', 'Cancelled')),
    review_round         integer NOT NULL DEFAULT 0,
    originator           text NOT NULL,
    created_at           timestamptz NOT NULL,
    submitted_at         timestamptz NULL,
    approved_by          text NULL,
    approved_at          timestamptz NULL,
    released_by          text NULL,
    released_at          timestamptz NULL,
    effective_from       date NULL,
    closed_by            text NULL,
    closed_at            timestamptz NULL,
    closed_reason        text NULL,
    CHECK (approved_by IS NULL OR approved_by <> originator),
    CHECK (released_by IS NULL OR released_by <> originator),
    CHECK (status <> 'Released' OR (approved_by IS NOT NULL AND released_by IS NOT NULL AND effective_from IS NOT NULL)),
    CHECK (status NOT IN ('Approved', 'Released') OR approved_by IS NOT NULL)
);

-- One line per affected revision: a document revision or an item revision, from -> to.
CREATE TABLE engineering.change_line (
    change_number    text NOT NULL REFERENCES engineering.change(change_number),
    line_number      integer NOT NULL CHECK (line_number BETWEEN 1 AND 999),
    kind             text NOT NULL CHECK (kind IN ('DocumentRevision', 'ItemRevision')),
    document_number  text NULL,
    part_number      text NULL,
    from_revision    text NULL,
    to_revision      text NOT NULL,
    note             text NULL,
    added_by         text NOT NULL,
    added_at         timestamptz NOT NULL,
    removed_by       text NULL,
    removed_at       timestamptz NULL,
    PRIMARY KEY (change_number, line_number),
    CHECK ((kind = 'DocumentRevision') = (document_number IS NOT NULL)),
    CHECK ((kind = 'ItemRevision') = (part_number IS NOT NULL)),
    FOREIGN KEY (document_number, to_revision) REFERENCES engineering.document_revision(document_number, revision),
    FOREIGN KEY (part_number, to_revision) REFERENCES engineering.revision(part_number, revision),
    CHECK ((removed_at IS NULL) = (removed_by IS NULL))
);

-- A required review and its decision, per review round (a returned change is reviewed again).
CREATE TABLE engineering.change_review (
    change_number  text NOT NULL REFERENCES engineering.change(change_number),
    round          integer NOT NULL CHECK (round >= 1),
    review_kind    text NOT NULL CHECK (review_kind IN ('Technical', 'Quality', 'Manufacturing', 'Procurement', 'Program', 'Maintenance')),
    required_by    text NOT NULL,
    reviewer       text NULL,
    decision       text NULL CHECK (decision IN ('Approve', 'ApproveWithComments', 'Reject')),
    notes          text NULL,
    decided_at     timestamptz NULL,
    PRIMARY KEY (change_number, round, review_kind),
    CHECK ((decision IS NULL) = (reviewer IS NULL) AND (decision IS NULL) = (decided_at IS NULL)),
    CHECK (decision <> 'Reject' OR notes IS NOT NULL)
);

-- Which reviews a change needs. Every set criterion must match; the reviews of all matching policies are
-- required. With no match, a Technical review is required: never fewer than one review besides the
-- originator, whatever the policies say.
CREATE TABLE engineering.review_policy (
    policy_id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name                      text NOT NULL,
    change_class              text NULL CHECK (change_class IN ('Major', 'Minor', 'Administrative')),
    change_type               text NULL CHECK (change_type IN ('Design', 'Documentation', 'Process', 'Supplier', 'Correction')),
    item_class                text NULL,
    program_id                uuid NULL,
    customer                  text NULL,
    required_reviews          text[] NOT NULL CHECK (cardinality(required_reviews) >= 1),
    combine_approval_release  boolean NOT NULL DEFAULT false,
    created_by                text NOT NULL,
    created_at                timestamptz NOT NULL,
    withdrawn_by              text NULL,
    withdrawn_at              timestamptz NULL,
    withdrawn_reason          text NULL
);

CREATE FUNCTION engineering.reject_change_delete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% rows are never deleted', TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;

CREATE TRIGGER change_kept BEFORE DELETE ON engineering.change FOR EACH ROW EXECUTE FUNCTION engineering.reject_change_delete();
CREATE TRIGGER change_line_kept BEFORE DELETE ON engineering.change_line FOR EACH ROW EXECUTE FUNCTION engineering.reject_change_delete();
CREATE TRIGGER change_review_kept BEFORE DELETE ON engineering.change_review FOR EACH ROW EXECUTE FUNCTION engineering.reject_change_delete();
CREATE TRIGGER review_policy_kept BEFORE DELETE ON engineering.review_policy FOR EACH ROW EXECUTE FUNCTION engineering.reject_change_delete();

-- A decided review never changes; a released, rejected or cancelled change never changes again.
CREATE FUNCTION engineering.freeze_review() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.decision IS NOT NULL THEN
        RAISE EXCEPTION 'review % of % is decided and frozen', OLD.review_kind, OLD.change_number USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER change_review_frozen BEFORE UPDATE ON engineering.change_review FOR EACH ROW EXECUTE FUNCTION engineering.freeze_review();

CREATE FUNCTION engineering.freeze_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status IN ('Released', 'Rejected', 'Cancelled') THEN
        RAISE EXCEPTION 'change % is % and frozen', OLD.change_number, OLD.status USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER change_frozen BEFORE UPDATE ON engineering.change FOR EACH ROW EXECUTE FUNCTION engineering.freeze_change();
