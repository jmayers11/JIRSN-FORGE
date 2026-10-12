-- V3.23: named, owner-private planning scenarios. Scenario inputs never alter source business records.
CREATE TABLE planning.scenario (
    scenario_id      uuid PRIMARY KEY,
    owner_person_id  uuid NOT NULL REFERENCES kernel.person(person_id),
    name             text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 100),
    change_json      text NOT NULL,
    created_at       timestamptz NOT NULL,
    changed_at       timestamptz NOT NULL,
    created_by       text NOT NULL,
    changed_by       text NOT NULL
);
CREATE INDEX scenario_owner_changed ON planning.scenario(owner_person_id, changed_at DESC, scenario_id);
