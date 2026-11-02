-- Owner decision 2026-11-01: HazCom training is a site-wide requirement for every new employee, before they work with hazardous
-- chemicals. It is an ordinary onboarding requirement, so each plant may retire it, add work-center requirements, or replace it with
-- different timing under Training & Performance.
INSERT INTO workforce.onboarding_requirement
    (requirement_id, work_center_code, qualification_code, due_days, note, active, created_at, created_by, updated_at, updated_by)
SELECT '0b1d5a57-0016-4a02-9a02-000000000001', NULL, 'HAZCOM', 7, 'Before working with hazardous chemicals (OSHA 29 CFR 1910.1200(h))', true,
       now(), 'system:migration', now(), 'system:migration'
WHERE NOT EXISTS (SELECT 1 FROM workforce.onboarding_requirement WHERE work_center_code IS NULL AND qualification_code = 'HAZCOM' AND active);
