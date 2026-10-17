-- V5-S1: retain explicit material parents for tracked byproduct instances.
-- Existing byproducts remain linked to their production order only: their physical source lots cannot be inferred.

CREATE TABLE inventory.transformation_line_parent (
    transformation_id bigint NOT NULL,
    line_number       int NOT NULL,
    part_number       text NOT NULL,
    lot_number        text NOT NULL,
    quantity          numeric(18, 6) NOT NULL CHECK (quantity > 0),
    PRIMARY KEY (transformation_id, line_number, part_number, lot_number),
    FOREIGN KEY (transformation_id, line_number)
        REFERENCES inventory.transformation_line(transformation_id, line_number),
    FOREIGN KEY (part_number, lot_number)
        REFERENCES inventory.lot(part_number, lot_number)
);

CREATE INDEX transformation_line_parent_instance
    ON inventory.transformation_line_parent (part_number, lot_number);

CREATE TRIGGER transformation_line_parent_append_only BEFORE UPDATE OR DELETE ON inventory.transformation_line_parent
    FOR EACH ROW EXECUTE FUNCTION inventory.reject_ledger_change();
