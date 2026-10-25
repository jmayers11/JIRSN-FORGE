-- Sprint 10 (WW-S10-29): an approved rework plan asks for training for the people named to do work they are not yet qualified for.
ALTER TABLE workforce.training_request DROP CONSTRAINT training_request_source_check;
ALTER TABLE workforce.training_request ADD CONSTRAINT training_request_source_check
    CHECK (source IN ('Onboarding', 'Expiry', 'Supervisor', 'Development', 'Rework'));
