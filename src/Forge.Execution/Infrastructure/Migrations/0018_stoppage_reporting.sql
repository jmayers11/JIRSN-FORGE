-- Owner decisions 2026-11-01: downtime is reported at the kiosk, told to maintenance or the supervisor who enters it on the operator's
-- behalf, or detected from the machine's connection. Each stoppage keeps who reported it and how it reached Forge; a detected one is
-- unexplained until a person gives it a reason.
ALTER TABLE execution.stoppage
    ADD COLUMN reported_by text NULL,
    ADD COLUMN source text NOT NULL DEFAULT 'Recorded' CHECK (source IN ('Recorded', 'Kiosk', 'Connector')),
    ADD COLUMN reclassified_by text NULL,
    ADD COLUMN reclassified_at timestamptz NULL,
    ADD CONSTRAINT stoppage_reclassified CHECK ((reclassified_by IS NULL) = (reclassified_at IS NULL));

INSERT INTO execution.reason_code (code, kind, category, description, active, changed_at, changed_by)
VALUES ('UNEXPLAINED', 'Downtime', 'Unplanned', 'Not yet explained: detected from the machine connection', true, now(), 'system:migration')
ON CONFLICT (code) DO NOTHING;

-- A stoppage is ended once, and may be given a (new) reason with who reported it; nothing else about it ever changes.
CREATE OR REPLACE FUNCTION execution.stoppage_end_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE'
       OR (NEW.stoppage_id, NEW.machine_code, NEW.work_center_code, NEW.order_number, NEW.sequence, NEW.expected_minutes, NEW.started_at, NEW.started_by, NEW.source)
          IS DISTINCT FROM (OLD.stoppage_id, OLD.machine_code, OLD.work_center_code, OLD.order_number, OLD.sequence, OLD.expected_minutes, OLD.started_at, OLD.started_by, OLD.source) THEN
        RAISE EXCEPTION 'stoppage % is only ever ended, once, or given a reason', OLD.stoppage_id;
    END IF;
    IF NEW.reclassified_at IS DISTINCT FROM OLD.reclassified_at THEN
        IF NEW.reclassified_at IS NULL OR (NEW.ended_at, NEW.ended_by) IS DISTINCT FROM (OLD.ended_at, OLD.ended_by) THEN
            RAISE EXCEPTION 'stoppage % is given a reason without ending it in the same change', OLD.stoppage_id;
        END IF;
        RETURN NEW;
    END IF;
    IF OLD.ended_at IS NOT NULL
       OR (NEW.category, NEW.reason_code, NEW.note, NEW.reported_by, NEW.reclassified_by)
          IS DISTINCT FROM (OLD.category, OLD.reason_code, OLD.note, OLD.reported_by, OLD.reclassified_by) THEN
        RAISE EXCEPTION 'stoppage % is only ever ended, once', OLD.stoppage_id;
    END IF;
    RETURN NEW;
END $$;
