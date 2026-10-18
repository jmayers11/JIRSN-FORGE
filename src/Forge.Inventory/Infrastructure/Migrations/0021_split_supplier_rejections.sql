-- V5 owner rulings (ADR 0055): a supplier rejection is decided per quantity.
--
-- Part of a rejection can be dispositioned. The decided part keeps the rejection and its quantity is reduced to what
-- was decided; the rest becomes a new pending rejection that names the one it was split from. An open return to
-- vendor can be reduced the same way. The rejected total of an inspection is therefore always the sum of its
-- rejections, quantities only ever go down, and nothing leaves quality hold without its own disposition.

ALTER TABLE inventory.supplier_rejection ADD COLUMN split_from bigint NULL REFERENCES inventory.supplier_rejection(rejection_id);
CREATE INDEX supplier_rejection_split_from ON inventory.supplier_rejection (split_from) WHERE split_from IS NOT NULL;

CREATE FUNCTION inventory.supplier_rejection_forward_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.quantity > OLD.quantity THEN
        RAISE EXCEPTION 'Supplier rejection % can only be reduced, never increased.', OLD.rejection_id;
    END IF;
    IF NEW.inspection_id <> OLD.inspection_id OR NEW.part_number <> OLD.part_number OR NEW.lot_number IS DISTINCT FROM OLD.lot_number
       OR NEW.location_code <> OLD.location_code OR NEW.split_from IS DISTINCT FROM OLD.split_from THEN
        RAISE EXCEPTION 'Supplier rejection % keeps what it rejected and where it came from.', OLD.rejection_id;
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER supplier_rejection_forward_only BEFORE UPDATE ON inventory.supplier_rejection
    FOR EACH ROW EXECUTE FUNCTION inventory.supplier_rejection_forward_only();

CREATE FUNCTION inventory.rtv_quantity_forward_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.quantity > OLD.quantity THEN
        RAISE EXCEPTION 'Return to vendor % can only be reduced, never increased.', OLD.rtv_id;
    END IF;
    IF NEW.quantity <> OLD.quantity AND OLD.status <> 'Open' THEN
        RAISE EXCEPTION 'Return to vendor % is %; only an open return can be reduced.', OLD.rtv_id, OLD.status;
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER rtv_quantity_forward_only BEFORE UPDATE ON inventory.rtv
    FOR EACH ROW EXECUTE FUNCTION inventory.rtv_quantity_forward_only();
