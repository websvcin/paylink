-- Phase 3: where an attempt came from and its risk score, alert rules, and settlement reconciliation. MySQL 8.0+.
-- Same as db/sqlite/tenant/0004_risk_alerts_settlements.sql. ALTER TABLE runs once; PayLink records applied scripts.

ALTER TABLE attempts ADD COLUMN ip_country VARCHAR(2) NULL;
ALTER TABLE attempts ADD COLUMN device_id VARCHAR(40) NULL;
ALTER TABLE attempts ADD COLUMN risk_score INT NULL;
ALTER TABLE payments ADD COLUMN settled_at VARCHAR(40) NULL;
ALTER TABLE payments ADD COLUMN settlement_ref VARCHAR(100) NULL;
ALTER TABLE payments ADD COLUMN settlement_fee BIGINT NULL;

CREATE TABLE IF NOT EXISTS alert_rules (
    id                   BIGINT         NOT NULL AUTO_INCREMENT PRIMARY KEY,
    kind                 VARCHAR(40)    NOT NULL,
    threshold            BIGINT         NOT NULL,
    window_minutes       INT            NOT NULL DEFAULT 60,
    channel              VARCHAR(20)    NOT NULL DEFAULT 'both',
    enabled              INT            NOT NULL DEFAULT 1,
    last_fired_at        VARCHAR(40)    NULL,
    created_at           VARCHAR(40)    NOT NULL,
    created_by           VARCHAR(320)   NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS alert_events (
    id                   BIGINT         NOT NULL AUTO_INCREMENT PRIMARY KEY,
    rule_id              BIGINT         NOT NULL,
    at                   VARCHAR(40)    NOT NULL,
    message              VARCHAR(500)   NOT NULL,
    emailed              INT            NOT NULL DEFAULT 0,
    KEY ix_alert_events_at (at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS settlement_batches (
    id                   BIGINT         NOT NULL AUTO_INCREMENT PRIMARY KEY,
    connector            VARCHAR(50)    NULL,
    file_name            VARCHAR(300)   NOT NULL,
    uploaded_at          VARCHAR(40)    NOT NULL,
    uploaded_by          VARCHAR(320)   NULL,
    line_count           INT            NOT NULL DEFAULT 0,
    matched              INT            NOT NULL DEFAULT 0,
    mismatched           INT            NOT NULL DEFAULT 0,
    unmatched            INT            NOT NULL DEFAULT 0
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS settlement_lines (
    id                   BIGINT         NOT NULL AUTO_INCREMENT PRIMARY KEY,
    batch_id             BIGINT         NOT NULL,
    reference            VARCHAR(200)   NOT NULL,
    gross                BIGINT         NOT NULL,
    fee                  BIGINT         NOT NULL DEFAULT 0,
    tax                  BIGINT         NOT NULL DEFAULT 0,
    net                  BIGINT         NOT NULL,
    settled_on           VARCHAR(40)    NULL,
    settlement_ref       VARCHAR(100)   NULL,
    payment_id           VARCHAR(40)    NULL,
    match_status         VARCHAR(20)    NOT NULL,
    note                 VARCHAR(300)   NULL,
    KEY ix_settlement_lines_batch (batch_id),
    KEY ix_settlement_lines_payment (payment_id),
    KEY ix_settlement_lines_date (settled_on)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
