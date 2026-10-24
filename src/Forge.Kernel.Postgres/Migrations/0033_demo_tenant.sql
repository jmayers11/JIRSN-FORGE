-- Sprint 9: a database may be designated a demo tenant (for example WhiskerWorks WW-P01). Only a designated demo database has a
-- simulated clock, a saved baseline and a reset, and only when the host also runs with Forge:Demo:Enabled. Designation is refused
-- while the database holds any other site, so a live plant such as PLANT1 can never be put on a simulated clock or reset.
CREATE TABLE kernel.demo_tenant (
    singleton            boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    demo_code            text NOT NULL CHECK (length(btrim(demo_code)) BETWEEN 1 AND 40),
    site_code            text NOT NULL CHECK (length(btrim(site_code)) BETWEEN 1 AND 40),
    database_name        text NOT NULL,
    clock_offset_seconds bigint NOT NULL DEFAULT 0 CHECK (clock_offset_seconds >= 0),
    designated_by        text NOT NULL,
    designated_at        timestamptz NOT NULL
);

-- A saved baseline: a whole-database copy plus the KPIs it was captured with, for comparison after scenarios run.
CREATE TABLE kernel.demo_baseline (
    baseline_id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    database_name        text NOT NULL,
    captured_at          timestamptz NOT NULL,
    simulated_at         timestamptz NOT NULL,
    clock_offset_seconds bigint NOT NULL,
    captured_by          text NOT NULL,
    reason               text NOT NULL,
    kpis                 jsonb NOT NULL
);

INSERT INTO kernel.permission_catalog(permission)
VALUES ('demo.control'), ('demo.reset')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('SecurityAdministrator', 'demo.control'),
    ('SecurityAdministrator', 'demo.reset'),
    ('Planner', 'demo.control'),
    ('Supervisor', 'demo.control')
ON CONFLICT DO NOTHING;
