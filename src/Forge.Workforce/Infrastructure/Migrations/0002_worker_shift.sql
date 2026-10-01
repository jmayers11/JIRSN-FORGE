-- The shift a worker is scheduled on. A schedule is a forecast input only; it never proves presence.
ALTER TABLE workforce.worker ADD COLUMN shift_code text NULL REFERENCES resources.shift(code);
