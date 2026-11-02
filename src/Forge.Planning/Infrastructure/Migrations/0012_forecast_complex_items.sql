-- Owner decisions 2026-11-02: forecasting for more complex items. A new item borrows a like item's pattern (scaled, from a launch month);
-- a family of variants, or the options of a configurable item, is forecast as a whole and split by share or attach rate; planner
-- adjustments (promotions, known large orders, end of life) sit on top of the method's figure, kept apart and explained.
CREATE TABLE planning.forecast_analog (
    part_number   text PRIMARY KEY REFERENCES engineering.item(part_number),
    like_part     text NOT NULL REFERENCES engineering.item(part_number),
    scale         numeric(18,4) NULL CHECK (scale > 0 AND scale <= 100),
    launch_month  date NULL CHECK (launch_month IS NULL OR extract(day FROM launch_month) = 1),
    set_by        text NOT NULL,
    set_at        timestamptz NOT NULL,
    CHECK (part_number <> like_part)
);

CREATE TABLE planning.forecast_family (
    family_code  text PRIMARY KEY,
    name         text NOT NULL,
    driver_part  text NULL REFERENCES engineering.item(part_number),
    set_by       text NOT NULL,
    set_at       timestamptz NOT NULL
);

CREATE TABLE planning.forecast_family_member (
    family_code  text NOT NULL REFERENCES planning.forecast_family(family_code) ON DELETE CASCADE,
    part_number  text PRIMARY KEY REFERENCES engineering.item(part_number),
    share        numeric(18,6) NULL CHECK (share > 0 AND share <= 100),
    position     integer NOT NULL
);

CREATE TABLE planning.forecast_adjustment (
    adjustment_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    part_number    text NOT NULL REFERENCES engineering.item(part_number),
    from_month     date NOT NULL CHECK (extract(day FROM from_month) = 1),
    to_month       date NULL CHECK (to_month IS NULL OR to_month >= from_month),
    kind           text NOT NULL CHECK (kind IN ('AddUnits', 'Percent', 'EndOfLife')),
    amount         numeric(18,4) NOT NULL,
    reason         text NOT NULL,
    set_by         text NOT NULL,
    set_at         timestamptz NOT NULL,
    cancelled_by   text NULL,
    cancelled_at   timestamptz NULL
);
