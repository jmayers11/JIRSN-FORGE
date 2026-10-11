-- A sealed container can be assigned to one shipment; shipping it releases its content reservations.
ALTER TABLE inventory.container DROP CONSTRAINT container_status_check;
ALTER TABLE inventory.container ADD CONSTRAINT container_status_check CHECK (status IN ('Open', 'Sealed', 'Shipped'));
ALTER TABLE inventory.container ADD COLUMN shipment_id bigint NULL REFERENCES inventory.shipment(shipment_id);
ALTER TABLE inventory.container DROP CONSTRAINT container_check;
ALTER TABLE inventory.container ADD COLUMN shipped_at timestamptz NULL;
ALTER TABLE inventory.container ADD CONSTRAINT container_seal_date CHECK ((status IN ('Sealed', 'Shipped')) = (sealed_at IS NOT NULL));
ALTER TABLE inventory.container ADD CONSTRAINT container_shipped_date CHECK ((status = 'Shipped') = (shipped_at IS NOT NULL));
ALTER TABLE inventory.container ADD CONSTRAINT container_shipment_state CHECK
    ((status = 'Shipped') = (shipment_id IS NOT NULL));

ALTER TABLE inventory.shipment ADD COLUMN container_code text NULL REFERENCES inventory.container(code);
