-- Independent demand (spec §12): customer orders, forecasts, service and contract deliveries.
CREATE TABLE planning.demand (
    demand_id    bigint GENERATED ALWAYS AS IDENTITY (START WITH 101) PRIMARY KEY,
    part_number  text NOT NULL REFERENCES engineering.item(part_number),
    quantity     numeric(18, 6) NOT NULL CHECK (quantity > 0),
    need_date    date NOT NULL,
    priority     integer NOT NULL CHECK (priority BETWEEN 1 AND 9),
    kind         text NOT NULL CHECK (kind IN ('CustomerOrder', 'Forecast', 'Service', 'Internal', 'ContractDelivery')),
    customer     text NULL,
    reference    text NULL,
    program_id   uuid NULL,
    status       text NOT NULL CHECK (status IN ('Open', 'Closed', 'Cancelled')),
    created_at   timestamptz NOT NULL,
    created_by   text NOT NULL,
    changed_at   timestamptz NULL,
    changed_by   text NULL
);
CREATE INDEX demand_open ON planning.demand (part_number, need_date) WHERE status = 'Open';
