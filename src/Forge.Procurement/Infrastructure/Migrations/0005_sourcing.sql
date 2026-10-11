-- Requisition is the existing authoritative sourcing need; quotations are commercial evidence, never supply.
CREATE TABLE procurement.rfq (
    rfq_id bigint GENERATED ALWAYS AS IDENTITY (START WITH 8001) PRIMARY KEY,
    requisition_id bigint NOT NULL REFERENCES procurement.requisition(requisition_id),
    title text NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    supplier_codes text[] NOT NULL CHECK (cardinality(supplier_codes) > 0),
    status text NOT NULL CHECK (status IN ('Draft','Released','Selected','Cancelled')),
    created_by text NOT NULL,
    created_at timestamptz NOT NULL,
    released_by text NULL,
    released_at timestamptz NULL,
    package jsonb NULL,
    selected_quote_id bigint NULL,
    selected_by text NULL,
    selected_at timestamptz NULL,
    selection_reason text NULL,
    CHECK ((released_by IS NULL) = (released_at IS NULL)),
    CHECK (released_by IS NULL OR released_by <> created_by),
    CHECK (status NOT IN ('Released','Selected') OR (released_at IS NOT NULL AND package IS NOT NULL)),
    CHECK ((selected_quote_id IS NULL) = (selected_by IS NULL)),
    CHECK (status <> 'Selected' OR (selected_quote_id IS NOT NULL AND selected_at IS NOT NULL AND selection_reason IS NOT NULL))
);
CREATE UNIQUE INDEX rfq_open_need ON procurement.rfq(requisition_id) WHERE status IN ('Draft','Released');

CREATE TABLE procurement.supplier_quote (
    quote_id bigint GENERATED ALWAYS AS IDENTITY (START WITH 9001) PRIMARY KEY,
    rfq_id bigint NOT NULL REFERENCES procurement.rfq(rfq_id),
    supplier_code text NOT NULL REFERENCES procurement.supplier(code),
    quantity numeric(18,6) NOT NULL CHECK (quantity > 0),
    unit_price numeric(18,6) NOT NULL CHECK (unit_price >= 0),
    currency text NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    promised_date date NOT NULL,
    valid_through date NOT NULL,
    proposed_alternate_part text NULL REFERENCES engineering.item(part_number),
    original_document_number text NOT NULL,
    original_document_revision text NOT NULL,
    recorded_by text NOT NULL,
    recorded_at timestamptz NOT NULL,
    note text NULL,
    FOREIGN KEY (original_document_number,original_document_revision) REFERENCES engineering.document_revision(document_number,revision)
);
ALTER TABLE procurement.rfq ADD CONSTRAINT rfq_selected_quote FOREIGN KEY (selected_quote_id) REFERENCES procurement.supplier_quote(quote_id);
CREATE INDEX supplier_quote_rfq ON procurement.supplier_quote(rfq_id, quote_id);

CREATE FUNCTION procurement.freeze_sourcing_evidence() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'sourcing records are retained' USING ERRCODE = 'insufficient_privilege'; END IF;
    IF TG_TABLE_NAME = 'supplier_quote' THEN RAISE EXCEPTION 'quotes are immutable evidence; record a new response' USING ERRCODE = 'insufficient_privilege'; END IF;
    IF (NEW.requisition_id,NEW.title,NEW.supplier_codes,NEW.created_by,NEW.created_at) IS DISTINCT FROM (OLD.requisition_id,OLD.title,OLD.supplier_codes,OLD.created_by,OLD.created_at)
       OR (OLD.package IS NOT NULL AND NEW.package IS DISTINCT FROM OLD.package)
       OR (OLD.released_by IS NOT NULL AND (NEW.released_by,NEW.released_at) IS DISTINCT FROM (OLD.released_by,OLD.released_at))
       OR OLD.status IN ('Selected','Cancelled') THEN
        RAISE EXCEPTION 'sourcing facts and released packages are retained' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER rfq_kept BEFORE UPDATE OR DELETE ON procurement.rfq FOR EACH ROW EXECUTE FUNCTION procurement.freeze_sourcing_evidence();
CREATE TRIGGER quote_kept BEFORE UPDATE OR DELETE ON procurement.supplier_quote FOR EACH ROW EXECUTE FUNCTION procurement.freeze_sourcing_evidence();
