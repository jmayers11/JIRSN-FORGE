-- Sprint 10 (WW-S10-14/29): what carrying out a rework record adds to its order. One person proposes the added operations, inspection
-- steps, material and trainees; a different person approves or rejects. Approval adds the lines to the order (execution) and asks for
-- training (workforce) in the same transaction. Plans are decided once and never edited; the NCR and the rework record stay as they are.
CREATE TABLE quality.rework_plan (
    plan_id                 uuid PRIMARY KEY,
    rework_id               bigint NOT NULL REFERENCES quality.rework(rework_id),
    order_number            bigint NOT NULL,
    status                  text NOT NULL CHECK (status IN ('Proposed', 'Approved', 'Rejected')),
    operations              jsonb NOT NULL,
    materials               jsonb NOT NULL,
    trainees                uuid[] NOT NULL,
    rationale               text NOT NULL CHECK (length(btrim(rationale)) BETWEEN 1 AND 800),
    proposed_by             text NOT NULL,
    proposed_by_person_id   uuid NULL,
    proposed_at             timestamptz NOT NULL,
    decided_by              text NULL,
    decided_by_person_id    uuid NULL,
    decided_at              timestamptz NULL,
    decision_note           text NULL,
    version                 integer NOT NULL,
    CHECK ((status = 'Proposed') = (decided_at IS NULL)),
    CHECK (status = 'Proposed' OR (decided_by IS NOT NULL AND decision_note IS NOT NULL AND decided_by <> proposed_by))
);

CREATE UNIQUE INDEX rework_plan_one_proposed ON quality.rework_plan(rework_id) WHERE status = 'Proposed';
CREATE INDEX rework_plan_order ON quality.rework_plan(order_number);
