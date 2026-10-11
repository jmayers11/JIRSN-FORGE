-- V3.10: production holds (V3.9) become Production holds in the kernel's generalized holds. Their ids, reasons, who
-- placed and released them and why are kept; the V3.9 table goes.

INSERT INTO kernel.hold (hold_id, kind, target_type, target_id, target_part, source, reason, evidence, affected_records, required_resolution,
                         placed_at, placed_by, released_at, released_by, release_reason)
SELECT hold_id, 'Production',
       CASE scope WHEN 'Order' THEN 'Order' WHEN 'Operation' THEN 'Operation' WHEN 'Part' THEN 'Part' WHEN 'WorkCenter' THEN 'WorkCenter' ELSE 'Machine' END,
       CASE scope
           WHEN 'Order' THEN order_number::text
           WHEN 'Operation' THEN order_number::text || '/' || sequence::text
           WHEN 'Part' THEN part_number
           WHEN 'WorkCenter' THEN work_center_code
           ELSE machine_code
       END,
       NULL, 'Production hold (V3.9)', reason, NULL, '{}', 'A supervisor or planner confirms production may continue.',
       placed_at, placed_by, released_at, released_by, release_reason
FROM execution.production_hold;

SELECT setval(pg_get_serial_sequence('kernel.hold', 'hold_id'), greatest(500, (SELECT coalesce(max(hold_id), 500) FROM kernel.hold)));

DROP TABLE execution.production_hold;
DROP FUNCTION execution.production_hold_release_only();
