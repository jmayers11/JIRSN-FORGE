-- Sprint 10: operation labor detail reads source-linked machine stoppages by order and operation.
CREATE INDEX stoppage_operation ON execution.stoppage (order_number, sequence, started_at, stoppage_id)
    WHERE order_number IS NOT NULL AND sequence IS NOT NULL;
