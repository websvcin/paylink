-- Phase 4: payment links. A reusable page at /l/{code} where payers enter (or see) an amount and pay. SQLite.
-- Same as db/mysql/tenant/0005_payment_links.sql. ALTER TABLE runs once; PayLink records applied scripts.

ALTER TABLE payments ADD COLUMN link_id VARCHAR(40) NULL;
CREATE INDEX IF NOT EXISTS ix_payments_link ON payments (link_id, status);

CREATE TABLE IF NOT EXISTS payment_links (
    id              VARCHAR(40)    NOT NULL PRIMARY KEY,
    code            VARCHAR(60)    NOT NULL,
    livemode        INTEGER        NOT NULL,
    title           VARCHAR(200)   NOT NULL,
    description     VARCHAR(1000)  NULL,
    amount          BIGINT         NULL,
    min_amount      BIGINT         NULL,
    max_amount      BIGINT         NULL,
    currency        VARCHAR(3)     NOT NULL,
    ask_email       INTEGER        NOT NULL DEFAULT 0,
    ask_phone       INTEGER        NOT NULL DEFAULT 0,
    return_url      VARCHAR(1000)  NULL,
    max_payments    INTEGER        NULL,
    expires_at      VARCHAR(40)    NULL,
    active          INTEGER        NOT NULL DEFAULT 1,
    created_at      VARCHAR(40)    NOT NULL,
    created_by      VARCHAR(320)   NULL,
    updated_at      VARCHAR(40)    NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_payment_links_code ON payment_links (code);
