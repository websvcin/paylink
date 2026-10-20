-- Phase 2: gateways. Refunds, the gateway's own payment id on each attempt, and when PayLink last asked a gateway
-- for an attempt's status. MySQL 8.0+. Same as db/sqlite/tenant/0003_refunds.sql.
-- (ALTER TABLE is not repeatable by hand; PayLink records applied scripts in schema_migrations and runs each once.)

ALTER TABLE attempts ADD COLUMN provider_payment_id VARCHAR(200) NULL;
ALTER TABLE attempts ADD COLUMN paid_method VARCHAR(40) NULL;
ALTER TABLE attempts ADD COLUMN polled_at VARCHAR(40) NULL;
ALTER TABLE payments ADD COLUMN amount_refunded BIGINT NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS refunds (
    id                   VARCHAR(40)    NOT NULL PRIMARY KEY,
    payment_id           VARCHAR(40)    NOT NULL,
    attempt_id           VARCHAR(40)    NOT NULL,
    livemode             INT            NOT NULL,
    amount               BIGINT         NOT NULL,
    currency             VARCHAR(3)     NOT NULL,
    status               VARCHAR(20)    NOT NULL,
    reason               VARCHAR(300)   NULL,
    connector            VARCHAR(50)    NOT NULL,
    gateway_ref          VARCHAR(200)   NULL,
    gateway_message      VARCHAR(500)   NULL,
    manual               INT            NOT NULL DEFAULT 0,
    idempotency_key      VARCHAR(200)   NULL,
    created_by           VARCHAR(320)   NULL,
    created_at           VARCHAR(40)    NOT NULL,
    updated_at           VARCHAR(40)    NOT NULL,
    KEY ix_refunds_payment (payment_id),
    KEY ix_refunds_gateway (connector, gateway_ref),
    KEY ix_refunds_status (status),
    UNIQUE KEY uq_refunds_idempotency (payment_id, idempotency_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
