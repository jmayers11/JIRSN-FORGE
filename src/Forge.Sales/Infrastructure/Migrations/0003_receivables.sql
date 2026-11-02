-- Owner decisions 2026-11-01: customer payments, credit limits and credit holds. Billing clerks record payments against invoices; a
-- customer over their limit or past due beyond the plant's grace days waits for a sales manager or credit manager to release an order
-- or shipment. Forge keeps no ledger: payments go to the accounting system in the cash-receipts export.
CREATE TABLE sales.payment (
    payment_id     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_code  text NOT NULL REFERENCES sales.customer(code),
    received_on    date NOT NULL,
    amount         numeric(18,2) NOT NULL CHECK (amount > 0),
    currency       text NOT NULL,
    method         text NOT NULL CHECK (method IN ('Check', 'Ach', 'Wire', 'Card', 'Other')),
    reference      text NULL,
    note           text NULL,
    status         text NOT NULL CHECK (status IN ('Recorded', 'Voided')),
    recorded_by    text NOT NULL,
    recorded_at    timestamptz NOT NULL,
    voided_by      text NULL,
    voided_at      timestamptz NULL,
    void_reason    text NULL
);
CREATE INDEX payment_customer ON sales.payment (customer_code);

CREATE TABLE sales.payment_application (
    payment_id      bigint NOT NULL REFERENCES sales.payment(payment_id),
    invoice_number  bigint NOT NULL,
    amount          numeric(18,2) NOT NULL CHECK (amount > 0),
    PRIMARY KEY (payment_id, invoice_number)
);

CREATE TABLE sales.customer_credit (
    customer_code  text PRIMARY KEY REFERENCES sales.customer(code),
    credit_limit   numeric(18,2) NULL CHECK (credit_limit IS NULL OR credit_limit >= 0),
    updated_by     text NOT NULL,
    updated_at     timestamptz NOT NULL
);

CREATE TABLE sales.credit_policy (
    policy_id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    past_due_days        integer NULL CHECK (past_due_days IS NULL OR past_due_days BETWEEN 0 AND 365),
    include_open_orders  boolean NOT NULL,
    set_by               text NOT NULL,
    set_at               timestamptz NOT NULL
);

CREATE TABLE sales.credit_release (
    release_id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_code       text NOT NULL REFERENCES sales.customer(code),
    sales_order_number  bigint NULL,
    shipment_id         bigint NULL,
    reason              text NOT NULL,
    released_by         text NOT NULL,
    released_at         timestamptz NOT NULL,
    CHECK ((sales_order_number IS NULL) <> (shipment_id IS NULL))
);
CREATE INDEX credit_release_customer ON sales.credit_release (customer_code);
