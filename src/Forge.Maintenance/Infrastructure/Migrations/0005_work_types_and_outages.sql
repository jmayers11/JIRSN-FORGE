-- V2.9: maintenance work types, what the work needs (asset, parts, procedure, qualifications) and planned
-- outages that are proposed by one person and approved by another before they take capacity.

-- Work types. Preventive work still comes from a PM plan; the other types are raised by people.
ALTER TABLE maintenance.work_order DROP CONSTRAINT work_order_kind_check;
ALTER TABLE maintenance.work_order ADD CONSTRAINT work_order_kind_check
    CHECK (kind IN ('Preventive', 'Corrective', 'Emergency', 'Predictive', 'Calibration', 'Inspection', 'Facility'));
DO $$
DECLARE c text;
BEGIN
    SELECT conname INTO c FROM pg_constraint
    WHERE conrelid = 'maintenance.work_order'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%Corrective%plan_id%';
    IF c IS NOT NULL THEN
        EXECUTE format('ALTER TABLE maintenance.work_order DROP CONSTRAINT %I', c);
    END IF;
END $$;
ALTER TABLE maintenance.work_order ADD CONSTRAINT work_order_preventive_has_plan CHECK (kind <> 'Preventive' OR plan_id IS NOT NULL);

-- Work on an asset that is not a machine (a building, a utility) has no machine.
ALTER TABLE maintenance.work_order ALTER COLUMN machine_code DROP NOT NULL;
ALTER TABLE maintenance.work_order ADD COLUMN asset_code text NULL REFERENCES maintenance.asset(asset_code);
ALTER TABLE maintenance.work_order ADD CONSTRAINT work_order_has_target CHECK (machine_code IS NOT NULL OR asset_code IS NOT NULL);
ALTER TABLE maintenance.work_order ADD COLUMN procedure_document text NULL;
ALTER TABLE maintenance.work_order ADD COLUMN procedure_revision text NULL;
ALTER TABLE maintenance.work_order ADD CONSTRAINT work_order_procedure_revision CHECK ((procedure_document IS NULL) = (procedure_revision IS NULL));
ALTER TABLE maintenance.work_order ADD COLUMN required_qualifications text[] NOT NULL DEFAULT '{}';

-- The parts a work order needs; a position says where on the asset the part is installed.
CREATE TABLE maintenance.work_order_part (
    work_order_id  bigint NOT NULL REFERENCES maintenance.work_order(work_order_id),
    line_number    integer NOT NULL CHECK (line_number BETWEEN 1 AND 999),
    part_number    text NOT NULL REFERENCES engineering.item(part_number),
    quantity       numeric(18, 6) NOT NULL CHECK (quantity > 0),
    position       text NULL CHECK (position IS NULL OR length(trim(position)) > 0),
    PRIMARY KEY (work_order_id, line_number),
    UNIQUE (work_order_id, part_number)
);

-- What a work order needs is changed only before the work starts.
CREATE FUNCTION maintenance.guard_work_order_part() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE s text;
BEGIN
    SELECT status INTO s FROM maintenance.work_order WHERE work_order_id = COALESCE(NEW.work_order_id, OLD.work_order_id);
    IF s NOT IN ('Open', 'Scheduled') THEN
        RAISE EXCEPTION 'work order % is %; its required parts are kept as they were', COALESCE(NEW.work_order_id, OLD.work_order_id), s USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN COALESCE(NEW, OLD);
END;
$$;

CREATE TRIGGER work_order_part_guarded BEFORE INSERT OR UPDATE OR DELETE ON maintenance.work_order_part FOR EACH ROW EXECUTE FUNCTION maintenance.guard_work_order_part();

-- Planned outages: proposed, then approved by a different person after the schedule preview. Only an
-- approved outage has a downtime window (capacity loss).
CREATE TABLE maintenance.outage (
    outage_id        bigint GENERATED ALWAYS AS IDENTITY (START WITH 501) PRIMARY KEY,
    machine_code     text NOT NULL REFERENCES resources.machine(code),
    starts_at        timestamptz NOT NULL,
    ends_at          timestamptz NOT NULL CHECK (ends_at > starts_at),
    reason           text NOT NULL CHECK (length(trim(reason)) > 0),
    work_order_id    bigint NULL REFERENCES maintenance.work_order(work_order_id),
    status           text NOT NULL CHECK (status IN ('Proposed', 'Approved', 'Rejected', 'Withdrawn')),
    proposed_by      text NOT NULL,
    proposed_at      timestamptz NOT NULL,
    decided_by       text NULL,
    decided_at       timestamptz NULL,
    decision_reason  text NULL,
    impact           text NULL,
    downtime_id      bigint NULL REFERENCES maintenance.downtime(downtime_id),
    CHECK ((status = 'Proposed') = (decided_at IS NULL)),
    CHECK ((decided_by IS NULL) = (decided_at IS NULL)),
    -- Whoever proposed an outage never approves or rejects it; they may withdraw it.
    CHECK (status IN ('Proposed', 'Withdrawn') OR decided_by <> proposed_by),
    CHECK (status <> 'Approved' OR (downtime_id IS NOT NULL AND impact IS NOT NULL))
);
CREATE INDEX outage_open ON maintenance.outage (machine_code, starts_at) WHERE status = 'Proposed';
-- One live proposal per work order.
CREATE UNIQUE INDEX outage_one_per_work_order ON maintenance.outage (work_order_id) WHERE work_order_id IS NOT NULL AND status = 'Proposed';

CREATE FUNCTION maintenance.guard_outage() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'outages are never deleted; withdraw or reject them' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF OLD.status <> 'Proposed' THEN
        RAISE EXCEPTION 'outage % is %; a decision is kept as it was made', OLD.outage_id, OLD.status USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF (NEW.machine_code, NEW.starts_at, NEW.ends_at, NEW.reason, NEW.work_order_id, NEW.proposed_by, NEW.proposed_at)
       IS DISTINCT FROM (OLD.machine_code, OLD.starts_at, OLD.ends_at, OLD.reason, OLD.work_order_id, OLD.proposed_by, OLD.proposed_at) THEN
        RAISE EXCEPTION 'outage % keeps what was proposed; propose a different one instead', OLD.outage_id USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER outage_guarded BEFORE UPDATE OR DELETE ON maintenance.outage FOR EACH ROW EXECUTE FUNCTION maintenance.guard_outage();
