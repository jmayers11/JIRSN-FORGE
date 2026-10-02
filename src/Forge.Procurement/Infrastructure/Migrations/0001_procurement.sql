CREATE SCHEMA IF NOT EXISTS procurement;

CREATE TABLE procurement.purchase_order (
    po_number    bigint GENERATED ALWAYS AS IDENTITY (START WITH 5001) PRIMARY KEY,
    supplier     text NOT NULL,
    status       text NOT NULL CHECK (status IN ('Draft', 'Approved', 'Closed', 'Cancelled')),
    created_at   timestamptz NOT NULL,
    created_by   text NOT NULL,
    approved_at  timestamptz NULL,
    approved_by  text NULL,
    closed_at    timestamptz NULL,
    closed_by    text NULL,
    -- Two people: the buyer who raised the order never approves it.
    CHECK (approved_by IS NULL OR approved_by <> created_by),
    CHECK (status IN ('Draft', 'Cancelled') OR approved_by IS NOT NULL)
);

CREATE TABLE procurement.purchase_order_line (
    po_number        bigint NOT NULL REFERENCES procurement.purchase_order(po_number),
    line_number      integer NOT NULL CHECK (line_number BETWEEN 1 AND 999),
    part_number      text NOT NULL REFERENCES engineering.item(part_number),
    quantity         numeric(18, 6) NOT NULL CHECK (quantity > 0),
    unit_of_measure  text NOT NULL,
    promised_date    date NOT NULL,
    status           text NOT NULL CHECK (status IN ('Open', 'Cancelled')),
    PRIMARY KEY (po_number, line_number)
);
