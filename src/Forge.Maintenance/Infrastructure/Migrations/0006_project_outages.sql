-- V2.10: a facility or capital project can need a machine stopped; its outage is proposed and approved like any other.
ALTER TABLE maintenance.outage ADD COLUMN project_code text NULL REFERENCES projects.project(code);
