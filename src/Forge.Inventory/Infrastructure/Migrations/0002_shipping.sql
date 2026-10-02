-- Shipping (spec §47): stock leaves the plant against a customer shipment. One person packs it
-- (confirming what is in the box), a different person releases it to the carrier.
CREATE TABLE inventory.shipment (
    shipment_id   bigint GENERATED ALWAYS AS IDENTITY (START WITH 7001) PRIMARY KEY,
    customer      text NOT NULL,
    reference     text NULL,
    -- The demand this ships against (planning.demand). Not a foreign key: Planning's schema is
    -- created after Inventory's; the shipping endpoint checks the demand before the shipment is made.
    demand_id     bigint NULL,
    ship_to       text NULL,
    status        text NOT NULL CHECK (status IN ('Open', 'Packed', 'Shipped', 'Cancelled')),
    created_by    text NOT NULL,
    created_at    timestamptz NOT NULL,
    packed_by     text NULL,
    packed_at     timestamptz NULL,
    shipped_by    text NULL,
    shipped_at    timestamptz NULL,
    carrier       text NULL,
    tracking      text NULL,
    cancel_reason text NULL,
    CHECK (shipped_by IS NULL OR shipped_by <> packed_by),
    CHECK (status <> 'Shipped' OR (packed_by IS NOT NULL AND shipped_by IS NOT NULL))
);
CREATE INDEX shipment_demand ON inventory.shipment (demand_id) WHERE demand_id IS NOT NULL;

CREATE TABLE inventory.shipment_line (
    shipment_id    bigint NOT NULL REFERENCES inventory.shipment(shipment_id),
    line_number    integer NOT NULL CHECK (line_number BETWEEN 1 AND 999),
    part_number    text NOT NULL REFERENCES engineering.item(part_number),
    quantity       numeric(18, 6) NOT NULL CHECK (quantity > 0),
    location_code  text NOT NULL REFERENCES inventory.location(code),
    lot_number     text NULL,
    PRIMARY KEY (shipment_id, line_number)
);

ALTER TABLE inventory.transaction ADD COLUMN shipment_id bigint NULL REFERENCES inventory.shipment(shipment_id);

ALTER TABLE inventory.transaction DROP CONSTRAINT transaction_kind_check;
ALTER TABLE inventory.transaction ADD CONSTRAINT transaction_kind_check
    CHECK (kind IN ('Receipt', 'Issue', 'Return', 'Transfer', 'Adjustment', 'ProductionReceipt', 'Scrap', 'CycleCount', 'QualityHold', 'QualityRelease', 'Shipment'));
ALTER TABLE inventory.transaction ADD CONSTRAINT transaction_shipment_check
    CHECK ((kind = 'Shipment') = (shipment_id IS NOT NULL));
