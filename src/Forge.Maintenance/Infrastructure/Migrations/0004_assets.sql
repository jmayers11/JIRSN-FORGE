-- V2.8: assets with a lifecycle history and installed components. A production asset points at its
-- machine; the machine stays the capacity unit in Resources and nothing about capacity is copied here.
CREATE TABLE maintenance.asset (
    asset_code                 text PRIMARY KEY,
    name                       text NOT NULL,
    asset_type                 text NOT NULL CHECK (asset_type IN ('Production', 'Building', 'Utility', 'TestEquipment', 'Tooling', 'Vehicle', 'Other')),
    manufacturer               text NULL,
    model                      text NULL,
    serial_number              text NULL,
    location                   text NULL,
    work_center_code           text NULL REFERENCES resources.work_center(code),
    machine_code               text NULL UNIQUE REFERENCES resources.machine(code),
    criticality                text NOT NULL CHECK (criticality IN ('Low', 'Medium', 'High', 'Critical')),
    commissioned_on            date NULL,
    expected_life_years        integer NULL CHECK (expected_life_years IS NULL OR expected_life_years BETWEEN 1 AND 200),
    replacement_cost           numeric(18, 2) NULL CHECK (replacement_cost IS NULL OR replacement_cost >= 0),
    status                     text NOT NULL CHECK (status IN ('Planned', 'Active', 'Restricted', 'Obsolete', 'Decommissioned')),
    created_by                 text NOT NULL,
    created_at                 timestamptz NOT NULL,
    decommission_requested_by  text NULL,
    decommission_requested_at  timestamptz NULL,
    decommission_reason        text NULL,
    CHECK ((decommission_requested_by IS NULL) = (decommission_requested_at IS NULL)),
    CHECK (machine_code IS NULL OR asset_type = 'Production')
);

-- An asset's history: every status change, edit and component movement, added and never changed.
CREATE TABLE maintenance.asset_event (
    event_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    asset_code    text NOT NULL REFERENCES maintenance.asset(asset_code),
    kind          text NOT NULL,
    from_status   text NULL,
    status        text NOT NULL,
    detail        text NOT NULL,
    actor         text NOT NULL,
    at            timestamptz NOT NULL,
    reason        text NULL
);
CREATE INDEX asset_event_asset ON maintenance.asset_event (asset_code, event_id);

CREATE TABLE maintenance.asset_component (
    component_id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    asset_code             text NOT NULL REFERENCES maintenance.asset(asset_code),
    part_number            text NOT NULL REFERENCES engineering.item(part_number),
    revision               text NULL,
    position               text NOT NULL CHECK (length(trim(position)) > 0),
    quantity               numeric(18, 6) NOT NULL CHECK (quantity > 0),
    installed_work_order   bigint NULL REFERENCES maintenance.work_order(work_order_id),
    installed_by           text NOT NULL,
    installed_at           timestamptz NOT NULL,
    removed_work_order     bigint NULL REFERENCES maintenance.work_order(work_order_id),
    removed_by             text NULL,
    removed_at             timestamptz NULL,
    removal_reason         text NULL,
    CHECK ((removed_at IS NULL) = (removed_by IS NULL) AND (removed_at IS NULL) = (removal_reason IS NULL))
);
CREATE INDEX asset_component_asset ON maintenance.asset_component (asset_code) WHERE removed_at IS NULL;
CREATE INDEX asset_component_part ON maintenance.asset_component (part_number);

CREATE FUNCTION maintenance.reject_asset_history_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'asset history is added to, never changed or deleted' USING ERRCODE = 'insufficient_privilege';
END;
$$;

CREATE TRIGGER asset_event_kept BEFORE UPDATE OR DELETE ON maintenance.asset_event FOR EACH ROW EXECUTE FUNCTION maintenance.reject_asset_history_change();

CREATE FUNCTION maintenance.guard_asset() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'assets are never deleted; decommission them' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF (NEW.asset_code, NEW.created_by, NEW.created_at) IS DISTINCT FROM (OLD.asset_code, OLD.created_by, OLD.created_at) THEN
        RAISE EXCEPTION 'asset % keeps its code and creation', OLD.asset_code USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF OLD.status = 'Decommissioned' THEN
        RAISE EXCEPTION 'asset % is decommissioned', OLD.asset_code USING ERRCODE = 'insufficient_privilege';
    END IF;
    -- A status change is always written to the history in the same transaction.
    IF NEW.status IS DISTINCT FROM OLD.status AND NOT EXISTS (
        SELECT 1 FROM maintenance.asset_event e WHERE e.asset_code = NEW.asset_code AND e.status = NEW.status AND e.from_status = OLD.status AND e.xmin = pg_current_xact_id()::xid) THEN
        RAISE EXCEPTION 'asset % status changes only with its history', OLD.asset_code USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER asset_guarded BEFORE UPDATE OR DELETE ON maintenance.asset FOR EACH ROW EXECUTE FUNCTION maintenance.guard_asset();

-- What was installed never changes; removal is recorded once.
CREATE FUNCTION maintenance.guard_asset_component() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'installed components are never deleted; remove them by work order' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF OLD.removed_at IS NOT NULL
       OR (NEW.asset_code, NEW.part_number, NEW.revision, NEW.position, NEW.quantity, NEW.installed_work_order, NEW.installed_by, NEW.installed_at)
          IS DISTINCT FROM (OLD.asset_code, OLD.part_number, OLD.revision, OLD.position, OLD.quantity, OLD.installed_work_order, OLD.installed_by, OLD.installed_at) THEN
        RAISE EXCEPTION 'component % keeps what was installed and is removed once', OLD.component_id USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER asset_component_guarded BEFORE UPDATE OR DELETE ON maintenance.asset_component FOR EACH ROW EXECUTE FUNCTION maintenance.guard_asset_component();
