-- V3.6: aging, shelf life and selection (V3 brief M, N).
--
-- Age, shelf life, expiration, open-container life, retest and storage conditions are separate facts. A lot keeps
-- the dates it was given (manufacture, expiry, first retest) for good; an extension or a retest never rewrites them,
-- it is its own record. A storage excursion is recorded against a location; lots stored there need a retest.

ALTER TABLE inventory.lot
    ADD COLUMN manufactured_on date NULL,
    ADD COLUMN expires_on      date NULL,
    ADD COLUMN retest_due      date NULL,
    ADD COLUMN opened_at       timestamptz NULL,
    ADD COLUMN opened_by       text NULL,
    ADD CONSTRAINT lot_opened CHECK ((opened_at IS NULL) = (opened_by IS NULL));

-- Each date is given once; afterwards it never changes.
CREATE FUNCTION inventory.lot_dates_once() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (OLD.manufactured_on IS NOT NULL AND NEW.manufactured_on IS DISTINCT FROM OLD.manufactured_on)
       OR (OLD.expires_on IS NOT NULL AND NEW.expires_on IS DISTINCT FROM OLD.expires_on)
       OR (OLD.retest_due IS NOT NULL AND NEW.retest_due IS DISTINCT FROM OLD.retest_due)
       OR (OLD.opened_at IS NOT NULL AND NEW.opened_at IS DISTINCT FROM OLD.opened_at) THEN
        RAISE EXCEPTION 'lot %/% dates are recorded once; extensions and retests are their own records', OLD.part_number, OLD.lot_number;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER lot_dates_once BEFORE UPDATE ON inventory.lot
    FOR EACH ROW EXECUTE FUNCTION inventory.lot_dates_once();

-- What a part's shelf life is, set by quality; every setting is kept.
CREATE TABLE inventory.shelf_life_rule (
    rule_id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    part_number       text NOT NULL REFERENCES engineering.item(part_number),
    shelf_life_days   integer NULL CHECK (shelf_life_days > 0),
    basis             text NOT NULL CHECK (basis IN ('Manufacture', 'Receipt')),
    retest_days       integer NULL CHECK (retest_days > 0),
    open_life_hours   integer NULL CHECK (open_life_hours > 0),
    storage_min_c     numeric(6, 2) NULL,
    storage_max_c     numeric(6, 2) NULL,
    storage_note      text NULL,
    selection         text NOT NULL CHECK (selection IN ('Fifo', 'Fefo')),
    reason            text NOT NULL,
    set_by            text NOT NULL,
    set_at            timestamptz NOT NULL,
    CHECK (storage_min_c IS NULL OR storage_max_c IS NULL OR storage_min_c < storage_max_c)
);
CREATE INDEX shelf_life_rule_part ON inventory.shelf_life_rule (part_number, rule_id DESC);
CREATE TRIGGER shelf_life_rule_append_only BEFORE UPDATE OR DELETE ON inventory.shelf_life_rule
    FOR EACH ROW EXECUTE FUNCTION inventory.reject_ledger_change();

-- An extension needs authority: requested by one person, decided by another. The original date stays on the lot.
CREATE TABLE inventory.shelf_life_extension (
    extension_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    part_number       text NOT NULL,
    lot_number        text NOT NULL,
    previous_expires  date NOT NULL,
    new_expires       date NOT NULL CHECK (new_expires > previous_expires),
    evidence          text NOT NULL,
    requested_by      text NOT NULL,
    requested_at      timestamptz NOT NULL,
    approved          boolean NULL,
    decided_by        text NULL,
    decided_at        timestamptz NULL,
    FOREIGN KEY (part_number, lot_number) REFERENCES inventory.lot(part_number, lot_number),
    CHECK ((approved IS NULL) = (decided_by IS NULL) AND (approved IS NULL) = (decided_at IS NULL)),
    CHECK (decided_by IS NULL OR decided_by <> requested_by)
);
CREATE INDEX shelf_life_extension_lot ON inventory.shelf_life_extension (part_number, lot_number);
CREATE UNIQUE INDEX shelf_life_extension_one_pending ON inventory.shelf_life_extension (part_number, lot_number) WHERE approved IS NULL;

CREATE FUNCTION inventory.extension_decide_once() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' OR OLD.approved IS NOT NULL
       OR (NEW.extension_id, NEW.part_number, NEW.lot_number, NEW.previous_expires, NEW.new_expires, NEW.evidence, NEW.requested_by, NEW.requested_at)
          IS DISTINCT FROM (OLD.extension_id, OLD.part_number, OLD.lot_number, OLD.previous_expires, OLD.new_expires, OLD.evidence, OLD.requested_by, OLD.requested_at) THEN
        RAISE EXCEPTION 'extension % is only ever decided, once', OLD.extension_id;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER extension_decide_once BEFORE UPDATE OR DELETE ON inventory.shelf_life_extension
    FOR EACH ROW EXECUTE FUNCTION inventory.extension_decide_once();

CREATE TABLE inventory.retest (
    retest_id    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    part_number  text NOT NULL,
    lot_number   text NOT NULL,
    passed       boolean NOT NULL,
    next_due     date NULL,
    evidence     text NOT NULL,
    tested_by    text NOT NULL,
    tested_at    timestamptz NOT NULL,
    FOREIGN KEY (part_number, lot_number) REFERENCES inventory.lot(part_number, lot_number),
    CHECK (passed OR next_due IS NULL)
);
CREATE INDEX retest_lot ON inventory.retest (part_number, lot_number, retest_id);
CREATE TRIGGER retest_append_only BEFORE UPDATE OR DELETE ON inventory.retest
    FOR EACH ROW EXECUTE FUNCTION inventory.reject_ledger_change();

CREATE TABLE inventory.storage_excursion (
    excursion_id   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    location_code  text NOT NULL REFERENCES inventory.location(code),
    condition      text NOT NULL,
    measured_c     numeric(6, 2) NULL,
    started_at     timestamptz NOT NULL,
    ended_at       timestamptz NOT NULL CHECK (ended_at >= started_at),
    note           text NULL,
    recorded_by    text NOT NULL,
    recorded_at    timestamptz NOT NULL
);
CREATE INDEX storage_excursion_location ON inventory.storage_excursion (location_code, started_at);
CREATE TRIGGER storage_excursion_append_only BEFORE UPDATE OR DELETE ON inventory.storage_excursion
    FOR EACH ROW EXECUTE FUNCTION inventory.reject_ledger_change();
