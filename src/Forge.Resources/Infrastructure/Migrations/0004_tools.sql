-- V3.8: tools as resources (V3 brief Z, AA).
--
-- A tool is a resource with its own identity: type, serial, where it is, who has it, its condition, the machines
-- and activities it may be used on, how often it has been used and its limit. Gauges and other measuring tools
-- carry a calibration due date. Every change is an event, kept for good, so whether a tool was valid can be
-- judged as of the moment it was used. A blade out for sharpening is a service cycle: sent out, returned, its uses
-- since service counted from zero again.

CREATE TABLE resources.tool_type (
    code               text PRIMARY KEY,
    name               text NOT NULL,
    kind               text NOT NULL CHECK (kind IN ('Gauge', 'Cutting', 'Fixture', 'Die', 'Mold', 'Other')),
    calibration_days   integer NULL CHECK (calibration_days > 0),
    usage_limit        integer NULL CHECK (usage_limit > 0),
    service_uses       integer NULL CHECK (service_uses > 0),
    active             boolean NOT NULL DEFAULT true,
    changed_at         timestamptz NOT NULL,
    changed_by         text NOT NULL
);

CREATE TABLE resources.tool (
    code                 text PRIMARY KEY,
    type_code            text NOT NULL REFERENCES resources.tool_type(code),
    description          text NOT NULL,
    serial               text NULL,
    location             text NULL,
    custody              text NOT NULL CHECK (custody IN ('InHouse', 'AtVendor', 'WithPerson')),
    custodian            text NULL,
    condition            text NOT NULL CHECK (condition IN ('Good', 'Worn', 'Damaged')),
    eligible_machines    text[] NOT NULL DEFAULT '{}',
    eligible_activities  text[] NOT NULL DEFAULT '{}',
    usage_count          integer NOT NULL DEFAULT 0 CHECK (usage_count >= 0),
    uses_since_service   integer NOT NULL DEFAULT 0 CHECK (uses_since_service >= 0),
    calibration_due      date NULL,
    certificate          text NULL,
    status               text NOT NULL CHECK (status IN ('Available', 'OutForService', 'Retired')),
    created_at           timestamptz NOT NULL,
    created_by           text NOT NULL,
    changed_at           timestamptz NOT NULL,
    changed_by           text NOT NULL,
    CHECK (custody = 'InHouse' OR custodian IS NOT NULL),
    CHECK (status <> 'OutForService' OR custody = 'AtVendor')
);
CREATE UNIQUE INDEX tool_serial ON resources.tool (type_code, serial) WHERE serial IS NOT NULL;

CREATE TABLE resources.tool_event (
    event_id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tool_code       text NOT NULL REFERENCES resources.tool(code),
    kind            text NOT NULL CHECK (kind IN ('Registered', 'Moved', 'Calibrated', 'CalibrationFailed', 'Used', 'SentForService', 'Returned', 'ConditionChanged', 'Retired')),
    at              timestamptz NOT NULL,
    actor           text NOT NULL,
    location        text NULL,
    custodian       text NULL,
    condition       text NULL,
    calibrated_on   date NULL,
    due_on          date NULL,
    certificate     text NULL,
    uses            integer NULL CHECK (uses > 0),
    reference       text NULL,
    note            text NULL
);
CREATE INDEX tool_event_tool ON resources.tool_event (tool_code, event_id);

CREATE FUNCTION resources.reject_history_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% rows are history and are never changed', TG_TABLE_NAME;
END $$;
CREATE TRIGGER tool_event_append_only BEFORE UPDATE OR DELETE ON resources.tool_event
    FOR EACH ROW EXECUTE FUNCTION resources.reject_history_change();
