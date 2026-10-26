-- Sprint 10 (WW-S10-24), owner decision 2026-10-25: costs follow US GAAP practice; standard cost with full absorption (material, labor,
-- overhead) is the method, kept by a Cost Accountant. Policies and standard costs are versions that take effect when recorded and
-- never rewrite earlier valuations. Rates are per direct labor hour and cost the work rework adds.
CREATE TABLE inventory.costing_policy (
    policy_id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    method          text NOT NULL CHECK (method IN ('Standard', 'WeightedAverage', 'Fifo')),
    currency        text NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    labor_rate      numeric(18, 6) NULL CHECK (labor_rate >= 0),
    overhead_rate   numeric(18, 6) NULL CHECK (overhead_rate >= 0),
    effective_from  timestamptz NOT NULL,
    set_by          text NOT NULL,
    reason          text NOT NULL
);

CREATE TABLE inventory.standard_cost (
    cost_id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    part_number     text NOT NULL REFERENCES engineering.item(part_number),
    material        numeric(18, 6) NOT NULL CHECK (material >= 0),
    labor           numeric(18, 6) NOT NULL CHECK (labor >= 0),
    overhead        numeric(18, 6) NOT NULL CHECK (overhead >= 0),
    currency        text NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    effective_from  timestamptz NOT NULL,
    set_by          text NOT NULL,
    reason          text NOT NULL
);
CREATE INDEX standard_cost_part ON inventory.standard_cost (part_number, effective_from);

-- The plant starts on standard cost in US dollars; rates are set by the Cost Accountant.
INSERT INTO inventory.costing_policy (method, currency, labor_rate, overhead_rate, effective_from, set_by, reason)
VALUES ('Standard', 'USD', NULL, NULL, '2000-01-01T00:00:00Z', 'system:migration', 'Owner decision 2026-10-25: US GAAP, standard cost with full absorption');
