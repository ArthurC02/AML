-- Batch items: grouping, idempotency and the durable input of each job.
ALTER TABLE job ADD COLUMN batch_id TEXT NULL;
ALTER TABLE job ADD COLUMN idempotency_key TEXT NULL;
ALTER TABLE job ADD COLUMN payload TEXT NULL;
CREATE UNIQUE INDEX uq_job_idempotency ON job (idempotency_key);
CREATE INDEX ix_job_batch ON job (batch_id);
