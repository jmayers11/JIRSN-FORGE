-- V2.6: an approved alternate issued in place of the part an order needs. The ledger moves the alternate;
-- it also records the part it stands in for, the relationship that allowed it and the primary quantity it
-- covers, so the order's material is counted in the primary's terms and "what was used" is kept.
ALTER TABLE inventory.transaction ADD COLUMN substitutes_for text NULL;
ALTER TABLE inventory.transaction ADD COLUMN alternate_id bigint NULL;
ALTER TABLE inventory.transaction ADD COLUMN primary_quantity numeric(18, 6) NULL;
ALTER TABLE inventory.transaction ADD CONSTRAINT transaction_substitution_complete
    CHECK ((substitutes_for IS NULL) = (alternate_id IS NULL) AND (substitutes_for IS NULL) = (primary_quantity IS NULL));
ALTER TABLE inventory.transaction ADD CONSTRAINT transaction_substitution_on_order
    CHECK (substitutes_for IS NULL OR (kind IN ('Issue', 'Return') AND order_number IS NOT NULL AND primary_quantity > 0));
