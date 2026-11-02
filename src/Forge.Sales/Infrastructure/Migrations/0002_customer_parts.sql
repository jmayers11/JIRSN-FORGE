-- Owner decisions 2026-11-01: each customer's own part numbers for our parts, so orders that carry the buyer's number map exactly.
CREATE TABLE sales.customer_part (
    customer_code  text NOT NULL REFERENCES sales.customer(code),
    customer_part  text NOT NULL CHECK (customer_part = upper(customer_part) AND length(customer_part) BETWEEN 1 AND 60),
    part_number    text NOT NULL REFERENCES engineering.item(part_number),
    description    text NULL,
    updated_by     text NOT NULL,
    updated_at     timestamptz NOT NULL,
    PRIMARY KEY (customer_code, customer_part)
);
