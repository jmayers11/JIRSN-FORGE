CREATE SCHEMA IF NOT EXISTS inventory;

CREATE TABLE inventory.location (
    code        text PRIMARY KEY,
    site_code   text NOT NULL REFERENCES resources.site(code),
    name        text NOT NULL,
    kind        text NOT NULL CHECK (kind IN ('Stores', 'Receiving', 'Inspection', 'Quarantine', 'Production', 'Shipping')),
    active      boolean NOT NULL DEFAULT true,
    created_at  timestamptz NOT NULL,
    created_by  text NOT NULL
);

CREATE TABLE inventory.lot (
    part_number        text NOT NULL REFERENCES engineering.item(part_number),
    lot_number         text NOT NULL,
    supplier_lot       text NULL,
    first_received_at  timestamptz NOT NULL,
    created_by         text NOT NULL,
    PRIMARY KEY (part_number, lot_number)
);

CREATE TABLE inventory.adjustment_request (
    request_id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind             text NOT NULL CHECK (kind IN ('Adjustment', 'Count')),
    part_number      text NOT NULL REFERENCES engineering.item(part_number),
    location_code    text NOT NULL REFERENCES inventory.location(code),
    lot_number       text NULL,
    status           text NOT NULL CHECK (status IN ('Available', 'Inspection', 'Hold')),
    quantity         numeric(18, 6) NOT NULL,
    system_quantity  numeric(18, 6) NOT NULL,
    reason           text NOT NULL,
    requested_by     text NOT NULL,
    requested_at     timestamptz NOT NULL,
    state            text NOT NULL CHECK (state IN ('Pending', 'Approved', 'Rejected', 'Matched')),
    decided_by       text NULL,
    decided_at       timestamptz NULL,
    transaction_id   bigint NULL,
    -- Two people: whoever asks for an adjustment never approves it.
    CHECK (decided_by IS NULL OR decided_by <> requested_by OR state = 'Matched')
);

-- The ledger. Every quantity Forge shows is a sum of these rows.
CREATE TABLE inventory.transaction (
    transaction_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind            text NOT NULL CHECK (kind IN ('Receipt', 'Issue', 'Return', 'Transfer', 'Adjustment', 'ProductionReceipt', 'Scrap', 'CycleCount', 'QualityHold', 'QualityRelease')),
    part_number     text NOT NULL REFERENCES engineering.item(part_number),
    quantity        numeric(18, 6) NOT NULL CHECK (quantity > 0),
    from_location   text NULL REFERENCES inventory.location(code),
    from_status     text NULL CHECK (from_status IN ('Available', 'Inspection', 'Hold')),
    to_location     text NULL REFERENCES inventory.location(code),
    to_status       text NULL CHECK (to_status IN ('Available', 'Inspection', 'Hold')),
    lot_number      text NULL,
    order_number    bigint NULL REFERENCES execution.production_order(order_number),
    po_number       bigint NULL,
    po_line         integer NULL,
    reason          text NULL,
    request_id      bigint NULL REFERENCES inventory.adjustment_request(request_id),
    at              timestamptz NOT NULL,
    actor           text NOT NULL,
    approved_by     text NULL,
    CHECK (from_location IS NOT NULL OR to_location IS NOT NULL),
    CHECK ((from_location IS NULL) = (from_status IS NULL)),
    CHECK ((to_location IS NULL) = (to_status IS NULL)),
    CHECK (approved_by IS NULL OR approved_by <> actor),
    FOREIGN KEY (part_number, lot_number) REFERENCES inventory.lot(part_number, lot_number),
    FOREIGN KEY (po_number, po_line) REFERENCES procurement.purchase_order_line(po_number, line_number)
);
CREATE INDEX transaction_part ON inventory.transaction (part_number, transaction_id);
CREATE INDEX transaction_order ON inventory.transaction (order_number) WHERE order_number IS NOT NULL;

ALTER TABLE inventory.adjustment_request
    ADD CONSTRAINT adjustment_request_transaction FOREIGN KEY (transaction_id) REFERENCES inventory.transaction(transaction_id);

CREATE FUNCTION inventory.reject_ledger_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'inventory transactions are append-only; post a correcting transaction instead'
        USING ERRCODE = 'insufficient_privilege';
END;
$$;

CREATE TRIGGER transaction_append_only BEFORE UPDATE OR DELETE ON inventory.transaction
    FOR EACH ROW EXECUTE FUNCTION inventory.reject_ledger_change();

-- Balances: each entry adds to where stock went and subtracts from where it came from.
CREATE VIEW inventory.balance AS
SELECT part_number, location_code, lot_number, status, sum(quantity) AS quantity
FROM (
    SELECT part_number, to_location AS location_code, lot_number, to_status AS status, quantity FROM inventory.transaction WHERE to_location IS NOT NULL
    UNION ALL
    SELECT part_number, from_location, lot_number, from_status, -quantity FROM inventory.transaction WHERE from_location IS NOT NULL
) moves
GROUP BY part_number, location_code, lot_number, status;
