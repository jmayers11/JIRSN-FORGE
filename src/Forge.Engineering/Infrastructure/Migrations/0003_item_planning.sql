-- Item planning parameters used by MRP (spec §9, §13).
ALTER TABLE engineering.item
    ADD COLUMN safety_stock numeric(18, 6) NOT NULL DEFAULT 0 CHECK (safety_stock >= 0),
    ADD COLUMN lot_policy text NOT NULL DEFAULT 'LotForLot' CHECK (lot_policy IN ('LotForLot', 'FixedQuantity', 'MinimumQuantity')),
    ADD COLUMN order_quantity numeric(18, 6) NOT NULL DEFAULT 0 CHECK (order_quantity >= 0),
    ADD COLUMN order_multiple numeric(18, 6) NOT NULL DEFAULT 0 CHECK (order_multiple >= 0),
    ADD COLUMN planning_updated_at timestamptz NULL,
    ADD COLUMN planning_updated_by text NULL,
    ADD CONSTRAINT item_fixed_quantity_set CHECK (lot_policy <> 'FixedQuantity' OR order_quantity > 0);
