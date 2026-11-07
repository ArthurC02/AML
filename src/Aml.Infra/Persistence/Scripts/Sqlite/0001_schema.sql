-- Portable DDL (see docs/feature/persistence.md): app-supplied text ids, UTC ISO-8601 text timestamps, enums as text.
-- The PoC has one schema script: when the schema or the stored JSON format changes, the database is deleted and rebuilt.
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
CREATE INDEX ix_case_subject ON aml_case (subject_ref);

-- One analysis revision: the full result and explanation as JSON (read back into their types).
CREATE TABLE case_analysis (
    case_id          TEXT    NOT NULL,
    revision         INTEGER NOT NULL,
    execution_status TEXT    NOT NULL,
    case_result      TEXT    NOT NULL,
    result_json      TEXT    NOT NULL,
    explanation_json TEXT    NULL,
    created_at       TEXT    NOT NULL,
    CONSTRAINT pk_case_analysis PRIMARY KEY (case_id, revision),
    CONSTRAINT fk_analysis_case FOREIGN KEY (case_id) REFERENCES aml_case (case_id)
);

-- The input files of each revision, so a changed Profile can be re-analyzed into a new revision.
CREATE TABLE case_input (
    case_id    TEXT    NOT NULL,
    revision   INTEGER NOT NULL,
    files_json TEXT    NOT NULL,
    created_at TEXT    NOT NULL,
    CONSTRAINT pk_case_input PRIMARY KEY (case_id, revision),
    CONSTRAINT fk_input_case FOREIGN KEY (case_id) REFERENCES aml_case (case_id)
);

-- What a revision was computed from: input file hashes, Profile versions and rule-set versions.
CREATE TABLE analysis_lineage (
    case_id               TEXT    NOT NULL,
    revision              INTEGER NOT NULL,
    schema_version        INTEGER NOT NULL,
    source_policy_version TEXT    NOT NULL,
    code_catalog_version  TEXT    NOT NULL,
    app_version           TEXT    NOT NULL,
    as_of                 TEXT    NOT NULL, -- analysis date (UTC, yyyy-MM-dd) the time-dependent rules ran on
    input_files_json      TEXT    NOT NULL,
    profiles_json         TEXT    NOT NULL,
    CONSTRAINT pk_analysis_lineage PRIMARY KEY (case_id, revision),
    CONSTRAINT fk_lineage_analysis FOREIGN KEY (case_id, revision) REFERENCES case_analysis (case_id, revision)
);

-- Data quality of a revision; rates are the cell counts over comparison_cells.
CREATE TABLE analysis_quality (
    case_id          TEXT    NOT NULL,
    revision         INTEGER NOT NULL,
    hits             INTEGER NOT NULL,
    comparison_cells INTEGER NOT NULL,
    missing_cells    INTEGER NOT NULL,
    uncertain_cells  INTEGER NOT NULL,
    conflict_cells   INTEGER NOT NULL,
    not_evaluated    INTEGER NOT NULL,
    findings_json    TEXT    NOT NULL,
    CONSTRAINT pk_analysis_quality PRIMARY KEY (case_id, revision),
    CONSTRAINT fk_quality_analysis FOREIGN KEY (case_id, revision) REFERENCES case_analysis (case_id, revision)
);

-- Queryable facts of each hit, written with the JSON in one transaction. The JSON stays the full record.
CREATE TABLE case_hit (
    case_id           TEXT    NOT NULL,
    revision          INTEGER NOT NULL,
    hit_number        INTEGER NOT NULL,
    profile_id        TEXT    NOT NULL,
    condition_types   TEXT    NOT NULL,
    legal_stage       TEXT    NOT NULL,
    identity_decision TEXT    NOT NULL,
    stage3_row        INTEGER NULL,
    hard_conflict     TEXT    NULL,
    pair_result       TEXT    NOT NULL,
    review_priority   TEXT    NULL,
    CONSTRAINT pk_case_hit PRIMARY KEY (case_id, revision, hit_number),
    CONSTRAINT fk_hit_analysis FOREIGN KEY (case_id, revision) REFERENCES case_analysis (case_id, revision)
);

CREATE TABLE hit_evidence (
    case_id           TEXT    NOT NULL,
    revision          INTEGER NOT NULL,
    hit_number        INTEGER NOT NULL,
    evidence_group    TEXT    NOT NULL,
    direction         TEXT    NOT NULL,
    comparison_status TEXT    NOT NULL,
    strength          TEXT    NOT NULL,
    detail_code       TEXT    NULL,
    CONSTRAINT pk_hit_evidence PRIMARY KEY (case_id, revision, hit_number, evidence_group),
    CONSTRAINT fk_evidence_hit FOREIGN KEY (case_id, revision, hit_number) REFERENCES case_hit (case_id, revision, hit_number)
);

-- reason_kind: IDENTITY, PAIR or DISPOSITION; seq keeps the emitted order.
CREATE TABLE hit_reason (
    case_id     TEXT    NOT NULL,
    revision    INTEGER NOT NULL,
    hit_number  INTEGER NOT NULL,
    reason_kind TEXT    NOT NULL,
    seq         INTEGER NOT NULL,
    reason_code TEXT    NOT NULL,
    CONSTRAINT pk_hit_reason PRIMARY KEY (case_id, revision, hit_number, reason_kind, seq),
    CONSTRAINT fk_reason_hit FOREIGN KEY (case_id, revision, hit_number) REFERENCES case_hit (case_id, revision, hit_number)
);
CREATE INDEX ix_hit_reason_code ON hit_reason (reason_code);

-- Batch items: grouping, idempotency and the durable input of each job.
CREATE TABLE job (
    job_id            TEXT    NOT NULL PRIMARY KEY,
    job_type          TEXT    NOT NULL,
    batch_id          TEXT    NULL,
    idempotency_key   TEXT    NULL,
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
    error_code        TEXT    NULL,
    -- Times the host stopped during the job and gave its attempt back; past Batch:MaxReleases the attempt counts.
    release_count     INTEGER NOT NULL DEFAULT 0,
    -- Last: it holds the case files, and SQLite reads a row's columns in order, so the claim and status queries never
    -- read past it.
    payload           TEXT    NULL
);
CREATE UNIQUE INDEX uq_job_idempotency ON job (idempotency_key);
CREATE INDEX ix_job_batch_status ON job (batch_id, status);
-- Claiming walks PENDING jobs in claim order and stops at the limit: no sort of the whole queue, and a job still waiting
-- out a retry delay is skipped inside the index.
CREATE INDEX ix_job_claim ON job (status, priority DESC, created_at, next_attempt_at);

-- List database: every captured Profile version with its content hash; the previous snapshot stays.
CREATE TABLE list_profile (
    profile_id   TEXT    NOT NULL,
    version      INTEGER NOT NULL,
    content_hash TEXT    NOT NULL,
    source_text  TEXT    NOT NULL,
    captured_at  TEXT    NOT NULL,
    CONSTRAINT pk_list_profile PRIMARY KEY (profile_id, version)
);

-- The format of this file; must equal CaseJson.SchemaVersion (checked at startup and by CaseStoreTests).
PRAGMA user_version = 6;
