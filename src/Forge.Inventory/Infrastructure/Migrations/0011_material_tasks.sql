-- V3.4: movement and point of use (V3 brief I, BB; spec §49).
--
-- Getting material to the machine is work of its own: it is requested for an order, allocated from specific
-- stock, picked (the picker scans the location and lot), and delivered (the deliverer scans where it went).
-- Each step is recorded with who and when; delivery is the only step that moves stock in the ledger. Put-away
-- and replenishment follow the same steps without an order. Cycle-count programs say what is counted how often.

CREATE TABLE inventory.material_task (
    task_id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind             text NOT NULL CHECK (kind IN ('Stage', 'Kit', 'PutAway', 'Replenish')),
    order_number     bigint NULL,
    kit_code         text NULL,
    part_number      text NOT NULL REFERENCES engineering.item(part_number),
    lot_number       text NULL,
    quantity         numeric(18, 6) NOT NULL CHECK (quantity > 0),
    from_location    text NULL REFERENCES inventory.location(code),
    to_location      text NOT NULL REFERENCES inventory.location(code),
    status           text NOT NULL CHECK (status IN ('Requested', 'Allocated', 'Picked', 'Delivered', 'Cancelled')),
    picked_quantity  numeric(18, 6) NULL CHECK (picked_quantity > 0),
    reason           text NULL,
    requested_by     text NOT NULL,
    requested_at     timestamptz NOT NULL,
    allocated_by     text NULL,
    allocated_at     timestamptz NULL,
    picked_by        text NULL,
    picked_at        timestamptz NULL,
    delivered_by     text NULL,
    delivered_at     timestamptz NULL,
    transaction_id   bigint NULL REFERENCES inventory.transaction(transaction_id),
    cancelled_by     text NULL,
    cancelled_at     timestamptz NULL,
    cancel_reason    text NULL,
    CHECK (kind IN ('PutAway', 'Replenish') OR order_number IS NOT NULL),
    CHECK (kind <> 'Kit' OR kit_code IS NOT NULL),
    CHECK (status IN ('Requested', 'Cancelled') OR from_location IS NOT NULL),
    CHECK (status NOT IN ('Picked', 'Delivered') OR (picked_quantity IS NOT NULL AND picked_quantity <= quantity)),
    CHECK (status <> 'Delivered' OR transaction_id IS NOT NULL),
    CHECK (status <> 'Cancelled' OR cancel_reason IS NOT NULL)
);
CREATE INDEX material_task_open ON inventory.material_task (status, part_number) WHERE status IN ('Requested', 'Allocated', 'Picked');
CREATE INDEX material_task_order ON inventory.material_task (order_number) WHERE order_number IS NOT NULL;

-- What is counted how often: every balance in a location (and the locations in it), or every balance of a part.
CREATE TABLE inventory.cycle_count_program (
    program_code     text PRIMARY KEY,
    name             text NOT NULL,
    location_code    text NULL REFERENCES inventory.location(code),
    part_number      text NULL REFERENCES engineering.item(part_number),
    frequency_days   int NOT NULL CHECK (frequency_days BETWEEN 1 AND 366),
    active           boolean NOT NULL,
    created_by       text NOT NULL,
    created_at       timestamptz NOT NULL,
    retired_by       text NULL,
    retired_at       timestamptz NULL,
    CHECK (location_code IS NOT NULL OR part_number IS NOT NULL)
);
