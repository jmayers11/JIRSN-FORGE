-- V3.5: rework, repair, use as is and scrap stay distinct (V3 brief K).
--
-- Repair joins the dispositions: the material becomes usable without conforming to the drawing, so like use as is
-- it needs a second person's approval. Rework and repair are then done as rework records against the order
-- operation whose good output they take back. The production reports stay exactly as recorded; a rework record
-- says how much went back, why (an NCR or a rework reason code), and, once closed, how much came out good and
-- how much was scrapped. History is never rewritten: a record is opened, then closed once.

ALTER TABLE quality.ncr DROP CONSTRAINT ncr_disposition_check;
ALTER TABLE quality.ncr ADD CONSTRAINT ncr_disposition_check
    CHECK (disposition IN ('UseAsIs', 'Rework', 'Scrap', 'ReturnToSupplier', 'Repair'));

CREATE TABLE quality.rework (
    rework_id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind            text NOT NULL CHECK (kind IN ('Rework', 'Repair')),
    order_number    bigint NOT NULL,
    sequence        integer NOT NULL,
    part_number     text NOT NULL,
    quantity        numeric(18, 6) NOT NULL CHECK (quantity > 0),
    ncr_id          bigint NULL REFERENCES quality.ncr(ncr_id),
    reason_code     text NULL REFERENCES execution.reason_code(code),
    instructions    text NOT NULL,
    opened_at       timestamptz NOT NULL,
    opened_by       text NOT NULL,
    closed_at       timestamptz NULL,
    closed_by       text NULL,
    good_quantity   numeric(18, 6) NULL CHECK (good_quantity >= 0),
    scrap_quantity  numeric(18, 6) NULL CHECK (scrap_quantity >= 0),
    close_note      text NULL,
    FOREIGN KEY (order_number, sequence) REFERENCES execution.order_operation(order_number, sequence),
    CHECK (ncr_id IS NOT NULL OR reason_code IS NOT NULL),
    CHECK ((closed_at IS NULL) = (closed_by IS NULL) AND (closed_at IS NULL) = (good_quantity IS NULL) AND (closed_at IS NULL) = (scrap_quantity IS NULL)),
    CHECK (closed_at IS NULL OR good_quantity + scrap_quantity = quantity)
);
CREATE INDEX rework_order ON quality.rework (order_number, sequence);
CREATE INDEX rework_open ON quality.rework (opened_at) WHERE closed_at IS NULL;

CREATE FUNCTION quality.rework_close_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' OR OLD.closed_at IS NOT NULL
       OR (NEW.rework_id, NEW.kind, NEW.order_number, NEW.sequence, NEW.part_number, NEW.quantity, NEW.ncr_id, NEW.reason_code, NEW.instructions, NEW.opened_at, NEW.opened_by)
          IS DISTINCT FROM (OLD.rework_id, OLD.kind, OLD.order_number, OLD.sequence, OLD.part_number, OLD.quantity, OLD.ncr_id, OLD.reason_code, OLD.instructions, OLD.opened_at, OLD.opened_by) THEN
        RAISE EXCEPTION 'rework % is only ever closed, once', OLD.rework_id;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER rework_close_only BEFORE UPDATE OR DELETE ON quality.rework
    FOR EACH ROW EXECUTE FUNCTION quality.rework_close_only();
