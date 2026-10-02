-- PM by machine run hours (from shop-floor labor) as well as, or instead of, calendar days.
ALTER TABLE maintenance.pm_plan ALTER COLUMN interval_days DROP NOT NULL;
ALTER TABLE maintenance.pm_plan ADD COLUMN interval_run_hours numeric(10, 2) NULL CHECK (interval_run_hours IS NULL OR interval_run_hours BETWEEN 1 AND 100000);
ALTER TABLE maintenance.pm_plan ADD COLUMN grace_run_hours numeric(10, 2) NOT NULL DEFAULT 0 CHECK (grace_run_hours BETWEEN 0 AND 10000);
ALTER TABLE maintenance.pm_plan ADD CONSTRAINT pm_plan_has_interval CHECK (interval_days IS NOT NULL OR interval_run_hours IS NOT NULL);

-- A work order is due by date, by run hours counted from run_hours_from, or both (whichever first).
ALTER TABLE maintenance.work_order ALTER COLUMN due_date DROP NOT NULL;
ALTER TABLE maintenance.work_order ADD COLUMN due_run_hours numeric(10, 2) NULL;
ALTER TABLE maintenance.work_order ADD COLUMN run_hours_from timestamptz NULL;
ALTER TABLE maintenance.work_order ADD CONSTRAINT work_order_has_due CHECK (due_date IS NOT NULL OR due_run_hours IS NOT NULL);
ALTER TABLE maintenance.work_order ADD CONSTRAINT work_order_run_hours_from CHECK (due_run_hours IS NULL OR run_hours_from IS NOT NULL);
