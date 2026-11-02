-- Owner decisions 2026-11-02: the V5 allocation optimizer. The plant's ranking rules for competing short orders, and which reservations
-- hold an approved alternate in place of the part the BOM calls for (and at what factor).
CREATE TABLE inventory.allocation_policy (
    policy_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    rules      text[] NOT NULL CHECK (cardinality(rules) > 0 AND rules <@ ARRAY['DueDate', 'Priority', 'SmallestShortage', 'ReleasedFirst']::text[]),
    set_by     text NOT NULL,
    set_at     timestamptz NOT NULL
);

CREATE TABLE inventory.reservation_alternate (
    reservation_id  bigint PRIMARY KEY REFERENCES inventory.stock_reservation(reservation_id),
    primary_part    text NOT NULL,
    factor          numeric(18,6) NOT NULL CHECK (factor > 0),
    alternate_id    bigint NULL
);
