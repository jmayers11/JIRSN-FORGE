-- Quote to cash (owner decisions 2026-10-31): customers, quotes, sales orders and invoices of shipped quantities. Forge keeps no
-- general ledger; the accounting system takes Forge's export.
CREATE SCHEMA IF NOT EXISTS sales;

CREATE TABLE sales.customer (
    code                    text PRIMARY KEY CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,39}$'),
    name                    text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    status                  text NOT NULL CHECK (status IN ('Active', 'OnHold', 'Inactive')),
    bill_to                 text NULL,
    ship_to                 text NULL,
    payment_terms_days      integer NOT NULL CHECK (payment_terms_days BETWEEN 0 AND 365),
    tax_rate_percent        numeric(6, 3) NOT NULL CHECK (tax_rate_percent BETWEEN 0 AND 25),
    tax_exempt_certificate  text NULL,
    currency                text NOT NULL CHECK (currency IN ('USD', 'EUR', 'GBP', 'CAD')),
    notes                   text NULL,
    created_at              timestamptz NOT NULL,
    created_by              text NOT NULL,
    changed_at              timestamptz NULL,
    changed_by              text NULL,
    -- A tax-exempt customer is charged no tax.
    CHECK (tax_exempt_certificate IS NULL OR tax_rate_percent = 0)
);
CREATE UNIQUE INDEX customer_name ON sales.customer (lower(name));

CREATE TABLE sales.quote (
    quote_number        bigint GENERATED ALWAYS AS IDENTITY (START WITH 7001) PRIMARY KEY,
    customer_code       text NOT NULL REFERENCES sales.customer(code),
    status              text NOT NULL CHECK (status IN ('Draft', 'Approved', 'Accepted', 'Declined', 'Cancelled')),
    valid_until         date NOT NULL,
    currency            text NOT NULL CHECK (currency IN ('USD', 'EUR', 'GBP', 'CAD')),
    notes               text NULL,
    created_at          timestamptz NOT NULL,
    created_by          text NOT NULL,
    approved_at         timestamptz NULL,
    approved_by         text NULL,
    closed_at           timestamptz NULL,
    closed_by           text NULL,
    sales_order_number  bigint NULL,
    -- Two people: the author never approves.
    CHECK (approved_by IS NULL OR approved_by <> created_by),
    CHECK (status NOT IN ('Approved', 'Accepted') OR approved_by IS NOT NULL),
    CHECK ((status = 'Accepted') = (sales_order_number IS NOT NULL))
);

CREATE TABLE sales.quote_line (
    quote_number     bigint NOT NULL REFERENCES sales.quote(quote_number),
    line_number      integer NOT NULL CHECK (line_number BETWEEN 1 AND 999),
    part_number      text NOT NULL REFERENCES engineering.item(part_number),
    quantity         numeric(18, 6) NOT NULL CHECK (quantity > 0),
    unit_price       numeric(18, 4) NOT NULL CHECK (unit_price >= 0),
    lead_time_days   integer NOT NULL CHECK (lead_time_days BETWEEN 0 AND 730),
    description      text NULL,
    PRIMARY KEY (quote_number, line_number)
);

CREATE TABLE sales.sales_order (
    order_number   bigint GENERATED ALWAYS AS IDENTITY (START WITH 8001) PRIMARY KEY,
    customer_code  text NOT NULL REFERENCES sales.customer(code),
    customer_name  text NOT NULL,
    customer_po    text NULL,
    quote_number   bigint NULL REFERENCES sales.quote(quote_number),
    ship_to        text NULL,
    currency       text NOT NULL CHECK (currency IN ('USD', 'EUR', 'GBP', 'CAD')),
    status         text NOT NULL CHECK (status IN ('Draft', 'Approved', 'Closed', 'Cancelled')),
    created_at     timestamptz NOT NULL,
    created_by     text NOT NULL,
    approved_at    timestamptz NULL,
    approved_by    text NULL,
    closed_at      timestamptz NULL,
    closed_by      text NULL,
    CHECK (approved_by IS NULL OR approved_by <> created_by),
    CHECK (status <> 'Approved' OR approved_by IS NOT NULL)
);
ALTER TABLE sales.quote ADD FOREIGN KEY (sales_order_number) REFERENCES sales.sales_order(order_number);

CREATE TABLE sales.sales_order_line (
    order_number     bigint NOT NULL REFERENCES sales.sales_order(order_number),
    line_number      integer NOT NULL CHECK (line_number BETWEEN 1 AND 999),
    part_number      text NOT NULL REFERENCES engineering.item(part_number),
    quantity         numeric(18, 6) NOT NULL CHECK (quantity > 0),
    unit_of_measure  text NOT NULL,
    unit_price       numeric(18, 4) NOT NULL CHECK (unit_price >= 0),
    due_date         date NOT NULL,
    status           text NOT NULL CHECK (status IN ('Open', 'Cancelled')),
    PRIMARY KEY (order_number, line_number)
);

CREATE TABLE sales.invoice (
    invoice_number          bigint GENERATED ALWAYS AS IDENTITY (START WITH 90001) PRIMARY KEY,
    order_number            bigint NOT NULL REFERENCES sales.sales_order(order_number),
    customer_code           text NOT NULL REFERENCES sales.customer(code),
    customer_name           text NOT NULL,
    customer_po             text NULL,
    status                  text NOT NULL CHECK (status IN ('Issued', 'Voided')),
    invoice_date            date NOT NULL,
    due_date                date NOT NULL CHECK (due_date >= invoice_date),
    currency                text NOT NULL CHECK (currency IN ('USD', 'EUR', 'GBP', 'CAD')),
    subtotal                numeric(18, 2) NOT NULL CHECK (subtotal >= 0),
    tax_rate_percent        numeric(6, 3) NOT NULL CHECK (tax_rate_percent BETWEEN 0 AND 25),
    tax_exempt_certificate  text NULL,
    tax                     numeric(18, 2) NOT NULL CHECK (tax >= 0),
    total                   numeric(18, 2) NOT NULL CHECK (total = subtotal + tax),
    issued_at               timestamptz NOT NULL,
    issued_by               text NOT NULL,
    voided_at               timestamptz NULL,
    voided_by               text NULL,
    void_reason             text NULL,
    CHECK ((status = 'Voided') = (voided_at IS NOT NULL AND voided_by IS NOT NULL AND void_reason IS NOT NULL))
);

-- Each line bills one confirmed shipment of one order line. A shipment is on at most one issued invoice.
CREATE TABLE sales.invoice_line (
    invoice_number  bigint NOT NULL REFERENCES sales.invoice(invoice_number),
    line_number     integer NOT NULL CHECK (line_number BETWEEN 1 AND 999),
    order_number    bigint NOT NULL,
    order_line      integer NOT NULL,
    shipment_id     bigint NOT NULL REFERENCES inventory.shipment(shipment_id),
    part_number     text NOT NULL REFERENCES engineering.item(part_number),
    quantity        numeric(18, 6) NOT NULL CHECK (quantity > 0),
    unit_price      numeric(18, 4) NOT NULL CHECK (unit_price >= 0),
    amount          numeric(18, 2) NOT NULL,
    voided          boolean NOT NULL DEFAULT false,
    PRIMARY KEY (invoice_number, line_number),
    FOREIGN KEY (order_number, order_line) REFERENCES sales.sales_order_line(order_number, line_number)
);
CREATE UNIQUE INDEX invoice_line_billed_once ON sales.invoice_line (shipment_id, order_number, order_line) WHERE NOT voided;
CREATE INDEX invoice_order ON sales.invoice (order_number);
