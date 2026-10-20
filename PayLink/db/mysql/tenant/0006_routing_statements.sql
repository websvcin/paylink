-- Phase 4: routing (per-method amount ranges) and UPI bank statement matching. MySQL 8.0+.
-- Same as db/sqlite/tenant/0006_routing_statements.sql. ALTER TABLE runs once; PayLink records applied scripts.

ALTER TABLE connector_settings ADD COLUMN min_amount BIGINT NULL;
ALTER TABLE connector_settings ADD COLUMN max_amount BIGINT NULL;

CREATE TABLE IF NOT EXISTS bank_statement_lines (
    id            BIGINT         NOT NULL AUTO_INCREMENT PRIMARY KEY,
    upload_id     VARCHAR(40)    NOT NULL,
    line_no       INT            NOT NULL,
    txn_date      VARCHAR(40)    NULL,
    utr           VARCHAR(40)    NULL,
    amount        BIGINT         NOT NULL,
    narration     VARCHAR(500)   NULL,
    result        VARCHAR(20)    NOT NULL,
    payment_id    VARCHAR(40)    NULL,
    note          VARCHAR(300)   NULL,
    created_at    VARCHAR(40)    NOT NULL,
    created_by    VARCHAR(320)   NULL,
    KEY ix_bank_statement_lines_upload (upload_id),
    KEY ix_bank_statement_lines_utr (utr)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
