-- A returned physical identity retains its original ledger and release history. This is new, exact evidence only.
CREATE TABLE quality.return_inspection_cohort(
 cohort_id bigint GENERATED ALWAYS AS IDENTITY(START WITH 90001) PRIMARY KEY,
 rma_id bigint NOT NULL,return_transaction_id bigint NOT NULL,
 preparation_transaction_id bigint NOT NULL UNIQUE,quantity numeric(18,6) NOT NULL CHECK(quantity>0),payload jsonb NOT NULL
);
CREATE INDEX return_inspection_cohort_rma ON quality.return_inspection_cohort(rma_id,cohort_id);
CREATE TRIGGER return_inspection_cohort_immutable BEFORE UPDATE OR DELETE ON quality.return_inspection_cohort FOR EACH ROW EXECUTE FUNCTION quality.reject_record_change();
ALTER TABLE quality.acceptance_inspection ADD COLUMN return_cohort_id bigint NULL REFERENCES quality.return_inspection_cohort(cohort_id);
ALTER TABLE quality.product_release_decision ADD COLUMN return_cohort_id bigint NULL REFERENCES quality.return_inspection_cohort(cohort_id);
CREATE INDEX acceptance_inspection_return_cohort ON quality.acceptance_inspection(return_cohort_id) WHERE return_cohort_id IS NOT NULL;
CREATE INDEX product_release_return_cohort ON quality.product_release_decision(return_cohort_id) WHERE return_cohort_id IS NOT NULL;
-- No old JSON, inspection, release decision, policy fingerprint or validity is rewritten.
