-- V2.6: controlled alternates. A relationship lets the alternate stand in for the primary where its
-- conditions hold, at a stated quantity conversion. Proposed relationships are never usable; a different
-- person with the matching authority approves or rejects; expired relationships stop; nothing is deleted.
CREATE TABLE engineering.alternate (
    alternate_id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    primary_part        text NOT NULL REFERENCES engineering.item(part_number),
    alternate_part      text NOT NULL REFERENCES engineering.item(part_number),
    kind                text NOT NULL CHECK (kind IN ('ApprovedAlternate', 'Equivalent', 'SupersededBy', 'Interchangeable', 'Conditional', 'Temporary',
                                                      'MaintenanceOnly', 'SupplierSpecific', 'ProgramSpecific', 'EngineeringApprovalRequired')),
    status              text NOT NULL CHECK (status IN ('Proposed', 'Approved', 'Rejected', 'Expired')),
    factor              numeric(18, 6) NOT NULL CHECK (factor > 0),
    program_id          uuid NULL,
    customer            text NULL,
    parent_part         text NULL REFERENCES engineering.item(part_number),
    asset_code          text NULL,
    machine_code        text NULL,
    operation_sequence  integer NULL,
    supplier_code       text NULL,
    valid_from          date NULL,
    valid_to            date NULL,
    quality_notes       text NULL,
    evidence            text NULL,
    change_number       text NULL REFERENCES engineering.change(change_number),
    proposed_by         text NOT NULL,
    proposed_at         timestamptz NOT NULL,
    decided_by          text NULL,
    decided_at          timestamptz NULL,
    decision_reason     text NULL,
    expired_by          text NULL,
    expired_at          timestamptz NULL,
    expired_reason      text NULL,
    CHECK (primary_part <> alternate_part),
    CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CHECK (kind <> 'Temporary' OR valid_to IS NOT NULL),
    CHECK (kind <> 'ProgramSpecific' OR program_id IS NOT NULL),
    CHECK (kind <> 'SupplierSpecific' OR supplier_code IS NOT NULL),
    -- Two people: whoever proposed a relationship never decides it.
    CHECK (decided_by IS NULL OR decided_by <> proposed_by),
    CHECK ((status = 'Proposed') = (decided_at IS NULL) OR status = 'Expired'),
    CHECK ((status = 'Expired') = (expired_at IS NOT NULL))
);
CREATE INDEX alternate_primary ON engineering.alternate (primary_part);
CREATE INDEX alternate_alternate ON engineering.alternate (alternate_part);

-- How MRP may use approved alternates on one BOM line. Kept apart from the (frozen) BOM line so it can be
-- changed with approval authority and a reason on a released revision; every setting is kept, the latest wins.
CREATE TABLE engineering.bom_line_policy (
    policy_id    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    part_number  text NOT NULL,
    revision     text NOT NULL,
    line_number  integer NOT NULL,
    policy       text NOT NULL CHECK (policy IN ('Never', 'Suggest', 'Automatic')),
    set_by       text NOT NULL,
    set_at       timestamptz NOT NULL,
    reason       text NOT NULL,
    FOREIGN KEY (part_number, revision, line_number) REFERENCES engineering.bom_line(part_number, revision, line_number)
);
CREATE INDEX bom_line_policy_line ON engineering.bom_line_policy (part_number, revision, line_number, policy_id);

CREATE TRIGGER alternate_kept BEFORE DELETE ON engineering.alternate FOR EACH ROW EXECUTE FUNCTION engineering.reject_change_delete();

CREATE FUNCTION engineering.reject_policy_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'BOM line policies are history: add a new setting' USING ERRCODE = 'insufficient_privilege';
END;
$$;

CREATE TRIGGER bom_line_policy_history BEFORE UPDATE OR DELETE ON engineering.bom_line_policy FOR EACH ROW EXECUTE FUNCTION engineering.reject_policy_change();

-- What a relationship says never changes once proposed; only its decision and expiry are recorded, once each.
CREATE FUNCTION engineering.freeze_alternate() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.primary_part, NEW.alternate_part, NEW.kind, NEW.factor, NEW.program_id, NEW.customer, NEW.parent_part, NEW.asset_code, NEW.machine_code,
        NEW.operation_sequence, NEW.supplier_code, NEW.valid_from, NEW.valid_to, NEW.proposed_by, NEW.proposed_at)
       IS DISTINCT FROM
       (OLD.primary_part, OLD.alternate_part, OLD.kind, OLD.factor, OLD.program_id, OLD.customer, OLD.parent_part, OLD.asset_code, OLD.machine_code,
        OLD.operation_sequence, OLD.supplier_code, OLD.valid_from, OLD.valid_to, OLD.proposed_by, OLD.proposed_at) THEN
        RAISE EXCEPTION 'alternate % keeps what was proposed; propose a new one', OLD.alternate_id USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF OLD.status IN ('Rejected', 'Expired') THEN
        RAISE EXCEPTION 'alternate % is % and frozen', OLD.alternate_id, OLD.status USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF OLD.decided_at IS NOT NULL AND (NEW.decided_by, NEW.decided_at, NEW.decision_reason) IS DISTINCT FROM (OLD.decided_by, OLD.decided_at, OLD.decision_reason) THEN
        RAISE EXCEPTION 'alternate % was already decided', OLD.alternate_id USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER alternate_frozen BEFORE UPDATE ON engineering.alternate FOR EACH ROW EXECUTE FUNCTION engineering.freeze_alternate();
