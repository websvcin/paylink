-- Phase 1: payments, attempts, the payment journey, keys, webhooks and basic risk rules. MySQL 8.0+.
-- Same tables as db/sqlite/tenant/0002_payments.sql. Amounts are integers in minor units (paise for INR).

CREATE TABLE IF NOT EXISTS connector_settings (
    connector_id         VARCHAR(50)    NOT NULL,
    mode                 VARCHAR(10)    NOT NULL,
    enabled              INT            NOT NULL DEFAULT 0,
    config_enc           MEDIUMTEXT     NULL,
    check_ok             INT            NULL,
    check_message        VARCHAR(500)   NULL,
    checked_at           VARCHAR(40)    NULL,
    sort_order           INT            NOT NULL DEFAULT 0,
    updated_at           VARCHAR(40)    NOT NULL,
    updated_by           VARCHAR(320)   NULL,
    PRIMARY KEY (connector_id, mode)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS payments (
    id                   VARCHAR(40)    NOT NULL PRIMARY KEY,
    livemode             INT            NOT NULL,
    status               VARCHAR(20)    NOT NULL,
    amount               BIGINT         NOT NULL,
    currency             VARCHAR(3)     NOT NULL,
    reference            VARCHAR(100)   NULL,
    description          VARCHAR(300)   NULL,
    customer_name        VARCHAR(200)   NULL,
    customer_email       VARCHAR(320)   NULL,
    customer_phone       VARCHAR(40)    NULL,
    customer_id          VARCHAR(100)   NULL,
    return_url           VARCHAR(1000)  NULL,
    metadata             MEDIUMTEXT     NULL,
    idempotency_key      VARCHAR(200)   NULL,
    request_hash         VARCHAR(64)    NULL,
    created_via          VARCHAR(20)    NOT NULL,
    created_by           VARCHAR(320)   NULL,
    api_key_id           BIGINT         NULL,
    trace_id             VARCHAR(40)    NOT NULL,
    created_at           VARCHAR(40)    NOT NULL,
    updated_at           VARCHAR(40)    NOT NULL,
    expires_at           VARCHAR(40)    NOT NULL,
    paid_at              VARCHAR(40)    NULL,
    attempt_count        INT            NOT NULL DEFAULT 0,
    connector            VARCHAR(50)    NULL,
    method               VARCHAR(20)    NULL,
    succeeded_attempt_id VARCHAR(40)    NULL,
    utr                  VARCHAR(40)    NULL,
    failure_reason       VARCHAR(40)    NULL,
    failure_message      VARCHAR(500)   NULL,
    hold_reason          VARCHAR(300)   NULL,
    KEY ix_payments_created (livemode, created_at),
    KEY ix_payments_status (status, expires_at),
    KEY ix_payments_reference (reference),
    KEY ix_payments_utr (utr),
    UNIQUE KEY uq_payments_idempotency (livemode, idempotency_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS attempts (
    id                   VARCHAR(40)    NOT NULL PRIMARY KEY,
    payment_id           VARCHAR(40)    NOT NULL,
    connector            VARCHAR(50)    NOT NULL,
    method               VARCHAR(20)    NOT NULL,
    status               VARCHAR(20)    NOT NULL,
    gateway_ref          VARCHAR(200)   NULL,
    next_action          MEDIUMTEXT     NULL,
    failure_reason       VARCHAR(40)    NULL,
    gateway_code         VARCHAR(100)   NULL,
    gateway_message      VARCHAR(500)   NULL,
    utr                  VARCHAR(40)    NULL,
    proof_file           VARCHAR(300)   NULL,
    ip                   VARCHAR(64)    NULL,
    user_agent           VARCHAR(500)   NULL,
    created_at           VARCHAR(40)    NOT NULL,
    updated_at           VARCHAR(40)    NOT NULL,
    KEY ix_attempts_payment (payment_id),
    KEY ix_attempts_gateway (connector, gateway_ref),
    KEY ix_attempts_queue (connector, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Append-only: every change to a payment adds a row; nothing is updated or deleted.
CREATE TABLE IF NOT EXISTS payment_events (
    id                   BIGINT         NOT NULL AUTO_INCREMENT PRIMARY KEY,
    payment_id           VARCHAR(40)    NOT NULL,
    attempt_id           VARCHAR(40)    NULL,
    at                   VARCHAR(40)    NOT NULL,
    type                 VARCHAR(50)    NOT NULL,
    status_before        VARCHAR(20)    NULL,
    status_after         VARCHAR(20)    NULL,
    actor_type           VARCHAR(20)    NOT NULL,
    actor_id             VARCHAR(200)   NULL,
    actor_name           VARCHAR(320)   NULL,
    ip                   VARCHAR(64)    NULL,
    user_agent           VARCHAR(500)   NULL,
    data                 MEDIUMTEXT     NULL,
    KEY ix_payment_events_payment (payment_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Secret keys (sk_test_/sk_live_) for the Payments API and tenant developer keys (tdk_). Only hashes are stored.
CREATE TABLE IF NOT EXISTS api_keys (
    id                   BIGINT         NOT NULL AUTO_INCREMENT PRIMARY KEY,
    name                 VARCHAR(200)   NOT NULL,
    kind                 VARCHAR(10)    NOT NULL,
    livemode             INT            NOT NULL DEFAULT 0,
    key_prefix           VARCHAR(40)    NOT NULL,
    key_hash             VARCHAR(64)    NOT NULL,
    capabilities         VARCHAR(500)   NULL,
    created_at           VARCHAR(40)    NOT NULL,
    created_by           VARCHAR(320)   NULL,
    expires_at           VARCHAR(40)    NULL,
    last_used_at         VARCHAR(40)    NULL,
    revoked_at           VARCHAR(40)    NULL,
    UNIQUE KEY uq_api_keys_hash (key_hash)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS webhook_endpoints (
    id                   BIGINT         NOT NULL AUTO_INCREMENT PRIMARY KEY,
    url                  VARCHAR(1000)  NOT NULL,
    livemode             INT            NOT NULL,
    secret_enc           MEDIUMTEXT     NOT NULL,
    events               VARCHAR(500)   NOT NULL DEFAULT '*',
    enabled              INT            NOT NULL DEFAULT 1,
    description          VARCHAR(300)   NULL,
    created_at           VARCHAR(40)    NOT NULL,
    created_by           VARCHAR(320)   NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS webhook_deliveries (
    id                   BIGINT         NOT NULL AUTO_INCREMENT PRIMARY KEY,
    endpoint_id          BIGINT         NOT NULL,
    event_id             VARCHAR(40)    NOT NULL,
    event_type           VARCHAR(50)    NOT NULL,
    payment_id           VARCHAR(40)    NULL,
    livemode             INT            NOT NULL,
    payload              MEDIUMTEXT     NOT NULL,
    status               VARCHAR(20)    NOT NULL,
    attempt_count        INT            NOT NULL DEFAULT 0,
    next_attempt_at      VARCHAR(40)    NULL,
    last_attempt_at      VARCHAR(40)    NULL,
    last_status_code     INT            NULL,
    last_response        VARCHAR(500)   NULL,
    last_duration_ms     INT            NULL,
    created_at           VARCHAR(40)    NOT NULL,
    KEY ix_webhook_deliveries_due (status, next_attempt_at),
    KEY ix_webhook_deliveries_payment (payment_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Every call to the Payments API and tenant developer API, without bodies. Kept for debugging integrations.
CREATE TABLE IF NOT EXISTS api_requests (
    id                   BIGINT         NOT NULL AUTO_INCREMENT PRIMARY KEY,
    at                   VARCHAR(40)    NOT NULL,
    key_id               BIGINT         NULL,
    key_name             VARCHAR(200)   NULL,
    livemode             INT            NULL,
    method               VARCHAR(10)    NOT NULL,
    path                 VARCHAR(300)   NOT NULL,
    status               INT            NOT NULL,
    duration_ms          INT            NOT NULL,
    idempotency_key      VARCHAR(200)   NULL,
    ip                   VARCHAR(64)    NULL,
    error                VARCHAR(100)   NULL,
    KEY ix_api_requests_at (at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Raw notifications from gateways, verified or not, before they change anything.
CREATE TABLE IF NOT EXISTS gateway_events (
    id                   BIGINT         NOT NULL AUTO_INCREMENT PRIMARY KEY,
    received_at          VARCHAR(40)    NOT NULL,
    connector            VARCHAR(50)    NOT NULL,
    mode                 VARCHAR(10)    NOT NULL,
    verified             INT            NOT NULL,
    gateway_ref          VARCHAR(200)   NULL,
    attempt_id           VARCHAR(40)    NULL,
    status               VARCHAR(20)    NULL,
    raw_body             MEDIUMTEXT     NULL,
    error                VARCHAR(500)   NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS risk_rules (
    id                   BIGINT         NOT NULL AUTO_INCREMENT PRIMARY KEY,
    kind                 VARCHAR(30)    NOT NULL,
    value                VARCHAR(300)   NOT NULL,
    action               VARCHAR(10)    NOT NULL,
    enabled              INT            NOT NULL DEFAULT 1,
    note                 VARCHAR(300)   NULL,
    created_at           VARCHAR(40)    NOT NULL,
    created_by           VARCHAR(320)   NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
