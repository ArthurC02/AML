-- The publication date a received page states (docs/feature/external-sources.md#common-path). Added columns only, both
-- NULL on a page stored before or not read; no stored record changes shape (PRAGMA user_version stays).
ALTER TABLE web_page ADD COLUMN published_on        TEXT NULL; -- ISO at its precision (YYYY, YYYY-MM, YYYY-MM-DD); NULL when MISSING
ALTER TABLE web_page ADD COLUMN published_on_source TEXT NULL; -- METADATA, TEXT or MISSING for a READ page
