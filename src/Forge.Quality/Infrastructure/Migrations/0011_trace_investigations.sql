CREATE TABLE quality.measurement_finding(
 finding_id bigint GENERATED ALWAYS AS IDENTITY(START WITH 70001) PRIMARY KEY,
 version integer NOT NULL CHECK(version>0),state text NOT NULL CHECK(state IN('Draft','Submitted','Approved','Withdrawn')),
 tool_code text NOT NULL,payload jsonb NOT NULL
);
CREATE INDEX measurement_finding_tool ON quality.measurement_finding(tool_code,finding_id);
CREATE TABLE quality.trace_investigation(
 investigation_id bigint GENERATED ALWAYS AS IDENTITY(START WITH 80001) PRIMARY KEY,
 version integer NOT NULL CHECK(version>0),state text NOT NULL CHECK(state IN('Open','Closed')),payload jsonb NOT NULL
);
CREATE TABLE quality.trace_snapshot(
 investigation_id bigint NOT NULL REFERENCES quality.trace_investigation(investigation_id),number integer NOT NULL CHECK(number>0),
 payload jsonb NOT NULL,PRIMARY KEY(investigation_id,number)
);
CREATE TABLE quality.trace_assessment(
 assessment_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 investigation_id bigint NOT NULL,snapshot_number integer NOT NULL,candidate_key text NOT NULL,
 version integer NOT NULL CHECK(version>0),state text NOT NULL CHECK(state IN('Proposed','Approved','Withdrawn')),payload jsonb NOT NULL,
 FOREIGN KEY(investigation_id,snapshot_number) REFERENCES quality.trace_snapshot(investigation_id,number)
);
CREATE UNIQUE INDEX trace_assessment_current ON quality.trace_assessment(investigation_id,snapshot_number,candidate_key) WHERE state<>'Withdrawn';
CREATE TABLE quality.trace_hold_link(
 investigation_id bigint NOT NULL,snapshot_number integer NOT NULL,candidate_key text NOT NULL,
 assessment_id bigint NOT NULL REFERENCES quality.trace_assessment(assessment_id),hold_id bigint NOT NULL REFERENCES kernel.hold(hold_id),
 payload jsonb NOT NULL,PRIMARY KEY(investigation_id,snapshot_number,candidate_key,hold_id),
 FOREIGN KEY(investigation_id,snapshot_number) REFERENCES quality.trace_snapshot(investigation_id,number)
);
CREATE TRIGGER trace_snapshot_immutable BEFORE UPDATE OR DELETE ON quality.trace_snapshot FOR EACH ROW EXECUTE FUNCTION quality.reject_record_change();
CREATE TRIGGER trace_hold_link_immutable BEFORE UPDATE OR DELETE ON quality.trace_hold_link FOR EACH ROW EXECUTE FUNCTION quality.reject_record_change();
CREATE FUNCTION quality.guard_investigation_version() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Investigation history is retained';END IF;
 IF NEW.version<>OLD.version+1 OR NEW.payload->>'createdBy' IS DISTINCT FROM OLD.payload->>'createdBy' OR NEW.payload->>'organizationCode' IS DISTINCT FROM OLD.payload->>'organizationCode' THEN RAISE EXCEPTION 'Exact sequential version and immutable creator/organization';END IF;
 IF jsonb_array_length(NEW.payload->'history')<>jsonb_array_length(OLD.payload->'history')+1 OR (NEW.payload->'history')-(jsonb_array_length(NEW.payload->'history')-1)<>OLD.payload->'history' THEN RAISE EXCEPTION 'Append-only investigation history';END IF;
 IF TG_TABLE_NAME='measurement_finding' THEN
  IF NEW.tool_code<>OLD.tool_code OR OLD.state<>'Draft' AND (NEW.payload->'content'<>OLD.payload->'content' OR NEW.payload->'sources'<>OLD.payload->'sources') THEN RAISE EXCEPTION 'Submitted measurement source/observation is immutable';END IF;
  IF NOT ((OLD.state='Draft' AND NEW.state IN('Draft','Submitted','Withdrawn')) OR (OLD.state='Submitted' AND NEW.state IN('Approved','Withdrawn')) OR (OLD.state='Approved' AND NEW.state='Withdrawn')) THEN RAISE EXCEPTION 'Measurement review lifecycle';END IF;
  IF NEW.state='Approved' AND EXISTS(SELECT 1 FROM jsonb_array_elements(OLD.payload->'history') h WHERE h->>'action' IN('Create','Edit') AND h->>'actor'=NEW.payload->>'approvedBy') THEN RAISE EXCEPTION 'Independent measurement assessment';END IF;
 ELSIF TG_TABLE_NAME='trace_investigation' THEN
  IF OLD.state<>'Open' OR NEW.state NOT IN('Open','Closed') OR NEW.payload->'root'<>OLD.payload->'root' OR NEW.payload->'rootSources'<>OLD.payload->'rootSources' THEN RAISE EXCEPTION 'Immutable investigation trigger and closed history';END IF;
 ELSIF TG_TABLE_NAME='trace_assessment' THEN
  IF NEW.investigation_id<>OLD.investigation_id OR NEW.snapshot_number<>OLD.snapshot_number OR NEW.candidate_key<>OLD.candidate_key OR OLD.state<>'Proposed' OR NEW.state NOT IN('Approved','Withdrawn') OR NEW.payload->>'proposedBy'<>OLD.payload->>'proposedBy' OR NEW.payload->>'basis'<>OLD.payload->>'basis' OR NEW.payload->>'evidence'<>OLD.payload->>'evidence' OR NEW.payload->>'plannedDisposition'<>OLD.payload->>'plannedDisposition' OR NEW.payload->>'decision'<>OLD.payload->>'decision' THEN RAISE EXCEPTION 'Immutable proposed impact assessment';END IF;
  IF NEW.state='Approved' AND NEW.payload->>'approvedBy'=OLD.payload->>'proposedBy' THEN RAISE EXCEPTION 'Independent impact approval';END IF;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER measurement_finding_version BEFORE UPDATE OR DELETE ON quality.measurement_finding FOR EACH ROW EXECUTE FUNCTION quality.guard_investigation_version();
CREATE TRIGGER trace_investigation_version BEFORE UPDATE OR DELETE ON quality.trace_investigation FOR EACH ROW EXECUTE FUNCTION quality.guard_investigation_version();
CREATE TRIGGER trace_assessment_version BEFORE UPDATE OR DELETE ON quality.trace_assessment FOR EACH ROW EXECUTE FUNCTION quality.guard_investigation_version();
