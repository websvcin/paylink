-- Phase 4: which tenant a payment link belongs to, so /l/{code} finds it. Codes are unique across the platform. MySQL 8.0+.
CREATE TABLE IF NOT EXISTS link_routes (
    code        VARCHAR(60)  NOT NULL PRIMARY KEY,
    tenant_id   BIGINT       NOT NULL,
    created_at  VARCHAR(40)  NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
