-- Owner decisions 2026-11-01: API and EDI. Trading partners (customers, suppliers, other systems), ANSI X12 documents in and out,
-- partner API keys (only a hash is kept) and HMAC-signed webhooks with their deliveries.
CREATE SCHEMA IF NOT EXISTS interchange;

CREATE TABLE interchange.trading_partner (
    code           text PRIMARY KEY,
    name           text NOT NULL,
    kind           text NOT NULL CHECK (kind IN ('Customer', 'Supplier', 'System')),
    party_code     text NULL,
    edi_qualifier  text NULL,
    edi_id         text NULL,
    our_qualifier  text NULL,
    our_id         text NULL,
    outbound       text[] NOT NULL DEFAULT '{}',
    active         boolean NOT NULL,
    test           boolean NOT NULL DEFAULT false,
    updated_by     text NOT NULL,
    updated_at     timestamptz NOT NULL,
    next_control   bigint NOT NULL DEFAULT 1,
    CHECK ((kind = 'System') = (party_code IS NULL)),
    CHECK ((edi_qualifier IS NULL) = (edi_id IS NULL) AND (edi_id IS NULL) = (our_qualifier IS NULL) AND (our_qualifier IS NULL) = (our_id IS NULL))
);
CREATE UNIQUE INDEX trading_partner_edi ON interchange.trading_partner (edi_qualifier, edi_id) WHERE edi_id IS NOT NULL;
CREATE UNIQUE INDEX trading_partner_party ON interchange.trading_partner (kind, party_code) WHERE party_code IS NOT NULL;

CREATE TABLE interchange.api_key (
    key_id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    partner_code  text NOT NULL REFERENCES interchange.trading_partner(code),
    prefix        text NOT NULL,
    key_hash      text NOT NULL UNIQUE,
    scopes        text[] NOT NULL,
    issued_by     text NOT NULL,
    issued_at     timestamptz NOT NULL,
    revoked_by    text NULL,
    revoked_at    timestamptz NULL
);

CREATE TABLE interchange.document (
    document_id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    direction            text NOT NULL CHECK (direction IN ('Inbound', 'Outbound')),
    partner_code         text NULL REFERENCES interchange.trading_partner(code),
    transaction_set      text NOT NULL,
    interchange_control  text NOT NULL,
    group_control        text NOT NULL,
    transaction_control  text NOT NULL,
    status               text NOT NULL CHECK (status IN ('Received', 'Processed', 'NeedsReview', 'Rejected', 'Ready', 'Sent', 'Accepted', 'Refused')),
    business_ref         text NULL,
    summary              text NULL,
    problems             text[] NOT NULL DEFAULT '{}',
    file_name            text NULL,
    content              text NOT NULL,
    created_at           timestamptz NOT NULL,
    created_by           text NOT NULL,
    updated_at           timestamptz NULL,
    updated_by           text NULL
);
CREATE UNIQUE INDEX document_inbound_once ON interchange.document (partner_code, interchange_control, group_control, transaction_control) WHERE direction = 'Inbound';
CREATE UNIQUE INDEX document_outbound_once ON interchange.document (partner_code, transaction_set, business_ref) WHERE direction = 'Outbound';
CREATE INDEX document_status ON interchange.document (status, document_id);

CREATE TABLE interchange.webhook (
    webhook_id     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name           text NOT NULL,
    url            text NOT NULL CHECK (url LIKE 'https://%'),
    events         text[] NOT NULL,
    partner_code   text NULL REFERENCES interchange.trading_partner(code),
    active         boolean NOT NULL,
    secret         text NOT NULL,
    secret_prefix  text NOT NULL,
    updated_by     text NOT NULL,
    updated_at     timestamptz NOT NULL
);

CREATE TABLE interchange.webhook_delivery (
    delivery_id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    webhook_id        bigint NOT NULL REFERENCES interchange.webhook(webhook_id),
    event_sequence    bigint NOT NULL,
    event_name        text NOT NULL,
    payload           text NOT NULL,
    status            text NOT NULL CHECK (status IN ('Pending', 'Delivered', 'Failed')),
    attempts          integer NOT NULL DEFAULT 0,
    next_attempt_at   timestamptz NOT NULL,
    last_status_code  integer NULL,
    last_error        text NULL,
    created_at        timestamptz NOT NULL,
    delivered_at      timestamptz NULL,
    UNIQUE (webhook_id, event_sequence)
);
CREATE INDEX webhook_delivery_due ON interchange.webhook_delivery (next_attempt_at) WHERE status = 'Pending';
