-- Owner decisions 2026-11-01: supplier invoices and the three-way match. An Accounts Payable Clerk enters each invoice against its
-- purchase order; within the plant's tolerance it is approved for payment, otherwise a purchasing manager decides.
CREATE TABLE procurement.invoice_match_policy (
    policy_id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tolerance_percent  numeric(6,3) NOT NULL CHECK (tolerance_percent BETWEEN 0 AND 25),
    tolerance_amount   numeric(18,2) NOT NULL CHECK (tolerance_amount >= 0),
    set_by             text NOT NULL,
    set_at             timestamptz NOT NULL
);

CREATE TABLE procurement.supplier_invoice (
    invoice_id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    supplier_code            text NOT NULL,
    supplier_name            text NOT NULL,
    supplier_invoice_number  text NOT NULL,
    po_number                bigint NOT NULL,
    invoice_date             date NOT NULL,
    due_date                 date NOT NULL CHECK (due_date >= invoice_date),
    currency                 text NOT NULL,
    subtotal                 numeric(18,2) NOT NULL,
    freight                  numeric(18,2) NOT NULL CHECK (freight >= 0),
    tax                      numeric(18,2) NOT NULL CHECK (tax >= 0),
    total                    numeric(18,2) NOT NULL,
    variance                 numeric(18,2) NOT NULL,
    status                   text NOT NULL CHECK (status IN ('Approved', 'Exception', 'Rejected', 'Voided', 'Paid')),
    match_summary            text NOT NULL,
    entered_by               text NOT NULL,
    entered_at               timestamptz NOT NULL,
    decided_by               text NULL,
    decided_at               timestamptz NULL,
    decision_note            text NULL,
    paid_on                  date NULL,
    payment_reference        text NULL
);
CREATE UNIQUE INDEX supplier_invoice_number_live ON procurement.supplier_invoice (supplier_code, upper(supplier_invoice_number)) WHERE status <> 'Voided';
CREATE INDEX supplier_invoice_po ON procurement.supplier_invoice (po_number);

CREATE TABLE procurement.supplier_invoice_line (
    invoice_id     bigint NOT NULL REFERENCES procurement.supplier_invoice(invoice_id),
    line_number    integer NOT NULL,
    po_line        integer NOT NULL,
    part_number    text NOT NULL,
    quantity       numeric(18,4) NOT NULL CHECK (quantity > 0),
    unit_price     numeric(18,6) NOT NULL CHECK (unit_price >= 0),
    amount         numeric(18,2) NOT NULL,
    po_unit_price  numeric(18,6) NULL,
    received_open  numeric(18,4) NOT NULL,
    variance       numeric(18,2) NOT NULL,
    problem        text NULL,
    PRIMARY KEY (invoice_id, line_number)
);
