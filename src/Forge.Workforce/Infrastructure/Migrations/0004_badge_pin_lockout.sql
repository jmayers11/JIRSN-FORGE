ALTER TABLE workforce.badge
    ADD COLUMN pin_failed_attempts integer NOT NULL DEFAULT 0 CHECK (pin_failed_attempts >= 0),
    ADD COLUMN pin_locked_until timestamptz NULL;
