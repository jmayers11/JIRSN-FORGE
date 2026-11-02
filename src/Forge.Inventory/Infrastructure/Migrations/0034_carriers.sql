-- Owner decisions 2026-11-01: shipping carriers. Carrier accounts (credentials encrypted by the host), and the carrier side of each
-- shipment: label or bill of lading, tracking or PRO number, packages, cost and the carrier's latest tracking.
CREATE TABLE inventory.carrier_account (
    code            text PRIMARY KEY,
    kind            text NOT NULL CHECK (kind IN ('Ups', 'FedEx', 'Usps', 'ShipEngine', 'EasyPost', 'Manual')),
    name            text NOT NULL,
    account_number  text NULL,
    sandbox         boolean NOT NULL,
    active          boolean NOT NULL,
    settings        jsonb NOT NULL DEFAULT '{}'::jsonb,
    credentials     text NULL CHECK (credentials IS NULL OR credentials LIKE 'enc:%'),
    updated_by      text NOT NULL,
    updated_at      timestamptz NOT NULL
);

CREATE TABLE inventory.shipment_carrier (
    shipment_id         bigint PRIMARY KEY REFERENCES inventory.shipment(shipment_id),
    account_code        text NULL REFERENCES inventory.carrier_account(code),
    carrier             text NOT NULL,
    service             text NULL,
    tracking            text NULL,
    pro_number          text NULL,
    bol_number          text NULL,
    cost                numeric(18,2) NULL,
    currency            text NULL,
    ship_to             jsonb NULL,
    packages            jsonb NOT NULL DEFAULT '[]'::jsonb,
    label               bytea NULL,
    label_content_type  text NULL,
    label_reference     text NULL,
    status              text NOT NULL CHECK (status IN ('Labeled', 'Manual', 'InTransit', 'Delivered', 'Exception', 'Voided')),
    tracking_summary    text NULL,
    last_tracked_at     timestamptz NULL,
    delivered_at        timestamptz NULL,
    updated_by          text NOT NULL,
    updated_at          timestamptz NOT NULL
);
CREATE INDEX shipment_carrier_tracking ON inventory.shipment_carrier (status) WHERE status IN ('Labeled', 'InTransit', 'Exception');
