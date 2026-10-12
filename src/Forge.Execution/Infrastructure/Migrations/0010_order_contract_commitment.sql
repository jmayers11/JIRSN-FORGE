ALTER TABLE execution.production_order ADD COLUMN contract_id bigint;
ALTER TABLE execution.production_order ADD COLUMN contract_clin text;
ALTER TABLE execution.production_order ADD CONSTRAINT order_contract_pair CHECK ((contract_id IS NULL) = (contract_clin IS NULL));

CREATE TABLE execution.order_contract_commitment (
    commitment_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_number bigint NOT NULL REFERENCES execution.production_order(order_number),
    contract_id bigint,
    clin text,
    recorded_at timestamptz NOT NULL,
    actor text NOT NULL,
    CHECK ((contract_id IS NULL) = (clin IS NULL))
);
CREATE INDEX order_contract_commitment_asof ON execution.order_contract_commitment(order_number,recorded_at DESC,commitment_id DESC);
