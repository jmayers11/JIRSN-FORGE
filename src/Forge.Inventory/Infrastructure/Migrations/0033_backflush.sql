-- Owner decisions 2026-11-01: automatic material deduction. Inventory controllers set which parts backflush and from which location;
-- reporting production at the consuming operation deducts them. What could not be deducted is kept here until someone resolves it.
CREATE TABLE inventory.backflush_setting (
    part_number    text PRIMARY KEY REFERENCES engineering.item(part_number),
    enabled        boolean NOT NULL,
    location_code  text NOT NULL,
    updated_by     text NOT NULL,
    updated_at     timestamptz NOT NULL
);

CREATE TABLE inventory.backflush_exception (
    exception_id     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_number     bigint NOT NULL,
    sequence         integer NOT NULL,
    report_id        bigint NOT NULL,
    part_number      text NOT NULL,
    quantity         numeric(18,4) NOT NULL CHECK (quantity > 0),
    deducted         numeric(18,4) NOT NULL CHECK (deducted >= 0),
    location_code    text NOT NULL,
    problem          text NOT NULL,
    status           text NOT NULL CHECK (status IN ('Open', 'Resolved')),
    raised_at        timestamptz NOT NULL,
    resolved_by      text NULL,
    resolved_at      timestamptz NULL,
    resolution_note  text NULL,
    UNIQUE (report_id, part_number)
);
CREATE INDEX backflush_exception_open ON inventory.backflush_exception (status) WHERE status = 'Open';
