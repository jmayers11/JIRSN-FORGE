ALTER TABLE procurement.purchase_order_line
    ADD COLUMN unit_price numeric(18, 4) NULL CHECK (unit_price IS NULL OR unit_price >= 0),
    ADD COLUMN currency text NOT NULL DEFAULT 'USD' CHECK (currency IN ('USD', 'EUR', 'GBP', 'CAD'));
