-- ADR 0057 §3.2: changeover set-up. An operation may name a set-up family (a fixture, a material gauge). When the
-- machine's previous job was the same family, the scheduler plans the reduced same-family set-up instead of the full one.
ALTER TABLE engineering.routing_operation ADD COLUMN setup_family text NULL;
ALTER TABLE engineering.routing_operation ADD COLUMN family_setup_minutes numeric(10, 2) NULL;
ALTER TABLE engineering.routing_operation ADD CONSTRAINT routing_operation_setup_family
    CHECK ((setup_family IS NULL) = (family_setup_minutes IS NULL) AND (family_setup_minutes IS NULL OR family_setup_minutes BETWEEN 0 AND setup_minutes));
