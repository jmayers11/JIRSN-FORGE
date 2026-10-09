-- V2.8: MRO stocking parameters and obsolescence records, beside the items they govern.

-- Every setting of an item's MRO policy is kept; the newest is in force. A cleared row stops replenishment.
CREATE TABLE engineering.item_mro (
    policy_id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    part_number      text NOT NULL REFERENCES engineering.item(part_number),
    min_quantity     numeric(18, 6) NOT NULL CHECK (min_quantity >= 0),
    reorder_point    numeric(18, 6) NOT NULL CHECK (reorder_point >= 0),
    max_quantity     numeric(18, 6) NOT NULL,
    critical_spare   boolean NOT NULL,
    shelf_life_days  integer NULL CHECK (shelf_life_days IS NULL OR shelf_life_days > 0),
    cleared          boolean NOT NULL DEFAULT false,
    set_by           text NOT NULL,
    set_at           timestamptz NOT NULL,
    reason           text NOT NULL CHECK (length(trim(reason)) > 0),
    CONSTRAINT item_mro_levels CHECK (cleared OR (min_quantity <= reorder_point AND reorder_point < max_quantity))
);
CREATE INDEX item_mro_part ON engineering.item_mro (part_number, policy_id DESC);

CREATE FUNCTION engineering.reject_history_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% is history: rows are added, never changed or deleted', TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;

CREATE TRIGGER item_mro_history BEFORE UPDATE OR DELETE ON engineering.item_mro FOR EACH ROW EXECUTE FUNCTION engineering.reject_history_change();

CREATE TABLE engineering.obsolescence (
    record_id                 bigint GENERATED ALWAYS AS IDENTITY (START WITH 9001) PRIMARY KEY,
    part_number               text NOT NULL REFERENCES engineering.item(part_number),
    status                    text NOT NULL CHECK (status IN ('NotRecommended', 'LastTimeBuy', 'EndOfLife', 'Obsolete')),
    last_time_buy             date NULL,
    end_of_life               date NULL,
    end_of_support            date NULL,
    evidence                  text NOT NULL CHECK (length(trim(evidence)) > 0),
    recorded_by               text NOT NULL,
    recorded_at               timestamptz NOT NULL,
    replacement_part          text NULL REFERENCES engineering.item(part_number),
    replacement_status        text NOT NULL CHECK (replacement_status IN ('None', 'Proposed', 'Approved', 'Rejected')),
    replacement_proposed_by   text NULL,
    replacement_proposed_at   timestamptz NULL,
    replacement_decided_by    text NULL,
    replacement_decided_at    timestamptz NULL,
    replacement_reason        text NULL,
    CHECK (replacement_part IS NULL OR replacement_part <> part_number),
    CHECK ((replacement_status = 'None') = (replacement_part IS NULL)),
    CHECK (replacement_status NOT IN ('Approved', 'Rejected') OR (replacement_decided_by IS NOT NULL AND replacement_decided_by <> replacement_proposed_by))
);
CREATE INDEX obsolescence_part ON engineering.obsolescence (part_number, record_id DESC);

-- What was recorded, and from what evidence, never changes. Only a replacement may be named (once, or again
-- after a rejection) and decided by someone other than whoever named it.
CREATE FUNCTION engineering.guard_obsolescence() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'obsolescence records are never deleted; record a newer one' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF (NEW.part_number, NEW.status, NEW.last_time_buy, NEW.end_of_life, NEW.end_of_support, NEW.evidence, NEW.recorded_by, NEW.recorded_at)
       IS DISTINCT FROM (OLD.part_number, OLD.status, OLD.last_time_buy, OLD.end_of_life, OLD.end_of_support, OLD.evidence, OLD.recorded_by, OLD.recorded_at) THEN
        RAISE EXCEPTION 'obsolescence record % keeps what was recorded; record a newer one', OLD.record_id USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF OLD.replacement_status = 'Approved' THEN
        RAISE EXCEPTION 'obsolescence record % has an approved replacement; record a newer one to change it', OLD.record_id USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF NOT ((OLD.replacement_status IN ('None', 'Rejected') AND NEW.replacement_status = 'Proposed')
            OR (OLD.replacement_status = 'Proposed' AND NEW.replacement_status IN ('Approved', 'Rejected')
                AND NEW.replacement_part IS NOT DISTINCT FROM OLD.replacement_part
                AND NEW.replacement_proposed_by IS NOT DISTINCT FROM OLD.replacement_proposed_by)) THEN
        RAISE EXCEPTION 'obsolescence record %: replacement % cannot become %', OLD.record_id, OLD.replacement_status, NEW.replacement_status USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER obsolescence_guarded BEFORE UPDATE OR DELETE ON engineering.obsolescence FOR EACH ROW EXECUTE FUNCTION engineering.guard_obsolescence();
