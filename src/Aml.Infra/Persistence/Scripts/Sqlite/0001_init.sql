-- Portable DDL (see docs/feature/persistence.md): app-supplied text ids, UTC ISO-8601 text timestamps, enums as text.
CREATE TABLE aml_case (
    case_id            TEXT    NOT NULL PRIMARY KEY,
    subject_ref        TEXT    NOT NULL,
    trigger_event_id   TEXT    NOT NULL,
    case_status        TEXT    NOT NULL,
    current_revision   INTEGER NOT NULL,
    effective_revision INTEGER NULL,
    created_at         TEXT    NOT NULL,
    updated_at         TEXT    NOT NULL,
    CONSTRAINT uq_case_trigger UNIQUE (trigger_event_id, subject_ref)
);

CREATE TABLE case_analysis (
    case_id          TEXT    NOT NULL,
    revision         INTEGER NOT NULL,
    execution_status TEXT    NOT NULL,
    case_result      TEXT    NOT NULL,
    result_json      TEXT    NOT NULL,
    created_at       TEXT    NOT NULL,
    CONSTRAINT pk_case_analysis PRIMARY KEY (case_id, revision),
    CONSTRAINT fk_analysis_case FOREIGN KEY (case_id) REFERENCES aml_case (case_id)
);

CREATE TABLE job (
    job_id            TEXT    NOT NULL PRIMARY KEY,
    job_type          TEXT    NOT NULL,
    case_id           TEXT    NULL,
    analysis_revision INTEGER NULL,
    status            TEXT    NOT NULL,
    priority          INTEGER NOT NULL,
    attempt_count     INTEGER NOT NULL,
    next_attempt_at   TEXT    NOT NULL,
    lease_owner       TEXT    NULL,
    lease_expiry      TEXT    NULL,
    created_at        TEXT    NOT NULL,
    completed_at      TEXT    NULL,
    error_code        TEXT    NULL
);
