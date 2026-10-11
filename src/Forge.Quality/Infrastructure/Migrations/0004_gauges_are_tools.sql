-- V3.8: today's gauges move into the tool model (Resources). Each gauge becomes a tool of the GAUGE type with its
-- calibration due date and certificate; its recorded calibrations become calibration events. quality.gauge and
-- quality.calibration are kept as they were and are no longer written.

INSERT INTO resources.tool_type (code, name, kind, calibration_days, usage_limit, service_uses, active, changed_at, changed_by)
SELECT 'GAUGE', 'Gauge', 'Gauge', NULL, NULL, NULL, true, now(), 'system:migration'
WHERE EXISTS (SELECT 1 FROM quality.gauge)
ON CONFLICT (code) DO NOTHING;

INSERT INTO resources.tool (code, type_code, description, custody, condition, calibration_due, certificate, status, created_at, created_by, changed_at, changed_by)
SELECT g.code, 'GAUGE', g.description, 'InHouse', 'Good', g.calibration_due, g.certificate, CASE WHEN g.active THEN 'Available' ELSE 'Retired' END,
       g.created_at, g.created_by, g.created_at, g.created_by
FROM quality.gauge g
ON CONFLICT (code) DO NOTHING;

-- Registered with the due date it had when defined (the current one when it was never recalibrated).
INSERT INTO resources.tool_event (tool_code, kind, at, actor, condition, due_on, certificate, note)
SELECT g.code, 'Registered', g.created_at, g.created_by, 'Good',
       CASE WHEN EXISTS (SELECT 1 FROM quality.calibration c WHERE c.gauge_code = g.code) THEN NULL ELSE g.calibration_due END,
       g.certificate, 'Moved from quality.gauge (V3.8)'
FROM quality.gauge g
WHERE EXISTS (SELECT 1 FROM resources.tool t WHERE t.code = g.code AND t.type_code = 'GAUGE');

INSERT INTO resources.tool_event (tool_code, kind, at, actor, calibrated_on, due_on, certificate, note)
SELECT c.gauge_code, 'Calibrated', c.recorded_at, c.recorded_by, c.calibrated_on, c.due_date, c.certificate, 'Moved from quality.calibration (V3.8)'
FROM quality.calibration c
WHERE EXISTS (SELECT 1 FROM resources.tool t WHERE t.code = c.gauge_code AND t.type_code = 'GAUGE')
ORDER BY c.calibration_id;

INSERT INTO resources.tool_event (tool_code, kind, at, actor, note)
SELECT g.code, 'Retired', g.created_at, 'system:migration', 'Inactive in quality.gauge (V3.8)'
FROM quality.gauge g
WHERE NOT g.active AND EXISTS (SELECT 1 FROM resources.tool t WHERE t.code = g.code AND t.type_code = 'GAUGE');

-- Inspections name the gauge they used; that reference now points at the tool, so new gauges (tools) can be used.
ALTER TABLE quality.inspection DROP CONSTRAINT inspection_gauge_code_fkey;
ALTER TABLE quality.inspection ADD CONSTRAINT inspection_gauge_tool_fkey FOREIGN KEY (gauge_code) REFERENCES resources.tool(code);
