-- V3.14: identify physical cartons, crates and pallets and retain the part/lot/location of each packed quantity.
CREATE TABLE inventory.container (
    code        text PRIMARY KEY CHECK (length(code) BETWEEN 1 AND 80),
    kind        text NOT NULL CHECK (kind IN ('Carton', 'Crate', 'Pallet')),
    status      text NOT NULL CHECK (status IN ('Open', 'Sealed')),
    created_by  text NOT NULL,
    created_at  timestamptz NOT NULL,
    sealed_at   timestamptz NULL,
    CHECK ((status = 'Sealed') = (sealed_at IS NOT NULL))
);

CREATE TABLE inventory.container_content (
    container_code  text NOT NULL REFERENCES inventory.container(code),
    line_number     integer NOT NULL CHECK (line_number BETWEEN 1 AND 999),
    part_number     text NOT NULL REFERENCES engineering.item(part_number),
    quantity        numeric(18, 6) NOT NULL CHECK (quantity > 0),
    location_code   text NOT NULL REFERENCES inventory.location(code),
    lot_number      text NULL,
    status          text NOT NULL CHECK (status IN ('Released', 'Inspection', 'Hold')),
    PRIMARY KEY (container_code, line_number)
);

CREATE INDEX container_content_stock ON inventory.container_content (part_number, location_code, lot_number, status);
