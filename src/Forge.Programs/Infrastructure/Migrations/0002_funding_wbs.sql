-- Contract funding: each obligation (or de-obligation, negative) comes from a contract action.
CREATE TABLE programs.funding (
    funding_id    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    contract_id   bigint NOT NULL REFERENCES programs.contract(contract_id),
    clin          text NULL,
    modification  text NOT NULL,
    amount        numeric(18, 2) NOT NULL CHECK (amount <> 0),
    obligated_on  date NOT NULL,
    created_at    timestamptz NOT NULL,
    created_by    text NOT NULL,
    FOREIGN KEY (contract_id, clin) REFERENCES programs.clin(contract_id, clin)
);

-- Work breakdown structure of a program. Codes are dotted (1, 1.2, 1.2.3); a child's code extends its parent's.
CREATE TABLE programs.wbs (
    program_id   uuid NOT NULL REFERENCES programs.program(program_id),
    code         text NOT NULL,
    parent_code  text NULL,
    title        text NOT NULL,
    created_at   timestamptz NOT NULL,
    created_by   text NOT NULL,
    PRIMARY KEY (program_id, code),
    FOREIGN KEY (program_id, parent_code) REFERENCES programs.wbs(program_id, code)
);
