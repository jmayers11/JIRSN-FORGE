-- ADR 0057 §3.2: the released copy of an operation keeps its set-up minutes and set-up family, so the scheduler can
-- plan the reduced same-family set-up. Orders released before this have neither; their set-up is never reduced.
ALTER TABLE execution.order_operation ADD COLUMN setup_minutes numeric(14, 4) NULL CHECK (setup_minutes >= 0);
ALTER TABLE execution.order_operation ADD COLUMN setup_family text NULL;
ALTER TABLE execution.order_operation ADD COLUMN family_setup_minutes numeric(14, 4) NULL CHECK (family_setup_minutes >= 0);
