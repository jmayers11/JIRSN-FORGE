CREATE SCHEMA IF NOT EXISTS programs;

-- The program id is the same uuid used for program access (kernel.program_access) and for
-- segregating orders, demand and audit records.
CREATE TABLE programs.program (
    program_id   uuid PRIMARY KEY,
    code         text NOT NULL UNIQUE,
    name         text NOT NULL,
    customer     text NULL,
    description  text NULL,
    status       text NOT NULL CHECK (status IN ('Active', 'Closed')),
    created_at   timestamptz NOT NULL,
    created_by   text NOT NULL
);

CREATE TABLE programs.contract (
    contract_id   bigint GENERATED ALWAYS AS IDENTITY (START WITH 1001) PRIMARY KEY,
    program_id    uuid NOT NULL REFERENCES programs.program(program_id),
    number        text NOT NULL UNIQUE,
    customer      text NOT NULL,
    title         text NOT NULL,
    period_start  date NOT NULL,
    period_end    date NOT NULL CHECK (period_end >= period_start),
    ceiling       numeric(18, 2) NULL CHECK (ceiling IS NULL OR ceiling >= 0),
    status        text NOT NULL CHECK (status IN ('Active', 'Complete', 'Cancelled')),
    created_at    timestamptz NOT NULL,
    created_by    text NOT NULL
);

CREATE TABLE programs.clin (
    contract_id  bigint NOT NULL REFERENCES programs.contract(contract_id),
    clin         text NOT NULL,
    parent_clin  text NULL,
    description  text NOT NULL,
    part_number  text NULL REFERENCES engineering.item(part_number),
    quantity     numeric(18, 6) NULL CHECK (quantity IS NULL OR quantity > 0),
    unit_price   numeric(18, 2) NULL CHECK (unit_price IS NULL OR unit_price >= 0),
    PRIMARY KEY (contract_id, clin),
    FOREIGN KEY (contract_id, parent_clin) REFERENCES programs.clin(contract_id, clin)
);

CREATE TABLE programs.delivery (
    delivery_id   bigint GENERATED ALWAYS AS IDENTITY (START WITH 1) PRIMARY KEY,
    contract_id   bigint NOT NULL,
    clin          text NOT NULL,
    quantity      numeric(18, 6) NOT NULL CHECK (quantity > 0),
    due_date      date NOT NULL,
    cancelled     boolean NOT NULL DEFAULT false,
    created_at    timestamptz NOT NULL,
    created_by    text NOT NULL,
    FOREIGN KEY (contract_id, clin) REFERENCES programs.clin(contract_id, clin)
);

CREATE TABLE programs.requirement (
    contract_id  bigint NOT NULL REFERENCES programs.contract(contract_id),
    number       integer NOT NULL,
    code         text NOT NULL,
    text         text NOT NULL,
    PRIMARY KEY (contract_id, number)
);

CREATE TABLE programs.milestone (
    milestone_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    program_id    uuid NOT NULL REFERENCES programs.program(program_id),
    title         text NOT NULL,
    due_date      date NOT NULL,
    completed_on  date NULL,
    completed_by  text NULL,
    created_at    timestamptz NOT NULL,
    created_by    text NOT NULL
);
