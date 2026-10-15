-- Both context owners now exist; add actual owner FKs here rather than ordering a Quality migration after Inventory.
ALTER TABLE quality.return_inspection_cohort ADD CONSTRAINT return_cohort_rma_fk FOREIGN KEY(rma_id) REFERENCES inventory.customer_return(rma_id);
ALTER TABLE quality.return_inspection_cohort ADD CONSTRAINT return_cohort_receipt_fk FOREIGN KEY(return_transaction_id) REFERENCES inventory.transaction(transaction_id);
ALTER TABLE quality.return_inspection_cohort ADD CONSTRAINT return_cohort_preparation_fk FOREIGN KEY(preparation_transaction_id) REFERENCES inventory.transaction(transaction_id);
CREATE TABLE inventory.return_release_consumption(
 transaction_id bigint PRIMARY KEY REFERENCES inventory.product_release_consumption(transaction_id),
 cohort_id bigint NOT NULL REFERENCES quality.return_inspection_cohort(cohort_id),
 rma_id bigint NOT NULL REFERENCES inventory.customer_return(rma_id),
 decision_id bigint NOT NULL REFERENCES quality.product_release_decision(decision_id),quantity numeric(18,6) NOT NULL CHECK(quantity>0)
);
CREATE INDEX return_release_consumption_rma ON inventory.return_release_consumption(rma_id,transaction_id);
CREATE INDEX return_release_consumption_cohort ON inventory.return_release_consumption(cohort_id,transaction_id);
CREATE TRIGGER return_release_consumption_immutable BEFORE UPDATE OR DELETE ON inventory.return_release_consumption FOR EACH ROW EXECUTE FUNCTION inventory.reject_ledger_change();
CREATE FUNCTION inventory.guard_return_release_consumption() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE cohort quality.return_inspection_cohort%ROWTYPE;decision quality.product_release_decision%ROWTYPE;posted inventory.product_release_consumption%ROWTYPE;already numeric;
BEGIN
 SELECT * INTO cohort FROM quality.return_inspection_cohort WHERE cohort_id=NEW.cohort_id;
 SELECT * INTO decision FROM quality.product_release_decision WHERE decision_id=NEW.decision_id FOR UPDATE;
 SELECT * INTO posted FROM inventory.product_release_consumption WHERE transaction_id=NEW.transaction_id;
 IF cohort.rma_id IS DISTINCT FROM NEW.rma_id OR decision.return_cohort_id IS DISTINCT FROM NEW.cohort_id OR posted.decision_id IS DISTINCT FROM NEW.decision_id OR posted.quantity IS DISTINCT FROM NEW.quantity THEN RAISE EXCEPTION 'Exact return cohort/release/ledger binding required'; END IF;
 PERFORM rma_id FROM inventory.customer_return WHERE rma_id=NEW.rma_id FOR UPDATE;
 SELECT COALESCE(sum(quantity),0) INTO already FROM inventory.return_release_consumption WHERE cohort_id=NEW.cohort_id;
 IF already+NEW.quantity>cohort.quantity THEN RAISE EXCEPTION 'Returned inspection cohort cannot be consumed twice';END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER return_release_consumption_guard BEFORE INSERT ON inventory.return_release_consumption FOR EACH ROW EXECUTE FUNCTION inventory.guard_return_release_consumption();
