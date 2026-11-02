-- Owner decision 2026-11-01: Hazard Communication (HazCom) training is one of the site's qualifications out of the box. OSHA 29 CFR
-- 1910.1200(h) requires it at initial assignment and whenever a new chemical hazard is introduced; renewal starts at 365 days, and each
-- plant may change or clear that period (Change renewal period) to match its own program.
INSERT INTO competency.qualification_type (code, name, kind, validity_days, created_at, created_by)
VALUES ('HAZCOM', 'Hazard Communication (HazCom)', 'Safety', 365, now(), 'system:migration')
ON CONFLICT (code) DO NOTHING;
