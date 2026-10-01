-- Shift calendars: site time zone, recurring shifts, work-center shifts and site closures.
ALTER TABLE resources.site ADD COLUMN time_zone text NOT NULL DEFAULT 'America/New_York';

CREATE TABLE resources.shift (
    code              text PRIMARY KEY,
    site_code         text NOT NULL REFERENCES resources.site(code),
    name              text NOT NULL,
    start_time        time NOT NULL,
    duration_minutes  integer NOT NULL CHECK (duration_minutes BETWEEN 1 AND 1440),
    days              smallint[] NOT NULL CHECK (cardinality(days) > 0 AND days <@ '{1,2,3,4,5,6,7}'::smallint[]),
    created_at        timestamptz NOT NULL,
    created_by        text NOT NULL
);

CREATE TABLE resources.work_center_shift (
    work_center_code  text NOT NULL REFERENCES resources.work_center(code),
    shift_code        text NOT NULL REFERENCES resources.shift(code),
    PRIMARY KEY (work_center_code, shift_code)
);

CREATE TABLE resources.site_closure (
    site_code   text NOT NULL REFERENCES resources.site(code),
    closed_on   date NOT NULL,
    note        text NULL,
    created_at  timestamptz NOT NULL,
    created_by  text NOT NULL,
    PRIMARY KEY (site_code, closed_on)
);
