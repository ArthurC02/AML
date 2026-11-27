-- Pages received by URL (docs/feature/external-sources.md#received-url-source-2): one row per canonical URL, its latest
-- fetch. Added after 0001 without changing the format (PRAGMA user_version stays): no stored record changes shape.
CREATE TABLE web_page (
    page_id       TEXT NOT NULL PRIMARY KEY, -- first 16 hex of SHA-256 of canonical_url
    canonical_url TEXT NOT NULL,
    status        TEXT NOT NULL,             -- PENDING, READ, REFUSED, FAILED
    problem       TEXT NULL,                 -- a PAGE_PROBLEM code when REFUSED or FAILED
    requested_at  TEXT NOT NULL,
    job_id        TEXT NULL,                 -- the FETCH_PAGE job the page is queued under; an outcome of any other job is not stored
    batch_id      TEXT NULL,
    final_url     TEXT NULL,
    media_type    TEXT NULL,
    title         TEXT NULL,
    fetched_at    TEXT NULL,
    content_hash  TEXT NULL,                 -- SHA-256 of the body as received
    -- Last, as in job: the long columns.
    main_text     TEXT NULL,
    reading_json  TEXT NULL
);
