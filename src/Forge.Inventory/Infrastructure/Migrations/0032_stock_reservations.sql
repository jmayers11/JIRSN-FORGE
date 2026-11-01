-- Owner decision 2026-10-31: releasing a production order or approving a sales order reserves free stock for it. The ledger stays the
-- only source of on-hand quantity; a reservation records what is held, and issues to the order or shipments against the demand draw it down.
CREATE TABLE inventory.stock_reservation (
    reservation_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    target          text NOT NULL CHECK (target IN ('ProductionOrder', 'Demand')),
    order_number    bigint NULL,
    demand_id       bigint NULL,
    part_number     text NOT NULL REFERENCES engineering.item(part_number),
    quantity        numeric(18, 6) NOT NULL CHECK (quantity > 0),
    status          text NOT NULL CHECK (status IN ('Active', 'Released')),
    created_at      timestamptz NOT NULL,
    created_by      text NOT NULL,
    released_at     timestamptz NULL,
    released_by     text NULL,
    release_reason  text NULL,
    CHECK ((target = 'ProductionOrder') = (order_number IS NOT NULL)),
    CHECK ((target = 'Demand') = (demand_id IS NOT NULL)),
    CHECK ((status = 'Released') = (released_at IS NOT NULL AND released_by IS NOT NULL AND release_reason IS NOT NULL))
);
CREATE UNIQUE INDEX stock_reservation_one_active_order ON inventory.stock_reservation (order_number, part_number) WHERE status = 'Active' AND target = 'ProductionOrder';
CREATE UNIQUE INDEX stock_reservation_one_active_demand ON inventory.stock_reservation (demand_id, part_number) WHERE status = 'Active' AND target = 'Demand';
CREATE INDEX stock_reservation_part ON inventory.stock_reservation (part_number) WHERE status = 'Active';
