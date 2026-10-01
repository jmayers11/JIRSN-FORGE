-- Separation of duties: the person who set up an order cannot release it.
ALTER TABLE execution.production_order ADD COLUMN released_by text NULL;
ALTER TABLE execution.production_order ADD CONSTRAINT order_releaser_not_creator
    CHECK (released_by IS NULL OR released_by <> created_by);
