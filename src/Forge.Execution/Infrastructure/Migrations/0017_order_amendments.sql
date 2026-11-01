-- Owner decisions 2026-10-31: released-order amendments. A Manufacturing Engineer proposes, a Planner who did not propose it approves,
-- and approval applies the change. Proposals are kept with their decision; the change itself is the order's audit history.
CREATE TABLE execution.order_amendment (
    amendment_id   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_number   bigint NOT NULL REFERENCES execution.production_order(order_number),
    status         text NOT NULL CHECK (status IN ('Proposed', 'Approved', 'Rejected', 'Withdrawn')),
    changes        jsonb NOT NULL,
    rationale      text NOT NULL CHECK (length(btrim(rationale)) BETWEEN 1 AND 2000),
    basis          text NOT NULL,
    proposed_at    timestamptz NOT NULL,
    proposed_by    text NOT NULL,
    decided_at     timestamptz NULL,
    decided_by     text NULL,
    decision_note  text NULL,
    CHECK ((status = 'Proposed') = (decided_by IS NULL)),
    CHECK (status = 'Withdrawn' OR decided_by IS NULL OR decided_by <> proposed_by)
);
CREATE INDEX order_amendment_order ON execution.order_amendment (order_number, amendment_id);
