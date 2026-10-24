-- Sprint 10 §17.2: released orders keep the BOM line's consumption point in their material snapshot.
ALTER TABLE execution.order_material
    ADD COLUMN consumed_at_sequence integer NULL CHECK (consumed_at_sequence IS NULL OR consumed_at_sequence BETWEEN 1 AND 9999);
