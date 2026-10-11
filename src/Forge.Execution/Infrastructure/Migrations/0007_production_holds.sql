-- V3.9: holds as a readiness dimension (V3 brief B).
--
-- A production hold stops work on an order, one of its operations, a part, a work center or a machine, with a
-- reason. Starting work under a hold is refused, and readiness names the hold. A hold is released once, with a
-- reason, by a different person from the one who placed it. Nothing else about a hold ever changes.

CREATE TABLE execution.production_hold (
    hold_id           bigint GENERATED ALWAYS AS IDENTITY (START WITH 501) PRIMARY KEY,
    scope             text NOT NULL CHECK (scope IN ('Order', 'Operation', 'Part', 'WorkCenter', 'Machine')),
    order_number      bigint NULL REFERENCES execution.production_order(order_number),
    sequence          integer NULL,
    part_number       text NULL,
    work_center_code  text NULL,
    machine_code      text NULL,
    reason            text NOT NULL,
    placed_at         timestamptz NOT NULL,
    placed_by         text NOT NULL,
    released_at       timestamptz NULL,
    released_by       text NULL,
    release_reason    text NULL,
    CHECK ((scope IN ('Order', 'Operation')) = (order_number IS NOT NULL)),
    CHECK ((scope = 'Operation') = (sequence IS NOT NULL)),
    CHECK ((scope = 'Part') = (part_number IS NOT NULL)),
    CHECK ((scope = 'WorkCenter') = (work_center_code IS NOT NULL)),
    CHECK ((scope = 'Machine') = (machine_code IS NOT NULL)),
    CHECK ((released_at IS NULL) = (released_by IS NULL) AND (released_at IS NULL) = (release_reason IS NULL)),
    CHECK (released_by IS NULL OR released_by <> placed_by)
);
CREATE INDEX production_hold_active ON execution.production_hold (scope) WHERE released_at IS NULL;

-- A hold is released once; nothing else about it ever changes.
CREATE FUNCTION execution.production_hold_release_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' OR OLD.released_at IS NOT NULL
       OR (NEW.hold_id, NEW.scope, NEW.order_number, NEW.sequence, NEW.part_number, NEW.work_center_code, NEW.machine_code, NEW.reason, NEW.placed_at, NEW.placed_by)
          IS DISTINCT FROM (OLD.hold_id, OLD.scope, OLD.order_number, OLD.sequence, OLD.part_number, OLD.work_center_code, OLD.machine_code, OLD.reason, OLD.placed_at, OLD.placed_by) THEN
        RAISE EXCEPTION 'hold % is only ever released, once', OLD.hold_id;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER production_hold_release_only BEFORE UPDATE OR DELETE ON execution.production_hold
    FOR EACH ROW EXECUTE FUNCTION execution.production_hold_release_only();
