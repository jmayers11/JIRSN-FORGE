-- Owner decisions 2026-11-02: the allocation optimizer uses leftover pieces and work in progress. Lots (usually remnants) can carry a
-- size; a BOM line's job can need a minimum piece size; an approved size-matched line sets the specific lot aside for its order; and
-- output of an open production order no demand is counting on can be promised to an order that is short, reserved for it on receipt.
CREATE TABLE inventory.lot_dimension (
    part_number  text NOT NULL,
    lot_number   text NOT NULL,
    length       numeric(18,4) NULL CHECK (length > 0),
    width        numeric(18,4) NULL CHECK (width > 0),
    thickness    numeric(18,4) NULL CHECK (thickness > 0),
    unit         text NOT NULL CHECK (unit IN ('Millimetre', 'Centimetre', 'Metre', 'Inch', 'Foot')),
    recorded_by  text NOT NULL,
    recorded_at  timestamptz NOT NULL,
    PRIMARY KEY (part_number, lot_number),
    FOREIGN KEY (part_number, lot_number) REFERENCES inventory.lot(part_number, lot_number),
    CHECK (length IS NOT NULL OR width IS NOT NULL OR thickness IS NOT NULL)
);

CREATE TABLE inventory.piece_size_rule (
    parent_part     text NOT NULL REFERENCES engineering.item(part_number),
    component_part  text NOT NULL REFERENCES engineering.item(part_number),
    min_length      numeric(18,4) NULL CHECK (min_length > 0),
    min_width       numeric(18,4) NULL CHECK (min_width > 0),
    min_thickness   numeric(18,4) NULL CHECK (min_thickness > 0),
    unit            text NOT NULL CHECK (unit IN ('Millimetre', 'Centimetre', 'Metre', 'Inch', 'Foot')),
    set_by          text NOT NULL,
    set_at          timestamptz NOT NULL,
    PRIMARY KEY (parent_part, component_part),
    CHECK (min_length IS NOT NULL OR min_width IS NOT NULL OR min_thickness IS NOT NULL)
);

CREATE TABLE inventory.lot_earmark (
    earmark_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_number    bigint NOT NULL REFERENCES execution.production_order(order_number),
    part_number     text NOT NULL,
    lot_number      text NOT NULL,
    quantity        numeric(18,6) NOT NULL CHECK (quantity > 0),
    created_by      text NOT NULL,
    created_at      timestamptz NOT NULL,
    released_by     text NULL,
    released_at     timestamptz NULL,
    release_reason  text NULL,
    FOREIGN KEY (part_number, lot_number) REFERENCES inventory.lot(part_number, lot_number)
);
CREATE INDEX lot_earmark_open ON inventory.lot_earmark (part_number, lot_number) WHERE released_at IS NULL;

CREATE TABLE inventory.wip_allocation (
    allocation_id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    wip_order_number       bigint NOT NULL REFERENCES execution.production_order(order_number),
    consumer_order_number  bigint NOT NULL REFERENCES execution.production_order(order_number),
    part_number            text NOT NULL REFERENCES engineering.item(part_number),
    quantity               numeric(18,6) NOT NULL CHECK (quantity > 0),
    fulfilled              numeric(18,6) NOT NULL DEFAULT 0 CHECK (fulfilled >= 0),
    status                 text NOT NULL CHECK (status IN ('Active', 'Fulfilled', 'Released')),
    why                    text NOT NULL,
    approved_by            text NOT NULL,
    approved_at            timestamptz NOT NULL,
    closed_by              text NULL,
    closed_at              timestamptz NULL,
    close_reason           text NULL,
    CHECK (wip_order_number <> consumer_order_number)
);
CREATE INDEX wip_allocation_active ON inventory.wip_allocation (wip_order_number) WHERE status = 'Active';
