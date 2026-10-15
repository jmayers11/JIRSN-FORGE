-- New exact evidence begins here. Legacy inspection rows retain unknown physical/sample coverage.
CREATE TABLE quality.acceptance_criteria (
 code text NOT NULL, revision integer NOT NULL CHECK(revision>0), version integer NOT NULL CHECK(version>0),
 state text NOT NULL CHECK(state IN('Draft','Submitted','Approved','Effective','Superseded','Withdrawn')),
 facility_code text NOT NULL REFERENCES resources.site(code), program_id uuid NULL REFERENCES programs.program(program_id),
 markings integer NOT NULL CHECK(markings BETWEEN 0 AND 7), content jsonb NOT NULL, created_by text NOT NULL,last_edited_by text NOT NULL,approved_by text NULL,
 effective_from timestamptz NULL,effective_to timestamptz NULL,sources jsonb NOT NULL,history jsonb NOT NULL,
 PRIMARY KEY(code,revision), CHECK(state NOT IN('Approved','Effective','Superseded') OR approved_by IS NOT NULL),
 CHECK(approved_by IS NULL OR approved_by NOT IN(created_by,last_edited_by)),
 CHECK(state NOT IN('Effective','Superseded') OR effective_from IS NOT NULL),CHECK(effective_to IS NULL OR effective_to>=effective_from)
);
CREATE UNIQUE INDEX acceptance_criteria_effective ON quality.acceptance_criteria(code) WHERE state='Effective';
CREATE UNIQUE INDEX acceptance_criteria_open ON quality.acceptance_criteria(code) WHERE state IN('Draft','Submitted','Approved');
CREATE INDEX acceptance_criteria_scope ON quality.acceptance_criteria(facility_code,(content->>'partNumber'),(content->>'revision'));
CREATE TABLE quality.acceptance_inspection (
 inspection_id bigint GENERATED ALWAYS AS IDENTITY(START WITH 20001) PRIMARY KEY,
 part_number text NOT NULL,lot_number text NOT NULL,location_code text NOT NULL,criteria_code text NOT NULL,criteria_revision integer NOT NULL,
 quantity numeric(18,6) NOT NULL CHECK(quantity>0), passed boolean NOT NULL,ncr_id bigint NULL REFERENCES quality.ncr(ncr_id),payload jsonb NOT NULL,
 FOREIGN KEY(criteria_code,criteria_revision) REFERENCES quality.acceptance_criteria(code,revision)
);
CREATE INDEX acceptance_inspection_subject ON quality.acceptance_inspection(part_number,lot_number,location_code);
CREATE UNIQUE INDEX acceptance_inspection_ncr ON quality.acceptance_inspection(ncr_id) WHERE ncr_id IS NOT NULL;
CREATE TABLE quality.product_release_decision (
 decision_id bigint GENERATED ALWAYS AS IDENTITY(START WITH 30001) PRIMARY KEY,
 part_number text NOT NULL,lot_number text NOT NULL,location_code text NOT NULL,quantity numeric(18,6) NOT NULL CHECK(quantity>0),payload jsonb NOT NULL
);
CREATE INDEX product_release_subject ON quality.product_release_decision(part_number,lot_number,location_code);
CREATE TABLE quality.product_release_evidence (
 decision_id bigint NOT NULL REFERENCES quality.product_release_decision(decision_id),inspection_id bigint NOT NULL REFERENCES quality.acceptance_inspection(inspection_id),
 PRIMARY KEY(decision_id,inspection_id)
);
CREATE TABLE quality.product_release_withdrawal(decision_id bigint PRIMARY KEY REFERENCES quality.product_release_decision(decision_id),payload jsonb NOT NULL);
CREATE TRIGGER product_release_withdrawal_immutable BEFORE UPDATE OR DELETE ON quality.product_release_withdrawal FOR EACH ROW EXECUTE FUNCTION quality.reject_record_change();
CREATE TRIGGER acceptance_inspection_immutable BEFORE UPDATE OR DELETE ON quality.acceptance_inspection FOR EACH ROW EXECUTE FUNCTION quality.reject_record_change();
CREATE TRIGGER product_release_immutable BEFORE UPDATE OR DELETE ON quality.product_release_decision FOR EACH ROW EXECUTE FUNCTION quality.reject_record_change();
CREATE TRIGGER product_release_evidence_immutable BEFORE UPDATE OR DELETE ON quality.product_release_evidence FOR EACH ROW EXECUTE FUNCTION quality.reject_record_change();
CREATE FUNCTION quality.guard_acceptance_criteria() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Acceptance history is never deleted'; END IF;
 IF TG_OP='INSERT' THEN
  IF NEW.state<>'Draft' OR NEW.version<>1 OR NEW.approved_by IS NOT NULL OR NEW.effective_from IS NOT NULL OR NEW.effective_to IS NOT NULL THEN RAISE EXCEPTION 'Begin as an unapproved draft';END IF;
  RETURN NEW;
 END IF;
 IF NEW.code<>OLD.code OR NEW.revision<>OLD.revision OR NEW.facility_code<>OLD.facility_code OR NEW.program_id IS DISTINCT FROM OLD.program_id OR NEW.markings<>OLD.markings OR NEW.created_by<>OLD.created_by OR NEW.version<>OLD.version+1 THEN RAISE EXCEPTION 'Immutable criteria identity/scope and sequential versions';END IF;
 IF OLD.state<>'Draft' AND (NEW.content<>OLD.content OR NEW.sources<>OLD.sources OR NEW.last_edited_by<>OLD.last_edited_by) THEN RAISE EXCEPTION 'Submitted criteria are immutable';END IF;
 IF jsonb_array_length(NEW.history)<>jsonb_array_length(OLD.history)+1 OR NEW.history-(jsonb_array_length(NEW.history)-1)<>OLD.history THEN RAISE EXCEPTION 'Criterion transition history is append-only';END IF;
 IF NEW.approved_by IS NOT NULL AND EXISTS(SELECT 1 FROM jsonb_array_elements(OLD.history) h WHERE h->>'action' IN('Create','Edit') AND h->>'actor'=NEW.approved_by) THEN RAISE EXCEPTION 'An earlier criterion author cannot approve their own arrangement';END IF;
 IF NOT ((OLD.state='Draft' AND NEW.state IN('Draft','Submitted','Withdrawn')) OR (OLD.state='Submitted' AND NEW.state IN('Approved','Withdrawn')) OR (OLD.state='Approved' AND NEW.state IN('Effective','Withdrawn')) OR (OLD.state='Effective' AND NEW.state IN('Superseded','Withdrawn'))) THEN RAISE EXCEPTION 'Invalid criteria lifecycle';END IF;
 IF OLD.approved_by IS NOT NULL AND NEW.approved_by IS DISTINCT FROM OLD.approved_by OR OLD.effective_from IS NOT NULL AND NEW.effective_from IS DISTINCT FROM OLD.effective_from THEN RAISE EXCEPTION 'Approval/effectivity provenance immutable';END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER acceptance_criteria_guard BEFORE INSERT OR UPDATE OR DELETE ON quality.acceptance_criteria FOR EACH ROW EXECUTE FUNCTION quality.guard_acceptance_criteria();
