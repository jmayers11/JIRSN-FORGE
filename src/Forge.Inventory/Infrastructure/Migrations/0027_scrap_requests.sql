-- Sprint 10 §20: planner-initiated inventory scrap with controlled disposition.
-- These tables hold requests, decisions and direct-scrap rules only. Every quantity change is still an
-- inventory.transaction: a hold while Quality decides, the scrap itself, or a compensating adjustment.

CREATE TABLE inventory.scrap_policy (
    policy_id    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    role_name    text NOT NULL CHECK (length(btrim(role_name)) > 0),
    reason_code  text NULL CHECK (reason_code IS NULL OR reason_code IN ('Damaged', 'Obsolete', 'Expired', 'Contaminated', 'Other')),
    max_quantity numeric(18, 6) NULL CHECK (max_quantity IS NULL OR max_quantity > 0),
    max_value    numeric(18, 2) NULL CHECK (max_value IS NULL OR max_value >= 0),
    state        text NOT NULL CHECK (state IN ('Proposed', 'Active', 'Rejected', 'Retired')),
    proposed_by  text NOT NULL,
    proposed_at  timestamptz NOT NULL,
    decided_by   text NULL,
    decided_at   timestamptz NULL,
    CHECK (state = 'Proposed' OR (decided_by IS NOT NULL AND decided_at IS NOT NULL)),
    CHECK (decided_by IS NULL OR decided_by <> proposed_by)
);

CREATE TABLE inventory.scrap_request (
    request_id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind                   text NOT NULL CHECK (kind IN ('Scrap', 'Reversal')),
    part_number            text NOT NULL,
    location_code          text NOT NULL REFERENCES inventory.location(code),
    lot_number             text NULL,
    stock_status           text NOT NULL CHECK (stock_status IN ('Released', 'Inspection', 'Hold')),
    execute_status         text NOT NULL CHECK (execute_status IN ('Released', 'Inspection', 'Hold')),
    quantity               numeric(18, 6) NOT NULL CHECK (quantity > 0),
    reason_code            text NOT NULL CHECK (reason_code IN ('Damaged', 'Obsolete', 'Expired', 'Contaminated', 'QualityReject', 'Other')),
    condition_note         text NULL,
    notes                  text NULL,
    linked_order_number    bigint NULL,
    linked_po_number       bigint NULL,
    linked_work_order_id   bigint NULL,
    linked_ncr             text NULL,
    linked_supplier        text NULL,
    requires_quality       boolean NOT NULL,
    policy_id              bigint NULL REFERENCES inventory.scrap_policy(policy_id),
    estimated_unit_value   numeric(18, 6) NULL CHECK (estimated_unit_value IS NULL OR estimated_unit_value >= 0),
    currency               text NULL,
    state                  text NOT NULL CHECK (state IN ('PendingApproval', 'PendingDisposition', 'Approved', 'Rejected', 'Cancelled', 'Executed')),
    requested_by           text NOT NULL,
    requested_at           timestamptz NOT NULL,
    hold_transaction_id    bigint NULL REFERENCES inventory.transaction(transaction_id),
    decided_by             text NULL,
    decided_at             timestamptz NULL,
    decision_note          text NULL,
    executed_by            text NULL,
    executed_at            timestamptz NULL,
    transaction_id         bigint NULL REFERENCES inventory.transaction(transaction_id),
    release_transaction_id bigint NULL REFERENCES inventory.transaction(transaction_id),
    corrects_request_id    bigint NULL REFERENCES inventory.scrap_request(request_id),
    CHECK ((kind = 'Reversal') = (corrects_request_id IS NOT NULL)),
    CHECK (state NOT IN ('Approved', 'Rejected', 'Executed') OR (decided_by IS NOT NULL AND decided_at IS NOT NULL)),
    CHECK (state <> 'Executed' OR (transaction_id IS NOT NULL AND executed_by IS NOT NULL AND executed_at IS NOT NULL)),
    -- A person never approves their own request; a direct-scrap rule records itself as the decider.
    CHECK (decided_by IS NULL OR decided_by <> requested_by),
    CHECK (state <> 'PendingDisposition' OR requires_quality)
);

CREATE INDEX scrap_request_open ON inventory.scrap_request (part_number, location_code)
    WHERE state IN ('PendingApproval', 'PendingDisposition', 'Approved');

-- At most one live correction per executed scrap.
CREATE UNIQUE INDEX scrap_request_one_reversal ON inventory.scrap_request (corrects_request_id)
    WHERE corrects_request_id IS NOT NULL AND state NOT IN ('Rejected', 'Cancelled');

-- Decided and posted requests are history: nothing about them changes afterwards.
CREATE FUNCTION inventory.keep_closed_scrap_request() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.state IN ('Executed', 'Rejected', 'Cancelled') THEN
        RAISE EXCEPTION 'scrap request % is % and cannot change', OLD.request_id, OLD.state;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER scrap_request_closed_kept BEFORE UPDATE ON inventory.scrap_request
    FOR EACH ROW EXECUTE FUNCTION inventory.keep_closed_scrap_request();
