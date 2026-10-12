-- Extend ledger-backed outside processing with immutable sent configuration and return links.
CREATE TABLE inventory.outside_process_dispatch (
    transaction_id bigint PRIMARY KEY REFERENCES inventory.transaction(transaction_id),
    process_key text NOT NULL,
    snapshot jsonb NOT NULL
);
CREATE INDEX outside_process_dispatch_key ON inventory.outside_process_dispatch(process_key);
CREATE TRIGGER outside_process_dispatch_immutable BEFORE UPDATE OR DELETE ON inventory.outside_process_dispatch
    FOR EACH ROW EXECUTE FUNCTION inventory.reject_ledger_change();

CREATE TABLE inventory.outside_process_return (
    transaction_id bigint PRIMARY KEY REFERENCES inventory.transaction(transaction_id),
    process_key text NOT NULL,
    snapshot jsonb NOT NULL
);
CREATE INDEX outside_process_return_key ON inventory.outside_process_return(process_key);
CREATE TRIGGER outside_process_return_immutable BEFORE UPDATE OR DELETE ON inventory.outside_process_return
    FOR EACH ROW EXECUTE FUNCTION inventory.reject_ledger_change();
