-- Phase 1: which tenant a payment belongs to, so the hosted checkout at /pay/{payment_id} finds it. SQLite.
CREATE TABLE IF NOT EXISTS payment_routes (
    payment_id  VARCHAR(40)  NOT NULL PRIMARY KEY,
    tenant_id   BIGINT       NOT NULL,
    created_at  VARCHAR(40)  NOT NULL
);
