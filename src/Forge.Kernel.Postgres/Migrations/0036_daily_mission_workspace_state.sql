-- Briefing acknowledgement and personal landing preferences are UI state only. They never
-- represent approval or completion of an authoritative operational workflow.
CREATE TABLE kernel.daily_mission_briefing_state (
    person_id          uuid NOT NULL REFERENCES kernel.person(person_id) ON DELETE CASCADE,
    operational_date   date NOT NULL,
    operational_day_id text NOT NULL,
    scope_key           text NOT NULL,
    viewed_at           timestamptz NOT NULL,
    dismissed_at        timestamptz,
    updated_at          timestamptz NOT NULL,
    PRIMARY KEY (person_id, operational_date)
);

CREATE TABLE kernel.daily_mission_workspace_preference (
    person_id      uuid NOT NULL REFERENCES kernel.person(person_id) ON DELETE CASCADE,
    scope_key      text NOT NULL,
    landing_route  text NOT NULL,
    hawk_accents   text NOT NULL DEFAULT 'subtle' CHECK (hawk_accents IN ('off', 'subtle', 'expressive')),
    program_scope  text NOT NULL DEFAULT 'all',
    updated_at     timestamptz NOT NULL,
    PRIMARY KEY (person_id, scope_key)
);
