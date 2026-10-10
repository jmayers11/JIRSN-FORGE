-- V2.11: the as-built record. Every issue of a component to a production order (and every return) writes one
-- line, at that moment, with what was known then: the component revision, its lot or serial, the approved
-- alternate it was issued through and who approved it, and the supplier and purchase order it was bought on.
-- Genealogy reads these lines; nothing is reconstructed from current data. The lines are never changed.
CREATE TABLE execution.as_built (
    as_built_id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_number          bigint NOT NULL REFERENCES execution.production_order(order_number),
    transaction_id        bigint NOT NULL UNIQUE,
    kind                  text NOT NULL CHECK (kind IN ('Issued', 'Returned')),
    component_part        text NOT NULL,
    component_revision    text NULL,
    lot_number            text NULL,
    serial                boolean NOT NULL,
    quantity              numeric(18, 6) NOT NULL CHECK (quantity > 0),
    substitutes_for       text NULL,
    primary_quantity      numeric(18, 6) NULL,
    alternate_id          bigint NULL,
    alternate_kind        text NULL,
    alternate_approved_by text NULL,
    alternate_approved_at timestamptz NULL,
    alternate_conditions  text NULL,
    supplier_code         text NULL,
    supplier_name         text NULL,
    po_number             bigint NULL,
    po_line               integer NULL,
    received_at           timestamptz NULL,
    recorded_at           timestamptz NOT NULL,
    recorded_by           text NOT NULL,
    CHECK ((substitutes_for IS NULL) = (alternate_id IS NULL)),
    CHECK (NOT serial OR lot_number IS NOT NULL)
);
CREATE INDEX as_built_order ON execution.as_built (order_number, as_built_id);
CREATE INDEX as_built_lot ON execution.as_built (component_part, lot_number) WHERE lot_number IS NOT NULL;

CREATE TRIGGER as_built_append_only BEFORE UPDATE OR DELETE ON execution.as_built
    FOR EACH ROW EXECUTE FUNCTION execution.forbid_change();

-- The qualifications a person held for an operation when they started it, kept with the labor record.
ALTER TABLE execution.labor ADD COLUMN qualifications text[] NULL;

CREATE FUNCTION execution.keep_labor_qualifications() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.qualifications IS DISTINCT FROM OLD.qualifications OR NEW.person_id <> OLD.person_id OR NEW.started_at <> OLD.started_at THEN
        RAISE EXCEPTION 'labor % keeps who started it, when and with which qualifications', OLD.labor_id;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER labor_qualifications_kept BEFORE UPDATE ON execution.labor
    FOR EACH ROW EXECUTE FUNCTION execution.keep_labor_qualifications();
