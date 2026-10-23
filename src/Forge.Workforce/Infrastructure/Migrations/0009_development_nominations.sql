CREATE TABLE workforce.development_nomination (
    nomination_id uuid PRIMARY KEY,
    person_id uuid NOT NULL REFERENCES workforce.worker(person_id),
    kind text NOT NULL CHECK (kind IN ('AdvancedTraining', 'LeadReadiness', 'PromotionReadiness', 'CompensationConsideration')),
    pathway text NOT NULL,
    rationale text NOT NULL,
    expected_plant_benefit text NOT NULL,
    evidence_references jsonb NOT NULL DEFAULT '[]'::jsonb CHECK (jsonb_typeof(evidence_references) = 'array'),
    estimated_training_hours numeric(12,3) NULL CHECK (estimated_training_hours IS NULL OR estimated_training_hours >= 0),
    estimated_cost numeric(14,2) NULL CHECK (estimated_cost IS NULL OR estimated_cost >= 0),
    currency text NULL,
    status text NOT NULL CHECK (status IN ('Draft','Submitted','InReview','Approved','Declined','Deferred','TrainingScheduled','Completed','PracticalAssessment','Authorized')),
    created_by text NOT NULL,
    created_by_person_id uuid NULL REFERENCES kernel.person(person_id),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    version integer NOT NULL CHECK (version > 0)
);

CREATE INDEX development_nomination_person_status ON workforce.development_nomination(person_id, status, updated_at DESC);

CREATE TABLE workforce.development_nomination_event (
    event_id uuid PRIMARY KEY,
    nomination_id uuid NOT NULL REFERENCES workforce.development_nomination(nomination_id),
    from_status text NULL,
    to_status text NOT NULL,
    note text NOT NULL,
    recorded_by text NOT NULL,
    recorded_by_person_id uuid NULL REFERENCES kernel.person(person_id),
    recorded_at timestamptz NOT NULL
);

CREATE INDEX development_nomination_event_history ON workforce.development_nomination_event(nomination_id, recorded_at, event_id);
