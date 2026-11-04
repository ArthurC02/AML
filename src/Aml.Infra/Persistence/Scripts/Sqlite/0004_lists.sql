-- List database (Data Processing half): every captured Profile version with its content hash; the previous snapshot stays.
CREATE TABLE list_profile (
    profile_id   TEXT    NOT NULL,
    version      INTEGER NOT NULL,
    content_hash TEXT    NOT NULL,
    source_text  TEXT    NOT NULL,
    captured_at  TEXT    NOT NULL,
    CONSTRAINT pk_list_profile PRIMARY KEY (profile_id, version)
);

-- The input files of each analysis revision, so a changed Profile can be re-analyzed into a new revision.
CREATE TABLE case_input (
    case_id    TEXT    NOT NULL,
    revision   INTEGER NOT NULL,
    files_json TEXT    NOT NULL,
    created_at TEXT    NOT NULL,
    CONSTRAINT pk_case_input PRIMARY KEY (case_id, revision),
    CONSTRAINT fk_input_case FOREIGN KEY (case_id) REFERENCES aml_case (case_id)
);
