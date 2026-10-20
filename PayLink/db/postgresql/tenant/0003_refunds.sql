-- Phase 2: gateways. Refunds, the gateway's own payment id on each attempt, and when PayLink last asked a gateway
-- for an attempt's status. PostgreSQL 13+. Same as db/sqlite/ and db/mysql/tenant/0003_refunds.sql.
-- (ALTER TABLE is not repeatable by hand; PayLink records applied scripts in schema_migrations and runs each once.)

ALTER TABLE attempts ADD COLUMN provider_payment_id VARCHAR(200) NULL;
ALTER TABLE attempts ADD COLUMN paid_method VARCHAR(40) NULL;
ALTER TABLE attempts ADD COLUMN polled_at VARCHAR(40) NULL;
ALTER TABLE payments ADD COLUMN amount_refunded BIGINT NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS refunds (
    id               VARCHAR(40)   NOT NULL PRIMARY KEY,
    payment_id       VARCHAR(40)   NOT NULL,
    attempt_id       VARCHAR(40)   NOT NULL,
    livemode         BOOLEAN    NOT NULL,
    amount           BIGINT        NOT NULL,
    currency         VARCHAR(3)    NOT NULL,
    status           VARCHAR(20)   NOT NULL,
    reason           VARCHAR(300)  NULL,
    connector        VARCHAR(50)   NOT NULL,
    gateway_ref      VARCHAR(200)  NULL,
    gateway_message  VARCHAR(500)  NULL,
    manual           BOOLEAN    NOT NULL DEFAULT FALSE,
    idempotency_key  VARCHAR(200)  NULL,
    created_by       VARCHAR(320)  NULL,
    created_at       VARCHAR(40)   NOT NULL,
    updated_at       VARCHAR(40)   NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_refunds_payment ON refunds (payment_id);
CREATE INDEX IF NOT EXISTS ix_refunds_gateway ON refunds (connector, gateway_ref);
CREATE INDEX IF NOT EXISTS ix_refunds_status ON refunds (status);
CREATE UNIQUE INDEX IF NOT EXISTS uq_refunds_idempotency ON refunds (payment_id, idempotency_key);
