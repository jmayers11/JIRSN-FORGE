-- ADR 0057 §2: an inspection takes time in the plan. The planned minutes are a gap in the order's flow between the
-- inspected operation and the next one; 0 (the default for every existing plan) changes nothing.
ALTER TABLE quality.inspection_plan ADD COLUMN planned_minutes integer NOT NULL DEFAULT 0 CHECK (planned_minutes BETWEEN 0 AND 20160);
