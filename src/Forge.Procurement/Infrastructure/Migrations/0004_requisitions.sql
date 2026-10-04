-- V2.7: requisitions from every source in one queue, and the source carried onto the purchase order line,
-- so "why was this purchased?" has a recorded answer for each line, its receipts and where the stock went.
CREATE TABLE procurement.requisition (
    requisition_id     bigint GENERATED ALWAYS AS IDENTITY (START WITH 7001) PRIMARY KEY,
    part_number        text NOT NULL REFERENCES engineering.item(part_number),
    quantity           numeric(18, 6) NOT NULL CHECK (quantity > 0),
    need_date          date NOT NULL,
    source_kind        text NOT NULL CHECK (source_kind IN ('CustomerOrder', 'Forecast', 'Service', 'Internal', 'ContractDelivery', 'Maintenance', 'MroReplenishment',
                                                            'FacilityProject', 'CapitalProject', 'Quality', 'Tooling', 'Engineering', 'Production', 'SafetyStock', 'Mixed')),
    source_key         text NOT NULL,
    source_label       text NOT NULL,
    demand_id          bigint NULL,
    planned_order      text NULL,
    required_revision  text NULL,
    supplier_code      text NULL REFERENCES procurement.supplier(code),
    program_id         uuid NULL,
    status             text NOT NULL CHECK (status IN ('Open', 'Ordered', 'Cancelled')),
    requested_by       text NOT NULL,
    requested_at       timestamptz NOT NULL,
    po_number          bigint NULL,
    po_line            integer NULL,
    ordered_by         text NULL,
    ordered_at         timestamptz NULL,
    cancelled_by       text NULL,
    cancelled_at       timestamptz NULL,
    cancelled_reason   text NULL,
    CHECK ((status = 'Ordered') = (po_number IS NOT NULL AND po_line IS NOT NULL)),
    CHECK ((status = 'Cancelled') = (cancelled_at IS NOT NULL))
);
CREATE INDEX requisition_open ON procurement.requisition (status, need_date);

ALTER TABLE procurement.purchase_order_line ADD COLUMN requisition_id bigint NULL REFERENCES procurement.requisition(requisition_id);
ALTER TABLE procurement.purchase_order_line ADD COLUMN source_kind text NULL;
ALTER TABLE procurement.purchase_order_line ADD COLUMN source_key text NULL;
ALTER TABLE procurement.purchase_order_line ADD COLUMN source_label text NULL;
ALTER TABLE procurement.purchase_order_line ADD CONSTRAINT line_source_complete
    CHECK ((source_kind IS NULL) = (source_key IS NULL) AND (source_kind IS NULL) = (source_label IS NULL));

CREATE FUNCTION procurement.reject_requisition_delete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'requisitions are never deleted; cancel them with a reason' USING ERRCODE = 'insufficient_privilege';
END;
$$;

CREATE TRIGGER requisition_kept BEFORE DELETE ON procurement.requisition FOR EACH ROW EXECUTE FUNCTION procurement.reject_requisition_delete();

-- What a requisition asks for, and why, never changes; only its ordering and cancellation are recorded.
-- A purchase order line keeps the source it was bought for.
CREATE FUNCTION procurement.freeze_requisition() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.part_number, NEW.quantity, NEW.need_date, NEW.source_kind, NEW.source_key, NEW.source_label, NEW.demand_id, NEW.requested_by, NEW.requested_at)
       IS DISTINCT FROM (OLD.part_number, OLD.quantity, OLD.need_date, OLD.source_kind, OLD.source_key, OLD.source_label, OLD.demand_id, OLD.requested_by, OLD.requested_at) THEN
        RAISE EXCEPTION 'requisition % keeps what was asked and why', OLD.requisition_id USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF OLD.status = 'Cancelled' THEN
        RAISE EXCEPTION 'requisition % is cancelled', OLD.requisition_id USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER requisition_frozen BEFORE UPDATE ON procurement.requisition FOR EACH ROW EXECUTE FUNCTION procurement.freeze_requisition();

CREATE FUNCTION procurement.keep_line_source() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.requisition_id, NEW.source_kind, NEW.source_key, NEW.source_label) IS DISTINCT FROM (OLD.requisition_id, OLD.source_kind, OLD.source_key, OLD.source_label) THEN
        RAISE EXCEPTION 'purchase order % line % keeps the source it was bought for', OLD.po_number, OLD.line_number USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER purchase_line_source_kept BEFORE UPDATE ON procurement.purchase_order_line FOR EACH ROW EXECUTE FUNCTION procurement.keep_line_source();
