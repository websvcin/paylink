-- Phase 1: which tenant a payment belongs to, so the hosted checkout at /pay/{payment_id} finds it. MySQL 8.0+.
CREATE TABLE IF NOT EXISTS payment_routes (
    payment_id  VARCHAR(40)  NOT NULL PRIMARY KEY,
    tenant_id   BIGINT       NOT NULL,
    created_at  VARCHAR(40)  NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
