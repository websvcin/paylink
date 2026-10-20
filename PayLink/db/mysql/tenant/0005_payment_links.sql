-- Phase 4: payment links. A reusable page at /l/{code} where payers enter (or see) an amount and pay. MySQL 8.0+.
-- Same as db/sqlite/tenant/0005_payment_links.sql. ALTER TABLE runs once; PayLink records applied scripts.

ALTER TABLE payments ADD COLUMN link_id VARCHAR(40) NULL;
CREATE INDEX ix_payments_link ON payments (link_id, status);

CREATE TABLE IF NOT EXISTS payment_links (
    id              VARCHAR(40)    NOT NULL PRIMARY KEY,
    code            VARCHAR(60)    NOT NULL,
    livemode        INT            NOT NULL,
    title           VARCHAR(200)   NOT NULL,
    description     VARCHAR(1000)  NULL,
    amount          BIGINT         NULL,
    min_amount      BIGINT         NULL,
    max_amount      BIGINT         NULL,
    currency        VARCHAR(3)     NOT NULL,
    ask_email       INT            NOT NULL DEFAULT 0,
    ask_phone       INT            NOT NULL DEFAULT 0,
    return_url      VARCHAR(1000)  NULL,
    max_payments    INT            NULL,
    expires_at      VARCHAR(40)    NULL,
    active          INT            NOT NULL DEFAULT 1,
    created_at      VARCHAR(40)    NOT NULL,
    created_by      VARCHAR(320)   NULL,
    updated_at      VARCHAR(40)    NOT NULL,
    UNIQUE KEY uq_payment_links_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
