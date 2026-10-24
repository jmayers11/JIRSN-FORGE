-- Sprint 10 §17.2: the routing operation that consumes a BOM component. NULL keeps today's meaning: needed from the first operation.
-- Partial-build feasibility uses it so a part needed only at final assembly does not block upstream fabrication.
ALTER TABLE engineering.bom_line
    ADD COLUMN consumed_at_sequence integer NULL CHECK (consumed_at_sequence IS NULL OR consumed_at_sequence BETWEEN 1 AND 9999);
