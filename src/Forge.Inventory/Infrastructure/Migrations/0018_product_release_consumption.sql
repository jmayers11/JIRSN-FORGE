-- Decision consumption metadata references the existing authoritative ledger; never a second stock balance.
CREATE TABLE inventory.product_release_consumption(
 decision_id bigint NOT NULL REFERENCES quality.product_release_decision(decision_id),
 transaction_id bigint PRIMARY KEY REFERENCES inventory.transaction(transaction_id),
 part_number text NOT NULL,lot_number text NOT NULL,location_code text NOT NULL REFERENCES inventory.location(code),
 quantity numeric(18,6) NOT NULL CHECK(quantity>0),actor text NOT NULL,at timestamptz NOT NULL,policy_fingerprint text NOT NULL,evidence_fingerprint text NOT NULL
);
CREATE INDEX product_release_consumption_scope ON inventory.product_release_consumption(part_number,lot_number);
CREATE INDEX product_release_consumption_decision ON inventory.product_release_consumption(decision_id);
CREATE TRIGGER product_release_consumption_immutable BEFORE UPDATE OR DELETE ON inventory.product_release_consumption FOR EACH ROW EXECUTE FUNCTION quality.reject_record_change();
CREATE FUNCTION inventory.guard_product_release_consumption() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE original inventory.transaction%ROWTYPE; approved quality.product_release_decision%ROWTYPE; used numeric;
BEGIN
 SELECT * INTO original FROM inventory.transaction WHERE transaction_id=NEW.transaction_id;
 SELECT * INTO approved FROM quality.product_release_decision WHERE decision_id=NEW.decision_id FOR UPDATE;
 IF original.kind<>'QualityRelease' OR original.from_status<>'Inspection' OR original.to_status<>'Released' OR original.part_number<>NEW.part_number OR original.lot_number IS DISTINCT FROM NEW.lot_number OR original.to_location<>NEW.location_code OR original.quantity<>NEW.quantity OR approved.part_number<>NEW.part_number OR approved.lot_number<>NEW.lot_number OR approved.location_code<>NEW.location_code THEN RAISE EXCEPTION 'Exact approved decision and ledger scope required';END IF;
 IF EXISTS(SELECT 1 FROM quality.product_release_withdrawal WHERE decision_id=NEW.decision_id) THEN RAISE EXCEPTION 'Withdrawn decision cannot be consumed';END IF;
 SELECT COALESCE(sum(quantity),0) INTO used FROM inventory.product_release_consumption WHERE decision_id=NEW.decision_id;
 IF used+NEW.quantity>approved.quantity THEN RAISE EXCEPTION 'Approved quantity already consumed';END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER product_release_consumption_guard BEFORE INSERT ON inventory.product_release_consumption FOR EACH ROW EXECUTE FUNCTION inventory.guard_product_release_consumption();
