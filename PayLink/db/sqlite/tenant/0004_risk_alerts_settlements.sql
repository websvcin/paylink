-- Phase 3: where an attempt came from and its risk score, alert rules, and settlement reconciliation. SQLite.
-- Same as db/mysql/tenant/0004_risk_alerts_settlements.sql. ALTER TABLE runs once; PayLink records applied scripts.

ALTER TABLE attempts ADD COLUMN ip_country VARCHAR(2) NULL;
ALTER TABLE attempts ADD COLUMN device_id VARCHAR(40) NULL;
ALTER TABLE attempts ADD COLUMN risk_score INTEGER NULL;
ALTER TABLE payments ADD COLUMN settled_at VARCHAR(40) NULL;
ALTER TABLE payments ADD COLUMN settlement_ref VARCHAR(100) NULL;
ALTER TABLE payments ADD COLUMN settlement_fee BIGINT NULL;

CREATE TABLE IF NOT EXISTS alert_rules (
    id              INTEGER       PRIMARY KEY AUTOINCREMENT,
    kind            VARCHAR(40)   NOT NULL,
    threshold       BIGINT        NOT NULL,
    window_minutes  INTEGER       NOT NULL DEFAULT 60,
    channel         VARCHAR(20)   NOT NULL DEFAULT 'both',
    enabled         INTEGER       NOT NULL DEFAULT 1,
    last_fired_at   VARCHAR(40)   NULL,
    created_at      VARCHAR(40)   NOT NULL,
    created_by      VARCHAR(320)  NULL
);

CREATE TABLE IF NOT EXISTS alert_events (
    id         INTEGER       PRIMARY KEY AUTOINCREMENT,
    rule_id    BIGINT        NOT NULL,
    at         VARCHAR(40)   NOT NULL,
    message    VARCHAR(500)  NOT NULL,
    emailed    INTEGER       NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_alert_events_at ON alert_events (at);

CREATE TABLE IF NOT EXISTS settlement_batches (
    id           INTEGER       PRIMARY KEY AUTOINCREMENT,
    connector    VARCHAR(50)   NULL,
    file_name    VARCHAR(300)  NOT NULL,
    uploaded_at  VARCHAR(40)   NOT NULL,
    uploaded_by  VARCHAR(320)  NULL,
    line_count   INTEGER       NOT NULL DEFAULT 0,
    matched      INTEGER       NOT NULL DEFAULT 0,
    mismatched   INTEGER       NOT NULL DEFAULT 0,
    unmatched    INTEGER       NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS settlement_lines (
    id              INTEGER       PRIMARY KEY AUTOINCREMENT,
    batch_id        BIGINT        NOT NULL,
    reference       VARCHAR(200)  NOT NULL,
    gross           BIGINT        NOT NULL,
    fee             BIGINT        NOT NULL DEFAULT 0,
    tax             BIGINT        NOT NULL DEFAULT 0,
    net             BIGINT        NOT NULL,
    settled_on      VARCHAR(40)   NULL,
    settlement_ref  VARCHAR(100)  NULL,
    payment_id      VARCHAR(40)   NULL,
    match_status    VARCHAR(20)   NOT NULL,
    note            VARCHAR(300)  NULL
);
CREATE INDEX IF NOT EXISTS ix_settlement_lines_batch ON settlement_lines (batch_id);
CREATE INDEX IF NOT EXISTS ix_settlement_lines_payment ON settlement_lines (payment_id);
CREATE INDEX IF NOT EXISTS ix_settlement_lines_date ON settlement_lines (settled_on);
