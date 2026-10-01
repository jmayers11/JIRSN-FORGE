CREATE SCHEMA IF NOT EXISTS engineering;

CREATE TABLE engineering.item (
    part_number      text PRIMARY KEY,
    description      text NOT NULL,
    unit_of_measure  text NOT NULL,
    make_buy         text NOT NULL CHECK (make_buy IN ('Make', 'Buy')),
    lead_time_days   integer NOT NULL CHECK (lead_time_days BETWEEN 0 AND 730),
    lot_tracked      boolean NOT NULL,
    serialized       boolean NOT NULL,
    created_at       timestamptz NOT NULL,
    created_by       text NOT NULL
);

CREATE TABLE engineering.revision (
    part_number     text NOT NULL REFERENCES engineering.item(part_number),
    revision        text NOT NULL,
    status          text NOT NULL CHECK (status IN ('Draft', 'Released')),
    effective_from  timestamptz NULL,
    effective_to    timestamptz NULL,
    created_at      timestamptz NOT NULL,
    created_by      text NOT NULL,
    released_by     text NULL,
    PRIMARY KEY (part_number, revision),
    CHECK (status = 'Draft' OR effective_from IS NOT NULL),
    CHECK (effective_to IS NULL OR effective_to > effective_from)
);
CREATE UNIQUE INDEX revision_one_draft ON engineering.revision (part_number) WHERE status = 'Draft';

-- No two released revisions of a part may be in force at the same instant.
CREATE EXTENSION IF NOT EXISTS btree_gist;
ALTER TABLE engineering.revision ADD CONSTRAINT revision_no_overlap
    EXCLUDE USING gist (part_number WITH =, tstzrange(effective_from, effective_to, '[)') WITH &&) WHERE (status = 'Released');

CREATE TABLE engineering.bom_line (
    part_number     text NOT NULL,
    revision        text NOT NULL,
    line_number     integer NOT NULL CHECK (line_number BETWEEN 1 AND 9999),
    component       text NOT NULL REFERENCES engineering.item(part_number),
    quantity_per    numeric(18, 6) NOT NULL CHECK (quantity_per > 0),
    scrap_percent   numeric(5, 2) NOT NULL CHECK (scrap_percent >= 0 AND scrap_percent < 100),
    PRIMARY KEY (part_number, revision, line_number),
    FOREIGN KEY (part_number, revision) REFERENCES engineering.revision(part_number, revision),
    CHECK (component <> part_number)
);

CREATE TABLE engineering.routing_operation (
    part_number              text NOT NULL,
    revision                 text NOT NULL,
    sequence                 integer NOT NULL CHECK (sequence BETWEEN 1 AND 9999),
    description              text NOT NULL,
    work_center_code         text NOT NULL REFERENCES resources.work_center(code),
    activity_code            text NOT NULL REFERENCES resources.activity_type(code),
    eligible_machines        text[] NOT NULL,
    required_qualifications  text[] NOT NULL,
    setup_minutes            numeric(10, 2) NOT NULL CHECK (setup_minutes >= 0),
    run_minutes_per_unit     numeric(12, 4) NOT NULL CHECK (run_minutes_per_unit >= 0),
    crew_size                integer NOT NULL CHECK (crew_size BETWEEN 1 AND 20),
    PRIMARY KEY (part_number, revision, sequence),
    FOREIGN KEY (part_number, revision) REFERENCES engineering.revision(part_number, revision)
);

-- A released revision is frozen: its BOM and routing cannot change.
CREATE FUNCTION engineering.reject_released_change() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    rev_status text;
BEGIN
    SELECT status INTO rev_status FROM engineering.revision
    WHERE part_number = COALESCE(NEW.part_number, OLD.part_number) AND revision = COALESCE(NEW.revision, OLD.revision);
    IF rev_status = 'Released' THEN
        RAISE EXCEPTION 'revision %/% is released and frozen', COALESCE(NEW.part_number, OLD.part_number), COALESCE(NEW.revision, OLD.revision)
            USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN COALESCE(NEW, OLD);
END;
$$;

CREATE TRIGGER bom_line_frozen BEFORE INSERT OR UPDATE OR DELETE ON engineering.bom_line
    FOR EACH ROW EXECUTE FUNCTION engineering.reject_released_change();
CREATE TRIGGER routing_operation_frozen BEFORE INSERT OR UPDATE OR DELETE ON engineering.routing_operation
    FOR EACH ROW EXECUTE FUNCTION engineering.reject_released_change();
