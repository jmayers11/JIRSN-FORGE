-- Sprint 10 §17–18: approved WIP storage capacity rules and approved partial builds, which reserve storage and material.
-- Occupancy is never stored here: it is the inventory ledger balance at the location. Reservations and partial builds only hold space
-- and material back from other plans until the work is placed, issued or cancelled.

-- What a site's safety reviewer approved for one part at one location: never derived from a room's volume.
CREATE TABLE inventory.storage_capacity_rule (
    rule_id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    location_code           text NOT NULL REFERENCES inventory.location(code),
    part_number             text NOT NULL,
    max_units               numeric(18, 6) NOT NULL CHECK (max_units > 0),
    max_utilization_percent numeric(5, 2) NULL CHECK (max_utilization_percent IS NULL OR max_utilization_percent > 0 AND max_utilization_percent <= 100),
    max_dwell_hours         integer NULL CHECK (max_dwell_hours IS NULL OR max_dwell_hours > 0),
    safety_reference        text NOT NULL CHECK (length(btrim(safety_reference)) BETWEEN 1 AND 200),
    state                   text NOT NULL CHECK (state IN ('Proposed', 'Active', 'Rejected', 'Retired')),
    proposed_by             text NOT NULL,
    proposed_at             timestamptz NOT NULL,
    decided_by              text NULL,
    decided_at              timestamptz NULL,
    CHECK (state = 'Proposed' OR (decided_by IS NOT NULL AND decided_at IS NOT NULL)),
    CHECK (decided_by IS NULL OR decided_by <> proposed_by)
);
CREATE UNIQUE INDEX storage_capacity_rule_live ON inventory.storage_capacity_rule (location_code, part_number) WHERE state IN ('Proposed', 'Active');

CREATE TABLE inventory.partial_build (
    build_id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_number     bigint NOT NULL,
    sequence         integer NOT NULL,
    quantity         numeric(18, 6) NOT NULL CHECK (quantity > 0),
    storage_location text NULL REFERENCES inventory.location(code),
    storage_quantity numeric(18, 6) NOT NULL DEFAULT 0 CHECK (storage_quantity >= 0),
    part_number      text NOT NULL,
    evidence         text NOT NULL,
    state            text NOT NULL CHECK (state IN ('Active', 'Completed', 'Cancelled')),
    approved_by      text NOT NULL,
    approved_at      timestamptz NOT NULL,
    closed_by        text NULL,
    closed_at        timestamptz NULL,
    CHECK ((storage_location IS NULL) = (storage_quantity = 0)),
    CHECK (state = 'Active' OR (closed_by IS NOT NULL AND closed_at IS NOT NULL))
);
-- One approved partial build per order at a time: a second one waits until the first is completed or cancelled.
CREATE UNIQUE INDEX partial_build_active ON inventory.partial_build (order_number) WHERE state = 'Active';
CREATE INDEX partial_build_storage ON inventory.partial_build (storage_location, part_number) WHERE state = 'Active' AND storage_location IS NOT NULL;

-- Material the approved partial build holds back for its order; what is later issued to the order counts against it.
CREATE TABLE inventory.partial_build_material (
    build_id    bigint NOT NULL REFERENCES inventory.partial_build(build_id),
    part_number text NOT NULL,
    quantity    numeric(18, 6) NOT NULL CHECK (quantity > 0),
    -- Net issues of the part to the order when the build was approved; later issues draw the reservation down.
    issued_at_approval numeric(18, 6) NOT NULL DEFAULT 0,
    PRIMARY KEY (build_id, part_number)
);

CREATE FUNCTION inventory.keep_partial_build() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.state <> 'Active' OR (NEW.order_number, NEW.sequence, NEW.quantity, NEW.storage_location, NEW.storage_quantity, NEW.part_number, NEW.evidence, NEW.approved_by, NEW.approved_at)
       IS DISTINCT FROM (OLD.order_number, OLD.sequence, OLD.quantity, OLD.storage_location, OLD.storage_quantity, OLD.part_number, OLD.evidence, OLD.approved_by, OLD.approved_at) THEN
        RAISE EXCEPTION 'partial build % keeps its approved facts; only an active build is closed', OLD.build_id;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER partial_build_kept BEFORE UPDATE ON inventory.partial_build
    FOR EACH ROW EXECUTE FUNCTION inventory.keep_partial_build();
