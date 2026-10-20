-- Phase 4: which tenant a payment link belongs to, so /l/{code} finds it. Codes are unique across the platform. SQLite.
CREATE TABLE IF NOT EXISTS link_routes (
    code        VARCHAR(60)  NOT NULL PRIMARY KEY,
    tenant_id   BIGINT       NOT NULL,
    created_at  VARCHAR(40)  NOT NULL
);
